using System;

namespace AutoParking;

public enum ReverseLateralLaw
{
    CrossTrackPd,
    ReversePurePursuit
}

[Serializable]
public sealed class AutoParkingSettings
{
    public int SettingsVersion { get; set; } = 1;

    // General
    public bool DryRun { get; set; } = true;
    public bool RefuseWithTrailer { get; set; } = true;

    /// <summary>
    ///  Weight on our control channels. The host averages every channel that touches the same
    ///  field and splits the result by sign, so a lower weight does not lose politely: another
    ///  plugin publishing full throttle turns our brake demand into a positive average, our pedal
    ///  never reaches the wheels, and the truck drains its air reservoirs fighting it. Measured in
    ///  the game at an opposing weight of about 14, hence a default well above that.
    ///
    ///  Two numbers worth knowing, both read off the shipped assemblies rather than inferred:
    ///  ControlProperties.Weight initialises to 1.0 and no official plugin touches it, so against
    ///  LaneAssist or ACC this weight is a 20:1 share, not ownership. The real reason the official
    ///  assists stand down is that we clear ApplicationState.EnableAssists. And steering is its own
    ///  averaged bucket, competing on exactly the same terms as the pedal, so a conflict shows up as
    ///  a smeared wheel rather than as one side winning.
    ///
    ///  The lower bound is load-bearing: GameOutput divides the weighted sum by the sum of weights
    ///  with no zero guard, so a bucket fed only by weight-0 channels yields NaN straight into shared
    ///  memory. Never allow 0 here.
    /// </summary>
    public double ControlWeight { get; set; } = 20.0;

    // Engagement limits
    public double MaxTakeoverDistanceM { get; set; } = 30.0;
    public double EntryDistanceM { get; set; } = 12.0;
    public double OvershootM { get; set; } = 0.5;

    // Vehicle geometry. What the game publishes here is uneven, and it is worth recording which of
    // these four is a fact we read and which is a number we type:
    //
    //   - Wheelbase      measured, see AutoCalibrateRadius below.
    //   - MaxSteerDeg    NOT in telemetry and not in the truck defs. The only value SCS ships is a
    //                    comment in def/vehicle/physics.sii ("maximum wheel turn (30degrees)"), which
    //                    is a global physics note rather than this truck's parameter. So this stays
    //                    hand-set, and it is the one input to the turning radius we cannot obtain.
    //   - VehicleLength
    //   - VehicleWidth   not in telemetry either. The chassis/cab/hood each carry their own model
    //                    bounding box (TruckLib.Models.Model.Open(path, fileSystem).BoundingBox -
    //                    measured 3.14 x 1.34 x 8.99 m for a Cascadia 6x2_short chassis), so real
    //                    extents exist but only per accessory part, and which parts are fitted
    //                    changes with the configuration. Unioning them is a separate piece of work;
    //                    until then these two are display-only, as the settings page says.
    public double WheelbaseM { get; set; } = 4.0;
    public double MaxSteerDeg { get; set; } = 33.0;
    public double VehicleLengthM { get; set; } = 6.5;
    public double VehicleWidthM { get; set; } = 2.6;

    /// <summary>
    ///  Overwrite <see cref="WheelbaseM"/> with the wheelbase measured from the wheel positions the
    ///  game publishes as vehicle configuration, so it follows the truck instead of a number typed
    ///  while driving some other one. Off means the hand-typed value wins outright.
    /// </summary>
    public bool AutoCalibrateRadius { get; set; } = true;

    /// <summary>
    ///  Size the swept box from the truck that is actually being driven: the game's own local AABB of
    ///  the player's vehicle, read from memory the way its traffic code reads every vehicle (design doc
    ///  §52). It is the only source for the vehicle's LENGTH - measured on the ATS course's 6x2 as
    ///  8.43 × 2.47 × 3.84 m against typed defaults of 6.5 × 2.6, i.e. the box was nearly two metres
    ///  shorter than the truck. When that read is unavailable the width falls back to the wheel centres
    ///  plus <see cref="TireHalfWidthM"/>; the length keeps the typed value.
    /// </summary>
    public bool AutoCalibrateBody { get; set; } = true;

