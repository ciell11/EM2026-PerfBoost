using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Accelerates AiAcquisitionPasses.AITransfers and daily AI listing passes with 100% pure managed
    /// Harmony patches (ZERO native INativeDetour stacking, zero risk of 0xC0000005 access violations):
    ///
    /// 1. Prunes dormant/folded and locked clubs from _scratchPriorityTeams and _scratchOtherTeams
    ///    before the O(teams * players) candidate scan.
    /// 2. Smart Empty-Scan Cooldown (optional, default 3 days, configurable via EmptyScanSkipDays):
    ///    - NEVER touches _scratchPriorityTeams (urgent <5-player teams ALWAYS scan every single day!)
    ///    - NEVER touches the user's club or any team that had >=1 candidate yesterday.
    ///    - For _scratchOtherTeams that scanned all ~6,000 players and found ZERO eligible candidates,
    ///      staggers their next redundant re-scan by EmptyScanSkipDays (default 3 days) — and
    ///      IMMEDIATELY wakes them up early if their roster shrinks, budget rises, month changes,
    ///      or total world player count changes (regens/retirements).
    /// 3. Memoizes 11 hot pure EMSkinCore C# calculations strictly during active AI passes
    ///    (single-thread scoped with re-entrant depth, instant epoch invalidation on any offer/deal/
    ///    pricelist mutation, guaranteed cleanup in a Harmony Finalizer):
    ///      - PlayerMoney.Value(DataPlayer, int) & PlayerMoney.Wage(DataPlayer)
    ///      - AiIdentity.StarRank(DataPlayer, DataTeam) & AiIdentity.AcademyGate (non-academy fast-path)
    ///      - MarketView.ClubRatio(DataPlayer, DataTeam) & ClubPlan.WantsOut(DataPlayer)
    ///      - AiSight.StarsError(DataPlayer, DataTeam)
    ///      - SquadNeeds.NeedOf(DataTeam) & SquadNeeds.Stars(DataPlayer)
    ///      - Dormant.Is(DataTeam) & LockedClub.Blocks(DataUser, DataTeam)
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

        private readonly struct EmptyScanEntry
        {
            public readonly int NextScanDayKey;
            public readonly int MonthKey;
            public readonly int SquadCount;
            public readonly int Budget;

            public EmptyScanEntry(int nextScanDayKey, int monthKey, int squadCount, int budget)
            {
                NextScanDayKey = nextScanDayKey;
                MonthKey = monthKey;
                SquadCount = squadCount;
                Budget = budget;
            }
        }

        private static TeamPredicate _isDormant;
        private static BoolGetter _isLockedActive;
        private static TeamPredicate _isLockedClub;
        private static FieldInfo _fLockedClubId;

        // Single-thread ownership during active AI passes (re-entrant depth supports nested/chained passes)
        private static int _aiTransfersThreadId;
        private static int _activePassDepth;
        private static bool _isMainAiTransfersPass;
        private static long _mutationEpoch = 1;
        private static long _cachedEpoch = -1;
        private static long _passStartTicks;
        private static int _mainPassCount;
        private static string _passLockedClubId;

        // Per-pass pure managed caches (ONLY accessed by _aiTransfersThreadId, cleared on exit)
        private static readonly Dictionary<PtrIntKey, int> _valueCache = new(4096);
        private static readonly Dictionary<IntPtr, int> _wageCache = new(4096);
        private static readonly Dictionary<PtrPair, int> _starRankCache = new(4096);
        private static readonly Dictionary<PtrPair, float> _clubRatioCache = new(4096);
        private static readonly Dictionary<IntPtr, bool> _wantsOutCache = new(4096);
        private static readonly Dictionary<PtrPair, float> _starsErrorCache = new(4096);
        private static readonly Dictionary<IntPtr, object> _needOfCache = new(512);
        private static readonly Dictionary<IntPtr, float> _starsCache = new(4096);
        private static readonly Dictionary<IntPtr, byte> _teamIsAcademyCache = new(512);
        private static readonly Dictionary<IntPtr, bool> _dormantPtrCache = new(512);
        private static readonly Dictionary<IntPtr, byte> _lockedTeamPtrCache = new(512);
        private static readonly Dictionary<IntPtr, byte> _lockedUserPtrCache = new(4096);

        // Cross-day empty-scan cooldown for _scratchOtherTeams ONLY (never touches _scratchPriorityTeams!)
        private static readonly Dictionary<IntPtr, EmptyScanEntry> _emptyOtherCooldowns = new(512);
        private static readonly List<(IntPtr ptr, int squadCount, int budget)> _scannedOtherThisPass = new(320);
        private static readonly HashSet<IntPtr> _teamsWithCandidatesThisPass = new(64);
        private static string _emptyCooldownSlot = "";
        private static int _emptyCooldownWorldPlayers = -1;
        private static int _lastEmptySkipped;
        private static int _lastDormantSkipped;

        private static long _passHitsValue, _passHitsWage, _passHitsStarRank, _passHitsClubRatio;
        private static long _passHitsWantsOut, _passHitsStarsError, _passHitsNeed, _passHitsStars;
        private static long _passHitsAcad, _passHitsDormant, _passHitsLocked;

        public static void InstallGamePatches(Harmony harmony)
        {
            try
            {
                // Scope pure managed memoization across ALL heavy daily AI transfer & listing passes
                var mainPre = new HarmonyMethod(typeof(FastAiTransfers), nameof(MainAiTransfers_Prefix)) { priority = Priority.First };
                var passPre = new HarmonyMethod(typeof(FastAiTransfers), nameof(AiPass_Prefix)) { priority = Priority.First };
                var passFin = new HarmonyMethod(typeof(FastAiTransfers), nameof(AiPass_Finalizer)) { priority = Priority.Last };

                int scopedPasses = 0;
                var aiTransfers = AccessTools.Method(typeof(AiAcquisitionPasses), nameof(AiAcquisitionPasses.AITransfers));
                if (aiTransfers != null)
                {
                    harmony.Patch(aiTransfers, prefix: mainPre, finalizer: passFin);
                    scopedPasses++;
                }

                (Type t, string m)[] secondaryPasses =
                {
                    (typeof(AiListingPasses), nameof(AiListingPasses.AIListedPlayerInterestPass)),
                    (typeof(AiListingPasses), nameof(AiListingPasses.AIPricelistInterestPass)),
                    (typeof(AiAcquisitionPasses), nameof(AiAcquisitionPasses.AIOptimisationPass)),
                    (typeof(AiAcquisitionPasses), nameof(AiAcquisitionPasses.AIElitePoachingPass)),
                    (typeof(AiAcquisitionPasses), nameof(AiAcquisitionPasses.AITopFreeAgentPickupPass)),
                    (typeof(AiAcquisitionPasses), nameof(AiAcquisitionPasses.AIYouthProspectPickupPass)),
                };
                foreach (var (pt, pm) in secondaryPasses)
                {
                    try
                    {
                        var method = AccessTools.Method(pt, pm);
                        if (method != null)
                        {
                            harmony.Patch(method, prefix: passPre, finalizer: passFin);
                            scopedPasses++;
                        }
                    }
                    catch { }
                }

                var buildBuckets = AccessTools.Method(typeof(AiAcquisitionPasses), nameof(AiAcquisitionPasses.BuildAITransfersBuckets));
                if (buildBuckets != null)
                {
                    harmony.Patch(
                        buildBuckets,
                        postfix: new HarmonyMethod(typeof(FastAiTransfers), nameof(BuildAITransfersBuckets_Postfix)) { priority = Priority.Last });
                }

                var buildOptBuckets = AccessTools.Method(typeof(AiAcquisitionPasses), nameof(AiAcquisitionPasses.BuildAIOptimisationBuckets));
                if (buildOptBuckets != null)
                {
                    harmony.Patch(
                        buildOptBuckets,
                        postfix: new HarmonyMethod(typeof(FastAiTransfers), nameof(BuildAIOptimisationBuckets_Postfix)) { priority = Priority.Last });
                }

                // Track which teams actually got a picked player during AITransfers (for empty-scan cooldown):
                // MUST be a Postfix checking __result != null! (RoleAwarePickPlayer is called even when
                // SquadNeeds.Pick rejects every candidate in the pool and returns null!)
                var rolePick = AccessTools.Method(typeof(AiCandidateSelection), nameof(AiCandidateSelection.RoleAwarePickPlayer));
                if (rolePick != null)
                {
                    harmony.Patch(
                        rolePick,
                        postfix: new HarmonyMethod(typeof(FastAiTransfers), nameof(RoleAwarePick_Postfix)) { priority = Priority.Last });
                }

                Plugin.Logger.LogMessage(
                    $"[FastAiTransfers] Scoped memoization active on {scopedPasses}/7 daily AI passes + bucket pruning (EmptyScanSkipDays={Plugin.CfgEmptyScanSkipDays.Value}).");

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

                // Pure managed Harmony memoization on 9 additional hot EMSkinCore C# static helpers
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

                if (TryPatchCore(coreHarmony, coreAsm, "SquadNeeds", "NeedOf", new[] { typeof(DataTeam) },
                    nameof(SquadNeeds_NeedOf_Prefix), nameof(SquadNeeds_NeedOf_Postfix)))
                    memoHooks++;

                if (TryPatchCore(coreHarmony, coreAsm, "SquadNeeds", "Stars", new[] { typeof(DataPlayer) },
                    nameof(SquadNeeds_Stars_Prefix), nameof(SquadNeeds_Stars_Postfix)))
                    memoHooks++;

                // Fast-path AiIdentity.AcademyGate for confirmed non-academy clubs (skips team.IsAcademy IL2CPP icall!)
                var aiIdType = coreAsm.GetType("AiIdentity", false);
                var acadGate = aiIdType?.GetMethod("AcademyGate", flags);
                if (acadGate != null && acadGate.GetParameters().Length == 3)
                {
                    coreHarmony.Patch(
                        acadGate,
                        prefix: new HarmonyMethod(typeof(FastAiTransfers), nameof(AiIdentity_AcademyGate_Prefix)) { priority = Priority.First });
                    memoHooks++;
                }

                Plugin.Logger.LogMessage(
                    $"[FastAiTransfers] Bound to EMSkinCore (Dormant={_isDormant != null}, LockedClub={_isLockedClub != null}, SafeManagedMemoHooks={memoHooks}/11).");
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
            Volatile.Write(ref _activePassDepth, 0);
            Volatile.Write(ref _aiTransfersThreadId, 0);
            _isMainAiTransfersPass = false;
            ResetState();
        }

        public static void ResetState()
        {
            Interlocked.Increment(ref _mutationEpoch);
            if (Volatile.Read(ref _aiTransfersThreadId) == 0)
            {
                ClearAllCaches(-1);
                _teamIsAcademyCache.Clear();
                _dormantPtrCache.Clear();
                _lockedTeamPtrCache.Clear();
                _lockedUserPtrCache.Clear();
                _emptyOtherCooldowns.Clear();
                _scannedOtherThisPass.Clear();
                _teamsWithCandidatesThisPass.Clear();
                _emptyCooldownSlot = "";
                _emptyCooldownWorldPlayers = -1;
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
            _needOfCache.Clear();
            _starsCache.Clear();
            _lockedUserPtrCache.Clear();
            _cachedEpoch = newEpoch;
        }

        private static void OnWorldMutated() => Interlocked.Increment(ref _mutationEpoch);

        private static void SilentNeg_Postfix(bool __result)
        {
            if (__result)
                Interlocked.Increment(ref _mutationEpoch);
        }

        private static void MainAiTransfers_Prefix() => BeginPass(isMainAiTransfers: true);
        private static void AiPass_Prefix() => BeginPass(isMainAiTransfers: false);

        private static void BeginPass(bool isMainAiTransfers)
        {
            try
            {
                if (!Plugin.CfgFastAiTransfers.Value)
                    return;

                int tid = Environment.CurrentManagedThreadId;
                int existingTid = Volatile.Read(ref _aiTransfersThreadId);
                if (existingTid == tid && _activePassDepth > 0)
                {
                    _activePassDepth++;
                    if (isMainAiTransfers)
                        _isMainAiTransfersPass = true;
                    return;
                }
                if (existingTid != 0 && existingTid != tid)
                    return;

                long epoch = Interlocked.Increment(ref _mutationEpoch);
                ClearAllCaches(epoch);
                _teamIsAcademyCache.Clear();
                _dormantPtrCache.Clear();
                _lockedTeamPtrCache.Clear();

                if (isMainAiTransfers)
                {
                    _scannedOtherThisPass.Clear();
                    _teamsWithCandidatesThisPass.Clear();
                    _lastEmptySkipped = 0;
                    _lastDormantSkipped = 0;
                }

                _passLockedClubId = null;
                if (_fLockedClubId != null)
                {
                    try { _passLockedClubId = _fLockedClubId.GetValue(null) as string; }
                    catch { _passLockedClubId = null; }
                }

                _passHitsValue = _passHitsWage = _passHitsStarRank = _passHitsClubRatio = 0;
                _passHitsWantsOut = _passHitsStarsError = _passHitsNeed = _passHitsStars = 0;
                _passHitsAcad = _passHitsDormant = _passHitsLocked = 0;
                _passStartTicks = Stopwatch.GetTimestamp();

                _isMainAiTransfersPass = isMainAiTransfers;
                _activePassDepth = 1;
                Volatile.Write(ref _aiTransfersThreadId, tid);
            }
            catch { }
        }

        private static void AiPass_Finalizer()
        {
            try
            {
                int tid = Environment.CurrentManagedThreadId;
                if (Volatile.Read(ref _aiTransfersThreadId) != tid)
                    return;

                if (--_activePassDepth > 0)
                    return;

                bool wasMain = _isMainAiTransfersPass;
                _isMainAiTransfersPass = false;
                _activePassDepth = 0;
                Volatile.Write(ref _aiTransfersThreadId, 0);

                long elapsedMs = _passStartTicks > 0
                    ? (Stopwatch.GetTimestamp() - _passStartTicks) * 1000L / Stopwatch.Frequency
                    : 0;

                long totalHits = _passHitsValue + _passHitsWage + _passHitsStarRank + _passHitsClubRatio
                    + _passHitsWantsOut + _passHitsStarsError + _passHitsNeed + _passHitsStars
                    + _passHitsAcad + _passHitsDormant + _passHitsLocked;

                if (totalHits > 0)
                    Interlocked.Add(ref PerfTelemetry.AiMemoHitsTotal, totalHits);

                if (wasMain)
                {
                    UpdateEmptyScanCooldowns();

                    int mainNum = Interlocked.Increment(ref _mainPassCount);
                    if (mainNum <= 2 || mainNum % 10 == 0)
                    {
                        Plugin.Logger.LogMessage(
                            $"[FastAiTransfers] AITransfers #{mainNum}: {elapsedMs}ms | prio {PerfTelemetry.LastPrioBefore}->{PerfTelemetry.LastPrioAfter}, other {PerfTelemetry.LastOtherBefore}->{PerfTelemetry.LastOtherAfter} (dormant={_lastDormantSkipped}, emptySkip={_lastEmptySkipped}) | hits: Dormant={_passHitsDormant}, Acad={_passHitsAcad}, Need={_passHitsNeed}, Stars={_passHitsStars}, Val={_passHitsValue}, Wage={_passHitsWage}, Ratio={_passHitsClubRatio}, Err={_passHitsStarsError}");
                    }
                }

                ClearAllCaches(-1);
                _teamIsAcademyCache.Clear();
                _dormantPtrCache.Clear();
                _lockedTeamPtrCache.Clear();
                _lockedUserPtrCache.Clear();
            }
            catch
            {
                _isMainAiTransfersPass = false;
                _activePassDepth = 0;
                Volatile.Write(ref _aiTransfersThreadId, 0);
            }
        }

        private static void RoleAwarePick_Postfix(DataTeam __0, DataPlayer __result)
        {
            if (_isMainAiTransfersPass && IsActiveOnCurrentThread() && __0 != null && __result != null)
            {
                _teamsWithCandidatesThisPass.Add(__0.Pointer);
            }
        }

        private static void UpdateEmptyScanCooldowns()
        {
            int skipDays = Math.Clamp(Plugin.CfgEmptyScanSkipDays.Value, 0, 14);
            if (skipDays <= 0 || _scannedOtherThisPass.Count == 0)
            {
                _scannedOtherThisPass.Clear();
                _teamsWithCandidatesThisPass.Clear();
                return;
            }

            try
            {
                var d = GlobalValues.CurrentDate;
                int dayKey = d.Year * 1000 + d.DayOfYear;
                int monthKey = d.Year * 12 + d.Month;

                for (int i = 0; i < _scannedOtherThisPass.Count; i++)
                {
                    var (ptr, squadCount, budget) = _scannedOtherThisPass[i];
                    if (_teamsWithCandidatesThisPass.Contains(ptr))
                    {
                        // Team had eligible candidates -> never put on cooldown, re-check immediately tomorrow
                        _emptyOtherCooldowns.Remove(ptr);
                    }
                    else
                    {
                        // Team scanned all ~6,000 players and found ZERO eligible candidates.
                        // Stagger cooldown (1..skipDays on first encounter, skipDays afterwards) to keep daily frame times flat!
                        bool existed = _emptyOtherCooldowns.ContainsKey(ptr);
                        int stagger = existed
                            ? skipDays
                            : 1 + (int)((uint)ptr.GetHashCode() % (uint)skipDays);
                        _emptyOtherCooldowns[ptr] = new EmptyScanEntry(dayKey + stagger, monthKey, squadCount, budget);
                    }
                }
            }
            catch { }
            finally
            {
                _scannedOtherThisPass.Clear();
                _teamsWithCandidatesThisPass.Clear();
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

        private static bool SquadNeeds_NeedOf_Prefix(DataTeam __0, ref object __result, out bool __state)
        {
            __state = false;
            if (!IsActiveOnCurrentThread() || __0 == null)
                return true;

            EnsureEpochFresh();
            if (_needOfCache.TryGetValue(__0.Pointer, out object cached))
            {
                _passHitsNeed++;
                __result = cached;
                return false;
            }

            __state = true;
            return true;
        }

        private static void SquadNeeds_NeedOf_Postfix(DataTeam __0, object __result, bool __state)
        {
            if (__state && IsActiveOnCurrentThread() && __0 != null && __result != null && Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                _needOfCache[__0.Pointer] = __result;
        }

        private static bool SquadNeeds_Stars_Prefix(DataPlayer __0, ref float __result, out bool __state)
        {
            __state = false;
            if (!IsActiveOnCurrentThread() || __0 == null)
                return true;

            EnsureEpochFresh();
            if (_starsCache.TryGetValue(__0.Pointer, out float cached))
            {
                _passHitsStars++;
                __result = cached;
                return false;
            }

            __state = true;
            return true;
        }

        private static void SquadNeeds_Stars_Postfix(DataPlayer __0, float __result, bool __state)
        {
            if (__state && IsActiveOnCurrentThread() && __0 != null && Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                _starsCache[__0.Pointer] = __result;
        }

        // Exact match to EMSkinCore.AiIdentity.AcademyGate(DataUser __0, DataTeam __1, ref bool __result) -> bool:
        // When !__1.IsAcademy, AcademyGate returns true (continue to vanilla) without touching __result!
        // Caching team.IsAcademy per IntPtr skips the IL2CPP property icall for ~85-90% of teams.
        private static bool AiIdentity_AcademyGate_Prefix(DataUser __0, DataTeam __1, ref bool __result)
        {
            if (!IsActiveOnCurrentThread() || __0 == null || __1 == null)
                return true;

            IntPtr teamPtr = __1.Pointer;
            if (!_teamIsAcademyCache.TryGetValue(teamPtr, out byte isAcad))
            {
                try { isAcad = __1.IsAcademy ? (byte)1 : (byte)0; }
                catch { isAcad = 1; }
                _teamIsAcademyCache[teamPtr] = isAcad;
            }

            if (isAcad == 0)
            {
                _passHitsAcad++;
                __result = true; // return true from AcademyGate Prefix => let vanilla proceed unchanged
                return false;
            }

            return true;
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

        // ==================== Bucket Pruning, Empty-Scan Cooldown & Pool Telemetry ====================

        private static void BuildAIOptimisationBuckets_Postfix()
        {
            PruneBucketsInternal(applyEmptyCooldown: false);
        }

        private static void BuildAITransfersBuckets_Postfix()
        {
            PruneBucketsInternal(applyEmptyCooldown: true);
        }

        private static void PruneBucketsInternal(bool applyEmptyCooldown)
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

                bool checkLocked = isLockedActive != null && isLockedClub != null && isLockedActive();
                TeamPredicate lockedPred = checkLocked ? isLockedClub : null;

                int prunedPrio = (isDormant != null || lockedPred != null)
                    ? PruneDormantLocked(prio, isDormant, lockedPred)
                    : 0;
                int prunedOtherDormant = (isDormant != null || lockedPred != null)
                    ? PruneDormantLocked(other, isDormant, lockedPred)
                    : 0;

                int prunedOtherEmpty = 0;
                if (applyEmptyCooldown && _isMainAiTransfersPass && other != null && other.Count > 0)
                {
                    prunedOtherEmpty = ApplyEmptyScanCooldown(other);
                }

                _lastDormantSkipped = prunedPrio + prunedOtherDormant;
                _lastEmptySkipped = prunedOtherEmpty;

                PerfTelemetry.RecordBucketPrune(
                    prioBefore, prioBefore - prunedPrio,
                    otherBefore, otherBefore - prunedOtherDormant - prunedOtherEmpty,
                    prunedPrio + prunedOtherDormant,
                    prunedOtherEmpty);
            }
            catch { }
        }

        private static int PruneDormantLocked(
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

        private static int ApplyEmptyScanCooldown(Il2CppSystem.Collections.Generic.List<DataTeam> other)
        {
            int skipDays = Math.Clamp(Plugin.CfgEmptyScanSkipDays.Value, 0, 14);
            if (skipDays <= 0)
                return 0;

            string slot = "";
            int dayKey = 0;
            int monthKey = 0;
            int worldPlayers = 0;
            string myTeamId = "";

            try
            {
                slot = SlotController.CurrentSlot ?? "";
                var d = GlobalValues.CurrentDate;
                dayKey = d.Year * 1000 + d.DayOfYear;
                monthKey = d.Year * 12 + d.Month;
                myTeamId = GlobalValues.TeamId ?? "";

                var allPlayers = DataPlayers.List?.TryCast<Il2CppSystem.Collections.Generic.List<DataPlayer>>();
                worldPlayers = allPlayers != null ? allPlayers.Count : 0;
            }
            catch
            {
                return 0;
            }

            // Reset cooldown table if slot changed or total world player count changed (e.g., regen/retirement wave)
            if (!string.Equals(slot, _emptyCooldownSlot, StringComparison.Ordinal)
                || (_emptyCooldownWorldPlayers > 0 && Math.Abs(worldPlayers - _emptyCooldownWorldPlayers) >= 20))
            {
                _emptyOtherCooldowns.Clear();
                _emptyCooldownSlot = slot;
                _emptyCooldownWorldPlayers = worldPlayers;
            }
            else if (_emptyCooldownWorldPlayers <= 0)
            {
                _emptyCooldownWorldPlayers = worldPlayers;
            }

            int skipped = 0;
            _scannedOtherThisPass.Clear();

            for (int i = other.Count - 1; i >= 0; i--)
            {
                var team = other[i];
                if (team == null) continue;

                IntPtr ptr = team.Pointer;
                int squadCount = 0;
                int budget = 0;

                try
                {
                    if (!string.IsNullOrEmpty(myTeamId) && string.Equals(team.Id, myTeamId, StringComparison.Ordinal))
                        continue;

                    var players = team.GetAllPlayers();
                    squadCount = players != null ? players.Count : 0;
                    budget = team.TransferBudget;
                }
                catch
                {
                    continue;
                }

                // Note: _scratchOtherTeams consists mostly of lower-tier clubs whose national bloc has 0
                // viable picks; _scratchPriorityTeams is always exempt (never passed here).
                // Allow a small $25k daily sponsor/ticket trickle without breaking the cooldown, while any
                // real transfer sale / prize / monthly budget jump or roster shrink wakes the club immediately!
                if (_emptyOtherCooldowns.TryGetValue(ptr, out var entry)
                    && dayKey < entry.NextScanDayKey
                    && monthKey == entry.MonthKey
                    && squadCount >= entry.SquadCount
                    && budget <= entry.Budget + 25_000)
                {
                    other.RemoveAt(i);
                    skipped++;
                }
                else
                {
                    _scannedOtherThisPass.Add((ptr, squadCount, budget));
                }
            }

            return skipped;
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
