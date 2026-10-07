# AGENTS.md — AutoParking

Working notes for anyone (human or agent) editing this plugin. Every rule below exists because
breaking it caused a real bug or a lost day; full provenance in
`docs\2026-09-30-autoparking-design.md` §17–§36.

## Commands

Run these from this repo root (the directory holding `AGENTS.md`), on Windows — Git Bash or
PowerShell. No absolute paths here: `..\..\` is the workspace that contains this repo under
`ThirdPartyPlugins\`, and it holds the ETS2LA host install and the host source as siblings of
`ThirdPartyPlugins\`. If you are an agent, resolve it once from the repo location you were given
rather than copying a path from a previous session. A bare clone of this repo is enough to build
and to run the offline check; only the deploy row needs a full ETS2LA workspace.

| Purpose | Command |
|---|---|
| Build | `dotnet build -c Release` → `bin\Release\AutoParking.dll` |
| Deploy | put `bin\Release\AutoParking.dll` into `..\..\ETS2LA-win-release-Portable\current\Plugins\` — that is the `current\` tree the `.csproj` HintPaths already read host DLLs from, so the deploy target is wherever this checkout's host install is. Then **restart ETS2LA**: plugins are shadow-copy loaded and a running host keeps the old DLL |
| Offline check | `dotnet run --project Tools/PlannerHarness -c Release` (planner self-test + closed-loop sim; pure math, no game and no host DLLs) |
| Data surface dump | `dotnet run --project Tools/MapSurfaceDump -c Release` — reflects the **installed** `TruckLib.dll` / `TruckLib.Models.dll` and prints every public member of each map item and PPD/PMD type. Run it before claiming a map field exists or is missing: the source snapshot is not authoritative, and grepping a hand-written list of candidate names out of a binary produced a false negative once (design doc §29) |
| Offline sector read | `dotnet run --project Tools/MapSectorDump -c Release -- <游戏目录或某个 .scs> [x z 半径]` — stages the `.mbd` plus one sector from every archive in mount order and classifies it with the plugin's own `MapItemSurface`/`MapItemProbe`, so the offline answer and the in-game 地图清单 cannot disagree. Settles "is this object in the map data at all?" without running the game (§30) |

No CI, no linter, no `dotnet test` project. The harness plus the in-game probes on the settings
page are the entire verification story.

**Run the harness whenever you touch anything under `Tools\`, not only the plugin build.** `dotnet build`
compiles the plugin project, which excludes `Tools\**`; a stale reference in `Program.cs` (a deleted check
file, a removed gate term) is invisible there. The symptom of exactly that, hit for real: the harness printed
**0 lines of ✓/✗ and still exited 1** — empty output is a build failure, not a green run.

The harness' process exit code is a gate: `0` means every assertion passed. **Never lower a
threshold in the harness to get a green run** — its bars are deliberately tighter than the
plugin's own tolerances, which is how it catches regressions the game would still accept.

## Repository boundaries

- **The plugin `.csproj` globs everything under the repo, so a stray `.cs` becomes part of the
  shipped DLL.** Developer-only code lives in `Tools\` — `Tools\**` is carved out by
  `DefaultItemExcludes` in the plugin csproj, and `Tools\PlannerHarness` (the offline harness)
  links the pure-math sources it needs instead of referencing the plugin. Put any new dev project
  under `Tools\` as well: the batch script `ThirdPartyPlugins\build_all.ps1` builds every csproj it
  finds one level down and copies the result into `current\Plugins\`, so a console app placed there
  would ship as a broken plugin.
- **The harness csproj lists every source file by hand** (`EnableDefaultCompileItems=false`). A new `.cs`
  under `Tools\PlannerHarness\` that is not added to `<Compile Include>` silently never compiles, so its
  assertions "pass" by never running. Register both halves when you add a check: the new `*Checks.cs`
  **and** the plugin source it exercises (`$(PluginRoot)Driving\Foo.cs`).
- **No NuGet dependencies.** `NuGet.Config` clears every package source, so restore has nowhere to
  fetch from. Host assemblies are referenced by `HintPath` into the install tree with
  `Private=false` — the plugin only ever loads from beside the host.
- `..\..\SourceCode` (the ETS2LA host) is a **read-only reference**: upstream rejects
  agent changes to it. Read it to learn the contract; never fix a plugin problem there.

## Project policy (owner-set, not negotiable in code)

- **Only the hotkeys may end a maneuver.** No observation — telemetry gap, blocker, replan cap,
  unexpected state — may auto-pause or auto-abort. Degrade, keep driving, keep reporting instead.
  A "safety" check that stops the truck on its own is a requirements violation here, not a feature.
- **Tractor unit only.** Trailer handling is out of scope; keep `RefuseWithTrailer` default on.
- **Evidence before fixes.** Do not reason a fix into existence. Add a log line, an in-game probe,
  or a failing harness case, read the measurement, then change the code. Control work on a real
  vehicle model cannot be validated by reading — several plausible fixes here were wrong until a
  trace said so.
- **Update the design doc with the change**: a §17-style log entry (symptom, the tell that found
  it, the measurement that confirmed the fix), *including hypotheses that turned out false*.
- **Commits: English only, never Chinese** (mirrors `.trae\rules\git-commit-message.md`, which is
  gitignored, so the rule lives here too). Commit inside this repo only — the workspace root is not
  a git repo. **Do not push unless explicitly asked.**
- **`README.md` and `README.zh-CN.md` are mirrors.** Edit both in the same change; the English one
  is the source.

## Host behaviors that have each cost a debugging session

- `aforward` and `abackward` are folded into a **single `acceleration` bucket that is
  weighted-averaged across every publishing channel**, then split by sign. Writing both fields
  turns a brake demand into half throttle; a low `ControlWeight` means another plugin's request
  wins. Send one signed value. And read `air=` before concluding a brake did nothing — the service
  brake is air-circuit based, so at 0 bar the pedal value is physically inert.
- **The averaging happens before the transport branch, the echoes happen after.** Where our pedals
  physically go depends on the host's `EnableModernOutputForPedals` (Experiments page): off = the
  legacy virtual-gamepad surface, on = written straight to memory, which also keeps working while
  the game is unfocused. So `user_brake` proving a command landed is a **legacy-transport**
  measurement, not a universal law — which is why the brake probe prints `transport=memory|legacy`.
  Gear and handbrake pulses always take the legacy path. See design doc §24.
- `truckFloat.user_*` is the virtual gamepad's echo: SDK-injected commands show up there as
  "player input". Never use `user_*` to decide whether a human is driving. Also, `ETS2LA.Logging`
  renders Spectre.Console markup, so a bare `[Tag]` inside a message is silently swallowed —
  write `[[Tag]]`.

- **Overlay window flags are read every frame, but from the host's copy of the definition.**
  `ImGui.Begin` uses `Definition.Flags` each frame (`Overlay.cs:385`) and `RegisterWindow` for a title
  it already knows swaps that copy in place (`Overlay.cs:639-648`) — so changing a flag at runtime is
  one more `RegisterWindow`, with no unregister and no recreated window (position holds, because `X/Y`
  are applied with `ImGuiCond.Once`). `WindowDefinition` is a **struct**, so mutating the local copy is
  silently a no-op. Matters here because the map canvas stops 8 px short of the panel border and ImGui
  grabs a window from anywhere along it: a press meant for the edge of the map can move the rect under
  the pick. Hence `LockMapWindow` (§33).

## Map data constraints (each one shaped a design decision)

- **Only nodes have a spatial index** (`Nodes.Within`, an R-tree). `Map.MapItems` is a plain
  dictionary, so "what is near the truck" can only be answered by walking nodes and reading
  `node.ForwardItem` / `node.BackwardItem`. Every item class does own at least one node — buildings,
  signs, loose models and POI areas included — so the node walk reaches them, and it reports a
  polyline item twice (once per end node). Deduplicate by `Uid`, never by "did I see this before".
- **The host filters map items while it parses**, by `DataSettings.DataFidelity`
  (`DataSettings.json`, default Medium): buildings and signs survive Medium, loose models such as
  street lamps need High, company/city POI areas need Extreme, and prefabs/roads that are not shown
  on the UI map are dropped below High — which is exactly the class of quiet depot a parking spot
  lives in. Changing fidelity means re-parsing, not a live knob. Read the value out of
  `DataSettings.Current` instead of assuming it.
- **Resolving a model's real extents means one PMD file parse per distinct token, and that is the
  expensive part.** Upstream's visualization plugin disabled its own model streaming over it:
  *"this lags ETS2LA for ~20 seconds at first start"*
  (`official-plugins\Plugins\VisualizationSockets\VisualizationSockets.cs:226`). Any footprint work
  here must cache per token, off the tick thread, with a budget — and the map inventory (§6.4)
  prints the distinct-token count precisely so that decision is made on a measurement.

- **What a plugin can actually get out of a map item** (verified with `Tools\MapSurfaceDump`, not by
  reading the source snapshot): every class that exposes nodes has ground geometry —
  `SingleNodeItem.Node`, `PolylineItem.Node`+`ForwardNode`, `PathItem.Nodes`, `PolygonItem.Nodes`.
  Real **extents** are nowhere in the map data: the k-DOP is on `MapItem.Kdop`, which is `internal`.
  And `PrefabDescriptor` carries `Nodes / NavCurves / Semaphores / Signs / SpawnPoints / MapPoints /
  Intersections / TriggerPoints` but **no placed-model list** — props bundled inside a prefab are
  unreachable unless we parse `.pdll`/`.ppd` model sections ourselves. So the drawing order is:
  item geometry (free), PMD boxes (per token, expensive), prefab interior (not available).

- **Tokens in map data are often numeric hashes, not names.** Measured in ATS (§30): `Model` tokens
  come out as `1061`, `5110`, `277`; `Sign` as `539`, `540`; only `Buildings` scheme names read as
  text (`scheme1048`). So `PmdFileHandler.GetPmdModel(token)`, which keys on the name suffix from
  `/def/world/*.sii`, will not resolve the numeric ones — any real-footprint work has to map
  hash → unit name first. Do not assume the name lookup works because one class happens to be textual.
- **A DLC adds sectors to a map without shipping its own `.mbd`.** True for the game's parser and for
  anything reading the archives offline: the sector directory is whatever the base `.mbd` names, and
  later archives drop more `sec*` files into it. `Tools\MapSectorDump` accumulates sector directories
  across archives for exactly this reason.

## Definition of done

Build with no new errors or warnings (the project currently carries a handful of pre-existing
ones), harness verdicts unchanged from before the edit, both READMEs and the design doc
current, and every claim about real-truck behavior labeled as measured or not yet measured.
