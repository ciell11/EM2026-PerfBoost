using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Eliminates massive wasted O(DormantTeams * AllPlayers) IL2CPP->Harmony work inside
    /// AiAcquisitionPasses.AITransfers (which otherwise spends ~6,300 ms EVERY SINGLE DAY scanning
    /// all ~5,000 players for 50+ dormant/folded/locked clubs that iamgri mod unconditionally blocks!).
    ///
    /// 100% identical AI transfer behavior: only prunes teams from _scratchPriorityTeams and
    /// _scratchOtherTeams that EMSkinCore's Dormant.Is(team) or LockedClub.IsClub(team) already
    /// reject 100% of candidate players for.
    /// </summary>
    internal static class FastAiTransfers
    {
        private delegate bool TeamPredicate(DataTeam team);
        private delegate bool BoolGetter();

        private static TeamPredicate _isDormant;
        private static BoolGetter _isLockedActive;
        private static TeamPredicate _isLockedClub;

        public static void InstallGamePatches(Harmony harmony)
        {
            try
            {
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
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[FastAiTransfers] Game patch warning: {ex.Message}");
            }
        }

        public static void AttachToCore(Assembly coreAsm)
        {
            _isDormant = null;
            _isLockedActive = null;
            _isLockedClub = null;

            try
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                var dormantType = coreAsm.GetType("Dormant", false);
                var isDormantMethod = dormantType?.GetMethod("Is", flags, null, new[] { typeof(DataTeam) }, null);
                if (isDormantMethod != null)
                    _isDormant = (TeamPredicate)Delegate.CreateDelegate(typeof(TeamPredicate), isDormantMethod);

                var lockedType = coreAsm.GetType("LockedClub", false);
                var activeGetter = lockedType?.GetMethod("get_Active", flags, null, Type.EmptyTypes, null);
                var isClubMethod = lockedType?.GetMethod("IsClub", flags, null, new[] { typeof(DataTeam) }, null);
                if (activeGetter != null && isClubMethod != null)
                {
                    _isLockedActive = (BoolGetter)Delegate.CreateDelegate(typeof(BoolGetter), activeGetter);
                    _isLockedClub = (TeamPredicate)Delegate.CreateDelegate(typeof(TeamPredicate), isClubMethod);
                }

                Plugin.Logger.LogMessage($"[FastAiTransfers] Bound to EMSkinCore (Dormant={_isDormant != null}, LockedClub={_isLockedClub != null}).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[FastAiTransfers] Core bind warning: {ex.Message}");
            }
        }

        private static void BuildAITransfersBuckets_Postfix()
        {
            try
            {
                if (!Plugin.CfgFastAiTransfers.Value)
                    return;

                var isDormant = _isDormant;
                var isLockedActive = _isLockedActive;
                var isLockedClub = _isLockedClub;

                if (isDormant == null && isLockedClub == null)
                    return;

                bool checkLocked = isLockedActive != null && isLockedClub != null && isLockedActive();

                var prio = AiAcquisitionPasses._scratchPriorityTeams;
                var other = AiAcquisitionPasses._scratchOtherTeams;

                int prioBefore = prio != null ? prio.Count : 0;
                int otherBefore = other != null ? other.Count : 0;

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

            int pruned = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var team = list[i];
                if (team == null) continue;

                bool blocked = false;
                if (isDormant != null && isDormant(team))
                    blocked = true;
                else if (isLockedClub != null && isLockedClub(team))
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
