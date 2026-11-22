using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Profiling;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Protects long multi-season careers (2028-2035+) from memory bloat, OOM crashes, and stale caches:
    /// 1. Monitors IL2CPP & .NET heap growth and performs proactive cleanup ONLY while idle on UI screens
    ///    (never during 3D matches, never during day simulation, never while saving).
    /// 2. Automatically flushes all PerfBoost module caches whenever the active career slot changes
    ///    so zero stale IL2CPP pointers can ever persist across career loads.
    /// </summary>
    internal static class LongCareerGuard
    {
        private static long _baselineHeapBytes;
        private static float _nextCheckTime;
        private static float _lastCleanupTime = -999f;
        private static float _lastScreenSwitchTime;
        private static string _lastKnownSlot = "";

        public static void Install(Harmony harmony)
        {
            try
            {
                var setSlot = AccessTools.PropertySetter(typeof(SlotController), nameof(SlotController.CurrentSlot));
                if (setSlot != null)
                {
                    harmony.Patch(setSlot, postfix: new HarmonyMethod(typeof(LongCareerGuard), nameof(OnSlotChanged_Postfix)));
                }

                _baselineHeapBytes = ReadCurrentHeapBytes();
                _nextCheckTime = Time.unscaledTime + 30f;

                Plugin.Logger.LogMessage(
                    $"[LongCareerGuard] Active (Enabled={Plugin.CfgLongCareerMemoryGuard.Value}, GrowthThreshold={Plugin.CfgMemoryGuardGrowthMb.Value} MB, Cooldown={Plugin.CfgMemoryGuardCooldownSec.Value}s).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[LongCareerGuard] Init warning: {ex.Message}");
            }
        }

        public static void NotifyScreenSwitched()
        {
            _lastScreenSwitchTime = Time.unscaledTime;
        }

        public static void OnSceneChanged(bool in3DMatch)
        {
            if (!in3DMatch)
            {
                // Refresh baseline after returning from a 3D match
                _baselineHeapBytes = ReadCurrentHeapBytes();
                _lastScreenSwitchTime = Time.unscaledTime;
            }
        }

        public static void Tick(bool in3DMatch)
        {
            try
            {
                // Detect slot changes even if set via native code bypassing managed setter
                string currentSlot = SafeCurrentSlot();
                if (!string.Equals(currentSlot, _lastKnownSlot, StringComparison.Ordinal))
                {
                    _lastKnownSlot = currentSlot;
                    FlushAllCachesOnSlotChange(currentSlot);
                }

                if (!Plugin.CfgLongCareerMemoryGuard.Value || in3DMatch)
                    return;

                float now = Time.unscaledTime;
                if (now < _nextCheckTime)
                    return;
                _nextCheckTime = now + 10f;

                // Never interrupt day simulation, active saves, or rapid screen switching
                if (now - _lastCleanupTime < Plugin.CfgMemoryGuardCooldownSec.Value)
                    return;
                if (now - _lastScreenSwitchTime < 2.5f)
                    return;
                if (IsDayBusy() || SmartAutosave.IsSavingNow)
                    return;

                long heapBytes = ReadCurrentHeapBytes();
                if (heapBytes <= 0)
                    return;

                if (_baselineHeapBytes <= 0 || heapBytes < _baselineHeapBytes)
                {
                    _baselineHeapBytes = heapBytes;
                    return;
                }

                long growthMb = (heapBytes - _baselineHeapBytes) / (1024L * 1024L);
                long totalMb = heapBytes / (1024L * 1024L);
                int thresholdMb = Math.Clamp(Plugin.CfgMemoryGuardGrowthMb.Value, 256, 4096);

                if (growthMb < thresholdMb)
                    return;

                // Perform gentle idle cleanup before heap balloons to 5-6 GB
                var sw = Stopwatch.StartNew();
                long beforeMb = totalMb;

                IL2CPP.il2cpp_gc_collect(1);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);

                long afterBytes = ReadCurrentHeapBytes();
                long afterMb = afterBytes / (1024L * 1024L);
                long freedMb = Math.Max(0, beforeMb - afterMb);

                // If native Unity allocations are also very high on a long career, trigger async asset unload
                long unityAllocMb = SafeTotalAllocatedMb();
                bool unloadedAssets = false;
                if (unityAllocMb >= Plugin.CfgUnloadAssetsThresholdMb.Value)
                {
                    Resources.UnloadUnusedAssets();
                    unloadedAssets = true;
                }

                sw.Stop();
                _baselineHeapBytes = afterBytes > 0 ? afterBytes : heapBytes;
                _lastCleanupTime = now;

                PerfTelemetry.RecordMemoryGuardCleanup(freedMb, sw.ElapsedMilliseconds, afterMb);
                Plugin.Logger.LogMessage(
                    $"[LongCareerGuard] Idle memory cleanup: heap {beforeMb} MB -> {afterMb} MB (freed {freedMb} MB in {sw.ElapsedMilliseconds} ms{(unloadedAssets ? ", +UnloadUnusedAssets" : "")}).");
            }
            catch { }
        }

        private static void OnSlotChanged_Postfix()
        {
            try
            {
                string currentSlot = SafeCurrentSlot();
                if (!string.Equals(currentSlot, _lastKnownSlot, StringComparison.Ordinal))
                {
                    _lastKnownSlot = currentSlot;
                    FlushAllCachesOnSlotChange(currentSlot);
                }
            }
            catch { }
        }

        private static void FlushAllCachesOnSlotChange(string newSlot)
        {
            HiddenUiSkipper.ResetState();
            AiPoolCache.ResetState();
            SmartAutosave.ResetState();
            _baselineHeapBytes = ReadCurrentHeapBytes();
            Interlocked.Increment(ref PerfTelemetry.SlotCacheFlushes);
        }

        private static string SafeCurrentSlot()
        {
            try { return SlotController.CurrentSlot ?? ""; }
            catch { return ""; }
        }

        private static bool IsDayBusy()
        {
            try { return CalendarController.IsDayBeingComputed; }
            catch { return false; }
        }

        private static long ReadCurrentHeapBytes()
        {
            try
            {
                long il2cppHeap = Profiler.GetMonoUsedSizeLong();
                long clrHeap = GC.GetTotalMemory(false);
                return il2cppHeap + clrHeap;
            }
            catch
            {
                return GC.GetTotalMemory(false);
            }
        }

        private static long SafeTotalAllocatedMb()
        {
            try { return Profiler.GetTotalAllocatedMemoryLong() / (1024L * 1024L); }
            catch { return 0; }
        }
    }
}
