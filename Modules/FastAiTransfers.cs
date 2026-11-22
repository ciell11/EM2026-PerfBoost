using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Accelerates AiAcquisitionPasses.AITransfers (the #1 bottleneck in daily simulation, ~6,100+ ms/day)
    /// with 100% identical gameplay and AI transfer decisions:
    ///
    /// 1. Prunes dormant/folded and locked clubs from _scratchPriorityTeams and _scratchOtherTeams
    ///    before the O(teams * players) candidate scan.
    /// 2. Installs lightweight native INativeDetour per-pass memoization wrappers (ONLY active on the
    ///    exact thread currently running AiAcquisitionPasses.AITransfers, and instantly invalidated on
    ///    any offer/deal/pricelist mutation) for pure hot lookups that otherwise cross IL2CPP->Harmony
    ///    trampolines hundreds of thousands of times per day:
    ///      - SellingTeamWillingnessCalculator.GetMinAskingPrice (7 stacked EMSkinCore Harmony detours!)
    ///      - DataPlayer.GetTransferCost (PlayerMoney.ValuePre)
    ///      - DataUser.ComputeWage (PlayerMoney.WagePre)
    ///      - AiMarketRules.HasActiveNegotiations (2 no-op renewal Harmony detours)
    ///      - DataOffers.IsInPriceList (no-op listing Harmony detour, CallerCount=40)
    ///      - AiCandidateSelection.IsProtectedStar (AiIdentity.Star)
    ///      - AiMarketRules.MajorSquadFreezeBlocksDeparture / PostSuccessRosterFrozen / PostSuccessBlocksArrival / SaleWouldLeaveSellerBelowFloor
    ///      - AiMarketRules.AiAcademyAgeCompatible (15ns fast-path for confirmed non-academy teams)
    /// 3. Pointer-caches EMSkinCore's Dormant.Is(DataTeam) and non-locked negative checks in
    ///    LockedClub.Blocks(DataUser, DataTeam) during AITransfers to avoid millions of redundant
    ///    team.Id / user.TeamId IL2CPP UTF-16 string allocations.
    /// </summary>
    internal static class FastAiTransfers
    {
        private delegate bool TeamPredicate(DataTeam team);
        private delegate bool BoolGetter();

        // Native IL2CPP x64 Cdecl delegates (must stay rooted in static fields while detours are active!)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AskPriceD(IntPtr player, IntPtr sellerTeam, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TransferCostD(IntPtr thisPlayer, IntPtr sellerTeam, byte rebalanced, byte ignoreContractLen, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Wage0D(IntPtr thisUser, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Wage2D(IntPtr thisUser, IntPtr buyerTeamId, byte ignoreBench, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte Bool1D(IntPtr arg0, IntPtr methodInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte Bool2D(IntPtr arg0, IntPtr arg1, IntPtr methodInfo);

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

        private readonly struct CostKey : IEquatable<CostKey>
        {
            public readonly IntPtr Player;
            public readonly IntPtr Seller;
            public readonly int Flags;

            public CostKey(IntPtr player, IntPtr seller, byte rebalanced, byte ignoreContractLen)
            {
                Player = player;
                Seller = seller;
                Flags = (rebalanced != 0 ? 1 : 0) | (ignoreContractLen != 0 ? 2 : 0);
            }

            public bool Equals(CostKey other) => Player == other.Player && Seller == other.Seller && Flags == other.Flags;
            public override bool Equals(object obj) => obj is CostKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Player, Seller, Flags);
        }

        // Core delegates (Dormant / LockedClub / PlayerMoney)
        private static TeamPredicate _isDormant;
        private static BoolGetter _isLockedActive;
        private static TeamPredicate _isLockedClub;
        private static FieldInfo _fLockedClubId;
        private static bool _hasPlayerMoneyWagePre;

        // Active pass guard (single-thread ownership during AITransfers => zero lock overhead on hot path!)
        private static int _aiTransfersThreadId;
        private static long _mutationEpoch = 1;
        private static long _cachedEpoch = -1;
        private static long _passStartTicks;
        private static int _passCount;

        // Per-pass caches (ONLY touched by the thread matching _aiTransfersThreadId, cleared on exit!)
        private static readonly Dictionary<PtrPair, int> _askCache = new(4096);
        private static readonly Dictionary<CostKey, int> _costCache = new(4096);
        private static readonly Dictionary<IntPtr, int> _wage0Cache = new(4096);
        private static readonly Dictionary<IntPtr, int> _wage2Cache = new(4096);
        private static readonly Dictionary<IntPtr, byte> _hasNegCache = new(4096);
        private static readonly Dictionary<IntPtr, byte> _inPriceListCache = new(4096);
        private static readonly Dictionary<PtrPair, byte> _protectedStarCache = new(2048);
        private static readonly Dictionary<PtrPair, byte> _majorFreezeCache = new(512);
        private static readonly Dictionary<IntPtr, byte> _postSuccessFrozenCache = new(512);
        private static readonly Dictionary<IntPtr, byte> _postSuccessArrivalCache = new(512);
        private static readonly Dictionary<IntPtr, byte> _sellerFloorCache = new(512);
        private static readonly Dictionary<IntPtr, byte> _teamIsAcademyCache = new(512);

        // Pointer caches for EMSkinCore.Dormant.Is & LockedClub.Blocks during AITransfers
        private static readonly Dictionary<IntPtr, bool> _dormantPtrCache = new(512);
        private static readonly Dictionary<IntPtr, byte> _lockedTeamPtrCache = new(512);
        private static readonly Dictionary<IntPtr, byte> _lockedUserPtrCache = new(4096);
        private static string _passLockedClubId;

        // Native detours + rooted delegates + trampolines
        private static readonly List<INativeDetour> _detours = new();
        private static bool _nativeDetoursInstalled;

        private static AskPriceD _myAsk, _origAsk;
        private static TransferCostD _myCost, _origCost;
        private static Wage0D _myWage0, _origWage0;
        private static Wage2D _myWage2, _origWage2;
        private static Bool1D _myHasNeg, _origHasNeg;
        private static Bool1D _myInPriceList, _origInPriceList;
        private static Bool2D _myProtectedStar, _origProtectedStar;
        private static Bool2D _myMajorFreeze, _origMajorFreeze;
        private static Bool1D _myPostSuccessFrozen, _origPostSuccessFrozen;
        private static Bool1D _myPostSuccessArrival, _origPostSuccessArrival;
        private static Bool1D _mySellerFloor, _origSellerFloor;
        private static Bool2D _myAcademyAge, _origAcademyAge;

        // Lightweight pass stats for diagnostics
        private static long _passHitsAsk, _passHitsCost, _passHitsWage, _passHitsNeg, _passHitsPriceList, _passHitsStar, _passHitsFreeze, _passHitsAcad, _passHitsCorePtr;

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

                // If iamgri mod is not installed, install native detours immediately on game methods.
                // If iamgri mod IS installed, AttachToCore() installs them AFTER EMSkinCore finishes patching.
                if (CoreBridge.ActiveCore == null)
                {
                    // Delay until either AttachToCore runs or first AITransfers pass if standalone
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
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(FastAiTransfers), nameof(OnWorldMutated)));
                }
            }
            catch { }
        }

        public static void AttachToCore(Harmony coreHarmony, Assembly coreAsm)
        {
            DetachNativeDetours();

            _isDormant = null;
            _isLockedActive = null;
            _isLockedClub = null;
            _fLockedClubId = null;
            _hasPlayerMoneyWagePre = false;

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
                        prefix: new HarmonyMethod(typeof(FastAiTransfers), nameof(Dormant_Is_Prefix))
                        {
                            priority = Priority.First
                        },
                        postfix: new HarmonyMethod(typeof(FastAiTransfers), nameof(Dormant_Is_Postfix)));
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
                        prefix: new HarmonyMethod(typeof(FastAiTransfers), nameof(LockedClub_Blocks_Prefix))
                        {
                            priority = Priority.First
                        });
                }

                var pmType = coreAsm.GetType("PlayerMoney", false);
                _hasPlayerMoneyWagePre = pmType?.GetMethod("WagePre", flags) != null;

                // Install native per-pass memoization detours AFTER EMSkinCore has finished its patches
                // so our detours wrap the entire Harmony chain cleanly (cache HIT = 15ns, MISS = full Harmony chain!).
                int nativeHooked = InstallNativeDetours();

                Plugin.Logger.LogMessage(
                    $"[FastAiTransfers] Bound to EMSkinCore (Dormant={_isDormant != null}, LockedClub={_isLockedClub != null}, NativeMemoDetours={nativeHooked}/12).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[FastAiTransfers] Core bind warning: {ex.Message}");
            }
        }

        public static void Detach()
        {
            Volatile.Write(ref _aiTransfersThreadId, 0);
            DetachNativeDetours();
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
            }
        }

        private static void DetachNativeDetours()
        {
            for (int i = _detours.Count - 1; i >= 0; i--)
            {
                try { _detours[i]?.Dispose(); } catch { }
            }
            _detours.Clear();
            _nativeDetoursInstalled = false;

            _origAsk = null;
            _origCost = null;
            _origWage0 = null;
            _origWage2 = null;
            _origHasNeg = null;
            _origInPriceList = null;
            _origProtectedStar = null;
            _origMajorFreeze = null;
            _origPostSuccessFrozen = null;
            _origPostSuccessArrival = null;
            _origSellerFloor = null;
            _origAcademyAge = null;
        }

        private static int InstallNativeDetours()
        {
            if (_nativeDetoursInstalled || !Plugin.CfgFastAiTransfers.Value)
                return _detours.Count;

            int count = 0;

            _myAsk = Detour_GetMinAskingPrice;
            if (TryHook(typeof(SellingTeamWillingnessCalculator), "NativeMethodInfoPtr_GetMinAskingPrice_Public_Static_Int32_DataPlayer_DataTeam_0", _myAsk, out _origAsk))
                count++;

            _myCost = Detour_GetTransferCost;
            if (TryHook(typeof(DataPlayer), "NativeMethodInfoPtr_GetTransferCost_Internal_Int32_DataTeam_Boolean_Boolean_0", _myCost, out _origCost))
                count++;

            _myWage0 = Detour_ComputeWage0;
            if (TryHook(typeof(DataUser), "NativeMethodInfoPtr_ComputeWage_Internal_Int32_0", _myWage0, out _origWage0))
                count++;

            _myWage2 = Detour_ComputeWage2;
            if (TryHook(typeof(DataUser), "NativeMethodInfoPtr_ComputeWage_Internal_Int32_String_Boolean_0", _myWage2, out _origWage2))
                count++;

            _myHasNeg = Detour_HasActiveNegotiations;
            if (TryHook(typeof(AiMarketRules), "NativeMethodInfoPtr_HasActiveNegotiations_Internal_Static_Boolean_DataUser_0", _myHasNeg, out _origHasNeg))
                count++;

            _myInPriceList = Detour_IsInPriceList;
            if (TryHook(typeof(DataOffers), "NativeMethodInfoPtr_IsInPriceList_Public_Static_Boolean_DataUser_0", _myInPriceList, out _origInPriceList))
                count++;

            _myProtectedStar = Detour_IsProtectedStar;
            if (TryHook(typeof(AiCandidateSelection), "NativeMethodInfoPtr_IsProtectedStar_Internal_Static_Boolean_DataPlayer_DataTeam_0", _myProtectedStar, out _origProtectedStar))
                count++;

            _myMajorFreeze = Detour_MajorSquadFreeze;
            if (TryHook(typeof(AiMarketRules), "NativeMethodInfoPtr_MajorSquadFreezeBlocksDeparture_Internal_Static_Boolean_DataTeam_ContractOffer_0", _myMajorFreeze, out _origMajorFreeze))
                count++;

            _myPostSuccessFrozen = Detour_PostSuccessRosterFrozen;
            if (TryHook(typeof(AiMarketRules), "NativeMethodInfoPtr_PostSuccessRosterFrozen_Internal_Static_Boolean_DataTeam_0", _myPostSuccessFrozen, out _origPostSuccessFrozen))
                count++;

            _myPostSuccessArrival = Detour_PostSuccessBlocksArrival;
            if (TryHook(typeof(AiMarketRules), "NativeMethodInfoPtr_PostSuccessBlocksArrival_Internal_Static_Boolean_DataTeam_0", _myPostSuccessArrival, out _origPostSuccessArrival))
                count++;

            _mySellerFloor = Detour_SaleWouldLeaveSellerBelowFloor;
            if (TryHook(typeof(AiMarketRules), "NativeMethodInfoPtr_SaleWouldLeaveSellerBelowFloor_Internal_Static_Boolean_DataTeam_0", _mySellerFloor, out _origSellerFloor))
                count++;

            _myAcademyAge = Detour_AiAcademyAgeCompatible;
            if (TryHook(typeof(AiMarketRules), "NativeMethodInfoPtr_AiAcademyAgeCompatible_Internal_Static_Boolean_DataUser_DataTeam_0", _myAcademyAge, out _origAcademyAge))
                count++;

            _nativeDetoursInstalled = true;
            return count;
        }

        private static bool TryHook<TDelegate>(Type owner, string nativeFieldName, TDelegate detour, out TDelegate orig)
            where TDelegate : Delegate
        {
            orig = null;
            try
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var field = owner.GetField(nativeFieldName, flags);
                if (field == null)
                    return false;

                var methodInfoPtr = (IntPtr)field.GetValue(null);
                if (methodInfoPtr == IntPtr.Zero)
                    return false;

                var codePtr = Marshal.ReadIntPtr(methodInfoPtr);
                if (codePtr == IntPtr.Zero)
                    return false;

                var handle = INativeDetour.CreateAndApply(codePtr, detour, out orig);
                if (handle != null && orig != null)
                {
                    _detours.Add(handle);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[FastAiTransfers] Native detour skipped ({owner.Name}.{nativeFieldName}): {ex.Message}");
            }
            return false;
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
            {
                ClearAllCaches(epoch);
            }
        }

        private static void ClearAllCaches(long newEpoch)
        {
            _askCache.Clear();
            _costCache.Clear();
            _wage0Cache.Clear();
            _wage2Cache.Clear();
            _hasNegCache.Clear();
            _inPriceListCache.Clear();
            _protectedStarCache.Clear();
            _majorFreezeCache.Clear();
            _postSuccessFrozenCache.Clear();
            _postSuccessArrivalCache.Clear();
            _sellerFloorCache.Clear();
            _lockedUserPtrCache.Clear();
            _cachedEpoch = newEpoch;
        }

        private static void OnWorldMutated()
        {
            Interlocked.Increment(ref _mutationEpoch);
        }

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

                // Standalone mode fallback (if iamgri mod is not installed)
                if (!_nativeDetoursInstalled && CoreBridge.ActiveCore == null)
                {
                    int n = InstallNativeDetours();
                    if (n > 0)
                        Plugin.Logger.LogMessage($"[FastAiTransfers] Standalone native memoization active ({n}/12 detours).");
                }

                long epoch = Interlocked.Increment(ref _mutationEpoch);
                ClearAllCaches(epoch);
                _teamIsAcademyCache.Clear();
                _dormantPtrCache.Clear();
                _lockedTeamPtrCache.Clear();

                _passLockedClubId = null;
                if (_fLockedClubId != null)
                {
                    try { _passLockedClubId = _fLockedClubId.GetValue(null) as string; }
                    catch { _passLockedClubId = null; }
                }

                _passHitsAsk = _passHitsCost = _passHitsWage = _passHitsNeg = _passHitsPriceList = 0;
                _passHitsStar = _passHitsFreeze = _passHitsAcad = _passHitsCorePtr = 0;
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

                long totalHits = _passHitsAsk + _passHitsCost + _passHitsWage + _passHitsNeg
                    + _passHitsPriceList + _passHitsStar + _passHitsFreeze + _passHitsAcad + _passHitsCorePtr;

                if (totalHits > 0)
                    Interlocked.Add(ref PerfTelemetry.AiMemoHitsTotal, totalHits);

                int passNum = Interlocked.Increment(ref _passCount);
                if (passNum == 1 || passNum % 25 == 0)
                {
                    Plugin.Logger.LogMessage(
                        $"[FastAiTransfers] Pass #{passNum}: {elapsedMs}ms | prio {PerfTelemetry.LastPrioBefore}->{PerfTelemetry.LastPrioAfter}, other {PerfTelemetry.LastOtherBefore}->{PerfTelemetry.LastOtherAfter} | hits: Ask={_passHitsAsk}, Cost={_passHitsCost}, Wage={_passHitsWage}, Neg={_passHitsNeg}, List={_passHitsPriceList}, Star={_passHitsStar}, Freeze={_passHitsFreeze}, Acad={_passHitsAcad}, CorePtr={_passHitsCorePtr}");
                }

                ClearAllCaches(-1);
                _teamIsAcademyCache.Clear();
                _dormantPtrCache.Clear();
                _lockedTeamPtrCache.Clear();
                _lockedUserPtrCache.Clear();
            }
            catch
            {
                Volatile.Write(ref _aiTransfersThreadId, 0);
            }
        }

        // ==================== Native Per-Pass Memoization Detours ====================

        private static int Detour_GetMinAskingPrice(IntPtr player, IntPtr sellerTeam, IntPtr methodInfo)
        {
            if (IsActiveOnCurrentThread() && player != IntPtr.Zero)
            {
                EnsureEpochFresh();
                var key = new PtrPair(player, sellerTeam);
                if (_askCache.TryGetValue(key, out int cached))
                {
                    _passHitsAsk++;
                    return cached;
                }

                int val = _origAsk(player, sellerTeam, methodInfo);
                if (Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                    _askCache[key] = val;
                return val;
            }
            return _origAsk(player, sellerTeam, methodInfo);
        }

        private static int Detour_GetTransferCost(IntPtr thisPlayer, IntPtr sellerTeam, byte rebalanced, byte ignoreContractLen, IntPtr methodInfo)
        {
            if (IsActiveOnCurrentThread() && thisPlayer != IntPtr.Zero)
            {
                EnsureEpochFresh();
                var key = new CostKey(thisPlayer, sellerTeam, rebalanced, ignoreContractLen);
                if (_costCache.TryGetValue(key, out int cached))
                {
                    _passHitsCost++;
                    return cached;
                }

                int val = _origCost(thisPlayer, sellerTeam, rebalanced, ignoreContractLen, methodInfo);
                if (Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                    _costCache[key] = val;
                return val;
            }
            return _origCost(thisPlayer, sellerTeam, rebalanced, ignoreContractLen, methodInfo);
        }

        private static int Detour_ComputeWage0(IntPtr thisUser, IntPtr methodInfo)
        {
            if (IsActiveOnCurrentThread() && thisUser != IntPtr.Zero)
            {
                EnsureEpochFresh();
                if (_wage0Cache.TryGetValue(thisUser, out int cached))
                {
                    _passHitsWage++;
                    return cached;
                }

                int val = _origWage0(thisUser, methodInfo);
                if (Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                    _wage0Cache[thisUser] = val;
                return val;
            }
            return _origWage0(thisUser, methodInfo);
        }

        private static int Detour_ComputeWage2(IntPtr thisUser, IntPtr buyerTeamId, byte ignoreBench, IntPtr methodInfo)
        {
            // When EMSkinCore.PlayerMoney.WagePre is active, DataPlayer wage depends ONLY on thisUser
            // (buyerTeamId & ignoreBench are ignored). Cache strictly for verified DataPlayer pointers!
            if (_hasPlayerMoneyWagePre && IsActiveOnCurrentThread() && thisUser != IntPtr.Zero)
            {
                EnsureEpochFresh();
                if (_wage2Cache.TryGetValue(thisUser, out int cached))
                {
                    _passHitsWage++;
                    return cached;
                }

                int val = _origWage2(thisUser, buyerTeamId, ignoreBench, methodInfo);
                if (Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                {
                    try
                    {
                        if (new DataUser(thisUser).TryCast<DataPlayer>() != null)
                            _wage2Cache[thisUser] = val;
                    }
                    catch { }
                }
                return val;
            }
            return _origWage2(thisUser, buyerTeamId, ignoreBench, methodInfo);
        }

        private static byte Detour_HasActiveNegotiations(IntPtr user, IntPtr methodInfo)
        {
            if (IsActiveOnCurrentThread() && user != IntPtr.Zero)
            {
                EnsureEpochFresh();
                if (_hasNegCache.TryGetValue(user, out byte cached))
                {
                    _passHitsNeg++;
                    return cached;
                }

                byte val = _origHasNeg(user, methodInfo);
                if (Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                    _hasNegCache[user] = val;
                return val;
            }
            return _origHasNeg(user, methodInfo);
        }

        private static byte Detour_IsInPriceList(IntPtr user, IntPtr methodInfo)
        {
            if (IsActiveOnCurrentThread() && user != IntPtr.Zero)
            {
                EnsureEpochFresh();
                if (_inPriceListCache.TryGetValue(user, out byte cached))
                {
                    _passHitsPriceList++;
                    return cached;
                }

                byte val = _origInPriceList(user, methodInfo);
                if (Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                    _inPriceListCache[user] = val;
                return val;
            }
            return _origInPriceList(user, methodInfo);
        }

        private static byte Detour_IsProtectedStar(IntPtr player, IntPtr sellerTeam, IntPtr methodInfo)
        {
            if (IsActiveOnCurrentThread() && player != IntPtr.Zero)
            {
                EnsureEpochFresh();
                var key = new PtrPair(player, sellerTeam);
                if (_protectedStarCache.TryGetValue(key, out byte cached))
                {
                    _passHitsStar++;
                    return cached;
                }

                byte val = _origProtectedStar(player, sellerTeam, methodInfo);
                if (Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                    _protectedStarCache[key] = val;
                return val;
            }
            return _origProtectedStar(player, sellerTeam, methodInfo);
        }

        private static byte Detour_MajorSquadFreeze(IntPtr team, IntPtr excludeOffer, IntPtr methodInfo)
        {
            if (IsActiveOnCurrentThread() && team != IntPtr.Zero)
            {
                EnsureEpochFresh();
                var key = new PtrPair(team, excludeOffer);
                if (_majorFreezeCache.TryGetValue(key, out byte cached))
                {
                    _passHitsFreeze++;
                    return cached;
                }

                byte val = _origMajorFreeze(team, excludeOffer, methodInfo);
                if (Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                    _majorFreezeCache[key] = val;
                return val;
            }
            return _origMajorFreeze(team, excludeOffer, methodInfo);
        }

        private static byte Detour_PostSuccessRosterFrozen(IntPtr team, IntPtr methodInfo)
        {
            if (IsActiveOnCurrentThread() && team != IntPtr.Zero)
            {
                EnsureEpochFresh();
                if (_postSuccessFrozenCache.TryGetValue(team, out byte cached))
                {
                    _passHitsFreeze++;
                    return cached;
                }

                byte val = _origPostSuccessFrozen(team, methodInfo);
                if (Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                    _postSuccessFrozenCache[team] = val;
                return val;
            }
            return _origPostSuccessFrozen(team, methodInfo);
        }

        private static byte Detour_PostSuccessBlocksArrival(IntPtr team, IntPtr methodInfo)
        {
            if (IsActiveOnCurrentThread() && team != IntPtr.Zero)
            {
                EnsureEpochFresh();
                if (_postSuccessArrivalCache.TryGetValue(team, out byte cached))
                {
                    _passHitsFreeze++;
                    return cached;
                }

                byte val = _origPostSuccessArrival(team, methodInfo);
                if (Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                    _postSuccessArrivalCache[team] = val;
                return val;
            }
            return _origPostSuccessArrival(team, methodInfo);
        }

        private static byte Detour_SaleWouldLeaveSellerBelowFloor(IntPtr team, IntPtr methodInfo)
        {
            if (IsActiveOnCurrentThread() && team != IntPtr.Zero)
            {
                EnsureEpochFresh();
                if (_sellerFloorCache.TryGetValue(team, out byte cached))
                {
                    _passHitsFreeze++;
                    return cached;
                }

                byte val = _origSellerFloor(team, methodInfo);
                if (Volatile.Read(ref _mutationEpoch) == _cachedEpoch)
                    _sellerFloorCache[team] = val;
                return val;
            }
            return _origSellerFloor(team, methodInfo);
        }

        private static byte Detour_AiAcademyAgeCompatible(IntPtr user, IntPtr team, IntPtr methodInfo)
        {
            if (IsActiveOnCurrentThread() && user != IntPtr.Zero && team != IntPtr.Zero)
            {
                if (_teamIsAcademyCache.TryGetValue(team, out byte isAcad))
                {
                    if (isAcad == 0)
                    {
                        _passHitsAcad++;
                        return 1; // Non-academy clubs have no academy age restriction
                    }
                    return _origAcademyAge(user, team, methodInfo);
                }

                byte flag = 1;
                try
                {
                    if (!new DataTeam(team).IsAcademy)
                        flag = 0;
                }
                catch { flag = 1; }

                _teamIsAcademyCache[team] = flag;
                if (flag == 0)
                {
                    // Confirm with original once? No: both vanilla & AiIdentity.AcademyGate return true for !IsAcademy,
                    // but verify on first sight just in case of future game changes:
                    byte first = _origAcademyAge(user, team, methodInfo);
                    if (first == 0)
                        _teamIsAcademyCache[team] = 1; // Unexpected override: never fast-path this team
                    return first;
                }
            }
            return _origAcademyAge(user, team, methodInfo);
        }

        // ==================== EMSkinCore Pointer Caches During AITransfers ====================

        private static bool Dormant_Is_Prefix(DataTeam __0, ref bool __result)
        {
            if (!IsActiveOnCurrentThread())
                return true;

            if (__0 == null)
            {
                __result = false;
                return false;
            }

            IntPtr ptr = __0.Pointer;
            if (_dormantPtrCache.TryGetValue(ptr, out bool cached))
            {
                _passHitsCorePtr++;
                __result = cached;
                return false;
            }

            return true;
        }

        private static void Dormant_Is_Postfix(DataTeam __0, bool __result)
        {
            if (IsActiveOnCurrentThread() && __0 != null)
            {
                _dormantPtrCache[__0.Pointer] = __result;
            }
        }

        private static bool LockedClub_Blocks_Prefix(DataUser __0, DataTeam __1, ref bool __result)
        {
            if (!IsActiveOnCurrentThread())
                return true;

            string clubId = _passLockedClubId;
            if (string.IsNullOrEmpty(clubId) || __0 == null || __1 == null)
            {
                _passHitsCorePtr++;
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
                return true; // Locked club buyer -> run exact original logic

            IntPtr userPtr = __0.Pointer;
            if (!_lockedUserPtrCache.TryGetValue(userPtr, out byte userIsLocked))
            {
                try { userIsLocked = string.Equals(__0.TeamId, clubId, StringComparison.Ordinal) ? (byte)1 : (byte)0; }
                catch { userIsLocked = 1; }
                _lockedUserPtrCache[userPtr] = userIsLocked;
            }

            if (userIsLocked != 0)
                return true; // Player belongs to locked club -> run exact original logic

            // Neither buyer nor player belongs to the locked club => LockedClub.Blocks is guaranteed false!
            _passHitsCorePtr++;
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