    /// <summary>
    ///  Half a tyre, added to each side of the outer wheel centre - used only when the body box above
    ///  could not be read. The telemetry publishes wheel positions and no tyre dimension at all
    ///  (checked against the whole GameTelemetryData), so this is the one part of the width that stays
    ///  typed. Cross-checked once: wheel centres ±1.03 + 0.20/side gave 2.46 m against the game's own
    ///  2.47 m.
    /// </summary>
    public double TireHalfWidthM { get; set; } = 0.2;

    /// <summary>
    ///  The box the obstacle conflict check sweeps across the route, and the one the map and AR draw.
    ///  One place on purpose: <c>RouteFootprint</c> kept the pre-§46 formula and disagreed with the
    ///  planner as soon as <see cref="EnvelopePadM"/> moved off its default (design doc §51).
    /// </summary>
    public double EnvelopeWidthM => VehicleWidthM + 2.0 * (EnvelopePadM + ObstacleMarginM);

    public double EnvelopeLengthM => VehicleLengthM + 0.1 + 2.0 * (EnvelopePadM + ObstacleMarginM);

    // Speeds
    public double ForwardSpeedKph { get; set; } = 6.0;
    public double ReverseSpeedKph { get; set; } = 3.0;
    public double ComfortDecel { get; set; } = 0.5;
    public double MaxAccel { get; set; } = 1.0;
    public double MaxBrakeDecel { get; set; } = 1.2;
    public double LaunchAccelMps2 { get; set; } = 0.6;

    // Longitudinal PID
    public double PidKp { get; set; } = 0.35;
    public double PidKi { get; set; } = 0.05;
    public double PidKd { get; set; } = 0.02;

    // Lateral
    public double ReverseKxCross { get; set; } = 0.06;
    public double ReverseKhHeading { get; set; } = 1.2;
    public double LookaheadBaseM { get; set; } = 1.5;
    public double LookaheadGainMps { get; set; } = 0.6;

    // Steering shaping: the geometric curvature command is scaled, deadbanded and then slew
    // limited so the wheel tracks proportionally instead of snapping to full lock.
    public double SteerGain { get; set; } = 1.0;
    public double SteerDeadband { get; set; } = 0.02;
    public double SteerRateLimitPerSecond { get; set; } = 1.5;
    public ReverseLateralLaw ReverseLateral { get; set; } = ReverseLateralLaw.ReversePurePursuit;

    // Planning
    public double GearSwitchPenaltyM { get; set; } = 6.0;

    /// <summary>
    ///  Plans on a circle larger than the vehicle's own minimum turning circle. Planning at the
    ///  limit makes every arc demand full steering lock, which leaves the controller no authority
    ///  to correct anything - a saturated actuator is no longer a linear one.
    /// </summary>
    public double PlanRadiusMargin { get; set; } = 1.35;

    /// <summary>
    ///  Length of the straight leg driven into the spot. The final heading of an arc is only
    ///  reached at the end of the arc, so without this a route stopped short leaves the bay
    ///  turned by the leftover arc; inside a straight tail any stopping point is aligned.
    /// </summary>
    public double TerminalStraightM { get; set; } = 1.5;
    public double PathSampleM { get; set; } = 0.25;

    /// <summary>
    ///  How far forward the smooth forward family may creep before it starts its turn. The maneuver
    ///  begins wherever the truck is, and on a cone course the cone that refuses a curve is usually
    ///  ten metres ahead of the nose - so the first metre count of the route is a straight, not an arc.
    ///  Zero turns the family off and leaves the older candidates as the only choices (§53).
    /// </summary>
    public double SmoothForwardLeadInM { get; set; } = 12.0;

    // Obstacles

