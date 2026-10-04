# THIS IS 100% AI-GENERATED CODE. NOT ONE LINE WAS WRITTEN BY A HUMAN.

- **No human has read and approved this code.** It was produced by AI coding agents over many
  sessions. There was no design review, no security review, no code review, and there is no
  maintainer who understands every path through it. Everything below was asserted by a model —
  and models assert confidently while being wrong.
- **It drives a vehicle.** Throttle, brake, gear selection and steering are sent into a running
  game at 60 Hz. A bug here is not a crash: it is a truck that keeps accelerating, does not brake,
  or turns the wheel the wrong way. Behavior in an edge case nobody has reached is **unknown**, not
  safe.
- **By design, it will never stop itself.** The only thing that ends a maneuver is a hotkey you
  press (see §8). That is an explicit requirement of this project and also its single most
  dangerous property. Keep your hand on `Abort`.
- **Verification is thin.** Numbers below such as "24/24" and "verified" mean one geometry
  self-test and one bicycle-model simulator agree with themselves. They do **not** mean the code is
  correct, complete, or safe under conditions the simulation never models. Real-truck tuning is
  still unfinished, as the status note says.
- **Do not deploy, fork, extend, or let a coding agent merge this on the assumption that somebody
  checked.** Use it in a game, in an empty lot, at your own risk. Issues and pull requests are
  welcome precisely because the work is unfinished.

Everything below — including this warning — was written by the same agents that wrote the code.

---

# AutoParking — ETS2LA V3 Third-Party Auto Parking Plugin

Pick a parking spot on a **flat (2D) map**, and the plugin draws it onto the game screen as an
**AR overlay**, then automatically performs **forward / reverse / gear shifting (automatic
transmission) / braking / steering** to back the tractor into the spot.

> **Current status: M4 — closed-loop verification on the real truck.** The planner and controller have
> been verified offline (see "Self-Test Tools" below), and gear-shift actions are confirmed to reach the
> game. The pedal problem was **transport, not tuning**: our throttle/brake values are dispatched through
> whichever path the host is configured for, and on a build with **Settings → Experiments → Enable Memory
> Output for Pedals** switched on they land (driver-reported; the log line now says which path ran, so the
> next run settles whether the ~0.5 km/h creep on long reverse legs was the same cause). What is still
> open: the echo field that proves a pedal command landed on that path — `user_brake` was measured on the
> old one — and the identity of the competing channel that averages against our brake and drains the air
> reservoirs. See "Troubleshooting".
>
> **Tractor only — trailers are not supported** (startup is refused by default when a trailer
> is attached).

---

## 1. Installation

1. Build:
   ```
   dotnet build -c Release
   ```
   The output is a single `AutoParking.dll`.
