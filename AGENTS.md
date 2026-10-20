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
| Object identity / extents / whole-map census | `dotnet run --project Tools/ObjectProbe -c Release -- <def\|files\|units\|curve\|kdop\|near\|mapscan\|token> [游戏目录] [参数]` — reads the archives directly: what `/def/world/*.sii` says a unit is, per-item k-DOP extents via the public `KdopBounds` base, and a whole-map count by item class × resolved model path (`mapscan` stages the entire map, ~6–8 min, `near <x> <z> [半径]` answers "what is within N meters of this world coordinate" from the files, which is how §38 proved the overlay was right and the spot genuinely had no cone items; both modes search DLC archives too, because the sector under the truck can live in one) |
| Live game memory census | `dotnet run --project Tools/MemoryProbe -c Release -- [pid\|name] [--near X Z [R]] [--strings token]` — reads the **running** game from outside the process: pattern-scans for the `base_ctrl_u` instance, sweeps its members for `array_dyn_t<kdop_item_t*>` lists, and dumps per-item `item_type` / k-DOP box / uid. This is how §41 proved the roadwork cones are runtime-spawned Sign items that exist only in game memory. Patterns were re-derived for 1.61 from the shipped `ets2la_plugin.dll` — re-check them after every game update |
| Route search prototype (Hybrid A*) | `dotnet run --project Tools/RouteSearch -c Release -- <scene.tsv> <route-out.txt> [--spot X Z HeadingDeg] [--env-w W --env-l L] [--turn-radius R] [--no-reverse] [--no-gear-switch]` — offline Hybrid A* over a scene dumped by MemoryProbe (viz mode): searches (x, z, heading) with 2 m motion primitives, drops items inside a detected bay like the plugin does, and defaults to the measured truck's envelope (8.93×2.97 m) and planning radius (10.29 m). **Its `conflicts=` is a 0.25 m raster count, not the planner's test** — see the row below before quoting a number (§53: the two disagreed by 0 vs 17 poses on one route). §45 used it to show the analytic planner's blind spot (no intermediate waypoints → cannot detour). Pure managed; output feeds the matplotlib plot in scratch |
| Conflict verdicts (authoritative) | The only test that decides whether a route is acceptable is `Planner.CountCorridorConflicts` → `Geometry.RectangleCorners` + `Geometry.PolygonsOverlap`: exact polygons, no cells, first 2 m / last 1 m exempt. Offline replays of it live in `scratch/exact_sweep.py` (single route) and `scratch/lead_rsa.py` (family sweep); any grid-based replica — RouteSearch's, or a 0.25 m raster in scratch — shifts verdicts by up to 16 poses depending on window origin, so label every raster number as approximate (§48, §53) |

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
- **The memory surface is two channels wide and gives up on zero.** `Local\ETS2LAPluginInput` is 26 bytes:
  `steering` and one signed `acceleration`, each with a flag and a timestamp (`core.hpp:23-33`); every boolean
  the host knows about goes to the legacy segment (`Output.cs:203-210`). So "park the truck through memory"
  can only ever mean the pedal — there is no handbrake or gear field to move. And the host writes
  `override_acceleration = weightedValue != 0` (`Output.cs:304`) while the game side, on losing that claim,
  does **not** clear the pedal value it last injected (`core.cpp:231-239`): an exact zero from us means
  "stop refreshing, keep the previously latched pedal", not "coast". `ControlOutput` therefore publishes a
  `-0.01` floor instead of 0 and keeps the drive channel alive for 1.0 s, past the game side's 0.2 s
  freshness gate. See design doc §39.