    /// <summary>
    ///  The variable half of the per-side envelope padding, on top of <see cref="EnvelopePadM"/>.
    ///  Only the sum reaches anything: the corridor check sweeps
    ///  VehicleWidthM + 2*(EnvelopePadM + ObstacleMarginM) meters wide (§51). Its floor used to be
    ///  0.2 m, which together with the pad floor made 0.2 m the tightest envelope expressible - and
    ///  the ATS cone course's measured 3.44 m bay mouth admits a 2.47 m truck with 0.49 m per side,
    ///  so a 0.25 m/side total left no route at all, smooth or otherwise (§53). Floor now 0.05.
    /// </summary>
    public double ObstacleMarginM { get; set; } = 0.05;

    /// <summary>
    ///  Per-side base padding of the obstacle envelope, on top of <see cref="ObstacleMarginM"/>.
    ///  Together they decide how wide a gap must be before a route may use it: the corridor check
    ///  sweeps a box of VehicleWidthM + 2*(pad + margin) meters. The old 0.25 + 0.5 reproduced the
    ///  historical 4.0 m width, which is a highway number: measured on the practice course, the bay
    ///  mouth is 3.44 m clear and the truck 2.47 m wide, so 0.10 m per side still leaves twelve clean
    ///  routes, 0.15 leaves three and 0.20 leaves none - including the two-shift route §52 believed
    ///  was passing there (§53, numbers in scratch/pad_table.py). What you shave off here comes
    ///  straight out of the tracking-error budget - raise it back for public roads.
    /// </summary>
    public double EnvelopePadM { get; set; } = 0.05;

    /// <summary>
    ///  Only conflicts this far ahead of the vehicle stop it. Anything further is not a reason to
    ///  stand still at the start of the maneuver - it is a reason to re-solve at the next gear
    ///  change, which is what makes segmented re-planning reachable at all.
    /// </summary>
    public double ObstacleLookaheadM { get; set; } = 4.0;

    // Re-planning
    public bool ReplanWhileStopped { get; set; } = true;
    public int MaxReplans { get; set; } = 3;

    // Tolerances and aborts
    public double ToleranceLateralM { get; set; } = 0.20;
    public double ToleranceHeadingDeg { get; set; } = 4.0;

    // Finish behaviour
    public bool HandbrakeOnFinish { get; set; } = true;
    public bool HazardOnFinish { get; set; } = false;
    public bool EngineOffOnFinish { get; set; } = false;
    public bool RestoreAssistsOnFinish { get; set; } = true;

    // Visualisation
    public bool ShowMapWindow { get; set; } = true;
    public bool ShowArOverlay { get; set; } = true;

    /// <summary>
    ///  Second-by-second curves of the control terms (cross-track, heading, steer raw vs shaped,
    ///  target vs actual speed, and the longitudinal P/I/D against the returned command). Off by
    ///  default: it is a tuning instrument, not something to drive with, and leaving a second overlay
    ///  registered costs the render thread every frame.
    /// </summary>
    public bool ShowControlPlot { get; set; } = false;

    /// <summary>
    ///  Draw the map items the game spawned at runtime (roadwork cones above all) on the flat map.
    ///  These never pass through sector parsing, so the host's MapData cannot see them - they come
    ///  from the game's own kdop item lists, read from the game process by KdopReader (§41). The
    ///  reader is read-only, runs on its own background task, and degrades to a status line if the
    ///  game is absent or a pattern misses after an update. Drawn only - nothing here blocks a
    ///  route yet, same 只画不拦 contract as the static layer.
    /// </summary>
    public bool ShowMemoryItems { get; set; } = true;

    /// <summary>
    ///  Feed the memory items (cones, boards, movers, buildings) into the planner's obstacle set,
    ///  so routes avoid them like any other blocker. Owner decision 2026-10-16: a physical object
    ///  standing near the lot must not be hit whatever exactly it is, so classification generosity
    ///  is acceptable here - a misread small sign blocking the route fails safe (the maneuver is
    ///  rejected or re-routed), while a missed cone fails into a collision. The known failure mode
    ///  is unchanged by this: if the cones leave no corridor, replans run out (MaxReplans) and the
    ///  maneuver falls back to the frozen route - only the hotkey ends that; keep a hand on it.
    /// </summary>
    public bool BlockRouteWithMemoryItems { get; set; } = true;

