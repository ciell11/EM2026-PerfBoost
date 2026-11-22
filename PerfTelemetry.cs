using System;
using System.Text;
using System.Threading;

namespace EM2026PerfBoost
{
    internal static class PerfTelemetry
    {
        public static long MatchSnapshotsTaken;
        public static long MatchSnapshotRetries;

        public static long AutosavesCoalesced;
        public static long AutosavesAllowed;

        public static long InboxRenewsSkipped;
        public static long InboxRenewsExecuted;
        public static long TalkRenewsSkipped;
        public static long TalkRenewsExecuted;
        public static long ShopHiddenSkipped;
        public static long BasecampHiddenSkipped;
        public static long RaycastersDisabled;

        public static long[] SubPassMs = new long[4];
        public static long[] SubPassCalls = new long[4];

        public static void AddSubPass(int idx, long ms)
        {
            if (idx < 0 || idx >= SubPassMs.Length || ms < 0) return;
            Interlocked.Increment(ref SubPassCalls[idx]);
            Interlocked.Add(ref SubPassMs[idx], ms);
        }

        public static long AiPoolCacheHits;
        public static long AiPoolCacheMisses;

        public static long ConsoleLinesFiltered;
        public static long DiskLinesFiltered;

        public static long SwitchCount;
        public static long SwitchTotalMs;
        public static long SwitchMaxMs;
        public static string SwitchMaxTarget = "-";

        public static long UiFramesCount;
        public static double UiFramesTotalMs;
        public static double UiWorstFrameMs;
        public static long UiFramesOver50Ms;

        public static long FpsEnforcements;
        public static long DayPausesAccelerated;
        public static long DaysAdvanced;
        public static long DaysTotalMs;
        public static long DaysMaxMs;

        public static long MemoryCleanups;
        public static long MemoryFreedMbTotal;
        public static long MemoryLastHeapMb;
        public static long SlotCacheFlushes;

        public static void RecordDayAdvance(long ms)
        {
            Interlocked.Increment(ref DaysAdvanced);
            Interlocked.Add(ref DaysTotalMs, ms);
            if (ms > Interlocked.Read(ref DaysMaxMs))
                Interlocked.Exchange(ref DaysMaxMs, ms);
        }

        public static void RecordMemoryGuardCleanup(long freedMb, long durationMs, long heapAfterMb)
        {
            Interlocked.Increment(ref MemoryCleanups);
            Interlocked.Add(ref MemoryFreedMbTotal, freedMb);
            Interlocked.Exchange(ref MemoryLastHeapMb, heapAfterMb);
        }

        public static void RecordSwitch(string target, long ms)
        {
            Interlocked.Increment(ref SwitchCount);
            Interlocked.Add(ref SwitchTotalMs, ms);
            if (ms > Interlocked.Read(ref SwitchMaxMs))
            {
                Interlocked.Exchange(ref SwitchMaxMs, ms);
                SwitchMaxTarget = target ?? "?";
            }
        }

        public static void RecordUiFrame(double ms)
        {
            if (ms <= 0 || ms > 5000) return;
            UiFramesCount++;
            UiFramesTotalMs += ms;
            if (ms > UiWorstFrameMs) UiWorstFrameMs = ms;
            if (ms >= 50.0) UiFramesOver50Ms++;
        }