- **`truckPlacement.rotation.X` already IS the heading this repo steers by — never add a half turn to it.**
  The widely copied `truck_forward = (2(wy+xz), 1-2(x²+y²))` is R·(0,0,+1), and SCS vehicle models face local
  **-Z**, so that vector is the **tail**. Any convention check anchored on it validates itself; §47 did exactly
  that and shipped a 180° regression the same day (§49 settles it: the drive-forward position diff, not just
  an eyeball).
  `Geometry.HeadingFromRotationComponent` stays raw, the harness asserts the sign, and
  `Tools\MemoryProbe --telemetry` prints both mappings against the nose. The chase-camera and AI-traffic
  auto-checks do **not** work on 1.61 (segment layout mismatch; traffic slots get reused). The proof that
  does work: drive forward and diff the position — done on 2026-10-16, `rotation.X` 0.249999 with a
  (-7.8, 0) m displacement, so raw yaw is the nose. Re-check with `--telemetry` before and after.
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
- **Real extents are in the map data already — no PMD parse needed.** `MapItem.Kdop` is `internal`, but its
  type derives from the **public** `KdopBounds`, whose `Minimums[5]/Maximums[5]` are the same 25-DOP the game
  collides with; one reflection lookup on the property and a cast gets them. Measured on a cone: 0.6 × 0.6 m
  on the ground, 0.9 m tall (`Tools\ObjectProbe kdop`, §38). The older warning here — "one PMD parse per
  distinct token, and that is the expensive part" (upstream's *"this lags ETS2LA for ~20 seconds"*,
  `VisualizationSockets.cs:226`) — still applies to **mesh-level** work, but not to footprints. Use
  `MapItemSurface.TryGroundBox` instead of `PmdFileHandler.GetPmdModel` unless you need geometry.

- **What a plugin can actually get out of a map item** (verified with `Tools\MapSurfaceDump`, not by
  reading the source snapshot): every class that exposes nodes has ground geometry —
  `SingleNodeItem.Node`, `PolylineItem.Node`+`ForwardNode`, `PathItem.Nodes`, `PolygonItem.Nodes` — plus the
  k-DOP box above. `PrefabDescriptor` carries `Nodes / NavCurves / Semaphores / Signs / SpawnPoints /
  MapPoints / Intersections / TriggerPoints` but **no placed-model list** (all five version readers
  decompiled, §38), so a prefab's bundled props are only reachable through its companion `.pmd/.pmg`
  (`TruckLib.Models.Model` → named `Parts`, `Skeleton` bones with transforms, `Piece` vertices + bbox).

- **A map token is a short code, not a hash — and it is not unique.** ETS2/ATS store `1061`, `2x002`,
  `dlc_fr_219`: those decode as real `Token` strings (max 12 chars of `0-9 a-z _`), they are just SCS's
  internal codes. The identity is `/def/world/*.sii`'s `model_desc`/`prefab_desc` path, and the lookup key
  **must be class-qualified**: `dlc_fr_219` is a barrier column as `bld_model.*`, a building scheme as
  `bld_scheme.*` and a transformer as `model.*`. Keying on the bare code mislabeled 78 objects the first
  time it was measured. `Rendering\MapItemIdentity.cs` is the one place that resolves this; `PmdFileHandler`
  only indexes `*model*.sii` files that do not contain "curve", so it cannot see curve/prop chains at all.

- **A parking bay is read out of the cones, never out of a flag bit.** Two `Mover` items sit on the bay's own
  centreline and carry `flags=0xC17` while other movers carry `0x417` - one differing bit, four samples, one
  scene: a correlation, not a contract. `Driving\ParkingBay.cs` therefore detects bays geometrically (two facing
  cone rows, capped at one end, open at the other) and only along the world axes, so a bay rotated off-axis is
  silently not found - that is deliberate, do not "fix" it into a guess. Items inside a bay are exempted from the
  obstacle set with a 0.6 m inset so the wall cones keep blocking (design doc §50).

- **A DLC adds sectors to a map without shipping its own `.mbd`.** True for the game's parser and for
  anything reading the archives offline: the sector directory is whatever the base `.mbd` names, and
  later archives drop more `sec*` files into it. `Tools\MapSectorDump` accumulates sector directories
  across archives for exactly this reason.

## Definition of done

Build with no new errors or warnings (the project currently carries a handful of pre-existing
ones), harness verdicts unchanged from before the edit, both READMEs and the design doc
current, and every claim about real-truck behavior labeled as measured or not yet measured.
