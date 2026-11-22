using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Accelerates inter-day transition pauses safely (with an automatic Time.timeScale watchdog so
    /// timeScale can NEVER get stuck on scene/menu switches) and profiles ContractEngine.NextDay
    /// sub-passes for empirical F9 telemetry.
    /// </summary>
    internal static class DaySpeedBooster
    {
        private static long _dayStartTimestamp;
        private static bool _coreDayPaceAttached;
        private static bool _standaloneBoostActive;
        private static float _highTimeScaleSince = -1f;

        public static void InstallGamePatches(Harmony harmony)
        {
            try
            {
                var beginDay = AccessTools.Method(typeof(CalendarController), nameof(CalendarController.BeginDay));
                if (beginDay != null)
                    harmony.Patch(beginDay, prefix: new HarmonyMethod(typeof(DaySpeedBooster), nameof(BeginDay_Prefix)));

                var reportDay = AccessTools.Method(typeof(CalendarController), nameof(CalendarController.ReportDayMeasurement));
                if (reportDay != null)
                    harmony.Patch(reportDay, postfix: new HarmonyMethod(typeof(DaySpeedBooster), nameof(DayEnd_Postfix)));
                else
                {
                    var announce = AccessTools.Method(typeof(CalendarController), nameof(CalendarController.AnnounceOutcome));
                    if (announce != null)
                        harmony.Patch(announce, postfix: new HarmonyMethod(typeof(DaySpeedBooster), nameof(DayEnd_Postfix)));
                }

                // Standalone fallback if iamgri mod's DayPace is not present
                var checkEvents = AccessTools.Method(typeof(CalendarController), nameof(CalendarController.CheckForEvents));
                var nextDayDate = AccessTools.Method(typeof(CalendarController), nameof(CalendarController.NextDayDate));
                if (checkEvents != null && nextDayDate != null)
                {
                    harmony.Patch(checkEvents, postfix: new HarmonyMethod(typeof(DaySpeedBooster), nameof(CheckForEvents_Postfix)));
                    harmony.Patch(nextDayDate, prefix: new HarmonyMethod(typeof(DaySpeedBooster), nameof(NextDayDate_Prefix)));
                }

                // Lightweight ContractEngine sub-pass telemetry (helps diagnose 5-8s day ticks empirically)
                PatchSubPass(harmony, typeof(ContractEngine), nameof(ContractEngine.RunDailyAiPasses), nameof(DailyAi_Pre), nameof(DailyAi_Post));
                PatchSubPass(harmony, typeof(ContractEngine), nameof(ContractEngine.TickAiSquadMaintenance), nameof(Maint_Pre), nameof(Maint_Post));
                PatchSubPass(harmony, typeof(AiAcquisitionPasses), nameof(AiAcquisitionPasses.AITransfers), nameof(AiTransfers_Pre), nameof(AiTransfers_Post));
                PatchSubPass(harmony, typeof(AiListingPasses), nameof(AiListingPasses.AIListedPlayerInterestPass), nameof(ListedPass_Pre), nameof(ListedPass_Post));

                Plugin.Logger.LogMessage($"[DaySpeedBooster] Active (FastDayTransitions={Plugin.CfgFastDayTransitions.Value}, DayPauseSpeed={Plugin.CfgDayPauseSpeed.Value:0.#}x).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[DaySpeedBooster] Game patch warning: {ex.Message}");
            }
        }

        private static void PatchSubPass(Harmony harmony, Type type, string methodName, string preName, string postName)
        {
            try
            {
                var m = AccessTools.Method(type, methodName);
                if (m != null)
                {
                    harmony.Patch(
                        m,
                        prefix: new HarmonyMethod(typeof(DaySpeedBooster), preName),
                        postfix: new HarmonyMethod(typeof(DaySpeedBooster), postName));
                }
            }
            catch { }
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
            ResetTimeScaleIfBoosted();
        }

        public static void ResetState()
        {
            ResetTimeScaleIfBoosted();
            Interlocked.Exchange(ref _dayStartTimestamp, 0);
        }

        public static void Tick(bool in3DMatch)
        {
            if (in3DMatch) return;
            try
            {
                // Safety watchdog: never allow Time.timeScale to stay elevated (> 1.5x) outside active day skipping
                if (Time.timeScale > 1.5f)
                {
                    if (_highTimeScaleSince < 0f)
                        _highTimeScaleSince = Time.unscaledTime;
                    else if (!CalendarController.IsDayBeingComputed && Time.unscaledTime - _highTimeScaleSince > 0.75f)
                        ResetTimeScaleIfBoosted();
                }
                else
                {
                    _highTimeScaleSince = -1f;
                }
            }
            catch { }
        }

        private static void ResetTimeScaleIfBoosted()
        {
            _standaloneBoostActive = false;
            _highTimeScaleSince = -1f;
            try
            {
                if (Time.timeScale > 1.05f)
                    Time.timeScale = 1f;
            }
            catch { }
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

                float desired = Math.Clamp(Plugin.CfgDayPauseSpeed.Value, 1f, 100f);
                if (Time.timeScale > 1.5f && desired > Time.timeScale)
                {
                    Time.timeScale = desired;
                    _highTimeScaleSince = Time.unscaledTime;
                    Interlocked.Increment(ref PerfTelemetry.DayPausesAccelerated);
                }
            }
            catch { }
        }

        private static void CheckForEvents_Postfix(bool __result)
        {
            try
            {
                if (_coreDayPaceAttached || !Plugin.CfgFastDayTransitions.Value || Plugin.In3DMatchScene)
                    return;

                if (!__result)
                {
                    float desired = Math.Clamp(Plugin.CfgDayPauseSpeed.Value, 1f, 100f);
                    if (desired > 1f && Time.timeScale <= 1.01f)
                    {
                        Time.timeScale = desired;
                        _standaloneBoostActive = true;
                        _highTimeScaleSince = Time.unscaledTime;
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
            ResetTimeScaleIfBoosted();
        }

        // ContractEngine sub-pass timing hooks
        private static void DailyAi_Pre(out long __state) => __state = Stopwatch.GetTimestamp();
        private static void DailyAi_Post(long __state) => PerfTelemetry.AddSubPass(0, ElapsedMs(__state));

        private static void Maint_Pre(out long __state) => __state = Stopwatch.GetTimestamp();
        private static void Maint_Post(long __state) => PerfTelemetry.AddSubPass(1, ElapsedMs(__state));

        private static void AiTransfers_Pre(out long __state) => __state = Stopwatch.GetTimestamp();
        private static void AiTransfers_Post(long __state) => PerfTelemetry.AddSubPass(2, ElapsedMs(__state));

        private static void ListedPass_Pre(out long __state) => __state = Stopwatch.GetTimestamp();
        private static void ListedPass_Post(long __state) => PerfTelemetry.AddSubPass(3, ElapsedMs(__state));

        private static long ElapsedMs(long start) =>
            start > 0 ? (Stopwatch.GetTimestamp() - start) * 1000L / Stopwatch.Frequency : 0;
    }
}
