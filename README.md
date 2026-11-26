# EM2026 PerfBoost

A lightweight, non-destructive performance, FPS, and long-career stability optimizer for **Esports Manager 2026** (EM2026 ModKit / BepInEx 6 IL2CPP), designed for full compatibility with **iamgri mod** and **Iamgri Better**.

- **Zero gameplay features cut** — all tournaments, simulation models, UI screens, and realism systems stay intact.
- **Zero save format changes** — 100% safe to add or remove mid-career.
- **3D matches untouched by default** — automatically stays passive during 3D match scenes (optional FPS unlock in 3D matches can be enabled in config).
- **Fully configurable** — every optimization can be tuned or toggled individually in `BepInEx\config\local.em2026.perfboost.cfg`.

---

## What It Optimizes

| Module | What It Does |
|---|---|
| **FPS Unlocker** (`FpsUnlocker`) | Unlocks UI frame rate to **`165 FPS`** by default (configurable in `.cfg`, `0` = vanilla, `-1` = uncapped) with VSync toggle **and Windows 1 ms multimedia timer precision (`timeBeginPeriod(1)`)** so `165 FPS` holds rock-solid instead of drifting down to ~145–153 FPS with sleep jitter. |
| **Faster Day Transitions** (`DaySpeedBooster`) | Accelerates the dead inter-day pause (`100x` default vs `20x`, configurable up to `200x`, strictly outside 3D matches and automatically restored before the next day starts) and tracks real day-advance timings in `F9` telemetry. |
| **Balanced RAM & Long-Career Guard** (`LongCareerGuard`) | Keeps RAM in check by running gentle non-blocking `gen0/gen1` idle trims every `512 MB` growth (`120s` cooldown) and async `Resources.UnloadUnusedAssets()` only above `2200 MB` (never during 3D matches, day simulation, or saves). Also flushes all caches cleanly on career/slot switches. |
| **Smart After-Match Autosave** (`SmartAutosave`) | When another mod enables saving after every match, coalesces redundant back-to-back 3–4s full saves within a configurable interval (default `180s`). **Never** skips periodic `AutosaveEveryDays` saves, manual saves, or exit-to-menu saves. |
| **Zero-Freeze Tab Switching** (`HiddenUiSkipper` + `UiFramePacer`) | Exact dirty-check memoization for hidden vanilla `InboxWindow.Renew` and `TalkWindow.Renew` (no longer wastes 150–300 ms rebuilding on quiet day rollovers when no emails/talks changed); skips hidden `ShopWindow_v2` and `Basecamp_v2` rebuilds; disables `GraphicRaycaster` on hidden `UiKit.Unseen` canvases; and caps Unity's incremental GC slice to `2 ms` during tab switches so opening any tab stays stutter-free. |
| **7-Pass AI Transfer & Listing Accelerator** (`FastAiTransfers`) | Prunes dormant/folded and locked clubs from `BuildAITransfersBuckets` & `BuildAIOptimisationBuckets`; slices non-priority lower-tier AI scans (`_scratchOtherTeams`) via a fair rotating round-robin daily budget (`MaxOtherTeamsPerDay = 24`, cycling every club cleanly once per in-game week while `_scratchPriorityTeams` and the player's club scan every day) + deal-outcome cooldown (`EmptyScanSkipDays = 5`) to cut `AITransfers` from **~6.0s/day down to ~0.7s/day starting on Day 1**; and memoizes 13 hot pure `EMSkinCore` C# lookups across **all 7 heavy daily AI passes** with instant mutation invalidation! |
| **AI Identity Pool Cache** (`AiPoolCache`) | Reuses `iamgri mod`'s 88-bloc / ~6,320-player scan (`AiIdentity._pool`, 60–131 ms/day) across quiet days and invalidates **immediately** on any transfer/offer (`OfferActivityVersion`), player/team count change, slot change, or weekly growth rollover. |
| **Thread-Safe AI Matches Fix** (`MatchThreadSafety`) | Takes a defensive thread-safe snapshot of `MatchesEngine.ProcessAIMatches` to prevent `InvalidOperationException: Collection was modified` when background day offloading and third-place decider mods run concurrently. |
| **IamgriBetter 1.3.0+ Optimizer** (`Better130Optimizer`) | Dynamically attaches to **IamgriBetter 1.3.0 / 1.3.0-hotfix** at runtime (zero hard dependency, 10 hot-path optimizations): stops `MenuPresentation.Refresh()` per-frame Canvas/TMP rebuilds, caches `BadgeColours.BeforeGet`, `TeamColor`, `TableColor`, and `CareerOptions.get_IsFrozen` with strictly isolated O(1) caches, and throttles 165×/sec Reflection/IL2CPP-allocating background ticks (`IconRecovery`, `TalentReports`, `CoreBuyoutRecovery`, `CareerOptions`, `ProfileRecovery`). |
| **Log & Stack-Trace Throttler** (`LogThrottler`) | Filters verbose `Info`/`Debug` spam from `iamgri mod`, `Iamgri Better`, `IamgriGrandSlam`, `IamgriThirdPlace`, and `IamgriUpdateFeatures` out of the synchronous Windows console window, and disables native Unity stack-trace walking on `Debug.Log` / `Debug.LogWarning` (keeping `Error`/`Exception` traces 100% enabled) to eliminate `Player.log` freeze storms. |

---

## Compatibility

- **Game:** Esports Manager 2026 (Steam, Unity 6000.5 / IL2CPP)
- **Mod Loader:** [EM2026 ModKit](https://www.nexusmods.com/esportsmanager2026/mods/66) v2.2+ (BepInEx 6 IL2CPP)
- **Mods:** Works standalone, with **iamgri mod 4.1.x**, and with **Iamgri Better 1.1.x – 1.3.0-hotfix**.
  Dynamically attaches to `EMSkinCore` and `IamgriBetter` at runtime with zero compile-time dependency, so it stays compatible across updates and hot-reloads.

---

## Installation & Updating Mid-Career

1. Install **EM2026 ModKit v2.2+** first.
2. Download `EM2026PerfBoost-v1.5.1.zip` from the [**Releases**](../../releases/latest) page.
3. Extract the archive into your **Esports Manager 2026** root folder (next to `EsportsManager.exe`), **overwriting the old `EM2026PerfBoost.dll` if updating**:
   ```text
   Esports Manager 2026\BepInEx\plugins\EM2026PerfBoost\EM2026PerfBoost.dll
   ```
4. Launch the game from Steam and load your existing career — **100% save-safe mid-career** (zero save format changes, existing `.cfg` settings preserved). The config file lives at:
   ```text
   BepInEx\config\local.em2026.perfboost.cfg
   ```

---

## Live Telemetry (`F9`)

Press **`F9`** at any time in-game to print a live performance summary to the BepInEx console and `BepInEx\LogOutput.log`.

*(Example output):*

```text
========== EM2026 PerfBoost Telemetry ==========
[Screens] Window Show: 20 calls, avg 12.6 ms, max 27 ms (Windows)
[UI FPS] 18420 frames, avg 161.4 FPS (6.2 ms), worst 41.8 ms, >50ms spikes: 0
[Autosave] Coalesced after-match saves: 4 (est. ~12.8s saved), allowed: 2
[Hidden UI] Inbox 5/7 | Talk 2/3 | Squad 4 | Staff 3 | Transfers 2 | Shop 1 | Basecamp 1 | Raycasters 4 | Layouts 357
[AiIdentity.Pool] Cache hits: 14, rebuilds: 5
[Days] Advanced: 30, avg 142.5 ms, max 410 ms | Pauses accelerated: 29
[FPS] Target: 165 (VSyncOff=True), enforcements: 2
[LongCareer] Idle cleanups: 1 (freed 612 MB, last heap 1480 MB) | Slot cache flushes: 1
[Stability] ProcessAIMatches safe snapshots: 12 (concurrent retries: 0)
[Logging] Console spam filtered: 1210, Disk filtered: 0
================================================
```

---

## Configuration

All options live in `BepInEx\config\local.em2026.perfboost.cfg`:

| Section | Key | Default | Description |
|---|---|---|---|
| `[FPS]` | `EnableFpsUnlock` | `true` | Enable FPS unlock in UI/manager scenes |
| `[FPS]` | `TargetFps` | `165` | Target FPS cap (`0` = vanilla, `-1` = uncapped, `30–1000` = custom cap) |
| `[FPS]` | `DisableVSync` | `true` | Disable VSync (`vSyncCount = 0`) when FPS unlock is active |
| `[FPS]` | `ApplyIn3DMatch` | `false` | Also apply `TargetFps` / `DisableVSync` inside 3D match scenes |
| `[DaySpeed]` | `FastDayTransitions` | `true` | Accelerate inter-day transition pauses |
| `[DaySpeed]` | `DayPauseSpeed` | `100` | Time scale during inter-day pause (`1–200`, never affects 3D matches) |
| `[LongCareer]` | `EnableMemoryGuard` | `true` | Proactive gentle idle memory trims to prevent multi-GB RAM bloat |
| `[LongCareer]` | `GrowthThresholdMb` | `512` | Heap growth (MB) above baseline before a gentle idle trim triggers |
| `[LongCareer]` | `CleanupCooldownSeconds` | `120` | Minimum real seconds between idle memory cleanups |
| `[LongCareer]` | `UnloadUnusedAssetsThresholdMb` | `2200` | Total Unity allocated MB threshold to also call async `UnloadUnusedAssets()` |
| `[Autosave]` | `CoalesceAfterMatchSaves` | `true` | Coalesce redundant back-to-back after-match saves |
| `[Autosave]` | `MinSecondsBetweenMatchSaves` | `180` | Minimum real seconds between automatic after-match saves (`0` = rely purely on `AutosaveEveryDays`) |
| `[UI]` | `SkipHiddenVanillaUiRebuilds` | `true` | Skip redundant hidden vanilla UI rebuilds underneath `iamgri mod` |
| `[UI]` | `DisableHiddenGraphicRaycasters` | `true` | Disable `GraphicRaycaster` on hidden `UiKit.Unseen` canvases |
| `[Simulation]` | `CacheAiIdentityPools` | `true` | Cache `AiIdentity._pool` across quiet days with exact mutation invalidation |
| `[Simulation]` | `AiPoolMaxCacheDays` | `3` | Maximum quiet in-game days before forcing a fresh rebuild |
| `[Simulation]` | `PruneDormantFromAiTransfers` | `true` | Prune dormant/locked clubs, round-robin slice non-priority scans, and memoize 13 hot pure `EMSkinCore` lookups across all 7 daily AI passes |
| `[Simulation]` | `MaxOtherTeamsPerDay` | `24` | Fair rotating daily slice for non-priority lower-tier clubs (`_scratchOtherTeams`, ~180 clubs; `24` scans every lower-tier club once per week for ~7–8× faster day skips from Day 1; `0` = unlimited; priority clubs & player's club always scan daily) |
| `[Simulation]` | `EmptyScanSkipDays` | `5` | Cooldown days for non-priority clubs that ran a full scan and completed 0 signings (`0` = disabled, `5` = skip redundant re-scans for 5 days; wakes immediately if squad shrinks, `TransferBudget` grows by >\$25k, month changes, or world player count shifts) |
| `[Stability]` | `ThreadSafeAIMatches` | `true` | Thread-safe list snapshot for `MatchesEngine.ProcessAIMatches` |
| `[Logging]` | `QuietConsoleInfo` | `true` | Filter verbose `Info`/`Debug` spam from the Windows console |
| `[Logging]` | `QuietDiskInfo` | `false` | Also filter verbose `Info` spam from `LogOutput.log` on disk |
| `[Logging]` | `StripUnityLogStackTraces` | `true` | Disable native Unity stack-trace walking on `Debug.Log`/`LogWarning` (keeps `Error`/`Exception` traces) |
| `[Memory]` | `SmoothIncrementalGC` | `true` | Cap Unity's incremental GC slice in UI scenes for smoother frame pacing (3D matches untouched) |
| `[Memory]` | `IncrementalGcSliceMs` | `4` | Incremental GC time budget per frame in milliseconds (`4 ms` balances smooth UI with fast collection on large 2029+ saves) |
| `[IamgriBetter]` | `OptimizeIamgriBetter130` | `true` | Eliminates per-frame Canvas/TMP rebuilds, reflection badge/color spam, and heavy icon-recovery scans in `IamgriBetter 1.3.0+` |
| `[Diagnostics]` | `LogPeriodicSummary` | `true` | Log a telemetry summary every 5 minutes (and on `F9`) |

---

## Troubleshooting

- **Windows 11 error `0x800711C7` (`Application Control policy blocked Il2Cppmscorlib`):**
  This comes from Windows 11 **Smart App Control** blocking BepInEx's locally generated `BepInEx\interop\*.dll` files before any plugins load. Right-click the downloaded `.zip` → **Properties** → check **Unblock** before extracting, add the game folder to Windows Defender exclusions, and verify Smart App Control settings in Windows Security.

---

## Building from Source

Requires **.NET 6.0 SDK** and a local installation of **Esports Manager 2026** with **EM2026 ModKit** (providing `BepInEx\core` and `BepInEx\interop`):

```powershell
dotnet build EM2026PerfBoost.csproj -c Release

# Or specify a custom Steam library path:
dotnet build EM2026PerfBoost.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Esports Manager 2026"
```

---

## Credits

- **EM2026 ModKit** (BepInEx 6 port for Unity 6000.5 / IL2CPP v107) — NeverFall & Xleb1g
- **iamgri mod** — Gri
- **Iamgri Better** — community addon authors

## License

Released under the [MIT License](LICENSE).
