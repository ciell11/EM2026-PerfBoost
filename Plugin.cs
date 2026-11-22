using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using EM2026PerfBoost.Modules;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace EM2026PerfBoost
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.gri.emskin", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("local.iamgri.better", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "local.em2026.perfboost";
        public const string PluginName = "EM2026 PerfBoost";
        public const string PluginVersion = "1.2.0";

        internal static ManualLogSource Logger;
        internal static Harmony GameHarmony;
        internal static Harmony CoreHarmony;

        // Config entries
        internal static ConfigEntry<bool> CfgMatchThreadSafety;
        internal static ConfigEntry<bool> CfgSmartAutosave;
        internal static ConfigEntry<int> CfgMinSecondsBetweenMatchSaves;
        internal static ConfigEntry<bool> CfgSkipHiddenVanillaUi;
        internal static ConfigEntry<bool> CfgDisableHiddenRaycasters;
        internal static ConfigEntry<bool> CfgAiPoolCache;
        internal static ConfigEntry<int> CfgAiPoolMaxCacheDays;
        internal static ConfigEntry<bool> CfgQuietConsoleInfo;
        internal static ConfigEntry<bool> CfgQuietDiskInfo;
        internal static ConfigEntry<bool> CfgSmoothIncrementalGc;
        internal static ConfigEntry<int> CfgGcSliceMs;
        internal static ConfigEntry<bool> CfgPeriodicTelemetry;

        private static bool _in3DMatchScene;
        private static string _lastSceneName = "";
        private static float _nextAutoReportTime;

        internal static bool In3DMatchScene => _in3DMatchScene;

        public override void Load()
        {
            Logger = Log;
            BindConfig();

            GameHarmony = new Harmony(PluginGuid + ".game");
            CoreHarmony = new Harmony(PluginGuid + ".core");

            try
            {
                // 1. Logging throttle (apply early so startup/runtime console spam stops immediately)
                LogThrottler.Install(GameHarmony);

                // 2. Bugfix: Thread-safe MatchesEngine.ProcessAIMatches snapshot
                if (CfgMatchThreadSafety.Value)
                    MatchThreadSafety.Install(GameHarmony);

                // 3. Hidden vanilla UI skipper & dirty-check memoization
                if (CfgSkipHiddenVanillaUi.Value)
                    HiddenUiSkipper.InstallGamePatches(GameHarmony);

                // 4. UI Frame pacer & SwitchWindows telemetry (strictly non-3D match)
                UiFramePacer.Install(GameHarmony);

                // 5. Frame tick hook on EventSystem.Update (same proven hook as EMSkin & IamgriBetter)
                var esUpdate = AccessTools.Method(typeof(EventSystem), nameof(EventSystem.Update));
                if (esUpdate != null)
                {
                    GameHarmony.Patch(esUpdate, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnFrameTick)));
                }
                else
                {
                    Logger.LogWarning("EventSystem.Update not found; core polling disabled.");
                }

                // Attempt immediate attach if EMSkinCore is already loaded
                if (CoreBridge.Poll(out var coreAsm))
                {
                    AttachToCore(coreAsm);
                }

                _nextAutoReportTime = Time.unscaledTime + 120f;
                Logger.LogMessage($"{PluginName} v{PluginVersion} loaded! Press F9 anytime for live performance telemetry.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to initialize {PluginName}: {ex}");
            }
        }

        public override bool Unload()
        {
            try
            {
                CoreHarmony?.UnpatchSelf();
                GameHarmony?.UnpatchSelf();
            }
            catch { }
            return base.Unload();
        }

        private void BindConfig()
        {
            CfgMatchThreadSafety = Config.Bind(
                "Stability", "ThreadSafeAIMatches", true,
                "Snapshots MatchesEngine.ProcessAIMatches list to prevent 'Collection was modified' exceptions when DayOffload + IamgriThirdPlace run concurrently.");

            CfgSmartAutosave = Config.Bind(
                "Autosave", "CoalesceAfterMatchSaves", true,
                "Prevents redundant back-to-back 3-4s full saves after every quick match while preserving periodic AutosaveEveryDays, manual saves, and exit-to-menu saves.");

            CfgMinSecondsBetweenMatchSaves = Config.Bind(
                "Autosave", "MinSecondsBetweenMatchSaves", 180,
                new ConfigDescription("Minimum real seconds between automatic after-match saves (0 = rely purely on iamgri's AutosaveEveryDays). Periodic & exit saves are NEVER skipped.",
                    new AcceptableValueRange<int>(0, 1800)));

            CfgSkipHiddenVanillaUi = Config.Bind(
                "UI", "SkipHiddenVanillaUiRebuilds", true,
                "Skips redundant hidden vanilla window rebuilds (Transfers/Shop/Basecamp once iamgri pages are built, and unchanged Inbox/Talk windows) while keeping all data 100% live.");

            CfgDisableHiddenRaycasters = Config.Bind(
                "UI", "DisableHiddenGraphicRaycasters", true,
                "Disables GraphicRaycaster components on vanilla canvases hidden by iamgri mod (UiKit.Unseen) to avoid wasted pointer raycasts.");

            CfgAiPoolCache = Config.Bind(
                "Simulation", "CacheAiIdentityPools", true,
                "Caches iamgri mod's AiIdentity 88-bloc player pool across quiet days, invalidating immediately on any transfer/offer/player-count change or weekly growth tick.");

            CfgAiPoolMaxCacheDays = Config.Bind(
                "Simulation", "AiPoolMaxCacheDays", 3,
                new ConfigDescription("Maximum in-game days to reuse AiIdentity pool without a world mutation before forcing a fresh rebuild.",
                    new AcceptableValueRange<int>(1, 7)));

            CfgQuietConsoleInfo = Config.Bind(
                "Logging", "QuietConsoleInfo", true,
                "Filters verbose Info/Debug spam from iamgri mod and Iamgri Better out of the synchronous Windows console window (Warnings/Errors/Messages always stay visible).");

            CfgQuietDiskInfo = Config.Bind(
                "Logging", "QuietDiskInfo", false,
                "Also filters verbose Info spam from LogOutput.log on disk. Keep false if you need full logs for bug reports.");

            CfgSmoothIncrementalGc = Config.Bind(
                "Memory", "SmoothIncrementalGC", true,
                "Sets a smooth incremental GC time slice in UI/manager scenes (never touches 3D match scenes) to prevent frame hitches.");

            CfgGcSliceMs = Config.Bind(
                "Memory", "IncrementalGcSliceMs", 2,
                new ConfigDescription("Incremental GC time budget per frame in milliseconds during UI scenes.",
                    new AcceptableValueRange<int>(1, 10)));

            CfgPeriodicTelemetry = Config.Bind(
                "Diagnostics", "LogPeriodicSummary", true,
                "Logs a one-block performance telemetry summary every 5 minutes (and on F9 key press).");
        }

        private static void AttachToCore(Assembly coreAsm)
        {
            try
            {
                CoreHarmony.UnpatchSelf();
                Logger.LogMessage($"Attaching to EMSkinCore ({coreAsm.GetName().Version})...");

                if (CfgSmartAutosave.Value)
                    SmartAutosave.AttachToCore(CoreHarmony, coreAsm);

                if (CfgAiPoolCache.Value)
                    AiPoolCache.AttachToCore(CoreHarmony, coreAsm);

                if (CfgDisableHiddenRaycasters.Value)
                    HiddenUiSkipper.AttachToCore(CoreHarmony, coreAsm);

                Logger.LogMessage("Successfully attached to EMSkinCore.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error attaching to EMSkinCore: {ex}");
            }
        }

        private static void OnFrameTick()
        {
            try
            {
                // Detect scene changes (stay passive during 3D match scenes)
                var activeScene = SceneManager.GetActiveScene();
                string sceneName = activeScene.name ?? "";
                if (!string.Equals(sceneName, _lastSceneName, StringComparison.Ordinal))
                {
                    _lastSceneName = sceneName;
                    _in3DMatchScene = IsMatchSceneName(sceneName);
                    UiFramePacer.OnSceneChanged(_in3DMatchScene);
                }

                // Re-attach if EMSkinCore loaded or hot-reloaded
                if (CoreBridge.Poll(out var newCore))
                {
                    AttachToCore(newCore);
                }

                // UI frame timing & GC pacer (never runs inside 3D matches)
                if (!_in3DMatchScene)
                {
                    UiFramePacer.OnUiFrame();
                }

                // Hotkey F9: print telemetry report immediately
                if (Input.GetKeyDown(KeyCode.F9))
                {
                    Logger.LogMessage("\n" + PerfTelemetry.BuildReport());
                }
                else if (CfgPeriodicTelemetry.Value && Time.unscaledTime >= _nextAutoReportTime)
                {
                    _nextAutoReportTime = Time.unscaledTime + 300f;
                    Logger.LogMessage("\n" + PerfTelemetry.BuildReport());
                }
            }
            catch
            {
                // Never break EventSystem.Update
            }
        }

        private static bool IsMatchSceneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("Match", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Game3D", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Arena", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