        public static string BuildReport()
        {
            var sb = new StringBuilder(512);
            sb.AppendLine("========== EM2026 PerfBoost Telemetry ==========");

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            long sw = Interlocked.Read(ref SwitchCount);
            long swTot = Interlocked.Read(ref SwitchTotalMs);
            long swMax = Interlocked.Read(ref SwitchMaxMs);
            double swAvg = sw > 0 ? (double)swTot / sw : 0;
            sb.AppendLine(string.Format(inv, "[Screens] Window Show: {0} calls, avg {1:0.0} ms, max {2} ms ({3})", sw, swAvg, swMax, SwitchMaxTarget));

            if (UiFramesCount > 0)
            {
                double avgFrame = UiFramesTotalMs / UiFramesCount;
                double avgFps = avgFrame > 0.01 ? 1000.0 / avgFrame : 0;
                sb.AppendLine(string.Format(inv, "[UI FPS] {0} frames, avg {1:0.0} FPS ({2:0.0} ms), worst {3:0.0} ms, >50ms spikes: {4}", UiFramesCount, avgFps, avgFrame, UiWorstFrameMs, UiFramesOver50Ms));
            }

            sb.AppendLine(string.Format(inv, "[Autosave] Coalesced after-match saves: {0} (est. ~{1:0.0}s saved), allowed: {2}", Interlocked.Read(ref AutosavesCoalesced), Interlocked.Read(ref AutosavesCoalesced) * 3.2, Interlocked.Read(ref AutosavesAllowed)));
            sb.AppendLine($"[Hidden UI] Inbox {Interlocked.Read(ref InboxRenewsSkipped)}/{Interlocked.Read(ref InboxRenewsSkipped) + Interlocked.Read(ref InboxRenewsExecuted)} | Talk {Interlocked.Read(ref TalkRenewsSkipped)}/{Interlocked.Read(ref TalkRenewsSkipped) + Interlocked.Read(ref TalkRenewsExecuted)} | Shop {Interlocked.Read(ref ShopHiddenSkipped)} | Basecamp {Interlocked.Read(ref BasecampHiddenSkipped)} | Raycasters {Interlocked.Read(ref RaycastersDisabled)}");
            sb.AppendLine($"[AiIdentity.Pool] Cache hits: {Interlocked.Read(ref AiPoolCacheHits)}, rebuilds: {Interlocked.Read(ref AiPoolCacheMisses)}");

            long days = Interlocked.Read(ref DaysAdvanced);
            double dayAvg = days > 0 ? (double)Interlocked.Read(ref DaysTotalMs) / days : 0;
            sb.AppendLine(string.Format(inv, "[Days] Advanced: {0}, avg {1:0.0} ms, max {2} ms | Pauses accelerated: {3}", days, dayAvg, Interlocked.Read(ref DaysMaxMs), Interlocked.Read(ref DayPausesAccelerated)));
            sb.AppendLine($"[ContractEngine] DailyAi: {Interlocked.Read(ref SubPassMs[0])}ms ({Interlocked.Read(ref SubPassCalls[0])}x) | Maint: {Interlocked.Read(ref SubPassMs[1])}ms ({Interlocked.Read(ref SubPassCalls[1])}x) | AITransfers: {Interlocked.Read(ref SubPassMs[2])}ms ({Interlocked.Read(ref SubPassCalls[2])}x) | Listed: {Interlocked.Read(ref SubPassMs[3])}ms ({Interlocked.Read(ref SubPassCalls[3])}x)");

            sb.AppendLine($"[FPS] Target: {Plugin.CfgTargetFps.Value} (VSyncOff={Plugin.CfgDisableVSync.Value}), enforcements: {Interlocked.Read(ref FpsEnforcements)}");
            sb.AppendLine($"[LongCareer] Idle cleanups: {Interlocked.Read(ref MemoryCleanups)} (freed {Interlocked.Read(ref MemoryFreedMbTotal)} MB, last heap {Interlocked.Read(ref MemoryLastHeapMb)} MB) | Slot cache flushes: {Interlocked.Read(ref SlotCacheFlushes)}");
            sb.AppendLine($"[Stability] ProcessAIMatches safe snapshots: {Interlocked.Read(ref MatchSnapshotsTaken)} (concurrent retries: {Interlocked.Read(ref MatchSnapshotRetries)})");
            sb.AppendLine($"[Logging] Console spam filtered: {Interlocked.Read(ref ConsoleLinesFiltered)}, Disk filtered: {Interlocked.Read(ref DiskLinesFiltered)}");
            sb.Append("================================================");
            return sb.ToString();
        }
    }
}
