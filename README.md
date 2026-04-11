# AnalyticsTelemetry (Slay the Spire 2 mod)

MIT license — see [LICENSE](LICENSE).

Source for a Nexus-style mod package: one folder under the game’s `mods` directory with `AnalyticsTelemetry.json` and `AnalyticsTelemetry.dll` (and optionally `AnalyticsTelemetry.pck` when `has_pck` is true in the manifest). This repository lives **next to** the game install (`mod-analytics-telemetry/`) so nothing in the shipped `data_sts2_*` tree is edited—only `mods/` receives build output.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download) (`dotnet --version`)
- [BaseLib](https://github.com/Alchyr/BaseLib-StS2/releases): copy **`BaseLib/`** (folder with `BaseLib.dll`, `BaseLib.pck`, `BaseLib.json`) into `Slay the Spire 2/mods/`. **Required for `dotnet build`:** this project references `mods/BaseLib/BaseLib.dll` directly so the compiled mod binds to the **same assembly version the game loads**. The NuGet package `Alchyr.Sts2.BaseLib` uses a different assembly version (e.g. 3.0.0.0 vs release `0.3.0.0`), which makes the game log `Could not load file or assembly 'BaseLib, Version=3.0.0.0'` and report **Loaded 1 mods (2 total)** — only BaseLib actually loads.
- Godot **4.5.1** Mono / [Megadot](https://megadot.megacrit.com/) — only needed for `dotnet publish` when you need to rebuild the `.pck`. Code-only iteration: `dotnet build`.

## Build

```bash
cd mod-analytics-telemetry
dotnet build -c Release
```

Artifacts are copied to `../mods/AnalyticsTelemetry/` (i.e. `Slay the Spire 2/mods/AnalyticsTelemetry/`).

For a full package including `.pck` (localization/assets changed), set `GodotPath` in `Directory.Build.props` to your Godot 4.5.1 Mono binary, then:

```bash
dotnet publish -c Release
```

## Compatibility

| Area | Notes |
|------|--------|
| **Game** | Targets **Slay the Spire 2** with the current Megadot / modding stack on Steam. Major game patches can break Harmony patches or publicized APIs until updated. |
| **BaseLib** | Install the **`BaseLib` folder** from [BaseLib-StS2 releases](https://github.com/Alchyr/BaseLib-StS2/releases) under `mods/BaseLib/`. The DLL there must be the **same file** the game loads; compiling this mod against a different BaseLib assembly version causes load failures (see Prerequisites). |
| **.NET** | **[.NET 9](https://dotnet.microsoft.com/download)** SDK — matches `TargetFramework` in `AnalyticsTelemetry.csproj`. Keep `<Version>` in that file in sync with `"version"` in `AnalyticsTelemetry.json`. |
| **Godot (optional)** | **Godot 4.5.1** Mono for `dotnet publish` / `.pck` export (`Godot.NET.Sdk` in the project). Not required for `dotnet build` when you are not changing packaged resources. |

## Privacy and data export

- **Local NDJSON** — Append-only session files under the game user data tree (`OS.GetUserDataDir()` … `AnalyticsTelemetry/sessions/`). You can delete them anytime; they are not uploaded unless you copy them yourself.
- **Run saves** — The mod reads Steam profile `current_run*.save` files to derive map room counts, gold, and related aggregates. **Steam account directory names are not written verbatim** in telemetry; a short hash is used where a stable id is needed (see runtime docs below).
- **Remote export** — Optional HTTP / Influx-style sinks (mod **Settings → Export**) send only what you configure, and only when enabled. Treat export URLs and tokens like any other secret.

## Automated tests

Does not require the game install or BaseLib (uses `GodotSharp` on NuGet only for `Godot.Color` in shared model types). Covers `MetricsVisualModel` signatures and `MetricsTimeSeriesMath` (sample count + hover interpolation):

```bash
dotnet test tests/AnalyticsTelemetry.UnitTests/AnalyticsTelemetry.UnitTests.csproj -c Release
```

**CI:** pushes and PRs to `main` / `master` run this test project (`.github/workflows/ci.yml`). **Tag releases** (`v*`) run tests and attach a **source zip** to the GitHub Release (`.github/workflows/release.yml`). GitHub-hosted runners do not have your game copy, so **prebuilt `AnalyticsTelemetry.dll` zips** are produced locally (see below).

## Runtime output

NDJSON sessions are written under the game user data folder, for example:

`~/.local/share/SlayTheSpire2/AnalyticsTelemetry/sessions/run-*.ndjson`

(Exact path follows Godot’s `OS.GetUserDataDir()` for this game on Linux.)

### Run-level events (save poller)

The mod polls Steam profile run saves under `…/SlayTheSpire2/steam/<account>/profile*/saves/current_run.save` and `current_run_mp.save`. Logged `eventType` values:

- `run_save_appeared` — save file present (new run or mod loaded mid-run)
- `run_save_progress` — size/mtime changed, throttled to about once per 45s
- `run_save_removed` — save file disappeared (run cleared)
- `run_context_snapshot` — parsed JSON preview (act index/id, map depth from `visited_map_coords`, ascension, party size, **hashed** `p_…` keys from each player’s `net_id`). Also **`roomVisitsByType`**: counts of visited map nodes by `map_point_type` (or `rooms[0].room_type` when needed), derived from `map_point_history` in the save (same shape tools like spirescope use). When the save JSON exposes a gold field (several possible key names), **`gold`** is included on this line. Emitted when a save appears and on throttled progress when that fingerprint changes (including when the path mix or gold changes).
- `run_gold` — emitted when `current_run*.save` changes on disk **and** the parsed gold value differs from the last one we logged (finer-grained than `run_save_progress`). Used for the live **Gold** chart and counters.

Steam account folder names are not written verbatim; a short hash is used in payloads.

### Combat lifecycle & scope (Harmony)

- `combat_started` / `combat_ended` — postfix on `CombatManager.StartCombatInternal` / `EndCombatInternal` (ordinal + act/map snapshot + optional wall duration).
- `combat_player_energy_turn` — includes `combatOrdinal`, `handSequence`, `playerKey` (from `Player.NetId`), plus per-step `playerKey` on energy mutations.

### In-game metrics panel (drill-down)

An **Analytics** button is drawn at the **top-right** (same `CanvasLayer` as before). The panel has a **dropdown** to switch views. Each view shows, when applicable:

- **Timeseries** — multi-line chart: **live** = Δ (change) per sample since the previous point (~1.5s wall time or a burst of 24+ events), so lines rise and fall with activity; **disk replay** = counts per 5‑minute NDJSON bucket (also non‑cumulative per bucket).
- **Session recording** — horizontal **ColorRect** bars for NDJSON event count, combat history lines, and run-save lines (scaled together so you can compare volumes at a glance).
- **Card flow** — five horizontal bars for plays / draws / discards / exhaust / generated (**always shown**, including zeros on the main menu, so the chart area is never empty).
- **Visited map rooms** — on **Overview** and **Run**, horizontal bars for each room/map point type seen on the current run (from the latest `run_context_snapshot` / save parse). Labels are raw game type strings (e.g. combat vs rest vs shop — exact names depend on the game build).
- **Counters** — two-column grid (events, history, damage, kills, TTK, energy, run-save, `Room:…` rows when path data exists, …).
- **Hands** — column heights for the last 16 energy turns (steps).
- **Full detail** — optional plain-text rollup: in the **in-run overlay** it is **hidden by default** (toggle *Show full text block*); **Settings → Mods** live tab still shows the full block. **Recent events** default to one-line summaries (`[seq] eventType`); enable *Raw NDJSON lines* for the old truncated JSON dump.

The overlay panel uses a **framed** `PanelContainer` (dark fill + gold-tint border) so it reads less like a debug slab.

Per-view behavior:

- **Overview** — session totals + scope.
- **Run** — same as session file scope; merge NDJSON on disk for multi-session analytics.
- **Act** / **Combat** — bucket for current act key or current combat ordinal.
- **Multiplayer** — group session bars/counters + per-player text in the detail section.

The panel is anchored **top-right** (under the Analytics button). Metrics and raw NDJSON use **separate** scroll areas so the log is not auto-scrolled off-screen.

Below the metrics block, **recent NDJSON** lines (truncated per line) match the session file.

### BaseLib Mod Config (deeper metrics)

The mod registers a **BaseLib** `SimpleModConfig` screen (same pipeline as other mods’ config UIs — typically **Settings → Mods**, or the entry BaseLib adds for mod configuration).

- **Live metrics** tab: same drill-down views and **same visual layout** (bars, counters, hands column chart, text detail) as the in-run overlay, auto-refresh while open, plus the **active session file path** for this game process.
- **Session files** tab: lists recent `*.ndjson` under `AnalyticsTelemetry/sessions/`; selecting a file shows a **tail preview** (useful after a run without cross-referencing the overlay).

## Troubleshooting

- **Mod greyed out or disabled in the mod list** — `AnalyticsTelemetry.json` declares `"dependencies": ["BaseLib"]`. Enable **BaseLib** in the same mod UI first; the game will not load dependent mods if the dependency is off or missing from `mods/`.
- **"Loaded 1 mods" with two mods enabled** — See prerequisites: rebuild using `mods/BaseLib/BaseLib.dll` as the compile reference. Check `user://logs/godot*.log` (e.g. `~/.local/share/SlayTheSpire2/logs/`) for `Error loading mod AnalyticsTelemetry` and a `BaseLib, Version=…` mismatch.
- **After a successful load** — Search the same log for `AnalyticsTelemetry diagnostics:` (four lines: mod, BaseLib, sts2, 0Harmony assembly full names and DLL paths). The first `session_start` line in the NDJSON session file also includes `hostReferences` with the same binding snapshot for sharing bug reports.
- **Stale install** — After `dotnet build`, the project copies `AnalyticsTelemetry.dll` and `AnalyticsTelemetry.json` into `mods/AnalyticsTelemetry/`. If the in-game version string lags what you expect, rebuild **Release** (or your usual config) and confirm both files updated together.
- **Settings → Mods config missing** — Check the game log for `ModConfigRegistry.Register failed`. Registration runs on the first engine frame after init; if it still fails, update BaseLib and ensure only one `BaseLib` folder exists under `mods/`.
- **Mod name looks grey in Mod Settings** — BaseLib only lists mods that registered a `ModConfig` with at least one persisted static setting (`HasSettings()`). This project includes a tiny internal sentinel property so registration succeeds while the screen stays fully custom-built.

## Sharing (Nexus / Discord / GitHub)

Keep this repo **next to** the game install so `dotnet build` copies into `../mods/AnalyticsTelemetry/`. The zip must contain that folder’s contents (manifest + `AnalyticsTelemetry.dll`, plus `.pck` if `has_pck` is true). Depend on **BaseLib** (see `AnalyticsTelemetry.json`).

### Scripts (local zip for manual Nexus upload)

| Script | Purpose |
|--------|---------|
| `scripts/build-release.sh` | `dotnet build -c Release` only (updates `../mods/AnalyticsTelemetry/`). |
| `scripts/package-mod-zip.sh` | Zip `../mods/AnalyticsTelemetry/` → `dist/AnalyticsTelemetry-<csproj-Version>.zip` (run after a build). |
| `scripts/build-and-package.sh` | Build + zip in one step (Linux / macOS / Git Bash). |
| `scripts/build-and-package.ps1` | Same as above on **Windows** (PowerShell 5.1+ from repo root: `.\scripts\build-and-package.ps1`). |

Example (Unix):

```bash
./scripts/build-and-package.sh
# upload dist/AnalyticsTelemetry-0.6.19.zip via Nexus “Upload a new file”
```
