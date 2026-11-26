using System;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using HarmonyLib;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Coalesces redundant back-to-back 3-4s full saves after quick matches (caused by IamgriBetter's
    /// MatchCheckpoint overriding EMSkinCore.Autosave.SavePre) while ALWAYS preserving:
    /// - manual saves
    /// - exit-to-menu saves
    /// - periodic AutosaveEveryDays saves
    /// - after-match saves whenever enough real time has passed since the last completed save.
    /// </summary>
    internal static class SmartAutosave
    {
        private static FieldInfo _fInMatchEnd;
        private static FieldInfo _fFinishing;
        private static FieldInfo _fOnSaveThread;
        private static FieldInfo _fEveryDays;
        private static FieldInfo _fDue;
        private static FieldInfo _fRunning;

        private static long _lastSaveTicksUtc;
        private static int _lastSaveGameDayKey = -1;
        private static string _lastSaveSlot = "";

        public static bool IsSavingNow
        {
            get
            {
                try
                {
                    if (_fRunning != null && _fRunning.GetValue(null) is bool r && r)
                        return true;
                    if (_fDue != null && _fDue.GetValue(null) is bool d && d)
                        return true;
                }
                catch { }
                return false;
            }
        }

        public static void ResetState()
        {
            Interlocked.Exchange(ref _lastSaveTicksUtc, 0);
            _lastSaveGameDayKey = -1;
            _lastSaveSlot = "";
        }

        public static void AttachToCore(Harmony coreHarmony, Assembly coreAsm)
        {
            try
            {
                var autosaveType = coreAsm.GetType("Autosave", false);
                if (autosaveType == null)
                {
                    Plugin.Logger.LogWarning("[SmartAutosave] EMSkinCore.Autosave not found.");
                    return;
                }

                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _fInMatchEnd = autosaveType.GetField("_inMatchEnd", flags);
                _fFinishing = autosaveType.GetField("_finishing", flags);
                _fOnSaveThread = autosaveType.GetField("_onSaveThread", flags);
                _fEveryDays = autosaveType.GetField("EveryDays", flags);
                _fDue = autosaveType.GetField("_due", flags);
                _fRunning = autosaveType.GetField("_running", flags);

                var savePre = autosaveType.GetMethod("SavePre", flags);
                if (savePre == null)
                {
                    Plugin.Logger.LogWarning("[SmartAutosave] EMSkinCore.Autosave.SavePre not found.");
                    return;
                }

                // Run BEFORE IamgriBetter.History.ProfileEnhancements.MatchCheckpoint
                coreHarmony.Patch(
                    savePre,
                    prefix: new HarmonyMethod(typeof(SmartAutosave), nameof(SavePre_Prefix))
                    {
                        priority = Priority.First
                    });

                var finish = autosaveType.GetMethod("Finish", flags);
                if (finish != null)
                {
                    coreHarmony.Patch(
                        finish,
                        postfix: new HarmonyMethod(typeof(SmartAutosave), nameof(OnSaveCompleted)));
                }

                // Also track native SaveWriter.Write completions (covers manual / menu / direct saves)
                var writeMethod = AccessTools.Method(typeof(SaveWriter), nameof(SaveWriter.Write));
                if (writeMethod != null)
                {
                    coreHarmony.Patch(
                        writeMethod,
                        postfix: new HarmonyMethod(typeof(SmartAutosave), nameof(OnSaveCompleted)));
                }

                Plugin.Logger.LogMessage($"[SmartAutosave] Hooked EMSkinCore.Autosave (min interval between match saves: {Plugin.CfgMinSecondsBetweenMatchSaves.Value}s).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[SmartAutosave] Attach failed: {ex}");
            }
        }

        private static void OnSaveCompleted()
        {
            try
            {
                Interlocked.Exchange(ref _lastSaveTicksUtc, DateTime.UtcNow.Ticks);
                _lastSaveGameDayKey = CurrentGameDayKey();
                _lastSaveSlot = SlotController.CurrentSlot ?? "";
            }
            catch
            {
            }
        }

        private static int CurrentGameDayKey()
        {
            try
            {
                var d = GlobalValues.CurrentDate;
                return d.Year * 1000 + d.DayOfYear;
            }
            catch
            {
                return -1;
            }
        }

        private static bool SavePre_Prefix(ref bool __result)
        {
            try
            {
                if (!Plugin.CfgSmartAutosave.Value)
                    return true;

                bool finishing = _fFinishing != null && _fFinishing.GetValue(null) is bool b1 && b1;
                bool onSaveThread = _fOnSaveThread != null && _fOnSaveThread.GetValue(null) is bool b2 && b2;
                if (finishing || onSaveThread)
                    return true;

                int inMatchEnd = _fInMatchEnd != null && _fInMatchEnd.GetValue(null) is int m ? m : 0;
                if (inMatchEnd <= 0)
                {
                    // Not an after-match save (manual / menu / periodic day save) — NEVER touch!
                    Interlocked.Increment(ref PerfTelemetry.AutosavesAllowed);
                    return true;
                }

                int everyDays = 7;
                if (_fEveryDays?.GetValue(null) is ConfigEntry<int> ce)
                    everyDays = ce.Value;

                if (everyDays <= 1)
                {
                    // User explicitly configured save-every-day
                    Interlocked.Increment(ref PerfTelemetry.AutosavesAllowed);
                    return true;
                }

                int minSeconds = Plugin.CfgMinSecondsBetweenMatchSaves.Value;
                if (minSeconds <= 0)
                {
                    // Respect iamgri's AutosaveEveryDays strictly for after-match saves
                    __result = false;
                    Interlocked.Increment(ref PerfTelemetry.AutosavesCoalesced);
                    return false;
                }

                string slot = SlotController.CurrentSlot ?? "";
                long lastTicks = Interlocked.Read(ref _lastSaveTicksUtc);
                double secondsSinceLastSave = lastTicks > 0
                    ? (DateTime.UtcNow.Ticks - lastTicks) / (double)TimeSpan.TicksPerSecond
                    : double.MaxValue;

                int dayKey = CurrentGameDayKey();
                bool sameSlot = string.Equals(slot, _lastSaveSlot, StringComparison.Ordinal);

                // Skip redundant after-match save if a save already ran recently on this slot
                if (sameSlot && (secondsSinceLastSave < minSeconds || dayKey == _lastSaveGameDayKey))
                {
                    __result = false;
                    Interlocked.Increment(ref PerfTelemetry.AutosavesCoalesced);
                    return false;
                }

                // Allow this after-match save (updates timer once SaveWriter/Finish runs)
                Interlocked.Increment(ref PerfTelemetry.AutosavesAllowed);
                return true;
            }
            catch
            {
                return true;
            }
        }
    }
}
