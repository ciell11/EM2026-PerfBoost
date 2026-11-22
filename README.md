# EM2026 PerfBoost

A lightweight, non-destructive performance and stability optimizer for **Esports Manager 2026** (EM2026 ModKit / BepInEx 6 IL2CPP), designed for full compatibility with **iamgri mod** and **Iamgri Better**.

- **Zero gameplay features cut** — all tournaments, simulation models, UI screens, and realism systems stay intact.
- **Zero save format changes** — 100% safe to add or remove mid-career.
- **3D matches untouched** — automatically stays passive during 3D match scenes.
- **Fully configurable** — every optimization can be toggled individually in `BepInEx\config\local.em2026.perfboost.cfg`.

---

## What It Optimizes

| Module | Problem Solved | Optimization |
|---|---|---|
| **Smart After-Match Autosave** (`SmartAutosave`) | `IamgriBetter.MatchCheckpoint` overrides `iamgri mod`'s `AutosaveEveryDays` skip and triggers a full 3–4s save after every quick match (locking `Continue`). | Coalesces redundant back-to-back after-match saves within a configurable interval (default `180s`). **Never** skips periodic `AutosaveEveryDays` saves, manual saves, or exit-to-menu saves. |
| **Hidden Vanilla UI Skipper** (`HiddenUiSkipper`) | Even when `iamgri mod` replaces screens, the hidden vanilla windows underneath still rebuild UI rows and recalculate Unity layouts on every tab switch. | Exact dirty-check memoization for `InboxWindow.Renew` and `TalkWindow.Renew`; skips hidden `SquadWindow`, `SquadStaffWindow`, `TransfersWindow`, `ShopWindow_v2`, and `Basecamp_v2` rebuilds once `iamgri` pages are built; disables `GraphicRaycaster`, `LayoutGroup`, and `ContentSizeFitter` on hidden `UiKit.Unseen` canvases. |
| **AI Identity Pool Cache** (`AiPoolCache`) | `iamgri mod` rescans all ~6,320 players across 88 national blocs every in-game day (`60–131 ms/day`). | Reuses `AiIdentity._pool` across quiet days and invalidates **immediately** on any transfer/offer (`OfferActivityVersion`), player/team count change, slot change, or weekly growth rollover. |
| **Thread-Safe AI Matches Fix** (`MatchThreadSafety`) | Concurrent `EMSkinCore.DayOffload` (background thread) + `IamgriThirdPlace` (main thread) can throw `InvalidOperationException: Collection was modified` inside `MatchesEngine.ProcessAIMatches`. | Takes a defensive thread-safe snapshot of the match list before `ProcessAIMatches` iterates it. |
| **Console Log Throttler** (`LogThrottler`) | Thousands of verbose `Info`/`Debug` lines from `iamgri mod` and `Iamgri Better` stall the synchronous Windows console window during day ticks. | Filters verbose `Info`/`Debug` spam out of the console window while keeping all `Warning`/`Error`/`Fatal` logs and leaving `LogOutput.log` on disk 100% intact by default. |
| **UI Frame & GC Pacer** (`UiFramePacer`) | Large GC spikes cause UI hitches when navigating menus. | Sets a smooth `2 ms` incremental GC slice in UI/manager scenes and restores the game's default GC slice during 3D matches. |

---

## Compatibility

