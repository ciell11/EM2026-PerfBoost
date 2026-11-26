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
        public const string PluginVersion = "1.5.0";

        internal static ManualLogSource Logger;
        internal static Harmony GameHarmony;
        internal static Harmony CoreHarmony;

        // Config entries: FPS
        internal static ConfigEntry<bool> CfgEnableFpsUnlock;
        internal static ConfigEntry<int> CfgTargetFps;
        internal static ConfigEntry<bool> CfgDisableVSync;
        internal static ConfigEntry<bool> CfgApplyFpsIn3DMatch;

        // Config entries: Day speed
        internal static ConfigEntry<bool> CfgFastDayTransitions;
        internal static ConfigEntry<float> CfgDayPauseSpeed;

        // Config entries: Long-career stability & memory guard
        internal static ConfigEntry<bool> CfgLongCareerMemoryGuard;
        internal static ConfigEntry<int> CfgMemoryGuardGrowthMb;
        internal static ConfigEntry<int> CfgMemoryGuardCooldownSec;
        internal static ConfigEntry<int> CfgUnloadAssetsThresholdMb;

        // Config entries: Existing modules
        internal static ConfigEntry<bool> CfgMatchThreadSafety;
        internal static ConfigEntry<bool> CfgSmartAutosave;
        internal static ConfigEntry<int> CfgMinSecondsBetweenMatchSaves;
        internal static ConfigEntry<bool> CfgSkipHiddenVanillaUi;
        internal static ConfigEntry<bool> CfgDisableHiddenRaycasters;
        internal static ConfigEntry<bool> CfgAiPoolCache;
        internal static ConfigEntry<int> CfgAiPoolMaxCacheDays;
        internal static ConfigEntry<bool> CfgFastAiTransfers;
        internal static ConfigEntry<int> CfgEmptyScanSkipDays;
        internal static ConfigEntry<bool> CfgQuietConsoleInfo;
        internal static ConfigEntry<bool> CfgQuietDiskInfo;
        internal static ConfigEntry<bool> CfgStripUnityLogStackTraces;
        internal static ConfigEntry<bool> CfgSmoothIncrementalGc;
        internal static ConfigEntry<int> CfgGcSliceMs;
        internal static ConfigEntry<bool> CfgOptimizeIamgriBetter;
        internal static ConfigEntry<bool> CfgPeriodicTelemetry;
        internal static ConfigEntry<int> CfgConfigSchemaVersion;

        private static bool _in3DMatchScene;
        private static int _lastSceneHandle = int.MinValue;
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

                // 2. FPS unlocker (default 165 FPS, also speeds up Unity coroutine day yields)
                if (CfgEnableFpsUnlock.Value)
                    FpsUnlocker.Install(GameHarmony);

                // 3. Faster inter-day transitions & real day-advance timing telemetry
                DaySpeedBooster.InstallGamePatches(GameHarmony);

                // 3b. Prune dormant/locked clubs from AITransfers buckets before O(teams * players) scan
                if (CfgFastAiTransfers.Value)
                    FastAiTransfers.InstallGamePatches(GameHarmony);

                // 4. Bugfix: Thread-safe MatchesEngine.ProcessAIMatches snapshot
                if (CfgMatchThreadSafety.Value)
                    MatchThreadSafety.Install(GameHarmony);

                // 5. Hidden vanilla UI skipper & dirty-check memoization
                if (CfgSkipHiddenVanillaUi.Value)
                    HiddenUiSkipper.InstallGamePatches(GameHarmony);

                // 6. Long-career memory guard & clean slot-switch cache flushing
                LongCareerGuard.Install(GameHarmony);

                // 7. UI frame telemetry & optional incremental GC slice pacer
                UiFramePacer.Install(GameHarmony);

                // 8. Frame tick hook on EventSystem.Update (same proven hook as EMSkin & IamgriBetter)
                var esUpdate = AccessTools.Method(typeof(EventSystem), nameof(EventSystem.Update));
                if (esUpdate != null)
                {
                    GameHarmony.Patch(esUpdate, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnFrameTick)));
                }
                else
                {
                    Logger.LogWarning("EventSystem.Update not found; core polling disabled.");
                }

                // Attempt immediate attach if EMSkinCore or IamgriBetter is already loaded
                if (CoreBridge.Poll(out var coreAsm))
                {
                    AttachToCore(coreAsm);
                }
                if (CfgOptimizeIamgriBetter.Value)
                {
                    Better130Optimizer.PollAndAttach(GameHarmony);
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
                FastAiTransfers.Detach();
                FpsUnlocker.RestoreVanilla();
                UiFramePacer.RestoreVanilla();
                CoreHarmony?.UnpatchSelf();
                GameHarmony?.UnpatchSelf();
            }
            catch { }
            return base.Unload();
        }

        private void BindConfig()
        {
            CfgEnableFpsUnlock = Config.Bind(
                "FPS", "EnableFpsUnlock", true,
                "Unlocks the frame rate cap in UI/manager scenes for smoother scrolling and faster coroutine day advancement.");

            CfgTargetFps = Config.Bind(
                "FPS", "TargetFps", 165,
                new ConfigDescription("Target FPS cap (0 = leave vanilla untouched, -1 = uncapped, 30-1000 = custom cap). Default: 165.",
                    new AcceptableValueRange<int>(-1, 1000)));

            CfgDisableVSync = Config.Bind(
                "FPS", "DisableVSync", true,
                "Sets QualitySettings.vSyncCount = 0 when FPS unlock is active so TargetFps takes effect.");

            CfgApplyFpsIn3DMatch = Config.Bind(
                "FPS", "ApplyIn3DMatch", false,
                "Also apply TargetFps / DisableVSync inside 3D match scenes. Leave false to keep your in-game 3D match settings untouched.");

            CfgFastDayTransitions = Config.Bind(
                "DaySpeed", "FastDayTransitions", true,
                "Accelerates the dead pause between simulated days (never touches 3D match speed or gameplay logic).");

            CfgDayPauseSpeed = Config.Bind(
                "DaySpeed", "DayPauseSpeed", 100f,
                new ConfigDescription("Time scale applied strictly during the inter-day pause (iamgri default is 20x; 100x cuts dead wait without affecting simulations).",
                    new AcceptableValueRange<float>(1f, 200f)));

            CfgLongCareerMemoryGuard = Config.Bind(
                "LongCareer", "EnableMemoryGuard", true,
                "Protects careers from multi-GB RAM bloat and sudden GC freezes by running gentle idle trims in menus (never during matches, day ticks, or saves).");

            CfgMemoryGuardGrowthMb = Config.Bind(
                "LongCareer", "GrowthThresholdMb", 512,
                new ConfigDescription("Heap growth (in MB) above baseline before a gentle idle trim is allowed.",
                    new AcceptableValueRange<int>(128, 4096)));

            CfgMemoryGuardCooldownSec = Config.Bind(
                "LongCareer", "CleanupCooldownSeconds", 120,
                new ConfigDescription("Minimum real seconds between automatic idle memory cleanups.",
                    new AcceptableValueRange<int>(30, 1800)));

            CfgUnloadAssetsThresholdMb = Config.Bind(
                "LongCareer", "UnloadUnusedAssetsThresholdMb", 2200,
                new ConfigDescription("If total Unity allocated memory exceeds this threshold (MB) during an idle cleanup, also call async Resources.UnloadUnusedAssets().",
                    new AcceptableValueRange<int>(1024, 8192)));

            CfgMatchThreadSafety = Config.Bind(
                "Stability", "ThreadSafeAIMatches", true,
                "Snapshots MatchesEngine.ProcessAIMatches list to prevent 'Collection was modified' exceptions when DayOffload and third-place decider mods run concurrently.");

            CfgSmartAutosave = Config.Bind(
                "Autosave", "CoalesceAfterMatchSaves", true,
                "Prevents redundant back-to-back 3-4s full saves after every quick match while preserving periodic AutosaveEveryDays, manual saves, and exit-to-menu saves.");

            CfgMinSecondsBetweenMatchSaves = Config.Bind(
                "Autosave", "MinSecondsBetweenMatchSaves", 180,
                new ConfigDescription("Minimum real seconds between automatic after-match saves (0 = rely purely on iamgri's AutosaveEveryDays). Periodic & exit saves are NEVER skipped.",
                    new AcceptableValueRange<int>(0, 1800)));

            CfgSkipHiddenVanillaUi = Config.Bind(
                "UI", "SkipHiddenVanillaUiRebuilds", true,
                "Skips safe hidden vanilla rebuilds (Shop/Basecamp once iamgri pages are built, and unchanged Inbox/Talk windows). Squad/Staff/Transfers are NEVER skipped so all player/staff/listing actions work 100%.");

            CfgDisableHiddenRaycasters = Config.Bind(
                "UI", "DisableHiddenGraphicRaycasters", true,
                "Disables GraphicRaycaster on vanilla canvases hidden by iamgri mod (UiKit.Unseen) without touching layouts.");

            CfgAiPoolCache = Config.Bind(
                "Simulation", "CacheAiIdentityPools", true,
                "Caches iamgri mod's AiIdentity 88-bloc player pool across quiet days, invalidating immediately on any transfer/offer/player-count change or weekly growth tick.");

            CfgAiPoolMaxCacheDays = Config.Bind(
                "Simulation", "AiPoolMaxCacheDays", 3,
                new ConfigDescription("Maximum in-game days to reuse AiIdentity pool without a world mutation before forcing a fresh rebuild.",
                    new AcceptableValueRange<int>(1, 7)));

            CfgFastAiTransfers = Config.Bind(
                "Simulation", "PruneDormantFromAiTransfers", true,
                "Accelerates daily AI transfer passes by pruning dormant/locked clubs and memoizing 11 hot pure EMSkinCore lookups with zero gameplay change.");

            CfgEmptyScanSkipDays = Config.Bind(
                "Simulation", "EmptyScanSkipDays", 7,
                new ConfigDescription("Staggers redundant re-scans for non-priority AI clubs (_scratchOtherTeams) that found no pick (0 = daily, 7 = weekly re-check / ~5-7x faster day skips; priority clubs, clubs with picks, and any club whose squad shrinks or TransferBudget grows by >$25k ALWAYS scan immediately).",
                    new AcceptableValueRange<int>(0, 14)));

            CfgQuietConsoleInfo = Config.Bind(
                "Logging", "QuietConsoleInfo", true,
                "Filters verbose Info/Debug spam from iamgri mod and Iamgri Better out of the synchronous Windows console window (Warnings/Errors/Messages always stay visible).");

            CfgQuietDiskInfo = Config.Bind(
                "Logging", "QuietDiskInfo", false,
                "Also filters verbose Info spam from LogOutput.log on disk. Keep false if you need full logs for bug reports.");

            CfgStripUnityLogStackTraces = Config.Bind(
                "Logging", "StripUnityLogStackTraces", true,
                "Disables native stack-trace capture on Unity Debug.Log and Debug.LogWarning (keeps Error/Exception stack traces 100% enabled) to eliminate Player.log disk/CPU storms.");

            CfgSmoothIncrementalGc = Config.Bind(
                "Memory", "SmoothIncrementalGC", true,
                "Caps Unity's incremental GC time slice in UI scenes for smoother frame pacing (never touches 3D match scenes).");

            CfgGcSliceMs = Config.Bind(
                "Memory", "IncrementalGcSliceMs", 4,
                new ConfigDescription("Incremental GC time budget per frame in milliseconds when SmoothIncrementalGC is enabled (4 ms balances smooth UI pacing with fast collection on large 2029+ saves).",
                    new AcceptableValueRange<int>(1, 10)));

            CfgOptimizeIamgriBetter = Config.Bind(
                "IamgriBetter", "OptimizeIamgriBetter130", true,
                "Eliminates per-frame Canvas/TMP rebuilds, reflection badge/color spam, and heavy icon-recovery scans in IamgriBetter 1.3.0+ with zero visual or gameplay change.");

            CfgPeriodicTelemetry = Config.Bind(
                "Diagnostics", "LogPeriodicSummary", true,
                "Logs a one-block performance telemetry summary every 5 minutes (and on F9 key press).");

            CfgConfigSchemaVersion = Config.Bind(
                "Diagnostics", "ConfigSchemaVersion", 0,
                "Internal config migration marker so drop-in updates upgrade old defaults automatically while keeping custom user values.");

            // One-time seamless migration for players upgrading over v1.2.x / v1.3.x / v1.4.x without deleting their .cfg
            if (CfgConfigSchemaVersion.Value < 150)
            {
                bool migrated = false;
                if (Math.Abs(CfgDayPauseSpeed.Value - 60f) < 0.01f) { CfgDayPauseSpeed.Value = 100f; migrated = true; }
                if (CfgMemoryGuardGrowthMb.Value == 1024 || CfgMemoryGuardGrowthMb.Value == 384) { CfgMemoryGuardGrowthMb.Value = 512; migrated = true; }
                if (CfgMemoryGuardCooldownSec.Value == 180 || CfgMemoryGuardCooldownSec.Value == 75) { CfgMemoryGuardCooldownSec.Value = 120; migrated = true; }
                if (CfgUnloadAssetsThresholdMb.Value == 2560 || CfgUnloadAssetsThresholdMb.Value == 1400) { CfgUnloadAssetsThresholdMb.Value = 2200; migrated = true; }
                if (CfgEmptyScanSkipDays.Value == 3 || CfgEmptyScanSkipDays.Value == 5) { CfgEmptyScanSkipDays.Value = 7; migrated = true; }
                if (!CfgSmoothIncrementalGc.Value && CfgGcSliceMs.Value == 2)
                {
                    CfgSmoothIncrementalGc.Value = true;
                    CfgGcSliceMs.Value = 4;
                    migrated = true;
                }
                CfgConfigSchemaVersion.Value = 150;
                Config.Save();
                if (migrated)
                    Logger?.LogMessage("[Config] Automatically migrated default values to v1.5.0 tuning (100x day pause, EmptyScanSkipDays=7, 512 MB gentle RAM trim).");
            }
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

                if (CfgFastDayTransitions.Value)
                    DaySpeedBooster.AttachToCore(CoreHarmony, coreAsm);

                if (CfgFastAiTransfers.Value)
                    FastAiTransfers.AttachToCore(CoreHarmony, coreAsm);

                Better130Optimizer.OnCoreAttached(coreAsm);

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
                // Detect scene changes via integer handle (ZERO IL2CPP string allocations per frame!)
                var activeScene = SceneManager.GetActiveScene();
                int sceneHandle = activeScene.handle;
                if (sceneHandle != _lastSceneHandle)
                {
                    _lastSceneHandle = sceneHandle;
                    string sceneName = activeScene.name ?? "";
                    _in3DMatchScene = IsMatchSceneName(sceneName);
                    FpsUnlocker.OnSceneChanged(_in3DMatchScene);
                    DaySpeedBooster.OnSceneChanged(_in3DMatchScene);
                    LongCareerGuard.OnSceneChanged(_in3DMatchScene);
                    UiFramePacer.OnSceneChanged(_in3DMatchScene);
                    Better130Optimizer.ResetState();
                }

                // Re-attach if EMSkinCore loaded or hot-reloaded
                if (CoreBridge.Poll(out var newCore))
                {
                    AttachToCore(newCore);
                }

                // Attach dynamically to IamgriBetter 1.3.0+ once loaded
                if (CfgOptimizeIamgriBetter.Value && !Better130Optimizer.IsAttached)
                {
                    Better130Optimizer.PollAndAttach(GameHarmony);
                }

                // Periodic lightweight enforcement, timeScale watchdog & idle memory guard
                FpsUnlocker.Tick(_in3DMatchScene);
                DaySpeedBooster.Tick(_in3DMatchScene);
                LongCareerGuard.Tick(_in3DMatchScene);

                // UI frame timing (never runs inside 3D matches)
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
