# Applying the official-plugin reverse engineering to AutoParking

- Date: 2026-10-14
- Target project: ETS2LA V3 (C# / .NET 10), installed build `ETS2LA.dll` assembly version `2026.10.5115.0`
- Workspace: repository root of this plugin. **No absolute paths in this document** — `..\..\` is the ETS2LA
  workspace that contains this repo, and `..\..\ReverseEngineering\` is where the reverse-engineering
  artifacts from this pass live.
- Purpose: record which conclusions from the decompiled official plugins change, confirm, or invalidate
  what AutoParking currently assumes, so nobody re-derives them.

> **Scope and honesty note.** Every claim below comes from static analysis of shipped binaries. Nothing was
> executed, injected, reflected-at-runtime or driven on a real truck. Items are labelled
> **[observed]** (read off the artifact), **[inferred]** (a reading of control flow), or **[unknown]**
> (needs a measurement). Where a claim contradicts `2026-09-30-autoparking-design.md`, that is called out
> explicitly — see §4.

---

## 1. What was reversed

Seven closed assemblies from the shipped install, all authored by `Tumppi066`:

| Artifact | Kind | Tick rate | Declared dependencies |
|---|---|--:|---|
| `Plugins\tumppi066.pathfinding\Pathfinding.dll` v1.2.8 | plugin | 30 Hz | `pathlib` |
| `Plugins\tumppi066.laneassist\LaneAssist.dll` v1.5.6 | plugin | 30 Hz | `pathfinding`, `pathlib`, `pidlib` |
| `Plugins\tumppi066.adaptivecruisecontrol\AdaptiveCruiseControl.dll` v1.5.0 | plugin | 15 Hz | `pathfinding`, `pathlib`, `pidlib` |
| `Plugins\tumppi066.visualizationsockets\VisualizationSockets.dll` v1.0.0 | plugin | 10 Hz | `pathlib` |
| `Plugins\tumppi066.internalvisualization\InternalVisualization.dll` v1.1.0 | plugin | 1 Hz | — |
| `Libraries\tumppi066.pathlib\PathLib.dll` v1.2.7 | `LibraryPlugin` | — | — |
| `Libraries\tumppi066.pidlib\PIDLib.dll` v1.0.4 | `LibraryPlugin` | — | — |

Method bodies were decompiled with `ilspycmd 11.1.0.9782`, then 25 selected methods were locked back into
REA evidence against `artifact SHA-256 + MVID + metadata token + signature hash + normalized-IL hash`
(ledger: `..\..\ReverseEngineering\_reconstruction\reconstruction-index.md`).

**The tokens and line numbers cited here are valid only for the SHA-256 values recorded in that ledger.**
After a host or plugin update, re-collect rather than trusting them.

Host types that this document quotes are decompiled and **saved alongside** the plugin output, so every
citation is checkable without re-running anything:
`..\..\ReverseEngineering\_host-decompiled\` holds `ETS2LA.Game.Output.GameOutput.cs` (332 lines),
`ETS2LA.Shared.Plugin.cs`, `ETS2LA.Shared.LibraryPlugin.cs`, `ETS2LA.Game.Output.ControlProperties.cs` and
`ETS2LA.Game.Output.ControlChannelDefinition.cs`. Reproduce with
`ilspycmd -t <Full.Type.Name> ..\..\ETS2LA-win-release-Portable\current\ETS2LA.Game.dll`.

---

## 2. Decisions of ours that the official code independently confirms

These need no action. They are recorded because each one was previously established by us at the cost of a
real-truck session, and now a second, independent implementation agrees.

| Our decision | Official corroboration | Label |
|---|---|---|
| Send **one signed pedal field**, never both `aforward` and `abackward` (`Driving\ControlOutput.cs:65-73`) | ACC uses `if (target − speed > −0.1) aforward = … else abackward = −…` and leaves the other null — `AdaptiveCruiseControl.cs:164-175` | [observed] |
| Turning **`ApplicationState.EnableAssists = false`** is the real way to stand the other assists down (`AutoParkingPlugin.cs:677`) | It is literally the first gate in both control plugins: LaneAssist `LaneAssistPlugin.cs:257`, ACC `AdaptiveCruiseControl.cs:155`. When false, they reset their PIDs and publish nothing. | [observed] |
| `steering` and pedal live in **separate averaged buckets** | LaneAssist publishes `steering` only (channel `LaneAssist.SteeringCorrection`); ACC publishes pedal only (`AdaptiveCruiseControl.Acceleration`). No official plugin mixes the two, whereas our `drive` channel carries both in one event. | [observed] |
| Our map-access route is supported, not a hack | `MapGeometry.cs:164-171/201/448` (`GetMapData` → `GetFileSystem` → `PpdFileHandler.SetFileSystem` → `Nodes.Within` → `GetPpdFile as PrefabDescriptor`) matches InternalVisualization `:98-117` and `PrefabsRenderer.cs:60/73/127` step for step. | [observed] |
| Only nodes have a spatial index; "what is near the truck" must be answered by walking nodes | All three data-heavy plugins use exactly `mapData.Nodes.Within(minX, minZ, maxX, maxZ)` and nothing else (Pathfinding `:122`, InternalVisualization `:117`, VisualizationSockets `:233`). | [observed] |
| `DataFidelity` filtering is a real constraint on what we can see | Pathfinding grows its path from the same filtered `MapData` and still cannot reach depot interiors — consistent with the §1 non-goal that parking slots in low-fidelity classes are not enumerable. | [inferred] |
| No native back door exists | `inspect-managed-native-boundaries` returns **0 PInvoke entries with `coverage.state = "complete"`** for all seven assemblies. Everything official does, it does through the host. | [observed] |

---

## 3. What is newly reusable

### 3.1 `PathLib` and `PIDLib` are legitimately linkable

Both sit in `current\Libraries\`, declare themselves as `LibraryPlugin` (identity only — no lifecycle, no Tick),
and are consumed by other plugins through `PluginInformation.Dependencies`. Linking them is exactly the
supported pattern: add a `<Reference>` with `HintPath` to
`..\..\ETS2LA-win-release-Portable\current\Libraries\tumppi066.pathlib\PathLib.dll` plus `Private=false`,
matching the existing `AutoParking.csproj` style, and list `tumppi066.pathlib` / `tumppi066.pidlib` in `Info.Dependencies`.

Whether we *should* use them is a separate question — see §5.2 for the PID caveat.

### 3.2 The official control-tuning display is worth copying first

`PIDLib.PIDController` carries its own ImPlot monitor: ten seconds of `PIDState` history, seven traces
(`Actual / Target / P / I / D / Output`), auto-scaled axes, opened and closed by a button in the ACC overlay
(`PIDController.cs:40-47, 266-373`; `AdaptiveCruiseControl.cs:223-237`).

LaneAssist does the same thing more cheaply and this is the more useful shape for us — it shows the two
**contribution terms separately**, not just the sum:

```
° <angle term>      ∆ <cross-track term>      slider <final steering>
```
(`LaneAssistPlugin.cs:406-408`)

**Recommendation:** the single highest-value adoption is an AutoParking overlay panel plotting
cross-track error, heading error, demanded steering and demanded acceleration per tick. Our current loop is
"drive it on a real truck, read the log, screenshot it"; a term-level plot turns each of those iterations
into something readable while the truck is still moving. Cost: a bounded history buffer in the 60 Hz Tick
and an ImGui plot; the official implementation does 24 LINQ min/max passes and 12 array allocations per
frame (`PIDController.cs:292-320`), so pre-allocate instead of imitating that part.

### 3.3 Cornering speed law with tuned constants

`PathCurvatureConstraint` computes a path speed limit as:

```
R     = circumradius of three path points spaced 1.5 m × PointStep apart
v     = sqrt(MaxLateralAcceleration × R^1.2)      ← exponent 1.2, not the physical 1.0
v     = max(v, MinimumSpeed)
weight= EaseOutSine(1 − distance / MaximumDistance), blended toward 55.555557 m/s (200 km/h)
result is ignored above 38.333336 m/s (138 km/h)
```
(`PathCurvatureConstraint.cs:42-58, 73-115`, defaults `ACCSettings.cs:27-33`)

The `R^1.2` exponent is a deliberate departure from `sqrt(a·R)`; with `MaxLateralAcceleration = 1.25 m/s²`
(R = 100 m: physical 11.2 m/s vs 22.4 m/s with 1.2) it amounts to "the game truck takes bends far faster than
physics allows". **[inferred]** — but the constant set is what an official plugin actually ships, so it is a
better starting point for our approach-speed shaping than a textbook formula. The author also notes that
raising `PointStep` reduces "phantom braking" (`AdaptiveCruiseControl.cs:313`), which is the same
oversampling-then-averaging artifact we would hit on a discretised footprint.

### 3.4 Trailer telemetry has exactly one switch

`GameTelemetry.Current.ReadTrailerData = true` (VisualizationSockets `:67`) is the only way to get trailer
world position, hook position and wheel positions. The plugin never resets it on disable. `RefuseWithTrailer`
stays default-on per project policy; this is only the note that a future trailer work item has one entry point.

### 3.5 A blinker-based human-intent channel that is not polluted

Our known trap is that `truckFloat.user_*` is the virtual gamepad echo, so it can never prove a human is
driving. Pathfinding sidesteps this entirely by reading the **actual light state** —
`telemetryData.truckBool.blinkerLeftActive` / `blinkerRightActive` — as the intent signal
(`PathfindingPlugin.cs:482-519`), and prevents self-feedback with a one-way `didStartIndicate` latch plus a
1 s `Stopwatch` gate. That pattern is safe to reuse because it is not sourced from the injection echo.

---

## 4. Corrections — read this against the existing design doc

### 4.1 `ControlProperties.Weight` defaults to **1.0**, and no official plugin sets it

Verified directly against the shipped assembly:

```csharp
public class ControlProperties {
    public ControlBooleanType BooleanType;
    public float Weight = 1f;          // ← field initializer
}
```

All six official control writes leave it alone (`new ControlProperties()` or setting only `BooleanType`) —
see `_rea-evidence` + `LaneAssistPlugin.cs:384`, `AdaptiveCruiseControl.cs:183`, `PathfindingPlugin.cs:149-153`.

**Consequence for us.** Our default is `ControlWeight = 20.0` (`Driving\ControlOutput.cs:20`, `Settings.cs:27`).
When LaneAssist is also publishing `steering`, the host's weighted average gives us
`20 / (20 + 1) = 95.2 %`, **not** exclusive ownership. Our real protection is §2's `EnableAssists = false`,
which stops LaneAssist publishing at all; `Weight` is only the second line of defense for the window before
that flag takes effect. This also means the 20 value is not "safe by construction": at `Weight = 20` a
conflicting official request still moves our output by ~4.8 %, which at low parking speed is small, but which
we should not describe as immunity. **[observed]** for the default and for the six call sites;
**[unknown]** for the arithmetic's real-world effect, which is only measurable on a truck.

This matches the formula already recorded in the design doc (`Output.cs:284-291`), so the design doc was
right; the correction is to the claim that "official plugins leave Weight at default therefore we dominate".

### 4.2 `steering` is averaged exactly like `acceleration` — and `Weight = 0` is a divide-by-zero

`GameOutput` was decompiled from the installed `ETS2LA.Game.dll` to settle this. Averaging is done over a
per-frame bucket keyed by field name, with the channel's `Weight` as the multiplier
(`GameOutput.ProcessChannel` and the flush loop):

```csharp
// accumulation, per channel per frame
if (text == "aforward" || text == "abackward") text = "acceleration";
curFrameFloats[text].Add(new Tuple<float,float>(weight, item));   // weight from channel.Properties.Weight

// flush, per bucket
float sumW = list.Sum(v => v.Item1);
float out  = list.Sum(v => v.Item1 * v.Item2) / sumW;             // ← division by the weight sum
out = Math.Clamp(out, -1f, 1f);
```

Three consequences, all **[observed]**:

1. **`steering` and `acceleration` are the only two named buckets**; every other float goes to the
   `else` branch and is written to legacy by field name. So `steering` competes across channels on identical
   terms to the pedal. §4.1's `20/(20+1)` arithmetic is therefore sound, and open question 1 from the first
   draft of this document is closed.
2. **`Weight = 0` on the only contributing channel divides by zero.** The host does not guard
   `sumW`, so a lone zero-weight channel yields `NaN`/`∞` into `WriteFloat`. `ControlWeight` is clamped to
   `[1.0, 100.0]` in `Settings.cs:154`, which already protects us — keep that lower bound, and do not
   introduce any code path that can publish with a default-initialised `ControlProperties` we then zero out.
3. **The transport branch is one notch richer than design doc §2.2 records.** `steering` writes modern only
   (float at offset 0, a "nonzero" bool at 4, a timestamp double at 5). `acceleration` writes **modern**
   (offsets 13/17/18) when `GameSettings.Current.EnableModernOutputForPedals` is on, and otherwise splits by
   sign into legacy `aforward`/`abackward`. Every other float, and all booleans including `TrueToToggle`
   pulses, go to legacy unconditionally. That matches the `transport=memory|legacy` distinction already in
   `AGENTS.md`; the design doc's phrasing ("`acceleration` 只写 legacy") should be corrected to
   "unless `EnableModernOutputForPedals`".

### 4.3 "Init does not touch the EventBus, subscribe in OnEnable" is our convention, not the host's

The design doc records this as a host convention (`Shared.cs:51-77`). The official code does not follow it
uniformly: ACC fetches telemetry in `Init()` (`AdaptiveCruiseControl.cs:87-91`), LaneAssist and Pathfinding
subscribe in `OnEnable()`, and VisualizationSockets overrides neither `Init` nor `Shutdown` at all. Keep our
convention — it is the cleaner one — but stop citing official practice as the reason. **[observed]**

### 4.4 `DrivingMode` is not ordered by capability

```
AdaptiveCruiseControlOnly = 0
FullSelfDriving           = 1
LaneAssistOnly            = 2
```

LaneAssist gates with `if (DrivingMode < DrivingMode.FullSelfDriving) return;` (`LaneAssistPlugin.cs:250`) and
ACC with `if (DrivingMode == DrivingMode.LaneAssistOnly || …)` (`AdaptiveCruiseControl.cs:155`). Both happen
to be correct **only because of this specific 0/1/2 arrangement**. Any AutoParking code that compares
`DrivingMode` with `<` or `>` inherits a coincidence. Prefer explicit `==` tests. **[observed]**

We currently save and restore `DrivingMode` (`AutoParkingPlugin.cs:674, 696`) without comparing it, so no
change is needed today; the note is to stop the pattern from appearing later.

### 4.5 Tick scheduling semantics, now read from the base class rather than the broken SourceCode

`Plugin.RunningThread` decompiled from the installed `ETS2LA.Shared.dll`:

- `OnEnable()` calls `Task.Run(RunningThread)` — **`Tick()` runs on a background thread, not the render thread**.
  Anything shared with an ImGui/AR callback needs its own synchronisation. We already take `lock (sync)`
  around telemetry staleness (`AutoParkingPlugin.cs:186-188`), which is consistent with this.
- The interval is recomputed from `TickRate` **every iteration**, so `TickRate` can be changed at runtime.
- **Overruns are not caught up**: if `Tick` exceeds the period, the next deadline is reset to "now". Exceeding
  budget degrades to "as fast as possible" with higher CPU, not to corrupted state.
- `Tick()` exceptions are swallowed into one `Logger.Error` and the loop continues. "Throw to abort" is not a
  semantic we can rely on — and, given project policy that only the hotkey may end a maneuver, it is not
  something we want.

This confirms the design-doc claims `TickRate => 20.0f` default and "exceptions do not kill the thread" using
the shipped binary. **[observed]**

### 4.6 Our 60 Hz is four times the official highest rate

LaneAssist 30, ACC 15, VisualizationSockets 10, InternalVisualization 1. AutoParking declares 60
(`AutoParkingPlugin.cs:148`), i.e. a **16.7 ms budget**. The geometry build is annotated as costing 10–50 ms
(`AutoParkingPlugin.cs:296`) and runs on the Tick thread — which is survivable today only because it is
throttled by `GeometryInterval` and gated on the map window being open, combined with §4.5's no-catch-up
scheduling. **[observed]** for the rates; **[inferred]** for why it is tolerable; **[unknown]** for the actual
Tick duration, which needs a stopwatch measurement.

### 4.7 `SourceCode` citations vs installed assemblies

The design doc cites paths like `ETS2LA.Game\Output\Output.cs:284-291`. `SourceCode\` is a broken clone (no
objects), so those line numbers cannot be checked and, per our own rule, the source snapshot is not
authoritative. `..\..\ReverseEngineering\_host-api-surface.md` is a reflection dump of the **installed**
host DLLs and `..\..\ReverseEngineering\_libraries\*\decompiled\` is decompiled installed code. When the two
disagree, prefer the installed artifact. **[observed]**

Two design-doc items have now been re-derived from the shipped binary and can drop their SourceCode-only
citation: the averaging formula (§4.1/§4.2, `GameOutput.ProcessChannel`) and the transport split
(§4.2 item 3). The remaining §2.2 claim not re-checked in this pass is the shadow-copy load path
(`%TEMP%\ETS2LA\PluginShadow`), which needs `ETS2LA.Backend.PluginHandler`, not decompiled here.

---

## 5. Do not adopt these

### 5.1 Do not subscribe to `Pathfinding.PlannedPathData` for parking

Tempting, wrong for us. Concretely:

- Path length is 5 items by default (max 10) — `PathfindingSettings.cs:16` — where an item is one
  `ParsedRoadList` (a chain of contiguous roads) or one prefab traversal.
- Pathfinding only enters a prefab by following **GPS route coverage** (`navigationData.Contains(nodeUid)`,
  `PathfindingUtils.cs:448-460`) and falls back to "the node most in front of the truck". A depot mouth that
  is not on the route is not entered deliberately.
- For a prefab item, `PlannedItem.GetLane()` returns `null` (`PlannedItem.cs:53/87/94/121`): **there is no lane
  concept inside a prefab at all**, so there is no lateral reference to park against.
- `PathCurvatureConstraint` and `LaneAssist` both assume forward driving; `Interpolate` returns `null` at
  path bounds (`PlannedPathData.cs:77-80`), so the last 1.5 m of any path is unreachable.
- Pathfinding has no reverse-gear concept anywhere in its 1 296 lines.

Our own node walk stays the right approach. AutoParking's stated non-goal of not overlapping with
"ACC + Pathfinding" (§1 of the design doc) is unaffected.

### 5.2 `PIDLib` has a 1-second proportional-only window after every Reset

```csharp
if (sinceStart.Elapsed.TotalSeconds < 1.0) { /* records I and D into history */ output = kp * error; return; }
```
(`PIDController.cs:217-232`)

Integral and derivative keep accumulating but are **excluded from the output** for a full second after
construction or `Reset()`. Official usage hides this because ACC and LaneAssist reset only on engage/disengage.
A parking maneuver that re-plans or re-legs frequently would keep resetting and could effectively run
proportional-only for the whole job. **[observed]** behaviour, **[inferred]** impact. If we adopt `PIDLib`, either
do not call `Reset()` between legs, or write our own controller — which is what `Driving\Follower.cs` already is.

Related hardcoded details to know before depending on them: the derivative low-pass coefficient `0.2` does not
scale with `deltaTime` (`PIDController.cs:49, 214`), and `deltaTime <= 0` silently degrades that call to
proportional-only (`:161-175`), so a `Reset()` immediately followed by an update loses the D term too.

### 5.3 ACC's cross-plugin events are published from render callbacks

`AdaptiveCruiseControl.LeadingVehicle` and `AdaptiveCruiseControl.TargetParsedSemaphore` are published only
inside the AR render callback (`LeadingVehicleConstraint.cs:303/343`, `PathSemaphoreConstraint.cs:361/365`),
and the leading-vehicle one is gated on `ACCSettings.RenderLeadingVehicle` (`:294`). **Consumers can silently
receive nothing while ACC is nominally enabled.** Any AutoParking feature that wants a leading vehicle must
query `TrafficProvider.Current` itself. **[observed]**

### 5.4 Official failure-handling patterns we should not imitate

- `invalidPrefabTypes` as a **permanent** negative cache: one PPD parse failure hides that model forever
  (`PrefabsRenderer.cs:56/63`). PPD failure can just mean the file system was not attached yet — which is
  exactly the state our own `MapGeometry.cs` passes through.
- Empty `catch {}` around per-point geometry (`PathCurvatureConstraint.cs:84-105`,
  `PlannedPathData.cs:182-184`, `LaneAssistPlugin.cs:647-649`).
- Event subscriptions with no matching unsubscribe (Pathfinding has no `OnDisable` at all; InternalVisualization
  and VisualizationSockets never unsubscribe) and a global telemetry flag never restored
  (VisualizationSockets `:67` vs `:299-304`).
- `OnEnable` in InternalVisualization enumerates `GameHandler.Current.Installations` and takes the **first
  parsed one by index**, comparing an `int` across ticks (`:87-95`); LaneAssist does the same
  (`LaneAssistPlugin.cs:164-175`); Pathfinding does it correctly by comparing the `Installation` reference
  against `ApplicationState.Current.RunningGame` (`:93-113`). Use Pathfinding's form — and check which one
  `MapGeometry.cs` uses.
- `EnsureContinuity` (`PathfindingUtils.cs:275-392`) is 1 371 bytes of ad-hoc seam patching with magic
  constants (4 m / 20 m / ×1.6), and the prefab→prefab branch's `else if` at `:585-588` can switch to an
  alternate path even when `EnableLaneChanges` is off. Do not treat it as a reference implementation.

---

## 6. Facts newly established (candidates for the design doc §2 table)

| Fact | Evidence | Label |
|---|---|---|
| `Plugin.OnDisable`/`Shutdown` base implementations only set `_IsRunning = false`; overriding them without calling `base` leaves the Tick thread running | decompiled `ETS2LA.Shared.Plugin` | [observed] |
| `LibraryPlugin` exposes `Info` only — libraries have no lifecycle and no Tick | decompiled `ETS2LA.Shared.LibraryPlugin` | [observed] |
| Cross-plugin data goes through **bare event-name strings**; `Dependencies` is a load-order declaration, not a compile-time link (LaneAssist depends on `tumppi066.pathfinding` yet never references its assembly) | `LaneAssistPlugin.cs:112` vs `:140-148` and Pathfinding's assembly references | [observed] |
| A publisher must publish on failure: Pathfinding emits `HasError = true` payloads on all six early exits (`PathfindingPlugin.cs:314/326/338/349/360/404`) so consumers can show a stopped state | [observed] |
| Output limit as loss-of-confidence: LaneAssist **stops publishing** when `|steering| > 1` and clears the path instead of clamping (`LaneAssistPlugin.cs:371-376`) | [observed] |
| Engagement debounce: LaneAssist requires 6 consecutive ticks (~200 ms at 30 Hz) before treating an engage/disengage transition as real (`:239-245`) | [observed] |
| Cross-side lane changes are explicitly refused (`PathLib` logs "crossing sides is not supported", `PlannedItem.cs:147-153`); lane changes are one lane at a time | [observed] |
| Lane-change length is `5 × max(20 m/s, current speed limit)` metres, smoothstep-blended over a fractional lane index, with geometry delegated to the host's `InterpolateBetweenLanes` (`PlannedItem.cs:159-202`) | [observed]; tuning values unverified |
| Filtered steering is disabled **above** 98 km/h: `RawControlSpeedThreshold = 27.222223` m/s both ways (`LaneAssistSettings.cs:14`), described as preventing lane sway (`Adjustments.cs:57`) — i.e. Kalman filtering is a net liability at speed | [observed]; rationale inferred from the UI string |
| Low-speed steering gain is read from the **host's global** `AssistanceSettings.Current.SteeringSensitivity` ({Slow 1.5, Normal 2.0, Fast 3.0}), ramping to 1.0 by 70 km/h (`LaneAssistPlugin.cs:39-57, 322-331`) | [observed] |
| Blame-the-path rather than the actuator: three separate "too far / too high / no point" conditions clear the path instead of limiting output | [observed] |
| Host buckets are only `steering` and `acceleration`; every other float writes legacy by field name, and booleans are handled once per channel via `BoolsProcessed` | decompiled `ETS2LA.Game.Output.GameOutput` | [observed] |
| `ProcessChannel` reflects `typeof(ControlVariables).GetFields()` per channel per frame — the host's own per-frame cost, and the reason a zero-weight-only bucket yields `NaN` (unguarded `/ sumW`) | decompiled `GameOutput` | [observed] |
| `TrueToToggle` is executed as `Task.Run` → write true → sleep → false against the **legacy** segment only, so pulse-type inputs (gears, handbrake) never take the modern path regardless of `EnableModernOutputForPedals` | decompiled `GameOutput`, consistent with `AGENTS.md` | [observed] |

---

## 7. Open questions this pass did not settle

1. **`TrafficLightState` numeric semantics.** ACC tests raw ints — `state != 2 && state != 1` for lights,
   `state != 1 && state != 0` for gates (`PathSemaphoreConstraint.cs:292/310`). The enum is host-side and was
   not decompiled, so light-colour meaning is still unconfirmed. **[unknown]**
2. **Are `core-plugins` / `official-plugins` public?** The leaked author paths
   (`C:\Users\Tumppi066\Documents\GitHub\core-plugins\…`, `…\official-plugins\…`,
   `/home/tuomas/Documents/GitHub\official-plugins\…`) show two upstream repos and two build machines, plus a
   source-file naming scheme (`Program.cs`, `Utils.cs`, `Libraries\PathLib\Classes.cs`). Existence and
   availability were not checked. **[unknown]**
3. **`Parlot` appears in all seven assemblies' references** yet no plugin source touches it. Presumably some
   host/TruckLib signature exposes a Parlot type, forcing Roslyn to record it for every consumer. **[inferred]**,
   unverified — and it matters only if it tells us something about our own `Private=false` referencing.
4. **Real Tick cost at 60 Hz** (§4.6) — needs a stopwatch, not analysis.
5. **Whether `DataFidelity` filtering actually drops the map classes our slot candidates live in on this
   install.** The RE pass confirms only that Pathfinding reads the same filtered `MapData`; the concrete
   fidelity behaviour of a given depot is still our own open measurement.

> Closed during this pass: the original question 1 — *does `steering` average across channels like
> `acceleration`* — was answered by decompiling `GameOutput`; see §4.2.

---

## 8. Next steps, and what happened to them

Status of this list, recorded 2026-10-14 (design doc §37 has the same run-through in the project's own log style).

| # | Step | Status |
|---|---|---|
| 1 | **Add a term-level control plot** (§3.2) | **Done.** `Driving\ControlTrace.cs` + `Rendering\ControlPlot.cs`, settings switch "控制分项曲线", default off. ImDrawList instead of ImPlot (see §3.2's reason); ring buffer 1024 samples; right edge anchored to the newest sample so curves survive a finished run. |
| 2 | **Restate the `ControlWeight` comment** (§4.1/§4.2) | **Done.** `Settings.cs` now carries the 1.0 default, the 20:1 share, `steering`'s separate bucket, and why the 1.0 lower bound is load-bearing. `ControlOutput`'s class summary carries the two-bucket and transport facts. |
| 3 | **Audit `MapGeometry.cs` installation selection** (§5.4) | **Done.** `FindMapData` now prefers `ApplicationState.Current.RunningGame` and only falls back to the first parsed one, with the fallback kept because `IsParsed` can hold while `GetMapData()` is null. The `(X, Y) = (map X, Z)` plane convention at the `Nodes.Within` call is now spelled out. |
| 4 | **Borrow the cornering-speed constants** (§3.3) | **Deferred deliberately.** The constants are `[observed]`; that they suit *our* truck is `[unknown]`. "Evidence before fixes" says a curve first - so is §1, which is why §1 was built before this. |
| 5 | Do not link `PIDLib` (§5.2) | **Standing recommendation, no action.** `Driving\Follower.cs` already does the job without the 1-second proportional-only window. |
| 6 | **Port the corrected host facts into the design doc** | **Done.** Design doc §37, as a dated log entry rather than a rewrite of §2 in place, so the cross-references the project already makes (§12, §17, §24, §27, §33, §36) keep working. §2.2's transport line is flagged there as needing its `EnableModernOutputForPedals` condition. |

### Worth knowing before you trust a "green" run

The harness was **already red at `HEAD` before any of this**: closed-loop 4/5, three ✗ marks, exit code 1
(gate is `Program.cs:153`, `simPassed == 5`). Verified by running a pristine `git worktree` at `HEAD`
alongside the modified tree - **the two outputs are byte-identical**, so these edits change no verdict
(`diff` empty, 13 build warnings in both).

The three ✗ are `右前 45 度进库` at **0.01 m** position error, the same case in `CompareSteering`, and a
`等待障碍离开` timeout. **Not fixed here**, on purpose: lowering that bar is explicitly forbidden by
`AGENTS.md`, and telling a convergence limit from a quantisation artifact needs measurements this analysis
cannot produce. See step 1.

Also worth knowing: `Tools\PlannerHarness\PlannerHarness.csproj` lists its sources by hand, so the new
`Driving\ControlTrace.cs` had to be registered or the harness would have failed to build while the plugin
built fine - the failure mode design doc §36 ends by warning about.

---

Still unverified after this pass, regardless of the above: real-truck behaviour of every claim tagged
`[unknown]`, the render cost and lock behaviour of the new window under a 60 Hz writer, and `TrafficLightState`
enum semantics (open question 1 of §7).