    public double ArGroundTrimM { get; set; } = 0.0;
    public double MapScalePxPerM { get; set; } = 1.25;
    public double MapZoom { get; set; } = 1.0;
    public double MapViewRadiusM { get; set; } = 120.0;
    public bool SnapToNavCurve { get; set; } = true;

    /// <summary>
    ///  A pick that lands in or near a cone-outlined parking bay is moved to that bay's centre and given
    ///  the bay's own heading (nose in through the opening), because a spot merely close to the game's
    ///  bay still fails the job. Bays come from <see cref="ParkingBays"/>; see design doc §50.
    /// </summary>
    public bool SnapToParkingBay { get; set; } = true;

    /// <summary>
    ///  How far a navigation curve or lane centerline vouches for the ground beside it. Half of a typical
    ///  aisle: a curve runs down the middle of the drive lane, and the lane is what we want confirmed.
    ///  Too large and "unconfirmed" disappears; too small and the bay you are parking into counts as unknown.
    /// </summary>
    public double ConfirmedCorridorHalfWidthM { get; set; } = 5.0;

    /// <summary>
    ///  Freeze the map panel's position and size. ImGui grabs a window from anywhere on its border,
    ///  and the canvas reaches to within Padding of that border, so a press meant for the edge of the
    ///  map moved the rect under the pick. Worth a switch rather than a permanent flag because the
    ///  panel genuinely needs repositioning some of the time.
    /// </summary>
    public bool LockMapWindow { get; set; } = false;

    // Saved so reloading the plugin does not silently lose the spot you picked.
    public bool HasSavedSpot { get; set; } = false;
    public double SavedSpotX { get; set; }
    public double SavedSpotZ { get; set; }
    public double SavedSpotHeadingRad { get; set; }

    // Marker-following auto-park. The defaults come from spec §6 of
    // docs/superpowers/specs/2026-10-19-universal-autoparking-design.md - that table lists 9 keys and
    // no ranges at all (the 0.05/0.30 pair in the spec is InsertPad's pad clamp, a different number),
    // so the clamp bounds below plus AutoParkByMarker and GameProcessName come from the plan
    // (docs/superpowers/plans/2026-10-20-universal-autoparking.md, Task 6). Nothing reads these yet:
    // the consumers arrive with the target-resolution wiring later in the same plan, so until then
    // this is inert configuration and the current maneuver behaves exactly as before.

    /// <summary>
    ///  Run the whole chain - resolve the game's marker, plan, draw the target pose and the route on
    ///  the map - while sending no control commands at all. The rule is a conjunction, not this flag
    ///  alone: the effective dry-run value is <see cref="DryRun"/> || <see cref="ShadowMode"/>, so the
    ///  truck only receives pedals, wheel, gearbox and handbrake when BOTH are off - the expression
    ///  Task 8 writes at the control-output boundary, and until that wiring lands both flags are
    ///  inert, as the header above says. Since
    ///  <see cref="DryRun"/> also defaults to true, turning this off is necessary but not sufficient -
    ///  the Dry-run switch is the other half and sits at the top of the same settings page. On by
    ///  default because the marker protocol is new and the first passes are meant to be watched rather
    ///  than driven. Shadow mode neither pauses nor aborts anything - only the hotkeys end a maneuver -
    ///  it only withholds the commands.
    /// </summary>
    public bool ShadowMode { get; set; } = true;

    /// <summary>
    ///  Resolve where the game wants the truck parked from the runtime memory items (0xC17 mover pair
    ///  → framing-vehicle gap → <see cref="SavedSpotX"/>) instead of demanding a map pick. Off
    ///  restores the previous behavior outright: no picked spot means the start is refused, which is
    ///  how the manual workflow stays reachable. With it on, the last tier is the hand-picked
    ///  <see cref="SavedSpotX"/> spot, and that tier is exempt from <see cref="MinConfidence"/> (see
    ///  the rule written there): a person chose the pose, so there is no detector uncertainty left to
    ///  threshold, and the fallback promised here works at the 0.6 default.
    /// </summary>
    public bool AutoParkByMarker { get; set; } = true;

