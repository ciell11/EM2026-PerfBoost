using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Accelerates inter-day transition pauses safely (never touches 3D match Time.timeScale)
    /// and measures actual day-advance durations for live F9 telemetry.
    /// </summary>
    internal static class DaySpeedBooster
    {
        private static long _dayStartTimestamp;
        private static bool _coreDayPaceAttached;
        private static bool _standaloneBoostActive;

        public static void InstallGamePatches(Harmony harmony)
        {
            try
            {
                var beginDay = AccessTools.Method(typeof(CalendarController), nameof(CalendarController.BeginDay));
                if (beginDay != null)
                    harmony.Patch(beginDay, prefix: new HarmonyMethod(typeof(DaySpeedBooster), nameof(BeginDay_Prefix)));

                var announce = AccessTools.Method(typeof(CalendarController), nameof(CalendarController.AnnounceOutcome));
                if (announce != null)
                    harmony.Patch(announce, postfix: new HarmonyMethod(typeof(DaySpeedBooster), nameof(DayEnd_Postfix)));

                // Standalone fallback if iamgri mod's DayPace is not present
                var checkEvents = AccessTools.Method(typeof(CalendarController), nameof(CalendarController.CheckForEvents));
                var nextDayDate = AccessTools.Method(typeof(CalendarController), nameof(CalendarController.NextDayDate));
                if (checkEvents != null && nextDayDate != null)
                {
                    harmony.Patch(checkEvents, postfix: new HarmonyMethod(typeof(DaySpeedBooster), nameof(CheckForEvents_Postfix)));
                    harmony.Patch(nextDayDate, prefix: new HarmonyMethod(typeof(DaySpeedBooster), nameof(NextDayDate_Prefix)));
                }

                Plugin.Logger.LogMessage($"[DaySpeedBooster] Active (FastDayTransitions={Plugin.CfgFastDayTransitions.Value}, DayPauseSpeed={Plugin.CfgDayPauseSpeed.Value:0.#}x).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DaySpeedBooster] Game patch warning: {ex.Message}");
            }
        }

        public static void AttachToCore(Harmony coreHarmony, Assembly coreAsm)
        {
            try
            {
                var dayPace = coreAsm.GetType("DayPace", false);
                if (dayPace == null) return;

                var afterEvents = dayPace.GetMethod("AfterEvents", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (afterEvents != null)
                {
                    coreHarmony.Patch(
                        afterEvents,
                        postfix: new HarmonyMethod(typeof(DaySpeedBooster), nameof(CoreDayPace_AfterEvents_Postfix)));
                    _coreDayPaceAttached = true;
                    Plugin.Logger.LogMessage($"[DaySpeedBooster] Hooked EMSkinCore.DayPace (inter-day pause speed -> {Plugin.CfgDayPauseSpeed.Value:0.#}x).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DaySpeedBooster] Core attach warning: {ex.Message}");
            }
        }

        public static void OnSceneChanged(bool in3DMatch)
        {
            if (in3DMatch && _standaloneBoostActive)
            {
                _standaloneBoostActive = false;
                try
                {
                    if (Time.timeScale > 1.01f)
                        Time.timeScale = 1f;
                }
                catch { }
            }
        }

        private static void BeginDay_Prefix()
        {
            Interlocked.Exchange(ref _dayStartTimestamp, Stopwatch.GetTimestamp());
        }

        private static void DayEnd_Postfix()
        {
            long start = Interlocked.Exchange(ref _dayStartTimestamp, 0);
            if (start <= 0) return;

            long elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000L / Stopwatch.Frequency;
            if (elapsedMs >= 0 && elapsedMs < 120000)
                PerfTelemetry.RecordDayAdvance(elapsedMs);
        }

        private static void CoreDayPace_AfterEvents_Postfix()
        {
            try
            {
                if (!Plugin.CfgFastDayTransitions.Value || Plugin.In3DMatchScene)
                    return;

                // Only boost when iamgri's DayPace has already put Time.timeScale into inter-day pause mode (> 1x)
                float desired = Math.Clamp(Plugin.CfgDayPauseSpeed.Value, 1f, 100f);
                if (Time.timeScale > 1.5f && desired > Time.timeScale)
                {
                    Time.timeScale = desired;
                    Interlocked.Increment(ref PerfTelemetry.DayPausesAccelerated);
                }
            }
            catch { }
        }

        private static void CheckForEvents_Postfix(bool __result)
        {
            try
            {
                // Only run standalone fallback if EMSkinCore.DayPace is NOT present
                if (_coreDayPaceAttached || !Plugin.CfgFastDayTransitions.Value || Plugin.In3DMatchScene)
                    return;

                if (!__result)
                {
                    float desired = Math.Clamp(Plugin.CfgDayPauseSpeed.Value, 1f, 100f);
                    if (desired > 1f && Time.timeScale <= 1.01f)
                    {
                        Time.timeScale = desired;
                        _standaloneBoostActive = true;
                        Interlocked.Increment(ref PerfTelemetry.DayPausesAccelerated);
                    }
                }
            }
            catch { }
        }

        private static void NextDayDate_Prefix()
        {
            if (!_standaloneBoostActive)
                return;

            _standaloneBoostActive = false;
            try
            {
                if (Time.timeScale > 1.01f)
                    Time.timeScale = 1f;
            }
            catch { }
        }
    }
}
