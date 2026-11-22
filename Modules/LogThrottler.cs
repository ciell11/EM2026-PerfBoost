using System;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// 1. Eliminates synchronous Win32 WriteConsole stalls caused by thousands of verbose Info/Debug lines
    ///    from iamgri mod and Iamgri Better during day ticks and UI clicks.
    /// 2. Disables expensive native stack-trace walking on Unity's Debug.Log / Debug.LogWarning calls
    ///    (keeping Error, Assert, and Exception stack traces 100% enabled!) to prevent Player.log freeze storms.
    /// </summary>
    internal static class LogThrottler
    {
        public static void Install(Harmony harmony)
        {
            try
            {
                if (Plugin.CfgStripUnityLogStackTraces.Value)
                {
                    try
                    {
                        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                        Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
                    }
                    catch { }
                }

                var consoleLogEvent = AccessTools.Method(
                    typeof(ConsoleLogListener),
                    nameof(ConsoleLogListener.LogEvent),
                    new[] { typeof(object), typeof(LogEventArgs) });

                if (consoleLogEvent != null)
                {
                    harmony.Patch(
                        consoleLogEvent,
                        prefix: new HarmonyMethod(typeof(LogThrottler), nameof(ConsoleLogEvent_Prefix))
                        {
                            priority = Priority.First
                        });
                }

                var diskLogEvent = AccessTools.Method(
                    typeof(DiskLogListener),
                    nameof(DiskLogListener.LogEvent),
                    new[] { typeof(object), typeof(LogEventArgs) });

                if (diskLogEvent != null)
                {
                    harmony.Patch(
                        diskLogEvent,
                        prefix: new HarmonyMethod(typeof(LogThrottler), nameof(DiskLogEvent_Prefix))
                        {
                            priority = Priority.First
                        });
                }

                Plugin.Logger.LogMessage(
                    $"[LogThrottler] Active (QuietConsoleInfo={Plugin.CfgQuietConsoleInfo.Value}, QuietDiskInfo={Plugin.CfgQuietDiskInfo.Value}, StripUnityLogStackTraces={Plugin.CfgStripUnityLogStackTraces.Value}).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[LogThrottler] Failed to patch log listeners: {ex.Message}");
            }
        }

        private static bool ShouldFilter(LogEventArgs args)
        {
            if (args == null) return false;

            var level = args.Level;
            if (level != LogLevel.Info && level != LogLevel.Debug)
                return false;

            string src = args.Source?.SourceName;
            if (string.IsNullOrEmpty(src))
                return false;

            return string.Equals(src, "iamgri mod", StringComparison.Ordinal)
                || string.Equals(src, "Iamgri Better", StringComparison.Ordinal);
        }

        private static bool ConsoleLogEvent_Prefix(LogEventArgs __1)
        {
            if (Plugin.CfgQuietConsoleInfo.Value && ShouldFilter(__1))
            {
                Interlocked.Increment(ref PerfTelemetry.ConsoleLinesFiltered);
                return false;
            }
            return true;
        }

        private static bool DiskLogEvent_Prefix(LogEventArgs __1)
        {
            if (Plugin.CfgQuietDiskInfo.Value && ShouldFilter(__1))
            {
                Interlocked.Increment(ref PerfTelemetry.DiskLinesFiltered);
                return false;
            }
            return true;
        }
    }
}