    /// <summary>
    ///  Path to the sidecar executable that reads the game process for the identity channel. JSON-only
    ///  knob on purpose: the host settings page ships Slider/Switch/Dropdown and has no text input, so
    ///  there is no control to add and no HandleAction case (it would be unreachable). Edit
    ///  AutoParking.json; documented in README.
    ///
    ///  Empty does NOT mean "MemoryProbe.exe next to the plugin DLL": the host shadow-copies the DLL
    ///  alone into %TEMP%\ETS2LA\PluginShadow/&lt;name&gt;_&lt;guid&gt;/, so at runtime that directory
    ///  holds no exe, and AppContext.BaseDirectory is not current\Plugins either. The resolution order
    ///  is therefore explicit SidecarPath first, then "&lt;host exe dir&gt;\Plugins\MemoryProbe.exe",
    ///  then AppContext.BaseDirectory as the last try - and the candidate actually chosen must be
    ///  logged, because the shadow copy makes every one of these paths ambiguous from inside the host.
    ///  Pending Task 8: nothing reads this key yet and none of the above is implemented here.
    /// </summary>
    public string SidecarPath { get; set; } = "";

    /// <summary>
    ///  Process name the sidecar is pointed at. It defaults to amtrucks, so ETS2 has to be named
    ///  explicitly. JSON-only for the same reason as <see cref="SidecarPath"/>: no text-input
    ///  component exists on the settings page, hence no control and no HandleAction case by design.
    /// </summary>
    public string GameProcessName { get; set; } = "eurotrucks2";

    /// <summary>
    ///  On the reverse leg, set the per-side obstacle padding from the measured lateral clear of the
    ///  framing-vehicle gap (see <see cref="SafetyMarginM"/>) instead of the fixed value:
    ///  pad = clamp((lateral clear - VehicleWidthM)/2 - SafetyMarginM, 0.05, 0.30). That is not a
    ///  shrinking rule - it widens the envelope in a roomy gap and tightens it in a narrow one.
    ///  Measured on the practice course (lateral clear 3.24 m against the typed 2.60 m width) the
    ///  default 0.10 gives 0.22 m/side where the fixed pair gives 0.10 m/side, so there it opens up;
    ///  it drops below the fixed value only under roughly 3.0 m of clear. Off keeps both legs on the
    ///  fixed values. The padding this replaces is the pre-§53 one - 0.25 pad + 0.50 margin, i.e.
    ///  0.75 m/side, a highway number whose box is wider than the 3.44 m bay mouth and so rejected
    ///  every route in it - not today's 0.10/side total, which already leaves twelve clean routes
    ///  there (see <see cref="EnvelopePadM"/> for the measured pad table).
    /// </summary>
    public bool DynamicEnvelope { get; set; } = true;

    /// <summary>
    ///  Per-side envelope used on the wide-lane approach leg: the corridor check sweeps
    ///  VehicleWidthM + 2*this there, so this is a stand-in for EnvelopePadM + ObstacleMarginM and
    ///  does not add to them. 0.30/side is only sane where the lane is visibly much wider than the
    ///  truck; narrow aisles need the measured value, which is what the reverse leg uses.
    /// </summary>
    public double ApproachEnvelopeM { get; set; } = 0.30;

    /// <summary>
    ///  What is still reserved per side when the reverse-leg envelope is back-computed from the gap:
    ///  pad = clamp((lateral clear - VehicleWidthM)/2 - this, 0.05, 0.30). Measured lateral clear of
    ///  the practice-course gap is 3.24 m, so with the typed 2.60 m width the default 0.10 yields
    ///  0.22 m/side. Raising it eats the tracking-error budget; the floor of 0 means "trust the
    ///  measurement with nothing left over", which is only defensible at crawling speed.
    /// </summary>
    public double SafetyMarginM { get; set; } = 0.10;

    /// <summary>
    ///  How long the truck has to stay stationary at the target before the leg counts as settled -
    ///  only then is the next marker resolved (multi-leg chaining) or the maneuver declared done.
    ///  It decides when something is considered arrived and nothing else: it never pauses or aborts,
    ///  and ending a maneuver stays the job of the hotkeys.
    /// </summary>
    public double SettleTimeS { get; set; } = 3.0;

