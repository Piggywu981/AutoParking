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
5. **Build the identity sidecar once**, before the plugin, so the plugin's build can copy it:
   ```
   dotnet build Tools/MemoryProbe -c Release
   ```
   The plugin's build then copies **four** files from
   `Tools\MemoryProbe\bin\Release\net10.0\win-x64\` beside `AutoParking.dll` — `MemoryProbe.exe`,
   `MemoryProbe.dll`, `MemoryProbe.runtimeconfig.json` and `MemoryProbe.deps.json` — and all four are
   deployed together. The exe is only the apphost: on its own it cannot start, because the probe is
   built `SelfContained=false` and loads the runtime and the app from the other three. It is optional
   in the literal sense: without it the plugin still starts and still follows the marker using
   geometry alone; what is lost is the sidecar's one log line confirming the stop-zone trigger
   (`state park_trigger_on_stopped`). "Parked vehicle" is decided from the item type and flags, not
   from an identity string, so marker following works either way. A missing exe is announced by a
   build warning and by one startup log line; the identity channel reports itself as degraded rather
   than failing the maneuver.

Dependencies: the plugin references `ETS2LA.*.dll`, `TruckLib*.dll`, and `Hexa.NET.ImGui.dll`
under `current\` directly, so it **must live in the same install tree as the ETS2LA host**.
These references are `Private=false` in the `.csproj`, so host DLLs are not copied into the
plugin directory. The pedal-transport readout calls into `ETS2LA.Game.GameSettings`, so it needs a
host build from the 2026-09-29 `5299cc9` commit onwards; an older host loads the plugin but throws
when that row or probe is rendered.

## 2. Settings Page and Map

- Settings page route (auto-discovered by convention, no registration needed):
  `/plugins/adjustments/local.autoparking`, i.e. Settings → Adjustments → **Auto Parking**.
- Three visualization windows are toggled from the settings page:
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
    The button row ends with a **lock-window checkbox**: while ticked, the panel's position and size
    are frozen (`ImGuiWindowFlags.NoMove | NoResize`), which is what makes a pick deterministic — see
    §8. Off by default, because the panel does need to be moved out of the way sometimes.
    **Orange-red discs are memory cones** - the roadwork-style objects the game spawns at runtime,
    which never pass through sector parsing and so can never appear in the host's map data (§41).
    The plugin reads the game's own item lists from the game process, read-only, on a dedicated
    background thread (0.5 s cadence, never the tick thread), and draws cones / construction boards
    / movers / buildings (solid magenta at their real footprint) with counts and the nearest
    distance in the canvas corner. With "内存条目拦路" on, these - buildings included - also enter
    the planner's obstacle set: routes avoid them, or the start is rejected / re-routed (§43).
  - **AR overlay** (on the game screen, on by default): spot box, vehicle footprint projection,
    forward-direction arrow, target pose, and the **route preview** - the corridor the vehicle will
    occupy and the line it will follow, drawn on the ground before it is driven (§6.7).
  - **Control terms plot** (ImGui window, **off by default**): the last few seconds of the control
    terms as curves - cross-track error, heading error, steer before and after the rate limiter,
    target against actual speed, and the longitudinal P/I/D against the command actually returned,
    with reverse legs shaded. It exists because a status row can only show one current number, and
    tuning needs to know whether that number is converging or oscillating. Turning it on registers
    a second overlay window, so it is off for normal use.

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
   cursor, so a one-pixel jitter would otherwise pick the heading). **If you pick near the edge of the
   canvas, tick 锁定窗口 first** — there, a press on the map is also a press on the panel border, and a
   window sliding under the drag makes the heading measure the window instead of the hand (§8).
   The AR overlay draws the spot simultaneously so you can verify that map coordinates line up
   with the actual in-game position.
3. To preview first, keep **Dry-run on**: all control values are computed and displayed but
   never sent to the game.
4. Turn Dry-run off, switch back to the game, and press the `Toggle` hotkey to start.
5. To take over, press `Toggle` again to pause; `Abort` gives up immediately and hands the
   truck back.

Startup is refused (reason shown in the status table and log) when: no spot selected /
telemetry timeout / SDK inactive or game paused / trailer attached / map data not ready /
path unsolvable or blocked by an obstacle / path too long.

### 4.1 Marker following — park where the game asks, one leg at a time

ETS2 driving-school and depot jobs do not have a single drop-off point: the task moves its own marker
from stage to stage, so a manual pick can at best express one of them. With **跟随游戏标记（通用泊车）**
on (the default) you do not pick at all — you press `Toggle` and the plugin resolves the place the game
wants the truck parked, in this order:

1. the **`0xC17` Mover pair** — two marker movers sitting on the aim point (confidence 0.9);
2. the **gap between the framing parked vehicles**, for the stage where the marker has not appeared in
   the bay yet (0.6);
3. the **spot you saved** with a map pick (0.4 — exempt from the confidence gate, because a person
   chose that pose).

It then plans **one leg at a time**, drives it, and advances when the game moves the marker: settling
at a leg's target for `SettleTimeS` re-resolves the next marker (a second leg, e.g. the reverse into
the bay), and a marker that moves more than `MarkerMoveToleranceM` (0.5 m) re-plans the current leg in
place. Re-planning continues a maneuver; it is not a pause and not an abort.

**Shadow mode is on by default and sends nothing.** It runs the whole chain — resolve, plan, draw the
target and the route on the map — and publishes no throttle, brake, steering, gear or handbrake
command. **Shadow sends nothing — drive the truck to the checkpoint yourself; the plugin only
resolves, plans and draws.** Commands flow only when **both** Dry-run and Shadow mode are off;
turning Shadow mode off while Dry-run is still on moves nothing.

Which tier chose the pose is drawn, not only logged (spec §4.6): the "目标车位" status row ends with
`目标 <provenance> conf=0.00` (e.g. `目标 mover-pair:0x… conf=0.90`, or `目标 saved-spot（手选）`),
and the AR pole prints the same line above the distance. Tier 2 only considers parked rows within
80 m of the truck and prefers the nearest gap, so a roomy row elsewhere in the map can no longer
outrank the one you are standing at — which is also what makes the map-pick fallback (0.4) reachable
when there is no local row at all.

Start gates (refusals, checked once when you press the key — never a mid-maneuver interruption):
no target from any tier of the resolver, trailer attached (`RefuseWithTrailer`), a route the planner
will not accept, unknown shifter configuration, and a target below the confidence gate. When commands
would really be sent they also include: engine off, still rolling, and brake air below the game's own
emergency threshold (plus the tunable floor, which ships disabled — see "Known limitations"). Whether
a refusal applies in dry-run/shadow too is deliberate: a gate that only protects real control does not
fire while you are watching a plan with the engine off.

Only the hotkeys end a maneuver — `Toggle` pauses, `Abort` gives up and hands the truck back. No
observation, including a vanished marker or a dead sidecar, aborts on its own; marker loss or drift
degrades to re-planning and reporting. **Tractor unit only** — a trailer is refused by default.

Turn 跟随游戏标记 off to go back to the pure manual workflow: a map pick is then the only way to
start, and the marker tiers are not consulted. With it on, a completed marker maneuver clears the
drawn target while the saved spot persists, so a map pick acts as the resolver's 0.4 fallback rather
than as the aim.

### 4.2 Known limitations of marker following

These are measured or reasoned, not hedged. Read them before trusting the feature with a real truck.

- **Bay detection is world-axis-only.** `ParkingBays` recognises rows of cones that run along the
  world axes; a bay rotated off them is silently not found (deliberate: never guess). The marker
  protocol is the primary path for that reason — bay detection only snaps picks and exempts a bay's
  own markings.
- **The parked-vehicle blocker is on, and it changes what is plannable.** The framing trucks exist
  only as `0x2F Hookup` items, so they used to be invisible to the planner. With the measured academy
  scene, the same route scores **0 conflicts without the framing trucks and 27 with them**, at the
  **default 6.5 m body**. The honest outcome is that an insert leg can now be *refused* rather than
  driven through a truck — and that refusal lands **after** the approach leg, at the checkpoint, not
  at start: the maneuver ends with the warning "已在检查点结束：下一腿无可行路线（见日志）" instead of
  claiming success. The 0/27 number belongs to the default body: a live run with `AutoCalibrateBody`
  on swaps in the measured 8.43 m body, which changes the envelope and can change whether a route
  resolves.
- **`MinAirBar` ships as 0 / disabled, and its unit is unresolved in this repo.** The two call sites
  that print `truckFloat.airPressure` call it bar; a constant read off the same field is named psi; and
  this repo's own logs read 114–123 in it. Only the shipped `HasBrakeAir()` check (against the game's
  own `airPressureEmergency`) is unit-safe. Do not set this floor from a unit assumption.
- **Identity needs the sidecar**, and even with it, identity is absent for about **0.9 s after every
  ~0.5 m of truck travel** — the cache is keyed by a rounded window and each cold window costs one
  full probe walk (measured 0.884 s for a 90 m window). So identity is informative mainly at
  standstill, which is when the resolver runs; while driving it is at best one window behind.

## 5. Settings Overview

| Group | Key settings | Default | Notes |
|---|---|---|---|
| Master | `DryRun` | true | Compute only, send nothing |
| | `RefuseWithTrailer` | true | Refuse to start with a trailer |
| | `ControlWeight` | 20 | The host averages every channel writing the same field, so a low weight is not deference — it is being overwritten. A measured competitor at ≈14 turned our brake demand into positive throttle and drained the air reservoirs. Raise this above any ACC/overtake plugin running alongside |
| Body | `WheelbaseM` / `MaxSteerDeg` | 4.0 / 33 | Determines minimum turning radius — the two most important geometry parameters |
| | `VehicleLengthM` / `VehicleWidthM` | 6.5 / 2.6 | The box the obstacle conflict check sweeps. With `AutoCalibrateBody` on (default) both are overwritten from the game's own vehicle AABB - 8.43 × 2.47 m measured on the 6x2 in the course, versus these typed defaults (§52) |
| | `AutoCalibrateBody` / `TireHalfWidthM` | true / 0.2 | Overwrite **both** `VehicleLengthM` and `VehicleWidthM` with the game's own local AABB of the player's vehicle - the same box its traffic code centres every AI vehicle on (§52; measured 8.43 × 2.47 × 3.84 m against typed 6.5 × 2.6). If that read is unavailable the width falls back to 2×(outer wheel centre + this allowance) and the length keeps the typed value. Tractor only: a trailer is a separate object |
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
| | `ObstacleMarginM` | 0.05 | Per-side dynamic margin of the obstacle envelope |
| | `EnvelopePadM` | 0.05 | Per-side base padding of the obstacle envelope. Width = `VehicleWidthM` + 2×(this + `ObstacleMarginM`) = **2.8 m** on the typed 2.6 m width (length 6.5 + 0.1 + 0.2 = 6.8 m). With `AutoCalibrateBody` on (the default) the measured 2.47 m body gives 2.67 × 8.73 m, which leaves ~0.38 m/side in the 3.44 m practice-course bay mouth. The two defaults were lowered from §46's 0.25/0.5 (and from its recommended 0.05 pad + 0.2 margin) to 0.05/0.05 by §51, after the ATS course sweep showed the wider box refused every route (§51, design doc §53) |
| | `ObstacleLookaheadM` | 4.0 | Only conflicts this far ahead stop the truck. Note the swept footprint is ~8 m long, so a blocker registers about 7.5 m out regardless of this number |
| Re-planning | `ReplanWhileStopped` | true | Re-solve the route **only while the vehicle is stopped** — at a gear change, after 2 s held by a blocker, or when the distance to the spot starts growing instead of shrinking (a second-segment correction). Never mid-drive: a new route shares the pose but not the arc length, and the progress estimator cannot tell the two references apart |
| | `MaxReplans` | 3 | Cap on swaps; after it the route is frozen again. Hitting the cap does **not** abort — only the hotkey ends the maneuver |
| Finish | `HandbrakeOnFinish` / `RestoreAssistsOnFinish` | true | Apply handbrake and restore driver assists on completion |
| Visualization | `MapScalePxPerM` / `MapZoom` / `MapViewRadiusM` / `SnapToNavCurve` | 1.25 / 1.0 / 120 / true | |
| | `LockMapWindow` | false | Freeze the map panel's rect while picking (§8) |
| | `ShowMemoryItems` | true | Draw runtime-spawned cones, boards, movers, buildings and parked vehicles, read read-only from the game process (§41/§42) |
| | `BlockRouteWithMemoryItems` | true | Feed memory items (cones/boards/movers/buildings/parked vehicles) into the planner's obstacle set - routes avoid them or the start is rejected (§43). The game's own `0xC17` parking-marker pair is exempt: those boxes are the aim point, not objects. Failure mode unchanged: a fully blocked lot falls back to the frozen route after MaxReplans; only the hotkey ends that |
| | `SnapToParkingBay` | true | Read the game's parking bay out of the cone layout (two facing rows, capped at one end, open at the other), draw it, snap a pick inside it to the bay centre with the nose pointing in through the opening - and stop treating the bay's **own furniture** inside it as an obstacle (cone rows, boards, the two 8×8 m movers), because those otherwise refuse the very spot the job asks for (§50). A parked vehicle inside the bay is NOT exempt: an occupied bay stays occupied. The wall cones stay obstacles either way: the test is inset by 0.6 m |
| | `ConfirmedCorridorHalfWidthM` | 5.0 | How far a nav curve or lane vouches for the ground beside it (§6.6) |
| | `ArGroundTrimM` | 0.0 | AR ground trim (manual offset beyond the wheel-contact-point estimate) |

All numeric values are clamped by `Clamp()` to the safe ranges in this table on save.

Reserved fields that are not wired up yet (changing them has no effect; do not use them to
infer behavior): `AutoCalibrateRadius` (auto calibration of the turning radius — the algorithm
is written up in the design doc §6.1 but not implemented), `OvershootM`, `HazardOnFinish`,
`EngineOffOnFinish`.

### 5.1 JSON-only settings (marker following)

Two keys have no control on the settings page. They live in `%APPDATA%\ETS2LA\AutoParking.json` and
are edited by hand:

| Key | Default | Meaning |
|---|---|---|
| `SidecarPath` | `""` | Explicit path to `MemoryProbe.exe`. Empty means "resolve it": `<host exe dir>\Plugins\MemoryProbe.exe` first, then the plugin's own directory |
| `GameProcessName` | `eurotrucks2` | Which process the sidecar reads. Its own default is `amtrucks`, so ETS2 must name it |

There is no UI for them because the host's settings page has no text-input component (it ships
sliders, switches and one dropdown), so a path field cannot be rendered at all. `SidecarPath` is not
resolved against the DLL's directory — the host shadow-copies the DLL alone into a temp folder, so at
runtime that directory contains no exe. Every candidate is logged at startup with whether it exists,
plus which one was chosen.

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
**Extreme**. Changing it means re-parsing (restart), not a live knob. Cones are `Sign` items, so they are
already in at Medium; it is the `Curve` layer (barrier/fence/prop chains, §6.6) that needs **Extreme** —
`IgnoredItemTypes` drops `Curve = 0x2C` below it.

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

### 6.6 What an object actually is (`MapItemIdentity`, `Tools\ObjectProbe`)

A map item does not store a name. It stores a short code — `dlc_fr_219`, `1573`, `2x002` — and the code is
**not unique**: `dlc_fr_219` is a barrier column as `bld_model.dlc_fr_219`, a building scheme as
`bld_scheme.dlc_fr_219`, and a transformer as `model.dlc_fr_219`. The readable identity is the model file the
unit points at (`/model2/sign/traffic_cone_01_fr.pmd`), which lives in `/def/world/*.sii`. So identity is
resolved as *class-qualified unit name → model path → family label* (锥桶/护栏/围栏/墙/施工件/箱子/植被/路面标线…),
built incrementally — the whole table costs ~10 s offline, so it fills 6 files per map rebuild and items stay
*unclassified* until it knows them. **Unnamed objects are drawn but never block.** That is deliberate: an
obstacle has to be named to be trusted.

Real extents come free, contrary to what §26/§29 assumed: `MapItem.Kdop` is internal, but its type derives
from the public `KdopBounds` whose `Minimums[5]/Maximums[5]` are the very 25-DOP the game collides with.
Measured on a cone: 0.6 m × 0.6 m on the ground, 0.9 m tall. No PMD parse per token is needed for footprints.

`Tools\ObjectProbe` is the offline instrument that produced all of the above:

```
dotnet run --project Tools/ObjectProbe -c Release -- def    [game dir] [keyword…]   # /def/world/*.sii units
dotnet run --project Tools/ObjectProbe -c Release -- files  [game dir] [path] [filt] # list an archive directory
dotnet run --project Tools/ObjectProbe -c Release -- units  [game dir] <sii> [filt]  # units in one SII file
dotnet run --project Tools/ObjectProbe -c Release -- curve  [game dir] <sector> [tok]# one sector's Curve items
dotnet run --project Tools/ObjectProbe -c Release -- kdop   [game dir] <sector> [filt]# per-item k-DOP extents
dotnet run --project Tools/ObjectProbe -c Release -- mapscan [game dir] [keyword…]   # whole map, by class × model
dotnet run --project Tools/ObjectProbe -c Release -- token   <字符串|数值…>            # pack/unpack short codes
dotnet run --project Tools/ObjectProbe -c Release -- near    [game dir] <x> <z> [radius] # what is within N m of a spot
```

Cones get their own marker on the flat map - orange disc with a dark rim, plus an outer ring when the map
marks them collidable - and the canvas corner reads `身份 锥桶×N(最近 x m) · 静态障碍 M`, or `身份 未解析`
while the table is still filling (blank would not tell "nothing here" apart from "not read yet"). That
nearest-meters number is the one to argue with the game about: a cone line standing in front of the truck
next to `锥桶 0` means those objects are not map items at all - design doc §38 measured exactly that on an
ATS test course - and no fidelity setting changes it.

Census numbers differ per map, so always read the running game's own `.mbd`: ETS2's `europe` carries 959
cone items, ATS's `usa` carries 7 514. And the sector under the truck can live in a DLC archive
(`dlc_ks.scs`), which is why `near` searches every archive rather than only the base ones.

`mapscan` stages the whole map to `%TEMP%` and takes ~6–8 minutes. Design doc §38 records the counts it gave
(959 cone items — 927 of them `Sign`; 108 132 barrier items, mostly `Curve`) and the two premises it disproved.


### 6.7 Candidate route cost (the `路径代价` log block)

Every plan logs **all** the routes the planner considered, not just the winner: length, gear switches,
the ground area the route *demands* (the vehicle envelope the collision test already uses, swept along the
path and rasterized to 1 m cells, so ground driven over twice counts once) and how much of that area sits
on ground the map does not vouch for. "Vouched for" means: inside the sampled radius, within
**可信走廊半宽** of a prefab navigation curve or road lane, and not underneath a collidable map object.
It is a measurement, not a veto — the winner is still chosen by length plus the gear-change penalty, and
the harness asserts that supplying the trust grid changes nothing (`仪表不改变选路`). The map's route line
now reads `占地 A m²（未确认 B m²）`. Design doc §34 says why exposure to *unconfirmed* ground is the thing
worth minimizing now that prefab interior geometry is unreachable (§32), and records the measured candidate
table: in the harness scenario the loop-the-aisle family loses on both axes (67.1 m / 339 m² / 194 m²
unconfirmed) against the chosen compact back-in (49.1 m / 261 m² / 154 m²). **Offline numbers are measured;
the in-game readout is not yet.**

### 6.8 The route preview in the game view (AR)

Once a route exists, the AR overlay draws it on the ground **before it is driven**: the corridor the
vehicle will occupy, its centerline, and a filled magenta disc where it changes gear (the map window uses
the same hue for the same decision). Forward legs are cyan, reverse
legs orange — the same hues the flat map uses, so a leg on the ground and the leg on the map are
obviously the same leg. The corridor width is `RouteFootprint.EnvelopeSize().WidthM`, i.e. exactly the
envelope the collision test blocks on and §6.6 measures, so what you see, what vetoes the route and what
the log counts cannot drift into three different vehicles.

It is drawn as one batched `Draw3DLineWithGradient(left[], right[])` ribbon per gear run plus a
centerline sampled every 1.5 m (`DrawStepM`), because a filled polygon per pair of poses **was reported
too heavy on a real truck**. With no plan yet, the older straight line to the spot (5 m ticks) is still
drawn — it answers "do map coordinates line up with the in-game position", which the preview cannot
answer before a route exists. **Measured: the load of the per-segment filled version was too high. Not yet
measured: the load and the look of this one.**

## 7. Architecture Overview

```
AutoParkingPlugin.cs   Lifecycle / telemetry sampling / control registration / settings IO / phase machine
Settings.cs            All settings + Clamp() + Clone() (persisted off the tick thread)
Geometry.cs            Planar poses, heading↔forward, signed lateral error, rectangle/SAT overlap
Driving\
  Planner.cs           Layered planning: straight reverse → Reeds-Shepp forward → reverse sweep → two-segment shuttle
  ReedsShepp.cs        Builds CSC/CCC from turning-circle geometry, self-checks by re-integrating the endpoint
  GroundTrust.cs       Which ground the map vouches for: nav curves and lanes, minus collidable items, in 1 m cells (§6.6)
  RouteFootprint.cs    A route's demanded envelope rasterized over that grid - swept area and unconfirmed area (§6.6)
  ParkingPath.cs       Arc-length sampled path + gear-change points
  Follower.cs          Gear state machine + longitudinal PID + lateral Pure Pursuit/reverse law + steering shaping
  ControlOutput.cs     The single place commands are sent: channel publish/renew/release
  ObstacleScanner.cs   Traffic/parked vehicles → polygons
  PlannerSelfTest.cs   The 24 cases from 6.1
Rendering\
  MapGeometry.cs       map/ppd data → drawable geometry (incl. prefab descriptor parsing)
  MapItemProbe.cs      What the same node walk contains, bucketed by map item type (read-only, §6.4)
  MapItemSurface.cs    One item's type name / token / collision flag / ground shape - shared with the offline reader
  MapItemIdentity.cs   Class-qualified unit code → model file → family label; what says a cone is a cone (§6.6)
  MapOverlay.cs        Flat map window + spot picking/zoom/buttons
  ArOverlay.cs         In-game AR drawing
SettingsPage.razor     @page "/plugins/adjustments/local.autoparking"
Memory\
  KdopReader.cs        Background reader for the game's own item lists - runtime-spawned cones (§41/§42) onto the flat map
Tools\PlannerHarness\  Offline harness (§6.3): own project, excluded from the DLL by DefaultItemExcludes
Tools\MapSurfaceDump\  Reflects the installed TruckLib assemblies - what a plugin can actually read
Tools\MapSectorDump\   Reads one map sector straight out of the .scs files, no game (§6.5)
Tools\ObjectProbe\     Def lookup, per-item k-DOP extents and whole-map inventory, no game (§6.6)
Tools\MemoryProbe\     Reads the running game's kdop item lists from outside the process (§41 of the design doc)
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
  two pixels used to be enough to pick an arbitrary angle. The other half of it is the window itself:
  the canvas stops 8 px short of the panel edge, and ImGui grabs a window from anywhere along that
  border, so a press meant for the edge of the map could drag the whole rect — and the projection is
  recomputed every frame, so the spot slid out from under the drag. **Tick 锁定窗口 (lock the window)**
  in the map window's button row while you pick; untick it to move the panel. Two earlier attempts
  are on record so nobody repeats them: a permanent `NoResize` did not close the path (the log showed
  the window still tracking the mouse 1:1), and a permanently locked panel was rejected because the
  panel genuinely needs moving. The `选位开始 / 选位结束` log lines carry the drag radius, how far the
  map slid in between, whether the canvas moved and whether the window was locked — one pick with the
  lock off and one with it on is the whole test, and `画布位移` must read 0 when locked.
  **Measured: the failure. Not yet measured: the fix.**
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