2. Put it into the ETS2LA plugin directory:
   ```
   <ETS2LA install dir>\current\Plugins\AutoParking.dll
   ```
   In a workspace that also holds a portable host build, that is the same `current\` tree the
   `.csproj` HintPaths point at: `..\..\ETS2LA-win-release-Portable\current\Plugins\` relative to
   this repo root (see `AGENTS.md`).
3. **Restart ETS2LA** (plugins are shadow-copy loaded; hot reload is unreliable).
4. **Turn on `Settings → Experiments → Enable Memory Output for Pedals`** in ETS2LA before running a
   maneuver. Without it the host writes our throttle/brake values into the legacy virtual-gamepad
   surface, which is where our pedal commands were being lost; the plugin's status table and log line
   report which path is actually in use (`transport=memory|legacy`). The host documents this toggle as
   not working on every system, and it may need a game restart to take effect.

Dependencies: the plugin references `ETS2LA.*.dll`, `TruckLib*.dll`, and `Hexa.NET.ImGui.dll`
under `current\` directly, so it **must live in the same install tree as the ETS2LA host**.
These references are `Private=false` in the `.csproj`, so host DLLs are not copied into the
plugin directory. The pedal-transport readout calls into `ETS2LA.Game.GameSettings`, so it needs a
host build from the 2026-09-29 `5299cc9` commit onwards; an older host loads the plugin but throws
when that row or probe is rendered.

## 2. Settings Page and Map

- Settings page route (auto-discovered by convention, no registration needed):
  `/plugins/adjustments/local.autoparking`, i.e. Settings → Adjustments → **Auto Parking**.
- Two visualization windows are toggled from the settings page:
  - **Flat map** (ImGui window, on by default): road lane lines, prefab edges/navigation curves,
    obstacles, current path, plus the static map layer — every map item class that has ground
    geometry gets drawn: magenta building segments, olive loose-model points (street lamps, poles,
    containers), blue-violet sign points, teal area rings (traffic / map / trigger zones) and gray
    for every other class, so an unfamiliar cluster is visible before it is named. Filled means the
    map format's own collision flag is set, hollow means it is not. Roads, terrain quads and the
    compound markers themselves are the only classes skipped, and each has a reason in `MapGeometry.cs`.
    **That layer is drawn but does not block the route yet** (§6.4 explains why the order is
    measure → draw → plan; see also the caps in `MapGeometry.cs`), and whether it shows anything at
    all depends on the host's Data Fidelity — buildings need Medium, models need High.
    The 地图数据 status row carries the counts (`静态 N 项 / 场站轮廓 M`).
    Sand-coloured rings are **prefab footprints approximated as the convex hull of the prefab's control
    nodes** — the only ground positions the map publishes for a prefab. They are orientation, not
    obstacles (the spot normally sits inside the outline being drawn), they over-approximate L-shaped
    and roundabout pieces, and a 2-node prefab forms no polygon at all. Design doc §31 shows what this
    looked like on a real depot: road-junction pieces, not the containers in front of the truck.
  - **AR overlay** (on the game screen, on by default): spot box, vehicle footprint projection,
    forward-direction arrow, target pose.

## 3. Bind Hotkeys (Required)

The overlay and the game are two separate applications. **The SCS SDK does not accept input
while the game window is unfocused**, so start/stop cannot use web buttons — in-game hotkeys
are mandatory:

| Control ID | Action |
|---|---|
| `local.autoparking.Toggle` | Idle→start; running→pause; paused→resume |
| `local.autoparking.Abort` | Immediate brake stop, release the control channel, hand control back to the driver-assist |

Where to bind: **Settings → Controls** (the plugin registers controls in `Init()`, so they
appear in the list right after startup — no need to enable the plugin first).

Note the asymmetry: that focus rule is about **input**, and no host setting changes it. **Output**
is the other half — with `Enable Memory Output for Pedals` on, our pedal commands keep landing even
while the game is unfocused. That removes an accidental safety net: clicking away to the map window
used to cut our pedal authority mid-maneuver, and now it does not. Keep `Abort` bound and reachable.

## 4. Usage Flow

1. Drive the truck near the parking spot (path length cap: see `MaxTakeoverDistanceM`).
2. Click/adjust the target pose on the flat map, or use "Saved spot" to recall the last one.
   The click places the spot and takes the truck's current heading; **drag out of the circle that
   appears to point the nose** (inside ~12 px the direction is ignored — the pivot is under your
   cursor, so a one-pixel jitter would otherwise pick the heading). The AR overlay draws the spot
   simultaneously so you can verify that map coordinates line up
   with the actual in-game position.
3. To preview first, keep **Dry-run on**: all control values are computed and displayed but
   never sent to the game.
4. Turn Dry-run off, switch back to the game, and press the `Toggle` hotkey to start.
5. To take over, press `Toggle` again to pause; `Abort` gives up immediately and hands the
   truck back.

Startup is refused (reason shown in the status table and log) when: no spot selected /
telemetry timeout / SDK inactive or game paused / trailer attached / map data not ready /
path unsolvable or blocked by an obstacle / path too long.

## 5. Settings Overview

| Group | Key settings | Default | Notes |
|---|---|---|---|
| Master | `DryRun` | true | Compute only, send nothing |
| | `RefuseWithTrailer` | true | Refuse to start with a trailer |
| | `ControlWeight` | 20 | The host averages every channel writing the same field, so a low weight is not deference — it is being overwritten. A measured competitor at ≈14 turned our brake demand into positive throttle and drained the air reservoirs. Raise this above any ACC/overtake plugin running alongside |
| Body | `WheelbaseM` / `MaxSteerDeg` | 4.0 / 33 | Determines minimum turning radius — the two most important geometry parameters |
| | `VehicleLengthM` / `VehicleWidthM` | 6.5 / 2.6 | Rectangular footprint used for obstacle conflict detection |
| Speed | `ForwardSpeedKph` / `ReverseSpeedKph` | 6 / 3 | Creep speed caps |
| | `MaxAccel` / `MaxBrakeDecel` / `ComfortDecel` | 1.0 / 1.2 / 0.5 | Pedal amounts and reference speed profile |
| | `LaunchAccelMps2` | 0.6 | Standing-start compensation (rolling resistance + drivetrain slack) |
| Longitudinal | `PidKp/Ki/Kd` | 0.35 / 0.05 / 0.02 | PID in the acceleration domain |
| Steering | `SteerGain` / `SteerDeadband` / `SteerRateLimitPerSecond` | 1.0 / 0.02 / 1.5 | Linearizes the geometric curvature command: gain → clamp → deadband → per-second rate limit |
| | `LookaheadBaseM` / `LookaheadGainMps` | 1.5 / 0.6 | Lookahead distance for forward Pure Pursuit |
| | `ReverseLateral` | Reverse Pure Pursuit | Lateral law in reverse; switchable to cross-track-error PD |
| Planning | `PlanRadiusMargin` | 1.35 | **Plan on a circle larger than the vehicle's own minimum turning radius**; otherwise every arc needs full lock, and once the actuator saturates the controller has no correction authority left |
| | `GearSwitchPenaltyM` | 6.0 | Cost per extra gear change |
| | `TerminalStraightM` | 1.5 | Length of the straight leg driven into the spot. An arc only reaches the correct heading at its very end, so stopping 0.35 m short leaves the bay turned by exactly that much |
| | `PathSampleM` | 0.25 | Path sampling step |
| Tolerance | `ToleranceLateralM` / `ToleranceHeadingDeg` | 0.20 / 4.0 | What counts as "in place" |
| | `ObstacleMarginM` | 0.5 | Vehicle footprint outward expansion |
| | `ObstacleLookaheadM` | 4.0 | Only conflicts this far ahead stop the truck. Note the swept footprint is ~8 m long, so a blocker registers about 7.5 m out regardless of this number |
| Re-planning | `ReplanWhileStopped` | true | Re-solve the route **only while the vehicle is stopped** — at a gear change, after 2 s held by a blocker, or when the distance to the spot starts growing instead of shrinking (a second-segment correction). Never mid-drive: a new route shares the pose but not the arc length, and the progress estimator cannot tell the two references apart |
| | `MaxReplans` | 3 | Cap on swaps; after it the route is frozen again. Hitting the cap does **not** abort — only the hotkey ends the maneuver |
| Finish | `HandbrakeOnFinish` / `RestoreAssistsOnFinish` | true | Apply handbrake and restore driver assists on completion |
| Visualization | `MapScalePxPerM` / `MapZoom` / `MapViewRadiusM` / `SnapToNavCurve` | 1.25 / 1.0 / 120 / true | |
| | `ArGroundTrimM` | 0.0 | AR ground trim (manual offset beyond the wheel-contact-point estimate) |

All numeric values are clamped by `Clamp()` to the safe ranges in this table on save.

Reserved fields that are not wired up yet (changing them has no effect; do not use them to
infer behavior): `AutoCalibrateRadius` (auto calibration of the turning radius — the algorithm
is written up in the design doc §6.1 but not implemented), `OvershootM`, `HazardOnFinish`,
`EngineOffOnFinish`.

## 6. Self-Test Tools

### 6.1 Planning Geometry Self-Test ("Run planning self-test" on the settings page)
24 fixed start/end pairs (straight aligned, 90° perpendicular bay, 45° angled bay, parallel
shift, unreachable, etc.). Assertions: solution existence, segment count ≤ 4, sampling
continuity (adjacent points ≤ 0.35 m), end-pose error < 1e-3, no NaN, curvature within the
planning radius, no gratuitous extra full loop, obstacle conflicts must be rejected.
Does not require the game to be running. Results go to the log + the settings page status
line. Currently **24/24**.

### 6.2 Command-Path Self-Test ("Test engage D / Test engage R" on the settings page)
Throttle/brake go through the **analog axis** path; gear/handbrake go through the **boolean
action** path — two different code paths. Press these two buttons with the garage disabled
and Dry-run off, then check the tail of the "Manual input" row in the status table:
whether `gear=actual/dash shifter=transmission type` changed.
This answers "do boolean actions actually reach the game?" in 2 seconds without running a
whole parking maneuver.

### 6.3 Offline Closed-Loop Simulation (`Tools\PlannerHarness`)
A bicycle model drives the real `Follower`, reporting lateral/heading error, gear-pulse
counts, and steering quality metrics (full-lock duration, steering reversals, total steering
wheel travel). Named cases, each one added to pin down a bug that was only visible in a trace:
**deaf gearbox** (pulses never engage — must degrade to "keep driving and re-send" instead of
waiting forever), **blocker appears mid-maneuver** (re-planning off holds until the budget runs
out, on reaches the spot), **automatic that drops to neutral at a standstill** (gear confirmation
must accept the dashboard reading, or one shift costs 110 pulses), **two-segment shuttle** (the
only multi-leg route; it used to drive through the target with `remaining` frozen), **unstoppable
overshoot** (must not pin the wheel chasing an aim point that ended up behind the truck), and a
synthetic **recession** sequence that feeds states where the distance to the spot grows, so the
"stop and re-solve a correction" watchdog is actually exercised.

It is a console project of its own:

```
dotnet run --project Tools/PlannerHarness -c Release
```

It sits under `Tools\`, which the plugin `.csproj` carves out with `DefaultItemExcludes` —
without that, default globbing would compile it straight into the shipped DLL. It links the
pure-math subset of the sources rather than referencing the plugin project, so it needs neither
the game nor the host DLLs and runs from a bare clone of this repo.

### 6.4 Map Inventory (the 地图清单 button on the settings page)
Read-only measurement of what actually stands inside the sampled circle around the truck, bucketed
by map item type: how many, how many carry the map format's collision flag, how near the closest
one is, and the most frequent model/scheme tokens plus the **total number of distinct tokens**.
One summary line is shown in the map window and in the 地图内容 ("map content") status row; the
button writes up to 8 per-type lines to the log (tagged `[[地图清单]]`) together with the sample
center, radius, node count, build milliseconds and the host's data fidelity.

The same button then runs a **whole-map census** on a background thread (tagged `[[全图清点]]`):
every parsed item on the map counted by type, with the elapsed time. That is what separates
"this class is not in the data" from "our node walk did not reach it" — two very different
diagnoses that look identical on screen.

Props bundled into a `Compound` — stacked crates, pallets, junction boxes — are walked into as well
and reported as 内含 N: a compound keeps its children in its **own** dictionaries, so to the map's
node index a whole stack is a single item (design doc §28). Without that, an inventory of a full
depot reads like an empty one.

Nothing here blocks a route yet — that is deliberate. Static obstacles are the next milestone, and
the classification rules for them have to come from the token names a real spot produces, not from
guesses. The distinct-token count is itself a measurement we need: upstream's visualization plugin
switched model loading off entirely because resolving one model description per token cost
*"~20 seconds at first start"* (`official-plugins\Plugins\VisualizationSockets\VisualizationSockets.cs:226`).

Note: which item types reach `MapData` at all is decided by the host's **Data Fidelity** setting
(`DataSettings.json`), and the filtering happens while the map is *parsed* — buildings and signs
survive Medium, street lamps and other loose models need **High**, company/city POI areas need
**Extreme**. Changing it means re-parsing (restart), not a live knob.

### 6.5 Offline sector read (`Tools\MapSectorDump`)
Asks §6.4's question — *what is actually there* — from the game's own `.scs` files instead of a live
session. It stages the `.mbd` plus the single sector around a coordinate, taking files from every
archive in mount order (base supplies the map, a DLC adds sectors without carrying an `.mbd` of its
own), and classifies them with the plugin's own `MapItemSurface` / `MapItemProbe`, so the offline
answer and the in-game inventory cannot drift apart.

```
dotnet run --project Tools/MapSectorDump -c Release -- <game dir or one .scs> [x z radius]
```

Run it before writing any obstacle-classification rule: it is the only cheap way to tell three
different situations apart — the object is not in the map data, it is in the data but unreachable
(prefab interior geometry), or it is present and merely off-screen. Design doc §30 records what it
answered the first time it was run.

## 7. Architecture Overview

```
AutoParkingPlugin.cs   Lifecycle / telemetry sampling / control registration / settings IO / phase machine
Settings.cs            All settings + Clamp() + Clone() (persisted off the tick thread)
Geometry.cs            Planar poses, heading↔forward, signed lateral error, rectangle/SAT overlap
Driving\
  Planner.cs           Layered planning: straight reverse → Reeds-Shepp forward → reverse sweep → two-segment shuttle
  ReedsShepp.cs        Builds CSC/CCC from turning-circle geometry, self-checks by re-integrating the endpoint
  ParkingPath.cs       Arc-length sampled path + gear-change points
  Follower.cs          Gear state machine + longitudinal PID + lateral Pure Pursuit/reverse law + steering shaping
  ControlOutput.cs     The single place commands are sent: channel publish/renew/release
  ObstacleScanner.cs   Traffic/parked vehicles → polygons
  PlannerSelfTest.cs   The 24 cases from 6.1
