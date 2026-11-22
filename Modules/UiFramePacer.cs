using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Scripting;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Smooths incremental GC slices in UI/manager scenes (never touches 3D match scenes)
    /// and measures MainWindows.SwitchWindows + UI frame timings for empirical F9 telemetry.
    /// </summary>
    internal static class UiFramePacer
    {
        private static ulong _defaultGcSliceNs = 3_000_000UL;
        private static bool _capturedDefaultSlice;

        public static void Install(Harmony harmony)
        {
            try
            {
                var showMethod = AccessTools.Method(typeof(MainWindows), nameof(MainWindows.Show), new[] { typeof(Windows) });
                if (showMethod != null)
                {
                    harmony.Patch(
                        showMethod,
                        prefix: new HarmonyMethod(typeof(UiFramePacer), nameof(ShowWindow_Prefix)),
                        postfix: new HarmonyMethod(typeof(UiFramePacer), nameof(ShowWindow_Postfix)));
                }

                ApplyGcSlice(in3DMatch: false);
                Plugin.Logger.LogMessage($"[UiFramePacer] Active (SmoothIncrementalGC={Plugin.CfgSmoothIncrementalGc.Value}, slice={Plugin.CfgGcSliceMs.Value}ms in UI scenes).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[UiFramePacer] Init warning: {ex.Message}");
            }
        }

        public static void OnSceneChanged(bool in3DMatch)
        {
            ApplyGcSlice(in3DMatch);
        }

        public static void RestoreVanilla()
        {
            if (!_capturedDefaultSlice || _defaultGcSliceNs == 0)
                return;
            try
            {
                if (GarbageCollector.isIncremental)
                    GarbageCollector.incrementalTimeSliceNanoseconds = _defaultGcSliceNs;
            }
            catch { }
        }

        public static void OnUiFrame()
        {
            double dtMs = Time.unscaledDeltaTime * 1000.0;
            PerfTelemetry.RecordUiFrame(dtMs);
        }

        private static void ApplyGcSlice(bool in3DMatch)
        {
            if (!Plugin.CfgSmoothIncrementalGc.Value)
                return;

            try
            {
                if (!GarbageCollector.isIncremental)
                    return;

                if (!_capturedDefaultSlice)
                {
                    _defaultGcSliceNs = GarbageCollector.incrementalTimeSliceNanoseconds;
                    _capturedDefaultSlice = true;
                }

                if (in3DMatch)
                {
                    // Restore game's exact GC behavior during 3D matches
                    if (_defaultGcSliceNs > 0)
                        GarbageCollector.incrementalTimeSliceNanoseconds = _defaultGcSliceNs;
                }
                else
                {
                    ulong targetNs = (ulong)Math.Clamp(Plugin.CfgGcSliceMs.Value, 1, 10) * 1_000_000UL;
                    GarbageCollector.incrementalTimeSliceNanoseconds = targetNs;
                }
            }
            catch
            {
                // Ignore if Unity GC API unavailable
            }
        }

        private static void ShowWindow_Prefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void ShowWindow_Postfix(Windows __0, long __state)
        {
            if (__state <= 0) return;
            long elapsedTicks = Stopwatch.GetTimestamp() - __state;
            long ms = elapsedTicks * 1000L / Stopwatch.Frequency;
            string name = __0 != null ? __0.GetType().Name : "?";
            PerfTelemetry.RecordSwitch(name, ms);
            LongCareerGuard.NotifyScreenSwitched();
        }
    }
}
