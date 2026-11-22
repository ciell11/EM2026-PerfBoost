using System;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Unlocks and enforces a configurable target FPS cap (default 165 FPS) with optional VSync disable.
    /// Speeds up both UI responsiveness and Unity coroutine yields during day advancement
    /// (CalendarController.NextDayLoop / StartEventsSequentially).
    /// Respects ApplyIn3DMatch setting so 3D matches stay untouched unless explicitly enabled.
    /// </summary>
    internal static class FpsUnlocker
    {
        private static int _savedVSyncCount = 1;
        private static int _savedTargetFps = 60;
        private static bool _capturedVanilla;
        private static bool _isApplying;
        private static float _nextEnforceTime;

        public static void Install(Harmony harmony)
        {
            try
            {
                CaptureVanilla();

                var post = new HarmonyMethod(typeof(FpsUnlocker), nameof(DisplaySettings_Postfix));
                PatchIfFound(harmony, typeof(DisplaySettings), nameof(DisplaySettings.Start), post);
                PatchIfFound(harmony, typeof(DisplaySettings), nameof(DisplaySettings.SetFrameRate), post);
                PatchIfFound(harmony, typeof(DisplaySettings), nameof(DisplaySettings.SetVSync), post);

                ApplyForCurrentScene(Plugin.In3DMatchScene);
                Plugin.Logger.LogMessage(
                    $"[FpsUnlocker] Active (Enabled={Plugin.CfgEnableFpsUnlock.Value}, TargetFps={Plugin.CfgTargetFps.Value}, DisableVSync={Plugin.CfgDisableVSync.Value}, ApplyIn3DMatch={Plugin.CfgApplyFpsIn3DMatch.Value}).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[FpsUnlocker] Init warning: {ex.Message}");
            }
        }

        private static void PatchIfFound(Harmony harmony, Type type, string methodName, HarmonyMethod postfix)
        {
            try
            {
                var m = AccessTools.Method(type, methodName);
                if (m != null)
                    harmony.Patch(m, postfix: postfix);
            }
            catch { }
        }

        private static void CaptureVanilla()
        {
            if (_capturedVanilla) return;
            try
            {
                _savedVSyncCount = QualitySettings.vSyncCount;
                _savedTargetFps = Application.targetFrameRate;
                _capturedVanilla = true;
            }
            catch { }
        }

        public static void OnSceneChanged(bool in3DMatch)
        {
            ApplyForCurrentScene(in3DMatch);
        }

        public static void Tick(bool in3DMatch)
        {
            if (!Plugin.CfgEnableFpsUnlock.Value)
                return;

            // Lightweight periodic enforcement every 1.5s in case another system resets targetFrameRate
            float now = Time.unscaledTime;
            if (now < _nextEnforceTime)
                return;
            _nextEnforceTime = now + 1.5f;

            ApplyForCurrentScene(in3DMatch);
        }

        public static void ApplyForCurrentScene(bool in3DMatch)
        {
            if (_isApplying || !Plugin.CfgEnableFpsUnlock.Value)
                return;

            int target = Plugin.CfgTargetFps.Value;
            if (target == 0)
                return; // 0 = leave vanilla untouched

            _isApplying = true;
            try
            {
                CaptureVanilla();

                if (in3DMatch && !Plugin.CfgApplyFpsIn3DMatch.Value)
                {
                    // Restore game's own VSync/FPS settings inside 3D matches
                    if (QualitySettings.vSyncCount != _savedVSyncCount)
                        QualitySettings.vSyncCount = _savedVSyncCount;
                    if (Application.targetFrameRate != _savedTargetFps)
                        Application.targetFrameRate = _savedTargetFps;
                    return;
                }

                bool changed = false;
                if (Plugin.CfgDisableVSync.Value && QualitySettings.vSyncCount != 0)
                {
                    QualitySettings.vSyncCount = 0;
                    changed = true;
                }

                int desiredFps = target < 0 ? -1 : Math.Clamp(target, 30, 1000);
                if (Application.targetFrameRate != desiredFps)
                {
                    Application.targetFrameRate = desiredFps;
                    changed = true;
                }

                if (changed)
                    Interlocked.Increment(ref PerfTelemetry.FpsEnforcements);
            }
            catch { }
            finally
            {
                _isApplying = false;
            }
        }

        public static void RestoreVanilla()
        {
            if (!_capturedVanilla) return;
            try
            {
                QualitySettings.vSyncCount = _savedVSyncCount;
                Application.targetFrameRate = _savedTargetFps;
            }
            catch { }
        }

        private static void DisplaySettings_Postfix()
        {
            try
            {
                if (_isApplying) return;
                // Update our captured vanilla values when the user changes in-game settings
                _savedVSyncCount = QualitySettings.vSyncCount;
                _savedTargetFps = Application.targetFrameRate;
                _capturedVanilla = true;

                ApplyForCurrentScene(Plugin.In3DMatchScene);
            }
            catch { }
        }
    }
}