Rendering\
  MapGeometry.cs       map/ppd data → drawable geometry (incl. prefab descriptor parsing)
  MapItemProbe.cs      What the same node walk contains, bucketed by map item type (read-only, §6.4)
  MapItemSurface.cs    One item's type name / token / collision flag / ground shape - shared with the offline reader
  MapOverlay.cs        Flat map window + spot picking/zoom/buttons
  ArOverlay.cs         In-game AR drawing
SettingsPage.razor     @page "/plugins/adjustments/local.autoparking"
Tools\PlannerHarness\  Offline harness (§6.3): own project, excluded from the DLL by DefaultItemExcludes
Tools\MapSurfaceDump\  Reflects the installed TruckLib assemblies - what a plugin can actually read
Tools\MapSectorDump\   Reads one map sector straight out of the .scs files, no game (§6.5)
docs\                  Design doc + per-item implementation log (incl. assumptions disproven by testing)
```

Control channels: `local.autoparking.drive` (0.3 s timeout, renewed every tick), `.gear`
(TrueToToggle pulse), `.hold` (parking brake; explicitly written false to release).

## 8. Troubleshooting

Log location: `<install dir>\current\ets2la.log`, search for `AutoParking`.

**Key point: this plugin has no automatic abort.** By requirement, no observation other than
the hotkeys terminates a maneuver; the only automatic intervention is "pedal + handbrake" on
overspeed. Losing control can only be stopped with the `Abort` hotkey — keep a hand on it
while debugging.

Meaning of a few log lines:

| Log | Meaning |
|---|---|
| `engaged: … · shift N · conflicts 0, dry-run=…` | Task accepted, following started |
| `start rejected: …` | Which rule from §4 refused the start |
| `brake probe transport=memory sent_accel=-0.50 … user_brake=0.50` | Which pedal transport the host used, the signed value we sent, and the game's echo. On the `legacy` transport a negative `sent_accel` reliably shows up as `user_brake` rising with `user_throttle` at 0; **that mapping has not been re-measured under `memory`**, so trust `air=` (transport-independent) first |
| `gear probe request=Reverse → gear=0 dash=-1 … shifter_type=…` | One line per gear pulse, with the stage that asked. `shifter_type` decides whether this truck accepts `gear_drive/gear_reverse`. **`gear=0` with `dash=-1` is normal**: an automatic falls back to neutral at every standstill, so confirmation reads either signal — a pulse storm here means that latch is failing |
| `controls registered (…Toggle / .Abort)` | One line at startup; missing means `Init()` never ran |
| `[[地图清单]] 中心=… 半径=… 节点=… 条目=… 构建=… ms 宿主保真度=…` | The map inventory of §6.4, written only when the button is pressed; the indented lines after it are one per item type |

Seven field observations that each turned out to be a real bug or a real constraint, with the tell
that found it — all recorded with evidence in
`docs\2026-09-30-autoparking-design.md` §17–§32:

- **Pedals do nothing (or stop the moment you click the overlay)** → read `transport=` on the brake
  probe / the **pedal transport** status row. `legacy` means the host routes our throttle and brake
  through the virtual-gamepad surface, where unfocused input is dropped entirely; `memory` means they
  are written straight to memory and survive focus loss. Turn on
  **Settings → Experiments → Enable Memory Output for Pedals** — that is what cleared our pedal
  problem, and it was never a PID issue. It does not work on every system.
- **Braking does nothing, the truck keeps rolling** → read `air=` and `brake_temp=`. ETS2's service
  brake works through the air circuit: at `air≈0` the pedal value is physically inert. Sustained
  braking against a competing throttle drains it, which is why the status table has a
  **braking capability** row and why the parking brake (a different circuit) takes over inside the
  last 2 m of the route.
- **`remaining` stops shrinking** (or the truck drives through the spot while the display still
  reads 12 m) → the arc-length projection has pinned. Check `remaining` / `cross` / `heading err`
  in the brake probe: if the errors stay near zero while the truck visibly leaves the line, the
  anchor is stale, not the steering law.
- **The wheel does not move** during a leg → first check whether that leg is a straight. `steer=0`
  on a straight reverse-in is correct output; only treat it as a fault when `cross` is also
  nonzero (which means the stale-anchor case above).
- **The spot ends up facing a random way** → the orientation is whatever the drag says *once you are
  outside the dead-zone circle*, because the click puts the pivot under your cursor; a drag of one or
  two pixels used to be enough to pick an arbitrary angle. Resizing the map panel is now disabled
  (`NoResize`): pressing near the map edge used to grab ImGui's resize border instead, and since the
  projection is recomputed every frame, the spot slid out from under the drag. **You can still move
  the panel by its title bar** — that can never collide with a pick, because the title bar sits above
  the canvas rectangle and the projection is latched when the button goes down. The
  `选位开始 / 选位结束` log lines carry the drag radius, how far the map slid in between and whether the
  canvas moved, so a single run says which of the three is still wrong.
- **Gear pulses repeat at ~1 Hz** → the automatic dropping to neutral at standstill is normal;
  confirmation must accept the dashboard reading. A pulse storm means it does not.
- **No buildings, street lamps or POI on the map, while the roads render fine** → the host drops
  whole item classes *while parsing*, by **Data Fidelity** (`DataSettings.json`): buildings and
  signs from Medium up, loose models (street lamps, poles, containers) only from **High**, and
  company/city POI areas only at **Extreme**. Below High it also throws away every prefab and road
  that is not shown on the UI map — which is exactly the class of quiet depot you park in.
  Read the number before touching anything: the 地图数据 row ends with `建筑 N 段 / 点位 M`, and
  设置 → 地图清单 logs the per-class breakdown plus the fidelity the host is actually using.
  And if the inventory lists no `Model` and no `Buildings` while the objects are standing right in
  front of you, they are prefab interior geometry — no layer will ever draw them (§6.5, design doc §30).

Two older pitfalls, still the first things to suspect:

1. **Brake becomes throttle**: the host folds `aforward`/`abackward` into a single
   `acceleration` bucket with a weighted average, then dispatches by sign. Sending both
   fields ⇒ they average into half throttle. The correct approach is to send **one signed
   field**.
2. **`user_*` is the virtual gamepad's echo**: values injected via the SDK show up in `user_*`
   as "player input". Do not use them to judge "is a human driving" — it will misfire. Note that
   this echo is where we *proved* braking worked back on the `legacy` transport; under `memory`
   output the commands go in through a different surface, so which echo field corresponds has not
   been re-measured. `air=` is the transport-independent tell.

Settings file: `%APPDATA%\ETS2LA\AutoParking.json` (includes the last selected spot; survives
plugin reloads). The host's pedal-transport switch is a sibling file in the same folder,
`GameSettings.json` (`EnableModernOutputForPedals`) — it is not one of our settings.
Logging uses Spectre.Console markup — **bare `[Tag]` in messages is silently
swallowed**.

## 9. References

Paths are relative to this repo root; `..\..\` is the workspace that contains it (see `AGENTS.md`).

- ETS2LA V3 host source: `..\..\SourceCode` (read-only reference, do not modify)
- Flat map rendering reference: `..\..\official-plugins\Plugins\InternalVisualization`
- Algorithm reference: V2 Python version (V3's core ACC is closed source)
- Design doc & implementation log: `docs\2026-09-30-autoparking-design.md`
- Rules for anyone editing this repo (and for coding agents): `AGENTS.md`

---

> 中文版说明文档：[README.zh-CN.md](README.zh-CN.md)