    /// <summary>
    ///  The gate in refusal form: a resolved target is refused at start when
    ///  confidence &lt; MinConfidence. Strict &lt;, so 0 disables the gate outright (no tier sits below
    ///  0) and a tier exactly equal to the threshold passes. Refusal to start, never an abort - see
    ///  the project policy that only hotkeys end a maneuver. The tiers are fixed in TargetResolver:
    ///  0.9 mover pair, 0.6 framing-vehicle gap, 0.4 saved spot.
    ///
    ///  Exemption (owner ruling R36): the gate does not apply to the user-asserted tier. Task 8 is to
    ///  refuse only when confidence &lt; MinConfidence AND the target's provenance is not the saved-spot
    ///  tier; TargetResolver emits that provenance with the literal prefix "saved-spot"
    ///  (Driving/TargetResolver.cs), so key on the provenance, never on the error string. The gate
    ///  exists to stop the plugin guessing, and a pose a human picked has no detector uncertainty to
    ///  threshold - with the 0.6 default that keeps the fallback <see cref="AutoParkByMarker"/>
    ///  promises reachable. Do not achieve it by lowering this default or ConfSaved.
    /// </summary>
    public double MinConfidence { get; set; } = 0.6;

    /// <summary>
    ///  The service brake runs on an air circuit, so below this pressure a brake demand is physically
    ///  inert (§24: read air= before concluding a brake did nothing). The gate in refusal form: refuse
    ///  the start when air &lt; MinAirBar. Strict &lt;, which is what makes the documented "0 = this
    ///  check is off" true - at 0 nothing can be below it, whereas air &lt;= 0 would refuse forever on
    ///  an unfired engine, and a tier/reading exactly equal to the threshold passes. Starting a
    ///  parking maneuver we cannot stop is the only thing this guards, and it is checked once at
    ///  start - it does not watch a running maneuver, and only the hotkeys end one.
    /// </summary>
    public double MinAirBar { get; set; } = 4.0;

    /// <summary>
    ///  While a marker-following leg executes, if the 0xC17 marker we planned to has moved more than
    ///  this, the leg is re-planned in place. Re-planning continues the maneuver; it does not pause
    ///  or end it. Small values churn through <see cref="MaxReplans"/> (which then falls back to the
    ///  frozen route, still running), large ones let the truck chase a stale target.
    /// </summary>
    public double MarkerMoveToleranceM { get; set; } = 0.5;

    /// <summary>
    ///  Shallow copy, used to hand the settings writer and the per-leg config a snapshot. The clamp
    ///  range of every key above lives in <see cref="Clamp"/> rather than in the setters, so a clone
    ///  is NOT re-normalized: whatever mutates a clone (the per-leg overrides do) has to keep its own
    ///  explicit Math.Clamp on the values it writes, or the copy escapes its documented range.
    /// </summary>
    public AutoParkingSettings Clone() => (AutoParkingSettings)MemberwiseClone();

    public double EffectiveMapPixelsPerMeter => Math.Clamp(MapScalePxPerM * MapZoom, 0.25, 6.0);

    public double MapDataRadiusM(double windowSizePx)
    {
        double visibleRadius = windowSizePx / (2.0 * EffectiveMapPixelsPerMeter);
        return Math.Max(MapViewRadiusM, visibleRadius + 20.0);
    }

