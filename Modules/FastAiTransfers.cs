using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Accelerates AiAcquisitionPasses.AITransfers with 100% pure managed Harmony patches
    /// (ZERO native INativeDetour stacking, zero risk of 0xC0000005 access violations) and
    /// 100% identical AI transfer behavior:
    ///
    /// 1. Prunes dormant/folded and locked clubs from _scratchPriorityTeams and _scratchOtherTeams
    ///    before the O(teams * players) candidate scan.
    /// 2. Memoizes 8 hot pure EMSkinCore C# calculations strictly during the active AITransfers pass
    ///    (single-thread scoped to the AITransfers thread, invalidated immediately on any offer/deal/
    ///    pricelist mutation, and guaranteed cleared in a Harmony Finalizer):
    ///      - PlayerMoney.Value(DataPlayer, int)
    ///      - PlayerMoney.Wage(DataPlayer)
    ///      - AiIdentity.StarRank(DataPlayer, DataTeam)
    ///      - MarketView.ClubRatio(DataPlayer, DataTeam)
    ///      - ClubPlan.WantsOut(DataPlayer)
    ///      - AiSight.StarsError(DataPlayer, DataTeam)
    ///      - Dormant.Is(DataTeam)
    ///      - LockedClub.Blocks(DataUser, DataTeam) (negative fast-path for non-locked clubs/players)
    /// </summary>
    internal static class FastAiTransfers
    {
        private delegate bool TeamPredicate(DataTeam team);
        private delegate bool BoolGetter();

        private readonly struct PtrPair : IEquatable<PtrPair>
        {
            public readonly IntPtr A;
            public readonly IntPtr B;

            public PtrPair(IntPtr a, IntPtr b)
            {
                A = a;
                B = b;
            }

            public bool Equals(PtrPair other) => A == other.A && B == other.B;
            public override bool Equals(object obj) => obj is PtrPair other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(A, B);
        }

        private readonly struct PtrIntKey : IEquatable<PtrIntKey>
        {
            public readonly IntPtr Ptr;
            public readonly int Val;

            public PtrIntKey(IntPtr ptr, int val)
            {
                Ptr = ptr;
                Val = val;
            }

            public bool Equals(PtrIntKey other) => Ptr == other.Ptr && Val == other.Val;
            public override bool Equals(object obj) => obj is PtrIntKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Ptr, Val);
        }

        private static TeamPredicate _isDormant;
        private static BoolGetter _isLockedActive;
        private static TeamPredicate _isLockedClub;
        private static FieldInfo _fLockedClubId;

        // Single-thread ownership during AITransfers => zero lock contention on hot path
        private static int _aiTransfersThreadId;
        private static long _mutationEpoch = 1;
        private static long _cachedEpoch = -1;
        private static long _passStartTicks;
        private static int _passCount;
        private static string _passLockedClubId;

        // Per-pass pure managed caches (ONLY accessed by _aiTransfersThreadId, cleared on exit)
        private static readonly Dictionary<PtrIntKey, int> _valueCache = new(4096);
        private static readonly Dictionary<IntPtr, int> _wageCache = new(4096);
        private static readonly Dictionary<PtrPair, int> _starRankCache = new(4096);
        private static readonly Dictionary<PtrPair, float> _clubRatioCache = new(4096);
        private static readonly Dictionary<IntPtr, bool> _wantsOutCache = new(4096);
        private static readonly Dictionary<PtrPair, float> _starsErrorCache = new(4096);
        private static readonly Dictionary<IntPtr, bool> _dormantPtrCache = new(512);
        private static readonly Dictionary<IntPtr, byte> _lockedTeamPtrCache = new(512);
        private static readonly Dictionary<IntPtr, byte> _lockedUserPtrCache = new(4096);

        private static long _passHitsValue, _passHitsWage, _passHitsStarRank, _passHitsClubRatio;
        private static long _passHitsWantsOut, _passHitsStarsError, _passHitsDormant, _passHitsLocked;

        public static void InstallGamePatches(Harmony harmony)
        {
            try
            {
                var aiTransfers = AccessTools.Method(typeof(AiAcquisitionPasses), nameof(AiAcquisitionPasses.AITransfers));
                if (aiTransfers != null)
                {
                    harmony.Patch(
                        aiTransfers,
                        prefix: new HarmonyMethod(typeof(FastAiTransfers), nameof(AiTransfers_Prefix))
                        {
                            priority = Priority.First
                        },
                        finalizer: new HarmonyMethod(typeof(FastAiTransfers), nameof(AiTransfers_Finalizer))
                        {
                            priority = Priority.Last
                        });
                }

                var buildBuckets = AccessTools.Method(typeof(AiAcquisitionPasses), nameof(AiAcquisitionPasses.BuildAITransfersBuckets));
                if (buildBuckets != null)
                {
                    harmony.Patch(
                        buildBuckets,
                        postfix: new HarmonyMethod(typeof(FastAiTransfers), nameof(BuildAITransfersBuckets_Postfix))
                        {
                            priority = Priority.Last
                        });
                    Plugin.Logger.LogMessage("[FastAiTransfers] Patched AiAcquisitionPasses.BuildAITransfersBuckets (prunes dormant/locked clubs before O(N*M) scan).");
                }

                var buildPool = AccessTools.Method(typeof(AiCandidateSelection), nameof(AiCandidateSelection.BuildAvailablePlayerPool));
                if (buildPool != null)
                {
                    harmony.Patch(
                        buildPool,
                        prefix: new HarmonyMethod(typeof(FastAiTransfers), nameof(BuildPool_Pre)),
                        postfix: new HarmonyMethod(typeof(FastAiTransfers), nameof(BuildPool_Post)));
                }

                // Instant epoch invalidators whenever ANY offer, deal, contract, or pricelist changes
                PatchMutator(harmony, typeof(DataOffers), nameof(DataOffers.BumpOfferActivityVersion));
                PatchMutator(harmony, typeof(DataOffers), nameof(DataOffers.MarkPricelistAdded));
                PatchMutator(harmony, typeof(DataOffers), nameof(DataOffers.ClearPricelistAdded));
                PatchMutator(harmony, typeof(ContractEngine), nameof(ContractEngine.CreateNewContractPlayer));
                PatchMutator(harmony, typeof(AiDealSettlement), nameof(AiDealSettlement.ApplyAIDeal));

                var silentNeg = AccessTools.Method(typeof(AiMarketRules), nameof(AiMarketRules.SilentAINegotiation));
                if (silentNeg != null)
                {
                    harmony.Patch(
                        silentNeg,
                        postfix: new HarmonyMethod(typeof(FastAiTransfers), nameof(SilentNeg_Postfix)));
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[FastAiTransfers] Game patch warning: {ex.Message}");
            }
        }

        private static void PatchMutator(Harmony harmony, Type type, string methodName)
        {
            try
            {
                var m = AccessTools.Method(type, methodName);
                if (m != null)
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(FastAiTransfers), nameof(OnWorldMutated)));
            }
            catch { }
        }

        public static void AttachToCore(Harmony coreHarmony, Assembly coreAsm)
        {
            _isDormant = null;
            _isLockedActive = null;
            _isLockedClub = null;
            _fLockedClubId = null;

            int memoHooks = 0;

            try
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                var dormantType = coreAsm.GetType("Dormant", false);
                var isDormantMethod = dormantType?.GetMethod("Is", flags, null, new[] { typeof(DataTeam) }, null);
                if (isDormantMethod != null)
                {
                    _isDormant = (TeamPredicate)Delegate.CreateDelegate(typeof(TeamPredicate), isDormantMethod);
                    coreHarmony.Patch(
                        isDormantMethod,
                        prefix: new HarmonyMethod(typeof(FastAiTransfers), nameof(Dormant_Is_Prefix)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(FastAiTransfers), nameof(Dormant_Is_Postfix)));
                    memoHooks++;
                }

                var lockedType = coreAsm.GetType("LockedClub", false);
                var activeGetter = lockedType?.GetMethod("get_Active", flags, null, Type.EmptyTypes, null);
                var isClubMethod = lockedType?.GetMethod("IsClub", flags, null, new[] { typeof(DataTeam) }, null);
                _fLockedClubId = lockedType?.GetField("_clubId", flags);
                if (activeGetter != null && isClubMethod != null)
                {
                    _isLockedActive = (BoolGetter)Delegate.CreateDelegate(typeof(BoolGetter), activeGetter);
                    _isLockedClub = (TeamPredicate)Delegate.CreateDelegate(typeof(TeamPredicate), isClubMethod);
                }

                var lockedBlocks = lockedType?.GetMethod("Blocks", flags, null, new[] { typeof(DataUser), typeof(DataTeam) }, null);
                if (lockedBlocks != null && _fLockedClubId != null)
                {
                    coreHarmony.Patch(
                        lockedBlocks,
                        prefix: new HarmonyMethod(typeof(FastAiTransfers), nameof(LockedClub_Blocks_Prefix)) { priority = Priority.First });
                    memoHooks++;
                }

                // Pure managed Harmony memoization on heavy EMSkinCore C# static helpers
                if (TryPatchCore(coreHarmony, coreAsm, "PlayerMoney", "Value", new[] { typeof(DataPlayer), typeof(int) },
                    nameof(PlayerMoney_Value_Prefix), nameof(PlayerMoney_Value_Postfix)))
                    memoHooks++;

                if (TryPatchCore(coreHarmony, coreAsm, "PlayerMoney", "Wage", new[] { typeof(DataPlayer) },
                    nameof(PlayerMoney_Wage_Prefix), nameof(PlayerMoney_Wage_Postfix)))
                    memoHooks++;

                if (TryPatchCore(coreHarmony, coreAsm, "AiIdentity", "StarRank", new[] { typeof(DataPlayer), typeof(DataTeam) },
                    nameof(AiIdentity_StarRank_Prefix), nameof(AiIdentity_StarRank_Postfix)))
                    memoHooks++;

                if (TryPatchCore(coreHarmony, coreAsm, "MarketView", "ClubRatio", new[] { typeof(DataPlayer), typeof(DataTeam) },
                    nameof(MarketView_ClubRatio_Prefix), nameof(MarketView_ClubRatio_Postfix)))
                    memoHooks++;

                if (TryPatchCore(coreHarmony, coreAsm, "ClubPlan", "WantsOut", new[] { typeof(DataPlayer) },
                    nameof(ClubPlan_WantsOut_Prefix), nameof(ClubPlan_WantsOut_Postfix)))
                    memoHooks++;

                if (TryPatchCore(coreHarmony, coreAsm, "AiSight", "StarsError", new[] { typeof(DataPlayer), typeof(DataTeam) },
                    nameof(AiSight_StarsError_Prefix), nameof(AiSight_StarsError_Postfix)))
                    memoHooks++;

                Plugin.Logger.LogMessage(
                    $"[FastAiTransfers] Bound to EMSkinCore (Dormant={_isDormant != null}, LockedClub={_isLockedClub != null}, SafeManagedMemoHooks={memoHooks}/8).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[FastAiTransfers] Core bind warning: {ex.Message}");
            }
        }

        private static bool TryPatchCore(
            Harmony coreHarmony,
            Assembly coreAsm,
            string typeName,
            string methodName,
            Type[] paramTypes,
            string prefixName,
            string postfixName)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var t = coreAsm.GetType(typeName, false);
                var m = t?.GetMethod(methodName, flags, null, paramTypes, null);
                if (m == null) return false;

                coreHarmony.Patch(
                    m,
                    prefix: new HarmonyMethod(typeof(FastAiTransfers), prefixName) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(FastAiTransfers), postfixName));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[FastAiTransfers] Core hook skipped ({typeName}.{methodName}): {ex.Message}");
                return false;
            }
        }

        public static void Detach()
        {
            Volatile.Write(ref _aiTransfersThreadId, 0);
            ResetState();
        }

        public static void ResetState()
        {
            Interlocked.Increment(ref _mutationEpoch);
            if (Volatile.Read(ref _aiTransfersThreadId) == 0)
            {
                ClearAllCaches(-1);
                _dormantPtrCache.Clear();
                _lockedTeamPtrCache.Clear();
                _lockedUserPtrCache.Clear();
            }
        }

        private static bool IsActiveOnCurrentThread()
        {
            int tid = Volatile.Read(ref _aiTransfersThreadId);
            return tid != 0 && tid == Environment.CurrentManagedThreadId;
        }

        private static void EnsureEpochFresh()
        {
            long epoch = Volatile.Read(ref _mutationEpoch);
            if (epoch != _cachedEpoch)
                ClearAllCaches(epoch);
        }

        private static void ClearAllCaches(long newEpoch)
        {
            _valueCache.Clear();
            _wageCache.Clear();
            _starRankCache.Clear();
            _clubRatioCache.Clear();
            _wantsOutCache.Clear();
            _starsErrorCache.Clear();
            _lockedUserPtrCache.Clear();
            _cachedEpoch = newEpoch;
        }

        private static void OnWorldMutated() => Interlocked.Increment(ref _mutationEpoch);

        private static void SilentNeg_Postfix(bool __result)
        {
            if (__result)
                Interlocked.Increment(ref _mutationEpoch);
        }

        private static void AiTransfers_Prefix()
        {
            try
            {
                if (!Plugin.CfgFastAiTransfers.Value)
                    return;

                long epoch = Interlocked.Increment(ref _mutationEpoch);
                ClearAllCaches(epoch);
                _dormantPtrCache.Clear();
                _lockedTeamPtrCache.Clear();

                _passLockedClubId = null;
                if (_fLockedClubId != null)
                {
                    try { _passLockedClubId = _fLockedClubId.GetValue(null) as string; }
                    catch { _passLockedClubId = null; }
                }

                _passHitsValue = _passHitsWage = _passHitsStarRank = _passHitsClubRatio = 0;
                _passHitsWantsOut = _passHitsStarsError = _passHitsDormant = _passHitsLocked = 0;
                _passStartTicks = Stopwatch.GetTimestamp();

                Volatile.Write(ref _aiTransfersThreadId, Environment.CurrentManagedThreadId);
            }
            catch { }
        }

        private static void AiTransfers_Finalizer()
        {
            try
            {
                if (Volatile.Read(ref _aiTransfersThreadId) == 0)
                    return;

                Volatile.Write(ref _aiTransfersThreadId, 0);

                long elapsedMs = _passStartTicks > 0
                    ? (Stopwatch.GetTimestamp() - _passStartTicks) * 1000L / Stopwatch.Frequency
                    : 0;

                long totalHits = _passHitsValue + _passHitsWage + _passHitsStarRank + _passHitsClubRatio
                    + _passHitsWantsOut + _passHitsStarsError + _passHitsDormant + _passHitsLocked;

                if (totalHits > 0)
                    Interlocked.Add(ref PerfTelemetry.AiMemoHitsTotal, totalHits);

                int passNum = Interlocked.Increment(ref _passCount);
                if (passNum == 1 || passNum % 25 == 0)
                {
                    Plugin.Logger.LogMessage(
                        $"[FastAiTransfers] Pass #{passNum}: {elapsedMs}ms | prio {PerfTelemetry.LastPrioBefore}->{PerfTelemetry.LastPrioAfter}, other {PerfTelemetry.LastOtherBefore}->{PerfTelemetry.LastOtherAfter} | hits: Value={_passHitsValue}, Wage={_passHitsWage}, StarRank={_passHitsStarRank}, Ratio={_passHitsClubRatio}, WantsOut={_passHitsWantsOut}, Err={_passHitsStarsError}, Dormant={_passHitsDormant}, Locked={_passHitsLocked}");
                }

                ClearAllCaches(-1);
                _dormantPtrCache.Clear();
                _lockedTeamPtrCache.Clear();
                _lockedUserPtrCache.Clear();
            }
            catch
            {
                Volatile.Write(ref _aiTransfersThreadId, 0);
            }
        }

        // ==================== Pure Managed EMSkinCore Per-Pass Caches ====================

        private static bool PlayerMoney_Value_Prefix(DataPlayer __0, int __1, ref int __result, out bool __state)
        {
            __state = false;
            if (!IsActiveOnCurrentThread() || __0 == null)
                return true;

            EnsureEpochFresh();
            var key = new PtrIntKey(__0.Pointer, __1);
            if (_valueCache.TryGetValue(key, out int cached))
            {
                _passHitsValue++;
                __result = cached;
                return false;
            }

            __state = true;
            return true;
        }

        private static void PlayerMoney_Value_Postfix(DataPlayer __0, int __1, int __result, bool __state)
        {
            if (__state && IsActiveOnCurrentThread() && __0 != null && Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                _valueCache[new PtrIntKey(__0.Pointer, __1)] = __result;
        }

        private static bool PlayerMoney_Wage_Prefix(DataPlayer __0, ref int __result, out bool __state)
        {
            __state = false;
            if (!IsActiveOnCurrentThread() || __0 == null)
                return true;

            EnsureEpochFresh();
            if (_wageCache.TryGetValue(__0.Pointer, out int cached))
            {
                _passHitsWage++;
                __result = cached;
                return false;
            }

            __state = true;
            return true;
        }

        private static void PlayerMoney_Wage_Postfix(DataPlayer __0, int __result, bool __state)
        {
            if (__state && IsActiveOnCurrentThread() && __0 != null && Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                _wageCache[__0.Pointer] = __result;
        }

        private static bool AiIdentity_StarRank_Prefix(DataPlayer __0, DataTeam __1, ref int __result, out bool __state)
        {
            __state = false;
            if (!IsActiveOnCurrentThread() || __0 == null || __1 == null)
                return true;

            EnsureEpochFresh();
            var key = new PtrPair(__0.Pointer, __1.Pointer);
            if (_starRankCache.TryGetValue(key, out int cached))
            {
                _passHitsStarRank++;
                __result = cached;
                return false;
            }

            __state = true;
            return true;
        }

        private static void AiIdentity_StarRank_Postfix(DataPlayer __0, DataTeam __1, int __result, bool __state)
        {
            if (__state && IsActiveOnCurrentThread() && __0 != null && __1 != null && Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                _starRankCache[new PtrPair(__0.Pointer, __1.Pointer)] = __result;
        }

        private static bool MarketView_ClubRatio_Prefix(DataPlayer __0, DataTeam __1, ref float __result, out bool __state)
        {
            __state = false;
            if (!IsActiveOnCurrentThread() || __0 == null || __1 == null)
                return true;

            EnsureEpochFresh();
            var key = new PtrPair(__0.Pointer, __1.Pointer);
            if (_clubRatioCache.TryGetValue(key, out float cached))
            {
                _passHitsClubRatio++;
                __result = cached;
                return false;
            }

            __state = true;
            return true;
        }

        private static void MarketView_ClubRatio_Postfix(DataPlayer __0, DataTeam __1, float __result, bool __state)
        {
            if (__state && IsActiveOnCurrentThread() && __0 != null && __1 != null && Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                _clubRatioCache[new PtrPair(__0.Pointer, __1.Pointer)] = __result;
        }

        private static bool ClubPlan_WantsOut_Prefix(DataPlayer __0, ref bool __result, out bool __state)
        {
            __state = false;
            if (!IsActiveOnCurrentThread() || __0 == null)
                return true;

            EnsureEpochFresh();
            if (_wantsOutCache.TryGetValue(__0.Pointer, out bool cached))
            {
                _passHitsWantsOut++;
                __result = cached;
                return false;
            }

            __state = true;
            return true;
        }

        private static void ClubPlan_WantsOut_Postfix(DataPlayer __0, bool __result, bool __state)
        {
            if (__state && IsActiveOnCurrentThread() && __0 != null && Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                _wantsOutCache[__0.Pointer] = __result;
        }

        private static bool AiSight_StarsError_Prefix(DataPlayer __0, DataTeam __1, ref float __result, out bool __state)
        {
            __state = false;
            if (!IsActiveOnCurrentThread() || __0 == null || __1 == null)
                return true;

            EnsureEpochFresh();
            var key = new PtrPair(__0.Pointer, __1.Pointer);
            if (_starsErrorCache.TryGetValue(key, out float cached))
            {
                _passHitsStarsError++;
                __result = cached;
                return false;
            }

            __state = true;
            return true;
        }

        private static void AiSight_StarsError_Postfix(DataPlayer __0, DataTeam __1, float __result, bool __state)
        {
            if (__state && IsActiveOnCurrentThread() && __0 != null && __1 != null && Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                _starsErrorCache[new PtrPair(__0.Pointer, __1.Pointer)] = __result;
        }

        private static bool Dormant_Is_Prefix(DataTeam __0, ref bool __result)
        {
            if (!IsActiveOnCurrentThread())
                return true;

            if (__0 == null)
            {
                __result = false;
                return false;
            }

            if (_dormantPtrCache.TryGetValue(__0.Pointer, out bool cached))
            {
                _passHitsDormant++;
                __result = cached;
                return false;
            }

            return true;
        }

        private static void Dormant_Is_Postfix(DataTeam __0, bool __result)
        {
            if (IsActiveOnCurrentThread() && __0 != null)
                _dormantPtrCache[__0.Pointer] = __result;
        }

        private static bool LockedClub_Blocks_Prefix(DataUser __0, DataTeam __1, ref bool __result)
        {
            if (!IsActiveOnCurrentThread())
                return true;

            string clubId = _passLockedClubId;
            if (string.IsNullOrEmpty(clubId) || __0 == null || __1 == null)
            {
                _passHitsLocked++;
                __result = false;
                return false;
            }

            EnsureEpochFresh();

            IntPtr teamPtr = __1.Pointer;
            if (!_lockedTeamPtrCache.TryGetValue(teamPtr, out byte teamIsLocked))
            {
                try { teamIsLocked = string.Equals(__1.Id, clubId, StringComparison.Ordinal) ? (byte)1 : (byte)0; }
                catch { teamIsLocked = 1; }
                _lockedTeamPtrCache[teamPtr] = teamIsLocked;
            }

            if (teamIsLocked != 0)
                return true;

            IntPtr userPtr = __0.Pointer;
            if (!_lockedUserPtrCache.TryGetValue(userPtr, out byte userIsLocked))
            {
                try { userIsLocked = string.Equals(__0.TeamId, clubId, StringComparison.Ordinal) ? (byte)1 : (byte)0; }
                catch { userIsLocked = 1; }
                _lockedUserPtrCache[userPtr] = userIsLocked;
            }

            if (userIsLocked != 0)
                return true;

            _passHitsLocked++;
            __result = false;
            return false;
        }

        // ==================== Bucket Pruning & Pool Telemetry ====================

        private static void BuildAITransfersBuckets_Postfix()
        {
            try
            {
                if (!Plugin.CfgFastAiTransfers.Value)
                    return;

                var isDormant = _isDormant;
                var isLockedActive = _isLockedActive;
                var isLockedClub = _isLockedClub;

                var prio = AiAcquisitionPasses._scratchPriorityTeams;
                var other = AiAcquisitionPasses._scratchOtherTeams;

                int prioBefore = prio != null ? prio.Count : 0;
                int otherBefore = other != null ? other.Count : 0;

                if (isDormant == null && isLockedClub == null)
                {
                    PerfTelemetry.RecordBucketPrune(prioBefore, prioBefore, otherBefore, otherBefore, 0);
                    return;
                }

                bool checkLocked = isLockedActive != null && isLockedClub != null && isLockedActive();

                int prunedPrio = PruneList(prio, isDormant, checkLocked ? isLockedClub : null);
                int prunedOther = PruneList(other, isDormant, checkLocked ? isLockedClub : null);

                PerfTelemetry.RecordBucketPrune(
                    prioBefore, prioBefore - prunedPrio,
                    otherBefore, otherBefore - prunedOther,
                    prunedPrio + prunedOther);
            }
            catch { }
        }

        private static int PruneList(
            Il2CppSystem.Collections.Generic.List<DataTeam> list,
            TeamPredicate isDormant,
            TeamPredicate isLockedClub)
        {
            if (list == null || list.Count == 0)
                return 0;

            bool cacheDormant = IsActiveOnCurrentThread();
            int pruned = 0;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                var team = list[i];
                if (team == null) continue;

                bool dormant = isDormant != null && isDormant(team);
                if (cacheDormant)
                    _dormantPtrCache[team.Pointer] = dormant;

                bool blocked = dormant;
                if (!blocked && isLockedClub != null && isLockedClub(team))
                    blocked = true;

                if (blocked)
                {
                    list.RemoveAt(i);
                    pruned++;
                }
            }

            return pruned;
        }

        private static void BuildPool_Pre(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void BuildPool_Post(long __state)
        {
            if (__state <= 0) return;
            long ms = (Stopwatch.GetTimestamp() - __state) * 1000L / Stopwatch.Frequency;
            PerfTelemetry.AddSubPass(4, ms);
        }
    }
}
