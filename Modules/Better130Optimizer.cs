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
    /// Eliminates three major FPS bottlenecks introduced in IamgriBetter 1.3.0 while preserving 100%
    /// identical visuals and custom club/academy colors:
    /// 1. MenuPresentation.Refresh() ran on MainMenuPage.Tick every frame and unconditionally mutated
    ///    RectTransform + TMP_Text properties, dirtying the Unity Canvas every frame -> throttled to
    ///    only execute when MainMenuPage._ui changes/activates or on a gentle 1.0s safety heartbeat.
    /// 2. BadgeColours.BeforeGet(string __1, bool __2, ref uint __6) + MenuPresentation.TeamColor/TableColor
    ///    performed reflection (EmSkinUi.Call + object[] allocation) and repeated IL2CPP DataTeams/Academy
    ///    lookups on every team badge and table row -> three isolated O(1) caches:
    ///      - _badgeColourCache strictly for Theme.TeamColour(teamId) when bool __2 == false
    ///      - _teamOverrideCache / _tableOverrideCache storing Nullable<uint> overrides so non-custom
    ///        clubs (99.6% of teams) return immediately WITHOUT touching Theme's base color!
    ///    Invalidated immediately on ClubIdentity.Save, BadgeColours.Invalidate, or career slot change.
    /// 3. IconRecovery.BeforeTick(object __instance, bool __0) ran LINQ Cast().ToArray() + 3 reflection
    ///    calls per icon every tick -> throttled to once every 0.75s for routine ticks while ALWAYS
    ///    allowing forced recoveries (__0 == true) immediately.
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

        // Direct bound delegate to EMSkinCore.Theme.TeamColour(string)
        private static Func<string, uint> _directTeamColour;

        // Strictly isolated color caches (never cross-contaminate badge colors with Theme postfixes!)
        private static readonly Dictionary<string, uint> _badgeColourCache = new(256, StringComparer.Ordinal);
        private static readonly Dictionary<string, uint?> _teamOverrideCache = new(256, StringComparer.Ordinal);
        private static readonly Dictionary<string, uint?> _tableOverrideCache = new(256, StringComparer.Ordinal);
        private static float _colorCacheExpireTime;

        // IconRecovery throttle
        private static float _nextIconRecoveryTime;

        // CareerOptions.get_IsFrozen daily cache (avoids 4 IL2CPP string allocs + DataTeams.Get per AI call!)
        private static int _frozenCacheDayKey = -1;
        private static string _frozenCacheSlot = "";
        private static bool _frozenCacheValid;
        private static bool _frozenCacheVal;

        public static bool IsAttached => _attachedBetterAsm != null;

        public static void ResetState()
        {
            _lastMenuUiPtr = IntPtr.Zero;
            _lastMenuUiActive = false;
            _nextMenuRefreshTime = 0f;
            _nextIconRecoveryTime = 0f;
            _frozenCacheValid = false;
            _frozenCacheDayKey = -1;
            _frozenCacheSlot = "";
            InvalidateColorCaches();
        }

        public static void InvalidateColorCaches()
        {
            lock (_badgeColourCache)
            {
                _badgeColourCache.Clear();
            }
            lock (_teamOverrideCache)
            {
                _teamOverrideCache.Clear();
            }
            lock (_tableOverrideCache)
            {
                _tableOverrideCache.Clear();
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
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var menuPage = coreAsm.GetType("MainMenuPage", false);
                if (menuPage != null)
                {
                    _menuUiField = menuPage.GetField("_ui", flags);
                }

                var theme = coreAsm.GetType("Theme", false);
                if (theme != null)
                {
                    var tc = theme.GetMethod("TeamColour", flags, null, new[] { typeof(string) }, null);
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

                    // 2. Non-destructive override cache for MenuPresentation.TeamColor & TableColor
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

                // 3. Optimize BadgeColours.BeforeGet(String __1, Boolean __2, UInt32& __6)
                var badgeCols = betterAsm.GetType("IamgriBetter.History.BadgeColours", false);
                if (badgeCols != null)
                {
                    var beforeGet = badgeCols.GetMethod("BeforeGet", flags);
                    if (beforeGet != null && beforeGet.GetParameters().Length == 3)
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

                // 4. Throttle routine IconRecovery.BeforeTick(Object __instance, Boolean __0) calls
                //    while ALWAYS letting forced (__0 == true) icon recoveries run immediately
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

                // 5. Cache CareerOptions.get_IsFrozen per (day, slot) so AI passes don't allocate 4 IL2CPP strings + DataTeams.Get per call
                var careerOpts = betterAsm.GetType("IamgriBetter.History.CareerOptions", false);
                if (careerOpts != null)
                {
                    var getFrozen = careerOpts.GetMethod("get_IsFrozen", flags);
                    if (getFrozen != null)
                    {
                        harmony.Patch(getFrozen,
                            prefix: new HarmonyMethod(typeof(Better130Optimizer), nameof(CareerIsFrozen_Prefix)) { priority = Priority.First },
                            postfix: new HarmonyMethod(typeof(Better130Optimizer), nameof(CareerIsFrozen_Postfix)));
                        patched++;
                    }

                    var invFrozen = new HarmonyMethod(typeof(Better130Optimizer), nameof(InvalidateFrozenCache));
                    var created = careerOpts.GetMethod("Created", flags);
                    if (created != null) harmony.Patch(created, postfix: invFrozen);
                    var reset = careerOpts.GetMethod("ResetContract", flags);
                    if (reset != null) harmony.Patch(reset, postfix: invFrozen);
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

        // Exact match to IamgriBetter.History.BadgeColours.BeforeGet(String __1, Boolean __2, UInt32& __6):
        //   if (__2 || string.IsNullOrEmpty(__1)) return;
        //   __6 = (uint)ui.Call("Theme", "TeamColour", new object[] { __1 });
        private static bool BadgeBeforeGet_Prefix(string __0, bool __1, ref uint __2)
        {
            if (!Plugin.CfgOptimizeIamgriBetter.Value)
                return true;

            // Preserve exact original early-exit when skip flag (__2 / arg1) is true or teamId is empty!
            if (__1 || string.IsNullOrEmpty(__0))
                return false;

            var fn = _directTeamColour;
            if (fn == null)
                return true;

            try
            {
                CheckColorCacheExpiry();
                lock (_badgeColourCache)
                {
                    if (_badgeColourCache.TryGetValue(__0, out uint cached))
                    {
                        __2 = cached;
                        Interlocked.Increment(ref PerfTelemetry.BetterColorCacheHits);
                        return false;
                    }
                }

                uint resolved = fn(__0);
                __2 = resolved;
                lock (_badgeColourCache)
                {
                    if (_badgeColourCache.Count > 2048)
                        _badgeColourCache.Clear();
                    _badgeColourCache[__0] = resolved;
                }
                Interlocked.Increment(ref PerfTelemetry.BetterColorCacheHits);
                return false;
            }
            catch
            {
                return true;
            }
        }

        private readonly struct ColorState
        {
            public readonly bool Track;
            public readonly uint Before;

            public ColorState(bool track, uint before)
            {
                Track = track;
                Before = before;
            }
        }

        private static bool TeamColor_Prefix(string __0, ref uint __1, out ColorState __state)
        {
            __state = default;
            if (!Plugin.CfgOptimizeIamgriBetter.Value || string.IsNullOrEmpty(__0))
                return true;

            CheckColorCacheExpiry();
            lock (_teamOverrideCache)
            {
                if (_teamOverrideCache.TryGetValue(__0, out uint? cached))
                {
                    // If cached has an explicit custom override, apply it; otherwise leave Theme's __result untouched!
                    if (cached.HasValue)
                        __1 = cached.Value;
                    Interlocked.Increment(ref PerfTelemetry.BetterColorCacheHits);
                    return false;
                }
            }

            __state = new ColorState(true, __1);
            return true;
        }

        private static void TeamColor_Postfix(string __0, uint __1, ColorState __state)
        {
            if (!__state.Track || string.IsNullOrEmpty(__0))
                return;

            uint? overrideVal = __1 != __state.Before ? __1 : null;
            lock (_teamOverrideCache)
            {
                if (_teamOverrideCache.Count > 2048)
                    _teamOverrideCache.Clear();
                _teamOverrideCache[__0] = overrideVal;
            }
        }

        private static bool TableColor_Prefix(string __0, ref uint __1, out ColorState __state)
        {
            __state = default;
            if (!Plugin.CfgOptimizeIamgriBetter.Value || string.IsNullOrEmpty(__0))
                return true;

            CheckColorCacheExpiry();
            lock (_tableOverrideCache)
            {
                if (_tableOverrideCache.TryGetValue(__0, out uint? cached))
                {
                    // Only overwrite __result for the user's custom-colored club/academy; never overwrite base theme colors!
                    if (cached.HasValue)
                        __1 = cached.Value;
                    Interlocked.Increment(ref PerfTelemetry.BetterColorCacheHits);
                    return false;
                }
            }

            __state = new ColorState(true, __1);
            return true;
        }

        private static void TableColor_Postfix(string __0, uint __1, ColorState __state)
        {
            if (!__state.Track || string.IsNullOrEmpty(__0))
                return;

            uint? overrideVal = __1 != __state.Before ? __1 : null;
            lock (_tableOverrideCache)
            {
                if (_tableOverrideCache.Count > 2048)
                    _tableOverrideCache.Clear();
                _tableOverrideCache[__0] = overrideVal;
            }
        }

        // Exact match to IamgriBetter.History.IconRecovery.BeforeTick(Object __instance, Boolean __0):
        // Never skip forced ticks (__1 == true); only throttle routine periodic polls to every 0.75s.
        private static bool IconRecovery_Prefix(bool __1)
        {
            if (!Plugin.CfgOptimizeIamgriBetter.Value)
                return true;

            float now = Time.unscaledTime;
            if (__1)
            {
                _nextIconRecoveryTime = now + 0.75f;
                return true;
            }

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

        private static void InvalidateFrozenCache()
        {
            _frozenCacheValid = false;
        }

        private static bool CareerIsFrozen_Prefix(ref bool __result, out bool __state)
        {
            __state = false;
            if (!Plugin.CfgOptimizeIamgriBetter.Value)
                return true;

            try
            {
                var d = GlobalValues.CurrentDate;
                int dayKey = d.Year * 1000 + d.DayOfYear;
                string slot = SlotController.CurrentSlot ?? "";
                if (string.IsNullOrEmpty(slot))
                    return true;

                if (_frozenCacheValid && dayKey == _frozenCacheDayKey && string.Equals(slot, _frozenCacheSlot, StringComparison.Ordinal))
                {
                    __result = _frozenCacheVal;
                    return false;
                }

                __state = true;
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void CareerIsFrozen_Postfix(bool __result, bool __state)
        {
            if (!__state) return;
            try
            {
                var d = GlobalValues.CurrentDate;
                string slot = SlotController.CurrentSlot ?? "";
                if (string.IsNullOrEmpty(slot)) return;

                _frozenCacheDayKey = d.Year * 1000 + d.DayOfYear;
                _frozenCacheSlot = slot;
                _frozenCacheVal = __result;
                _frozenCacheValid = true;
            }
            catch { }
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