    public void Clamp()
    {
        if (SettingsVersion < 1)
            SettingsVersion = 1;

        ControlWeight = Math.Clamp(ControlWeight, 1.0, 100.0);
        MaxTakeoverDistanceM = Math.Clamp(MaxTakeoverDistanceM, 5.0, 100.0);
        EntryDistanceM = Math.Clamp(EntryDistanceM, 6.0, 30.0);
        OvershootM = Math.Clamp(OvershootM, 0.0, 2.0);

        WheelbaseM = Math.Clamp(WheelbaseM, 2.5, 7.5);
        MaxSteerDeg = Math.Clamp(MaxSteerDeg, 20.0, 45.0);
        VehicleLengthM = Math.Clamp(VehicleLengthM, 4.0, 12.0);
        VehicleWidthM = Math.Clamp(VehicleWidthM, 1.8, 3.2);
        TireHalfWidthM = Math.Clamp(TireHalfWidthM, 0.05, 0.45);

        ForwardSpeedKph = Math.Clamp(ForwardSpeedKph, 2.0, 15.0);
        ReverseSpeedKph = Math.Clamp(ReverseSpeedKph, 1.0, 8.0);
        ComfortDecel = Math.Clamp(ComfortDecel, 0.2, 1.5);
        MaxAccel = Math.Clamp(MaxAccel, 0.3, 3.0);
        MaxBrakeDecel = Math.Clamp(MaxBrakeDecel, 0.3, 3.0);
        LaunchAccelMps2 = Math.Clamp(LaunchAccelMps2, 0.1, 1.5);

        PidKp = Math.Clamp(PidKp, 0.0, 2.0);
        PidKi = Math.Clamp(PidKi, 0.0, 1.0);
        PidKd = Math.Clamp(PidKd, 0.0, 1.0);

        ReverseKxCross = Math.Clamp(ReverseKxCross, 0.01, 0.30);
        ReverseKhHeading = Math.Clamp(ReverseKhHeading, 0.2, 4.0);
        LookaheadBaseM = Math.Clamp(LookaheadBaseM, 0.5, 4.0);
        LookaheadGainMps = Math.Clamp(LookaheadGainMps, 0.0, 2.0);
        SteerGain = Math.Clamp(SteerGain, 0.1, 3.0);
        SteerDeadband = Math.Clamp(SteerDeadband, 0.0, 0.2);
        SteerRateLimitPerSecond = Math.Clamp(SteerRateLimitPerSecond, 0.2, 6.0);

        GearSwitchPenaltyM = Math.Clamp(GearSwitchPenaltyM, 0.0, 20.0);
        PlanRadiusMargin = Math.Clamp(PlanRadiusMargin, 1.0, 2.5);
        TerminalStraightM = Math.Clamp(TerminalStraightM, 0.0, 5.0);
        SmoothForwardLeadInM = Math.Clamp(SmoothForwardLeadInM, 0.0, 25.0);
        PathSampleM = Math.Clamp(PathSampleM, 0.1, 1.0);

        ObstacleMarginM = Math.Clamp(ObstacleMarginM, 0.05, 1.5);
        EnvelopePadM = Math.Clamp(EnvelopePadM, 0.0, 1.0);
        ApproachEnvelopeM = Math.Clamp(ApproachEnvelopeM, 0.05, 0.50);
        SafetyMarginM = Math.Clamp(SafetyMarginM, 0.0, 0.30);
        SettleTimeS = Math.Clamp(SettleTimeS, 1.0, 10.0);
        MinConfidence = Math.Clamp(MinConfidence, 0.0, 1.0);
        MinAirBar = Math.Clamp(MinAirBar, 0.0, 10.0);
        MarkerMoveToleranceM = Math.Clamp(MarkerMoveToleranceM, 0.1, 5.0);
        ObstacleLookaheadM = Math.Clamp(ObstacleLookaheadM, 1.5, 15.0);
        MaxReplans = Math.Clamp(MaxReplans, 0, 8);

        ToleranceLateralM = Math.Clamp(ToleranceLateralM, 0.05, 1.0);
        ToleranceHeadingDeg = Math.Clamp(ToleranceHeadingDeg, 1.0, 15.0);

        MapScalePxPerM = Math.Clamp(MapScalePxPerM, 0.5, 3.0);
        MapZoom = Math.Clamp(MapZoom, 1.0, 4.0);
        MapViewRadiusM = Math.Clamp(MapViewRadiusM, 20.0, 200.0);
        ConfirmedCorridorHalfWidthM = Math.Clamp(ConfirmedCorridorHalfWidthM, 2.0, 20.0);
        ArGroundTrimM = Math.Clamp(ArGroundTrimM, -1.0, 1.0);
    }
}
