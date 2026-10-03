# AutoParking — ETS2LA V3 Third-Party Auto Parking Plugin

Pick a parking spot on a **flat (2D) map**, and the plugin draws it onto the game screen as an
**AR overlay**, then automatically performs **forward / reverse / gear shifting (automatic
transmission) / braking / steering** to back the tractor into the spot.

> **Current status: M4 — closed-loop tuning on the real truck.** The planner and controller have
> been verified offline (see "Self-Test Tools" below). Gear-shift actions are confirmed to reach
> the game; what still stops a real end-to-end park is **longitudinal authority** (the truck
> creeps at ~0.5 km/h on long reverse legs, so long maneuvers run out of time) and **another
> control channel holding throttle during the maneuver**, which overrides our brake and drains the
> air reservoirs. See "Troubleshooting" for how both are read out of the log.
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
   In this repo it is deployed to `E:\ETS2LA\V3-C#\ETS2LA-win-release-Portable\current\Plugins\`.
3. **Restart ETS2LA** (plugins are shadow-copy loaded; hot reload is unreliable).

Dependencies: the plugin references `ETS2LA.*.dll`, `TruckLib*.dll`, and `Hexa.NET.ImGui.dll`
under `current\` directly, so it **must live in the same install tree as the ETS2LA host**.
These references are `Private=false` in the `.csproj`, so host DLLs are not copied into the
plugin directory.

## 2. Settings Page and Map

- Settings page route (auto-discovered by convention, no registration needed):
  `/plugins/adjustments/local.autoparking`, i.e. Settings → Adjustments → **Auto Parking**.
- Two visualization windows are toggled from the settings page:
  - **Flat map** (ImGui window, on by default): road lane lines, prefab edges/navigation curves,
    obstacles, current path.
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

## 4. Usage Flow

1. Drive the truck near the parking spot (path length cap: see `MaxTakeoverDistanceM`).
2. Click/adjust the target pose on the flat map, or use "Saved spot" to recall the last one.
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

### 6.3 Offline Closed-Loop Simulation (outside the repo, `scratch\PlannerHarness`)
A bicycle model drives the real `Follower`, reporting lateral/heading error, gear-pulse
counts, and steering quality metrics (full-lock duration, steering reversals, total steering
wheel travel. Named cases, each one added to pin down a bug that was only visible in a trace:
**deaf gearbox** (pulses never engage — must degrade to "keep driving and re-send" instead of
waiting forever), **blocker appears mid-maneuver** (re-planning off holds until the budget runs
out, on reaches the spot), **automatic that drops to neutral at a standstill** (gear confirmation
must accept the dashboard reading, or one shift costs 110 pulses), **two-segment shuttle** (the
only multi-leg route; it used to drive through the target with `remaining` frozen), **unstopppable
overshoot** (must not pin the wheel chasing an aim point that ended up behind the truck), and a
synthetic **recession** sequence that feeds states where the distance to the spot grows, so the
"stop and re-solve a correction" watchdog is actually exercised.
It lives outside the plugin directory because the plugin `.csproj` uses default globbing —
any `.cs` file would be compiled into the DLL.

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
  MapOverlay.cs        Flat map window + spot picking/zoom/buttons
  ArOverlay.cs         In-game AR drawing
SettingsPage.razor     @page "/plugins/adjustments/local.autoparking"
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
| `brake probe sent_accel=-0.50 … user_brake=0.50` | Actual signed value sent + game's echo. When `sent_accel` is negative you should see `user_brake` rise and `user_throttle` stay 0 |
| `gear probe request=Reverse → gear=0 dash=-1 … shifter_type=…` | One line per gear pulse, with the stage that asked. `shifter_type` decides whether this truck accepts `gear_drive/gear_reverse`. **`gear=0` with `dash=-1` is normal**: an automatic falls back to neutral at every standstill, so confirmation reads either signal — a pulse storm here means that latch is failing |
| `controls registered (…Toggle / .Abort)` | One line at startup; missing means `Init()` never ran |

Four field observations that each turned out to be a real bug, with the tell that found it —
all recorded with evidence in `docs\2026-09-30-autoparking-design.md` §17–§22:

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
- **Gear pulses repeat at ~1 Hz** → the automatic dropping to neutral at standstill is normal;
  confirmation must accept the dashboard reading. A pulse storm means it does not.

Two older pitfalls, still the first things to suspect:

1. **Brake becomes throttle**: the host folds `aforward`/`abackward` into a single
   `acceleration` bucket with a weighted average, then dispatches by sign. Sending both
   fields ⇒ they average into half throttle. The correct approach is to send **one signed
   field**.
2. **`user_*` is the virtual gamepad's echo**: values injected via the SDK show up in `user_*`
   as "player input". Do not use them to judge "is a human driving" — it will misfire.

Settings file: `%APPDATA%\ETS2LA\AutoParking.json` (includes the last selected spot; survives
plugin reloads). Logging uses Spectre.Console markup — **bare `[Tag]` in messages is silently
swallowed**.

## 9. References

- ETS2LA V3 source: `E:\ETS2LA\V3-C#\SourceCode` (read-only reference, do not modify)
- Flat map rendering reference: `official-plugins\Plugins\InternalVisualization`
- Algorithm reference: V2 Python version (V3's core ACC is closed source)
- Design doc & implementation log: `docs\2026-09-30-autoparking-design.md`

---

> 中文版说明文档：[README.zh-CN.md](README.zh-CN.md)