- **Game:** Esports Manager 2026 (Steam, Unity 6000.5 / IL2CPP)
- **Mod Loader:** [EM2026 ModKit](https://www.nexusmods.com/esportsmanager2026/mods/66) v2.2+ (BepInEx 6 IL2CPP)
- **Mods:** Works standalone, with **iamgri mod 4.x**, and with **Iamgri Better 1.1.x**.
  Dynamically attaches to `EMSkinCore` at runtime with zero compile-time dependency, so it stays compatible across `iamgri mod` updates and hot-reloads.

---

## Installation

1. Install **EM2026 ModKit v2.2+** first.
2. Download `EM2026PerfBoost-v1.2.0.zip` from the [**Releases**](../../releases/latest) page.
3. Extract the archive into your **Esports Manager 2026** root folder (next to `EsportsManager.exe`), so you end up with:
   ```text
   Esports Manager 2026\BepInEx\plugins\EM2026PerfBoost\EM2026PerfBoost.dll
   ```
4. Launch the game from Steam. The configuration file will be created automatically on first launch at:
   ```text
   BepInEx\config\local.em2026.perfboost.cfg
   ```

---

## Live Telemetry (`F9`)

Press **`F9`** at any time in-game to print a live performance report to the console and `BepInEx\LogOutput.log`:

```text
========== EM2026 PerfBoost Telemetry ==========
[Screens] Window Show: 8 calls, avg 11.9 ms, max 21 ms (Windows)
[UI FPS] 5449 frames, avg 55.4 FPS (18.1 ms), worst 42.3 ms, >50ms spikes: 0
[Autosave] Coalesced after-match saves: 6 (est. ~19.2s saved), allowed: 3
[Hidden UI] Inbox 4/5 | Talk 2/3 | Squad 3 | Staff 2 | Transfers 2 | Shop 1 | Basecamp 1 | Raycasters 4 | Layouts 220
[AiIdentity.Pool] Cache hits: 14, rebuilds: 5
[Stability] ProcessAIMatches safe snapshots: 12 (concurrent retries: 0)
[Logging] Console spam filtered: 1240, Disk filtered: 0
================================================
```

---

## Configuration

All options live in `BepInEx\config\local.em2026.perfboost.cfg`:

| Section | Key | Default | Description |
|---|---|---|---|
| `[Autosave]` | `CoalesceAfterMatchSaves` | `true` | Coalesce redundant back-to-back after-match saves |
| `[Autosave]` | `MinSecondsBetweenMatchSaves` | `180` | Minimum real seconds between automatic after-match saves (`0` = rely purely on `AutosaveEveryDays`) |
| `[UI]` | `SkipHiddenVanillaUiRebuilds` | `true` | Skip redundant hidden vanilla UI rebuilds underneath `iamgri mod` |
| `[UI]` | `DisableHiddenGraphicRaycasters` | `true` | Disable `GraphicRaycaster`, `LayoutGroup`, and `ContentSizeFitter` on hidden `UiKit.Unseen` canvases |
| `[Simulation]` | `CacheAiIdentityPools` | `true` | Cache `AiIdentity._pool` across quiet days with exact mutation invalidation |
| `[Simulation]` | `AiPoolMaxCacheDays` | `3` | Maximum quiet in-game days before forcing a fresh rebuild |
| `[Stability]` | `ThreadSafeAIMatches` | `true` | Thread-safe list snapshot for `MatchesEngine.ProcessAIMatches` |
| `[Logging]` | `QuietConsoleInfo` | `true` | Filter verbose `Info`/`Debug` spam from the Windows console |
| `[Logging]` | `QuietDiskInfo` | `false` | Also filter verbose `Info` spam from `LogOutput.log` on disk |
| `[Memory]` | `SmoothIncrementalGC` | `true` | Smooth incremental GC time slice in UI scenes (untouched in 3D matches) |
| `[Memory]` | `IncrementalGcSliceMs` | `2` | Incremental GC time budget per frame in milliseconds (`1–10`) |
| `[Diagnostics]` | `LogPeriodicSummary` | `true` | Log a telemetry summary every 5 minutes (and on `F9`) |

---

## Building from Source

Requires **.NET 6.0 SDK** and a local installation of **Esports Manager 2026** with **EM2026 ModKit** (providing `BepInEx\core` and `BepInEx\interop`):

```powershell
dotnet build EM2026PerfBoost.csproj -c Release

# Or specify a custom Steam library path:
dotnet build EM2026PerfBoost.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Esports Manager 2026"
```

---

## License

Released under the [MIT License](LICENSE).
