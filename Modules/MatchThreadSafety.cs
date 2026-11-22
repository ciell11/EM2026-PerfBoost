using System;
using System.Threading;
using HarmonyLib;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Fixes InvalidOperationException ("Collection was modified; enumeration operation may not execute")
    /// inside MatchesEngine.ProcessAIMatches when EMSkinCore.DayOffload runs BeforeNextDay on a background
    /// thread concurrently with IamgriThirdPlace.ThirdPlaceManager.SyncTodayMatches on the main thread.
    /// </summary>
    internal static class MatchThreadSafety
    {
        public static void Install(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(
                    typeof(MatchesEngine),
                    nameof(MatchesEngine.ProcessAIMatches),
                    new[] { typeof(Il2CppSystem.Collections.Generic.List<DataMatch>) });

                if (target == null)
                {
                    Plugin.Logger.LogWarning("[MatchThreadSafety] MatchesEngine.ProcessAIMatches not found.");
                    return;
                }

                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(typeof(MatchThreadSafety), nameof(ProcessAIMatches_Prefix))
                    {
                        priority = Priority.First
                    });

                Plugin.Logger.LogMessage("[MatchThreadSafety] Patched MatchesEngine.ProcessAIMatches with thread-safe list snapshot.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[MatchThreadSafety] Patch failed: {ex}");
            }
        }

        private static void ProcessAIMatches_Prefix(ref Il2CppSystem.Collections.Generic.List<DataMatch> __0)
        {
            var src = __0;
            if (src == null) return;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    int count = src.Count;
                    if (count <= 0) return;

                    var copy = new Il2CppSystem.Collections.Generic.List<DataMatch>(count);
                    for (int i = 0; i < count; i++)
                    {
                        // Guard against concurrent shrink
                        if (i >= src.Count) break;
                        var item = src[i];
                        if (item != null)
                            copy.Add(item);
                    }

                    __0 = copy;
                    Interlocked.Increment(ref PerfTelemetry.MatchSnapshotsTaken);
                    return;
                }
                catch
                {
                    Interlocked.Increment(ref PerfTelemetry.MatchSnapshotRetries);
                    Thread.SpinWait(64);
                }
            }
        }
    }
}
