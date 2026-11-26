using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Caches EMSkinCore.AiIdentity._pool (the 88-bloc scan of all ~6,320 players taking 60-131 ms/day)
    /// across quiet days when NO world mutation occurred. Immediately invalidates on:
    /// - any transfer/offer/signing (DataOffers.OfferActivityVersion or ContractTransfers.Count or Pricelist.Count change)
    /// - any player/team count change (regens, retirements, new career)
    /// - weekly growth rollover (DayOfYear / 7 changes)
    /// - slot change or max cache age (CfgAiPoolMaxCacheDays).
    /// </summary>
    internal static class AiPoolCache
    {
        private static FieldInfo _fPool;
        private static FieldInfo _fDay;

        private static string _cachedSlot = "";
        private static long _cachedWorldSig = -1;
        private static int _cachedBuildDayKey = -1;
        private static object _stashedPool;

        public static void AttachToCore(Harmony coreHarmony, Assembly coreAsm)
        {
            try
            {
                var aiType = coreAsm.GetType("AiIdentity", false);
                if (aiType == null)
                {
                    Plugin.Logger.LogWarning("[AiPoolCache] EMSkinCore.AiIdentity not found.");
                    return;
                }

                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _fPool = aiType.GetField("_pool", flags);
                _fDay = aiType.GetField("_day", flags);

                var newDay = aiType.GetMethod("NewDay", flags);
                var pool = aiType.GetMethod("Pool", flags);

                if (_fPool == null || newDay == null)
                {
                    Plugin.Logger.LogWarning("[AiPoolCache] AiIdentity._pool or NewDay not found.");
                    return;
                }

                coreHarmony.Patch(
                    newDay,
                    prefix: new HarmonyMethod(typeof(AiPoolCache), nameof(NewDay_Prefix)),
                    postfix: new HarmonyMethod(typeof(AiPoolCache), nameof(NewDay_Postfix)));

                if (pool != null)
                {
                    coreHarmony.Patch(
                        pool,
                        prefix: new HarmonyMethod(typeof(AiPoolCache), nameof(Pool_Prefix)));
                }

                ResetCache();
                Plugin.Logger.LogMessage($"[AiPoolCache] Hooked EMSkinCore.AiIdentity (max quiet-day reuse: {Plugin.CfgAiPoolMaxCacheDays.Value} days).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[AiPoolCache] Attach failed: {ex}");
            }
        }

        public static void ResetState() => ResetCache();

        private static void ResetCache()
        {
            _cachedSlot = "";
            _cachedWorldSig = -1;
            _cachedBuildDayKey = -1;
            _stashedPool = null;
        }

        private static bool TryComputeWorldSignature(out string slot, out int dayKey, out long sig)
        {
            slot = "";
            dayKey = -1;
            sig = -1;

            try
            {
                slot = SlotController.CurrentSlot ?? "";
                if (string.IsNullOrEmpty(slot))
                    return false;

                var d = GlobalValues.CurrentDate;
                dayKey = d.Year * 1000 + d.DayOfYear;
                int weekBucket = d.Year * 60 + (d.DayOfYear / 7);

                var players = DataPlayers.List?.TryCast<Il2CppSystem.Collections.Generic.List<DataPlayer>>();
                var teams = DataTeams.List?.TryCast<Il2CppSystem.Collections.Generic.List<DataTeam>>();
                int pCount = players != null ? players.Count : 0;
                int tCount = teams != null ? teams.Count : 0;
                if (pCount == 0 || tCount == 0)
                    return false;

                long offerVer = DataOffers.OfferActivityVersion;
                int ctCount = DataOffers.ContractTransfers != null ? DataOffers.ContractTransfers.Count : 0;
                int plCount = DataOffers.Pricelist != null ? DataOffers.Pricelist.Count : 0;

                unchecked
                {
                    long h = 19;
                    h = h * 31 + weekBucket;
                    h = h * 31 + pCount;
                    h = h * 31 + tCount;
                    h = h * 31 + offerVer;
                    h = h * 31 + ctCount;
                    h = h * 31 + plCount;
                    sig = h;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void NewDay_Prefix(out object __state)
        {
            __state = null;
            try
            {
                if (!Plugin.CfgAiPoolCache.Value || _fPool == null)
                    return;

                // Fast exit: AiIdentity.NewDay() is called frequently on the same day and returns immediately
                // when _day == today. Only stash/restore on an actual day rollover!
                if (_fDay != null && _fDay.GetValue(null) is DateTime prevDay)
                {
                    var gd = GlobalValues.CurrentDate;
                    if (prevDay.Year == gd.Year && prevDay.Month == gd.Month && prevDay.Day == gd.Day)
                        return;
                }

                var currentPool = _fPool.GetValue(null);
                if (currentPool == null)
                    return;

                if (!TryComputeWorldSignature(out string slot, out int dayKey, out long sig))
                    return;

                int maxAge = Math.Max(1, Plugin.CfgAiPoolMaxCacheDays.Value);
                bool sameSlot = string.Equals(slot, _cachedSlot, StringComparison.Ordinal);
                int ageDays = dayKey - _cachedBuildDayKey;

                if (sameSlot
                    && sig == _cachedWorldSig
                    && ageDays >= 0
                    && ageDays < maxAge)
                {
                    __state = currentPool;
                }
            }
            catch
            {
                __state = null;
            }
        }

        private static void NewDay_Postfix(object __state)
        {
            try
            {
                if (__state != null && _fPool != null)
                {
                    // Restore unchanged pool after NewDay cleared day-specific tables
                    _fPool.SetValue(null, __state);
                    _stashedPool = __state;
                    Interlocked.Increment(ref PerfTelemetry.AiPoolCacheHits);
                }
                else
                {
                    _stashedPool = null;
                }
            }
            catch { }
        }

        private static void Pool_Prefix()
        {
            try
            {
                if (!Plugin.CfgAiPoolCache.Value || _fPool == null) return;
                if (_fPool.GetValue(null) != null)
                    return; // Already cached; Pool() returns immediately

                // Pool() is about to do a fresh rebuild — snapshot current world signature
                if (TryComputeWorldSignature(out string slot, out int dayKey, out long sig))
                {
                    _cachedSlot = slot;
                    _cachedBuildDayKey = dayKey;
                    _cachedWorldSig = sig;
                }
                else
                {
                    ResetCache();
                }

                Interlocked.Increment(ref PerfTelemetry.AiPoolCacheMisses);
            }
            catch { }
        }
    }
}
