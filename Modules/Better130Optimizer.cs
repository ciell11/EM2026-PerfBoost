using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Targeted, non-destructive performance optimizer for IamgriBetter 1.3.0+ (including 1.3.0-hotfix).
    /// Discovers IamgriBetter dynamically at runtime with ZERO compile-time dependency so EM2026PerfBoost
    /// stays 100% compatible standalone, with older IamgriBetter 1.1.x, and with future 1.3.x updates.
    ///
    /// Eliminates three major FPS bottlenecks introduced in IamgriBetter 1.3.0:
    /// 1. MenuPresentation.Refresh() ran on MainMenuPage.Tick every frame and unconditionally mutated
    ///    RectTransform + TMP_Text properties, dirtying the Unity Canvas every frame -> throttled to
    ///    only execute when MainMenuPage._ui changes/activates or on a gentle 1.0s safety heartbeat.
    /// 2. BadgeColours.BeforeGet + MenuPresentation.TeamColor/TableColor performed reflection
    ///    (EmSkinUi.Call + object[] allocation) and repeated IL2CPP DataTeams/Academy lookups on every
    ///    team badge and table row -> fast O(1) dictionary cache, invalidated immediately on
    ///    ClubIdentity.Save, BadgeColours.Invalidate, or career slot change.
    /// 3. IconRecovery.BeforeTick ran LINQ Cast().ToArray() + 3 reflection calls per icon every tick
    ///    -> throttled to run at most once every 0.75s while still recovering any missing icons.
    /// </summary>
    internal static class Better130Optimizer
    {
        private static Assembly _attachedBetterAsm;
        private static float _nextDiscoverTime;

        // Cached reflection handle to MainMenuPage._ui (from EMSkinCore)
        private static FieldInfo _menuUiField;
        private static IntPtr _lastMenuUiPtr = IntPtr.Zero;
        private static bool _lastMenuUiActive;
        private static float _nextMenuRefreshTime;

        // Cached reflection handles for BadgeColours / Theme.TeamColour
        private static Func<string, uint> _directTeamColour;
        private static readonly Dictionary<string, uint> _teamColorCache = new(256, StringComparer.Ordinal);
        private static readonly Dictionary<string, uint> _tableColorCache = new(256, StringComparer.Ordinal);
        private static float _colorCacheExpireTime;

        // IconRecovery throttle
        private static float _nextIconRecoveryTime;

        public static bool IsAttached => _attachedBetterAsm != null;

        public static void ResetState()
        {
            _lastMenuUiPtr = IntPtr.Zero;
            _lastMenuUiActive = false;
            _nextMenuRefreshTime = 0f;
            _nextIconRecoveryTime = 0f;
            InvalidateColorCaches();
        }

        public static void InvalidateColorCaches()
        {
            lock (_teamColorCache)
            {
                _teamColorCache.Clear();
            }
            lock (_tableColorCache)
            {
                _tableColorCache.Clear();
            }
            _colorCacheExpireTime = 0f;
        }

        public static void OnCoreAttached(Assembly coreAsm)
        {
            _menuUiField = null;
            _directTeamColour = null;
            InvalidateColorCaches();

            if (coreAsm == null)
                return;

            try
            {
                var menuPage = coreAsm.GetType("MainMenuPage", false);
                if (menuPage != null)
                {
                    _menuUiField = menuPage.GetField("_ui", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }

                var theme = coreAsm.GetType("Theme", false);
                if (theme != null)
                {
                    var tc = theme.GetMethod("TeamColour", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                    if (tc != null && tc.ReturnType == typeof(uint))
                    {
                        _directTeamColour = (Func<string, uint>)Delegate.CreateDelegate(typeof(Func<string, uint>), tc);
                    }
                }
            }
            catch
            {
                // Safe fallback if EMSkinCore layout changes
            }
        }

        public static void PollAndAttach(Harmony harmony)
        {
            if (_attachedBetterAsm != null || !Plugin.CfgOptimizeIamgriBetter.Value)
                return;

            float now = Time.unscaledTime;
            if (now < _nextDiscoverTime)
                return;
            _nextDiscoverTime = now + 2.0f;

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = assemblies.Length - 1; i >= 0; i--)
                {
                    var asm = assemblies[i];
                    if (string.Equals(asm.GetName().Name, "IamgriBetter", StringComparison.OrdinalIgnoreCase))
                    {
                        AttachToBetter(harmony, asm);
                        return;
                    }
                }
            }
            catch
            {
                // Ignore transient reflection errors during plugin load
            }
        }

        private static void AttachToBetter(Harmony harmony, Assembly betterAsm)
        {
            _attachedBetterAsm = betterAsm;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            int patched = 0;

            try
            {
                // 1. Throttle MenuPresentation.Refresh (stops per-frame Canvas & TMP rebuild storm)
                var menuPres = betterAsm.GetType("IamgriBetter.History.MenuPresentation", false);
                if (menuPres != null)
                {
                    var refresh = menuPres.GetMethod("Refresh", flags);
                    if (refresh != null)
                    {
                        harmony.Patch(refresh,
                            prefix: new HarmonyMethod(typeof(Better130Optimizer), nameof(MenuRefresh_Prefix))
                            {
                                priority = Priority.First
                            });
                        patched++;
                    }

                    // 2. Cache MenuPresentation.TeamColor & TableColor (avoids per-row DataTeams + Academy IL2CPP calls)
                    var teamColor = menuPres.GetMethod("TeamColor", flags);
                    if (teamColor != null)
                    {
                        harmony.Patch(teamColor,
                            prefix: new HarmonyMethod(typeof(Better130Optimizer), nameof(TeamColor_Prefix))
                            {
                                priority = Priority.First
                            },
                            postfix: new HarmonyMethod(typeof(Better130Optimizer), nameof(TeamColor_Postfix)));
                        patched++;
                    }

                    var tableColor = menuPres.GetMethod("TableColor", flags);
                    if (tableColor != null)
                    {
                        harmony.Patch(tableColor,
                            prefix: new HarmonyMethod(typeof(Better130Optimizer), nameof(TableColor_Prefix))
                            {
                                priority = Priority.First
                            },
                            postfix: new HarmonyMethod(typeof(Better130Optimizer), nameof(TableColor_Postfix)));
                        patched++;
                    }
                }

                // 3. Optimize BadgeColours.BeforeGet (replaces per-badge reflection EmSkinUi.Call with direct cached delegate)
                var badgeCols = betterAsm.GetType("IamgriBetter.History.BadgeColours", false);
                if (badgeCols != null)
                {
                    var beforeGet = badgeCols.GetMethod("BeforeGet", flags);
                    if (beforeGet != null)
                    {
                        harmony.Patch(beforeGet,
                            prefix: new HarmonyMethod(typeof(Better130Optimizer), nameof(BadgeBeforeGet_Prefix))
                            {
                                priority = Priority.First
                            });
                        patched++;
                    }

                    var inv = badgeCols.GetMethod("Invalidate", flags);
                    if (inv != null)
                    {
                        harmony.Patch(inv, postfix: new HarmonyMethod(typeof(Better130Optimizer), nameof(OnColorInvalidated)));
                    }
                }

                // Invalidate color caches immediately whenever the user saves custom team/academy colors
                var clubId = betterAsm.GetType("IamgriBetter.History.ClubIdentity", false);
                if (clubId != null)
                {
                    var save = clubId.GetMethod("Save", flags);
                    if (save != null)
                    {
                        harmony.Patch(save, postfix: new HarmonyMethod(typeof(Better130Optimizer), nameof(OnColorInvalidated)));
                    }
                }

                // 4. Throttle IconRecovery.BeforeTick (avoids per-tick LINQ ToArray + 3x reflection per icon)
                var iconRec = betterAsm.GetType("IamgriBetter.History.IconRecovery", false);
                if (iconRec != null)
                {
                    var beforeTick = iconRec.GetMethod("BeforeTick", flags);
                    if (beforeTick != null)
                    {
                        harmony.Patch(beforeTick,
                            prefix: new HarmonyMethod(typeof(Better130Optimizer), nameof(IconRecovery_Prefix))
                            {
                                priority = Priority.First
                            });
                        patched++;
                    }
                }

                Plugin.Logger.LogMessage(
                    $"[Better130Optimizer] Attached to IamgriBetter {betterAsm.GetName().Version} ({patched} hot-path optimizations active).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Better130Optimizer] Attach warning: {ex.Message}");
            }
        }

        private static bool MenuRefresh_Prefix()
        {
            if (!Plugin.CfgOptimizeIamgriBetter.Value)
                return true;

            try
            {
                float now = Time.unscaledTime;
                IntPtr uiPtr = IntPtr.Zero;
                bool active = false;

                if (_menuUiField != null && _menuUiField.GetValue(null) is GameObject go && go != null)
                {
                    uiPtr = go.Pointer;
                    active = go.activeInHierarchy;
                }

                // Always let Refresh run immediately if menu object changed, just became active, or safety timer elapsed
                if (uiPtr != _lastMenuUiPtr || active != _lastMenuUiActive || now >= _nextMenuRefreshTime)
                {
                    _lastMenuUiPtr = uiPtr;
                    _lastMenuUiActive = active;
                    _nextMenuRefreshTime = now + (active ? 1.0f : 2.0f);
                    return true;
                }

                Interlocked.Increment(ref PerfTelemetry.BetterMenuRefreshesSkipped);
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool BadgeBeforeGet_Prefix(string __0, ref uint __2)
        {
            if (!Plugin.CfgOptimizeIamgriBetter.Value || string.IsNullOrEmpty(__0))
                return true;

            // Only bypass reflection when we have a direct bound delegate to EMSkinCore.Theme.TeamColour
            var fn = _directTeamColour;
            if (fn == null)
                return true;

            try
            {
                CheckColorCacheExpiry();
                lock (_teamColorCache)
                {
                    if (_teamColorCache.TryGetValue(__0, out uint cached))
                    {
                        __2 = cached;
                        Interlocked.Increment(ref PerfTelemetry.BetterColorCacheHits);
                        return false;
                    }
                }

                uint resolved = fn(__0);
                __2 = resolved;
                lock (_teamColorCache)
                {
                    if (_teamColorCache.Count > 2048)
                        _teamColorCache.Clear();
                    _teamColorCache[__0] = resolved;
                }
                Interlocked.Increment(ref PerfTelemetry.BetterColorCacheHits);
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool TeamColor_Prefix(string __0, ref uint __1)
        {
            if (!Plugin.CfgOptimizeIamgriBetter.Value || string.IsNullOrEmpty(__0))
                return true;

            CheckColorCacheExpiry();
            lock (_teamColorCache)
            {
                if (_teamColorCache.TryGetValue(__0, out uint cached))
                {
                    __1 = cached;
                    Interlocked.Increment(ref PerfTelemetry.BetterColorCacheHits);
                    return false;
                }
            }
            return true;
        }

        private static void TeamColor_Postfix(string __0, uint __1)
        {
            if (!Plugin.CfgOptimizeIamgriBetter.Value || string.IsNullOrEmpty(__0))
                return;

            lock (_teamColorCache)
            {
                if (_teamColorCache.Count > 2048)
                    _teamColorCache.Clear();
                _teamColorCache[__0] = __1;
            }
        }

        private static bool TableColor_Prefix(string __0, ref uint __1)
        {
            if (!Plugin.CfgOptimizeIamgriBetter.Value || string.IsNullOrEmpty(__0))
                return true;

            CheckColorCacheExpiry();
            lock (_tableColorCache)
            {
                if (_tableColorCache.TryGetValue(__0, out uint cached))
                {
                    __1 = cached;
                    Interlocked.Increment(ref PerfTelemetry.BetterColorCacheHits);
                    return false;
                }
            }
            return true;
        }

        private static void TableColor_Postfix(string __0, uint __1)
        {
            if (!Plugin.CfgOptimizeIamgriBetter.Value || string.IsNullOrEmpty(__0))
                return;

            lock (_tableColorCache)
            {
                if (_tableColorCache.Count > 2048)
                    _tableColorCache.Clear();
                _tableColorCache[__0] = __1;
            }
        }

        private static bool IconRecovery_Prefix()
        {
            if (!Plugin.CfgOptimizeIamgriBetter.Value)
                return true;

            float now = Time.unscaledTime;
            if (now < _nextIconRecoveryTime)
            {
                Interlocked.Increment(ref PerfTelemetry.BetterIconChecksSkipped);
                return false;
            }

            _nextIconRecoveryTime = now + 0.75f;
            return true;
        }

        private static void OnColorInvalidated()
        {
            InvalidateColorCaches();
        }

        private static void CheckColorCacheExpiry()
        {
            float now = Time.unscaledTime;
            if (now >= _colorCacheExpireTime)
            {
                InvalidateColorCaches();
                _colorCacheExpireTime = now + 15.0f;
            }
        }
    }
}
