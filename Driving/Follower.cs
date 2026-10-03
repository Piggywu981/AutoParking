using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  Everything the controller needs about the vehicle, as plain values. Keeping the telemetry
///  types out of here is what lets the follower be driven by a simulator in the test harness.
/// </summary>
public readonly record struct VehicleState(DateTime Utc, Vector2 Position, double HeadingRad, double SignedSpeed,
                                           int Gear, int GearDashboard)
{
    public Pose2 Pose => new(Position.X, Position.Y, HeadingRad);

    /// <summary>Both gear readouts, for the status line - a shift that only one of them sees is a clue on its own.</summary>
    public string GearText => $"{Gear}/{GearDashboard}";
}

public enum GearRequest
{
    None,
    Drive,
    Reverse,
    Neutral
}

/// <summary>
///  What the controller wants this tick. There is deliberately no abort channel: nothing the
///  follower observes can end the maneuver, only the hotkey can.
/// </summary>
public readonly record struct ControlDemand(float Steer, float Throttle, float Brake, bool HoldBrake,
                                            GearRequest Gear, bool Finished)
{
    public static ControlDemand Hold(float brake) => new(0f, 0f, brake, true, GearRequest.None, false);

    /// <summary>Service brake without the handbrake - the gear wait has to drive off again right after.</summary>
    public static ControlDemand BrakeOnly(float brake) => new(0f, 0f, brake, false, GearRequest.None, false);
}

/// <summary>
///  Follows a planned route: picks a gear, keeps speed along the route with a longitudinal PID,
///  and steers with pure pursuit when driving forward / a cross-track PD when reversing.
/// </summary>
public sealed class Follower
{
    private enum Stage
    {
        SelectGear,
        Driving,
        StoppingForGearChange,
        WaitingForGear,
        Finished
    }

    private ParkingPath path;
    private readonly AutoParkingSettings settings;
    private readonly double wheelbase;
    private readonly double maxCurvature;
    private readonly Pose2 goal;

    /// <summary>
    ///  Asks for a fresh route from a pose we are standing at. Null means "nothing better". The
    ///  follower never solves anything itself - it only decides when to ask and whether to accept.
    /// </summary>
    private readonly Func<Pose2, ObstacleSnapshot, ParkingPath?>? replanner;

    // A gear request is a 50 ms edge that the game may or may not take, and nothing observed can
    // stop the maneuver - only the hotkey can. So retry a few times, then drive off in the
    // requested direction and keep re-pulsing from the driving stage.
    private const double GearPulseRetrySeconds = 1.0;
    private const int GearPulseMaxAttempts = 4;

    // How long to insist on a full stop before the shift is asked for anyway.
    private const double GearStopPatienceSeconds = 4.0;

        // How far outside the leg being driven the nearest-point projection may reach. The lower
        // edge gets no slack: with a metre of it, a route whose reverse leg lies on top of the
        // approach leaves the projection sitting on an approach sample, and the per-tick advance
        // limit is smaller than the gap to the next leg's first sample - so progress freezes for
        // good while the truck drives the leg correctly and straight through the target.
        private const double LegWindowM = 1.0;

    // A blocked route is worth re-solving once we have waited long enough that it is not a car
    // momentarily creeping past. At a gear boundary, adopt a new route only if it is meaningfully
    // shorter - otherwise two near-equal maneuvers alternate and the driver sees the plan flip.
    private const double BlockedReplanSeconds = 2.0;
    private const double BlockedForgetSeconds = 2.0;
    private const double ReplanImprovementMarginM = 0.5;

    // How much the distance to the spot may grow before we call it a lost reference.
    private const double RecessionM = 0.6;

    private readonly List<Run> runs = new();

    private Stage stage = Stage.SelectGear;
    private int index;
    private int runIndex;
    private double progress;
    private DriveDirection wantedGear;
    private DriveDirection? confirmedGear;
    private bool gearConfirmed;
    private DateTime gearPulseAt = DateTime.MinValue;
    private DateTime gearStopSinceUtc = DateTime.MinValue;
    private int gearAttempts;
    private DateTime startedAt = DateTime.MinValue;
    private DateTime lastStepUtc = DateTime.MinValue;
    private DateTime lastStepCallUtc = DateTime.MinValue;
    private double stepSeconds = 1.0 / 60.0;
    private double integral;
    private double lastError;
    private double lastSteer;
    private DateTime blockedSince = DateTime.MinValue;
    private DateTime lastBlockedUtc = DateTime.MinValue;
    private DateTime nextReplanUtc = DateTime.MinValue;
    private double bestRemaining = double.MaxValue;
    private bool recovering;
    private string status = "等待";

    private readonly struct Run
    {
        public Run(DriveDirection travel, double start, double end)
        {
            Travel = travel;
            Start = start;
            End = end;
        }

        public DriveDirection Travel { get; }
        public double Start { get; }
        public double End { get; }
    }

    public Follower(ParkingPath path, AutoParkingSettings settings, double wheelbase,
                    Func<Pose2, ObstacleSnapshot, ParkingPath?>? replanner = null)
    {
        this.path = path;
        this.settings = settings;
        this.wheelbase = wheelbase;
        this.replanner = replanner;
        maxCurvature = 1.0 / Math.Max(0.5, Kinematics.MinTurnRadius(settings));

        PathPoint end = path.Points.Count > 0 ? path.Points[^1] : default;
        goal = new Pose2(end.Position.X, end.Position.Y, end.HeadingRad);

        BuildRuns();
        wantedGear = runs.Count > 0 ? runs[0].Travel : DriveDirection.Forward;
    }

    public string Status => status;

    /// <summary>How many times the route has been swapped mid-maneuver.</summary>
    public int Replans { get; private set; }

    public double RemainingDistance => Math.Max(0.0, path.Length - progress);

    public double CrossTrackErrorMeters { get; private set; }

    public double HeadingErrorDegrees { get; private set; }

    public DriveDirection CurrentTravel => confirmedGear ?? wantedGear;

    private void BuildRuns()
    {
        runs.Clear();
        IReadOnlyList<PathPoint> points = path.Points;
        if (points.Count == 0)
        {
            runs.Add(new Run(DriveDirection.Forward, 0.0, 0.0));
            return;
        }

        DriveDirection current = points[0].Travel;
        double start = 0.0;

        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].Travel == current)
                continue;

            // The boundary is the last sample the vehicle can actually reach while still travelling
            // in the current direction. The next leg's first sample is already a step into the bay,
            // so claiming the boundary for it leaves a gap the projection can never cross - and the
            // change of direction is then never requested.
            double boundary = points[i - 1].DistanceAlong;
            runs.Add(new Run(current, start, boundary));
            current = points[i].Travel;
            start = boundary;
        }

        runs.Add(new Run(current, start, points[^1].DistanceAlong));
    }

    public ControlDemand Step(VehicleState vehicle, ObstacleSnapshot obstacles)
    {
        if (path.Points.Count < 2)
        {
            status = "路径为空，保持制动";
            return ControlDemand.Hold(0.6f);
        }

        if (startedAt == DateTime.MinValue)
        {
            startedAt = vehicle.Utc;
            lastStepUtc = vehicle.Utc;
            lastStepCallUtc = vehicle.Utc;
        }
        else
        {
            double gap = (vehicle.Utc - lastStepCallUtc).TotalSeconds;
            stepSeconds = Math.Clamp(gap, 0.0, 0.5);
            lastStepCallUtc = vehicle.Utc;

            // A pause stops us from being called at all. On resume, drop the leftovers from
            // before the gap or the watchdog and the derivative term both fire on stale data.
            if (gap > 1.0)
            {
                integral = 0.0;
                lastError = 0.0;
                lastStepUtc = vehicle.Utc;
            }
        }

        ControlDemand? runaway = HandleOverspeed(vehicle);
        if (runaway != null)
            return runaway.Value;

        UpdateProgress(vehicle);

        // The user's rule, and the only defence against a reference that has silently detached from
        // the truck: while driving towards the spot, the distance to it may shrink or hold, never
        // grow. When it grows by more than a car length's worth of slack, stop and re-solve from
        // where we actually are - a second-stage correction - instead of backing through the spot.
        double remaining = RemainingDistance;
        if (remaining < bestRemaining)
            bestRemaining = remaining;
        else if (stage == Stage.Driving && remaining > bestRemaining + RecessionM)
        {
            bestRemaining = remaining;
            recovering = true;
            status = $"距终点不再缩短（{remaining:0.0} m），停车准备二段修正";
        }

        if (recovering)
        {
            if (Math.Abs(vehicle.SignedSpeed) > 0.1)
                return ControlDemand.BrakeOnly(0.6f);

            recovering = false;

            if (TryReplan(vehicle, obstacles, 0.0, "二段修正"))
                return ControlDemand.BrakeOnly(0.2f);
        }

        if (RemainingDistance <= Math.Max(0.25, settings.ToleranceLateralM) && Math.Abs(vehicle.SignedSpeed) < 0.12)
        {
            stage = Stage.Finished;
            status = "到位，拉手刹";
            return new ControlDemand(0f, 0f, 0.4f, settings.HandbrakeOnFinish, GearRequest.Neutral, true);
        }

        switch (stage)
        {
            case Stage.SelectGear:
            case Stage.StoppingForGearChange:
                return StopAndRequestGear(vehicle);

            case Stage.WaitingForGear:
                return WaitForGear(vehicle);

            case Stage.Finished:
                return new ControlDemand(0f, 0f, 0.4f, settings.HandbrakeOnFinish, GearRequest.None, true);
        }

        ControlDemand? blocked = HandleObstacles(vehicle, obstacles);
        if (blocked != null)
            return blocked.Value;

        return Drive(vehicle, obstacles);
    }

    private ControlDemand StopAndRequestGear(VehicleState vehicle)
    {
        bool wasMoving = Math.Abs(vehicle.SignedSpeed) > 0.15;

        if (stage != Stage.StoppingForGearChange)
            gearStopSinceUtc = vehicle.Utc;

        stage = Stage.StoppingForGearChange;

        // Braking to a standstill first is the polite order, but it can be unwinnable: another
        // channel holding the throttle, or a grade, keeps the truck rolling and the wait has no
        // end. Past the patience window the shift is requested anyway - the game ignores a gear
        // action while rolling, so the cost of asking early is nothing, and the alternative is
        // standing on the brake forever with nothing able to finish the maneuver but the hotkey.
        double stopWaited = (vehicle.Utc - gearStopSinceUtc).TotalSeconds;
        bool patient = stopWaited <= GearStopPatienceSeconds;

        status = wasMoving ? (patient ? "换挡前刹停" : $"刹停无望（{stopWaited:0.0} s），边发挡边等") : "请求挡位";

        if (wasMoving && patient)
            return ControlDemand.BrakeOnly(0.5f);

        gearAttempts++;
        gearPulseAt = vehicle.Utc;
        stage = Stage.WaitingForGear;
        status = $"脉冲挡位 {wantedGear}（第 {gearAttempts} 次，遥测 {vehicle.GearText}）";

        return new ControlDemand(0f, 0f, 0.5f, false,
                                 wantedGear == DriveDirection.Forward ? GearRequest.Drive : GearRequest.Reverse,
                                 false);
    }

    /// <summary>
    ///  Did the gearbox take the request? The engaged ratio is the wrong thing to watch: an
    ///  automatic falls back to neutral for exactly the reason we asked for the shift - the truck
    ///  stopping - so a confirmation read off it disappears the moment it succeeds. The dashboard
    ///  keeps the selected position, and either readout agreeing is proof the action landed.
    /// </summary>
    private static bool GearSelected(VehicleState vehicle, DriveDirection direction)
    {
        return direction == DriveDirection.Forward
            ? vehicle.Gear > 0 || vehicle.GearDashboard > 0
            : vehicle.Gear < 0 || vehicle.GearDashboard < 0;
    }

    private ControlDemand WaitForGear(VehicleState vehicle)
    {
        // In DryRun the gear pulse never reaches the game, so telemetry can never confirm it.
        bool taken = settings.DryRun || GearSelected(vehicle, wantedGear);
        if (taken)
        {
            confirmedGear = wantedGear;
            gearConfirmed = true;
            gearAttempts = 0;
            stage = Stage.Driving;
            integral = 0.0;
            lastError = 0.0;
            status = $"挡位已确认 {wantedGear}";
            return ControlDemand.BrakeOnly(0.2f);
        }

        if ((vehicle.Utc - gearPulseAt).TotalSeconds > GearPulseRetrySeconds)
        {
            stage = Stage.StoppingForGearChange;
            status = $"挡位未确认（遥测 {vehicle.GearText}），重试";
            return ControlDemand.BrakeOnly(0.5f);
        }

        // Keep asking has a ceiling: if the gearbox action never lands, waiting is not a plan.
        // Proceed with the pulse still being re-sent from Drive() so the truck moves as soon as
        // the game takes it, and the run is not stuck on the brake with no automatic stop left.
        if (gearAttempts >= GearPulseMaxAttempts)
        {
            confirmedGear = wantedGear;
            gearConfirmed = false;
            gearAttempts = 0;
            stage = Stage.Driving;
            integral = 0.0;
            lastError = 0.0;
            status = $"挡位遥测仍为 {vehicle.GearText}，按 {wantedGear} 继续并补发脉冲";
        }

        return ControlDemand.BrakeOnly(0.5f);
    }

    private ControlDemand Drive(VehicleState vehicle, ObstacleSnapshot obstacles)
    {
        Run run = CurrentRun();
        if (run.Travel != confirmedGear)
        {
            // A gear change means the vehicle is stopped, so it is a free decision point: re-solve
            // from here and take the new route only if it is clearly better.
            if (TryReplan(vehicle, obstacles, ReplanImprovementMarginM, "换挡边界"))
                return ControlDemand.BrakeOnly(0.2f);

            run = CurrentRun();
        }

        if (run.Travel != confirmedGear)
        {
            wantedGear = run.Travel;
            stage = Stage.SelectGear;
            status = $"即将反向，准备换 {run.Travel}";
            return ControlDemand.BrakeOnly(0.2f);
        }

        bool reversing = run.Travel == DriveDirection.Reverse;
        double speedLimit = UnitConversions(reversing ? settings.ReverseSpeedKph : settings.ForwardSpeedKph);

        path.TryPointAt(progress, out PathPoint anchor);
        CrossTrackErrorMeters = Geometry.SignedLateral(anchor.Position, Geometry.ForwardFromHeading(anchor.HeadingRad), vehicle.Position);
        HeadingErrorDegrees = Geometry.SmallestAngleDifference(vehicle.HeadingRad, anchor.HeadingRad) * 180.0 / Math.PI;

        double steer = reversing ? ReverseSteering(vehicle, anchor) : ForwardSteering(vehicle, run);
        steer = ShapeSteer(steer);

        double reference = ReferenceSpeed(run, speedLimit);
        double acceleration = Longitudinal(vehicle, reference);

        float throttle = acceleration > 0 ? (float)Math.Clamp(acceleration / settings.MaxAccel, 0.0, 1.0) : 0f;
        float brake = acceleration < 0 ? (float)Math.Clamp(-acceleration / settings.MaxBrakeDecel, 0.0, 1.0) : 0f;

        status = $"{(reversing ? "倒车" : "前进")} 剩 {RemainingDistance:0.0} m · 误差 {CrossTrackErrorMeters:0.00} m · 目标 {reference * 3.6:0.0} km/h";

        // The gear was never seen in telemetry, so keep asking while driving - the game takes it
        // the moment conditions allow and the truck starts moving on its own.
        GearRequest pulse = GearRequest.None;
        if (!gearConfirmed && (vehicle.Utc - gearPulseAt).TotalSeconds > GearPulseRetrySeconds)
        {
            gearPulseAt = vehicle.Utc;
            pulse = wantedGear == DriveDirection.Forward ? GearRequest.Drive : GearRequest.Reverse;
            status += $" · 补发 {wantedGear}（遥测 {vehicle.GearText}）";
        }

        return new ControlDemand((float)steer, throttle, brake, false, pulse, false);
    }

    /// <summary>
    ///  Pure pursuit is only defined for an aim point ahead of the direction of travel. When the
    ///  vehicle has driven past the point it is meant to be reversing from - guaranteed whenever it
    ///  could not stop in time - the projection pins at the leg start, the aim ends up behind us,
    ///  and the angle sits next to 180 degrees where its sine flips sign: the command saturates and
    ///  the wheel stops where it is. The tangent law has no such singularity and converges back
    ///  onto the line from wherever the overshoot left us.
    /// </summary>
    private static bool AimPointAhead(Vector2 delta, Vector2 travelDirection)
        => delta.X * travelDirection.X + delta.Y * travelDirection.Y > 0.0;

    /// <summary>
    ///  Tangent tracking: bend back onto the route from the cross-track offset and the heading
    ///  error against the anchor's tangent. Has no view of where the route goes next, which is why
    ///  pursuit is preferred - but it has no singularity either, so it is what we fall back to when
    ///  the aim point is no longer ahead of us.
    /// </summary>
    private double PathTangentControl(bool reversing)
    {
        double headingErrorRad = HeadingErrorDegrees * Math.PI / 180.0;
        double curvature = settings.ReverseKxCross * CrossTrackErrorMeters + settings.ReverseKhHeading * headingErrorRad;

        // Reversing travels towards larger arc length with the nose pointing the other way, so the
        // heading rate answers the wheel command inverted - the same flip the pursuit branch makes.
        return CurvatureToSteer(reversing ? curvature : -curvature);
    }

    private double ForwardSteering(VehicleState vehicle, Run run)
    {
        double lookahead = Math.Clamp(settings.LookaheadBaseM + settings.LookaheadGainMps * Math.Abs(vehicle.SignedSpeed), 1.0, 6.0);
        double aimDistance = progress + lookahead;

        if (!path.TryPointAt(Math.Min(aimDistance, run.End), out PathPoint target))
            return 0.0;

        target = BeyondEnd(target, aimDistance - run.End);

        Vector2 delta = target.Position - vehicle.Position;
        if (delta.LengthSquared() < 1e-4f)
            return 0.0;

        if (!AimPointAhead(delta, Geometry.ForwardFromHeading(vehicle.HeadingRad)))
            return PathTangentControl(false);

        double alpha = Geometry.SmallestAngleDifference(Geometry.HeadingFromForward(delta), vehicle.HeadingRad);
        double curvature = 2.0 * Math.Sin(alpha) / lookahead;
        return CurvatureToSteer(curvature);
    }

    /// <summary>
    ///  Projects the aim point straight on past the end of the route. Clamping it to the final
    ///  sample makes the vehicle chase a point that is sitting on the finish line, so it cuts in
    ///  diagonally and arrives turned - and it divides an angle measured over a short distance by
    ///  the long lookahead it was designed with, which inflates the command exactly where the
    ///  wheels should be coming back to centre.
    /// </summary>
    private static PathPoint BeyondEnd(PathPoint point, double beyond)
    {
        if (beyond <= 0.0)
            return point;

        Vector2 travel = point.Travel == DriveDirection.Forward
            ? Geometry.ForwardFromHeading(point.HeadingRad)
            : -Geometry.ForwardFromHeading(point.HeadingRad);

        return point with
        {
            Position = point.Position + travel * (float)beyond,
            DistanceAlong = point.DistanceAlong + beyond
        };
    }

    private double ReverseSteering(VehicleState vehicle, PathPoint anchor)
    {
        if (settings.ReverseLateral == ReverseLateralLaw.ReversePurePursuit)
        {
            double lookahead = Math.Clamp(settings.LookaheadBaseM + settings.LookaheadGainMps * Math.Abs(vehicle.SignedSpeed), 1.0, 6.0);

            // The car travels towards larger arc length while its nose points the other way, so
            // the aim point is ahead along the route but the angle is measured from the tail.
            double aimDistance = progress + lookahead;
            if (!path.TryPointAt(Math.Min(aimDistance, path.Length), out PathPoint target))
                return 0.0;

            target = BeyondEnd(target, aimDistance - path.Length);

            Vector2 delta = target.Position - vehicle.Position;
            if (delta.LengthSquared() < 1e-4f)
                return 0.0;

            double tailHeading = Geometry.NormalizeRadians(vehicle.HeadingRad + Math.PI);
            if (!AimPointAhead(delta, Geometry.ForwardFromHeading(tailHeading)))
                return PathTangentControl(true);

            double alpha = Geometry.SmallestAngleDifference(Geometry.HeadingFromForward(delta), tailHeading);

            // Heading rate is v*tan(steer)/L, and v is negative in reverse, so the wheel command
            // carries the opposite sign to the forward case for the same aim error.
            return CurvatureToSteer(-2.0 * Math.Sin(alpha) / lookahead);
        }

        return PathTangentControl(true);
    }

    private double CurvatureToSteer(double curvature)
    {
        double clamped = Math.Clamp(curvature, -maxCurvature, maxCurvature);
        double steerAngle = Math.Atan(clamped * wheelbase);
        double maxSteer = Math.Clamp(settings.MaxSteerDeg * Math.PI / 180.0, 0.05, 1.2);
        return Math.Tan(steerAngle) / Math.Tan(maxSteer);
    }

    /// <summary>
    ///  Shapes the geometric curvature command into a wheel angle we can actually follow:
    ///  proportional gain, a deadband so tyre compliance does not get chased, then a slew limit
    ///  expressed per second. The previous constant was 0.08 per tick, which at 60 Hz swings the
    ///  wheel from centre to full lock in 0.2 s - effectively bang-bang steering that saturates
    ///  and oscillates instead of tracking.
    /// </summary>
    private double ShapeSteer(double command)
    {
        double shaped = Math.Clamp(command * settings.SteerGain, -1.0, 1.0);

        if (Math.Abs(shaped) < settings.SteerDeadband)
            shaped = 0.0;

        double limit = settings.SteerRateLimitPerSecond * stepSeconds;
        double delta = Math.Clamp(shaped - lastSteer, -limit, limit);
        lastSteer += delta;
        return lastSteer;
    }

    private double ReferenceSpeed(Run run, double speedLimit)
    {
        double toRunEnd = Math.Max(0.0, run.End - progress);
        double toRouteEnd = Math.Max(0.0, path.Length - progress);
        double brakingLimit = Math.Sqrt(2.0 * settings.ComfortDecel * Math.Min(toRunEnd, toRouteEnd));
        double sinceStart = Math.Max(0.0, progress - run.Start);
        double launch = Math.Sqrt(Math.Max(0.0, 2.0 * settings.ComfortDecel * sinceStart));

        return Math.Min(speedLimit, Math.Max(Math.Min(brakingLimit, launch), 0.25));
    }

    private double Longitudinal(VehicleState vehicle, double reference)
    {
        // dt is the telemetry sample interval now that the state carries the sample timestamp, so
        // the ceiling has to cover a slow feed (10 Hz) instead of silently halving it.
        double dt = Math.Clamp((vehicle.Utc - lastStepUtc).TotalSeconds, 0.005, 0.25);
        lastStepUtc = vehicle.Utc;

        double speed = Math.Abs(vehicle.SignedSpeed);
        double error = reference - speed;

        bool frozen = gearPulseAt != DateTime.MinValue && (vehicle.Utc - gearPulseAt).TotalSeconds < 0.3;
        if (!frozen && Math.Abs(error) < 3.0)
        {
            integral += error * dt;
            integral = Math.Clamp(integral, -1.0, 1.0);
        }

        // V2's ACC clears the integrator below 10 km/h, which is fatal here: parking happens at
        // creep speed, so the integral is the only thing that can out-muscle rolling resistance.
        // Only clear it when the vehicle is genuinely parked.
        if (speed < 0.05)
            integral = 0.0;

        double derivative = (error - lastError) / dt;
        lastError = error;

        double command = settings.PidKp * error + settings.PidKi * integral + settings.PidKd * derivative;

        // Rolling resistance and drivetrain slack mean a small PID output simply does not move
        // a stopped vehicle, so ask for a fixed launch acceleration until it starts rolling.
        if (speed < 0.15 && reference > 0.1)
            command = Math.Max(command, settings.LaunchAccelMps2);

        // Approach-to-stop clamp, ported from the V2 ACC. Only meaningful once the vehicle is
        // actually rolling and close to the end: at standstill the term is ~0 and feeding it
        // through Math.Min would pin the command at zero, so the car could never start moving.
        double toStop = Math.Max(0.0, path.Length - progress);
        if (speed > 0.05 && toStop < 6.0)
        {
            double stopDistance = Math.Max(toStop, 0.2);
            double stopCommand = -(speed * speed) / (2.0 * stopDistance) * 1.2;
            command = Math.Min(command, stopCommand);
        }

        return Math.Clamp(command, -settings.MaxBrakeDecel, settings.MaxAccel);
    }

    private ControlDemand? HandleObstacles(VehicleState vehicle, ObstacleSnapshot obstacles)
    {
        if (obstacles.Polygons.Count == 0)
        {
            blockedSince = DateTime.MinValue;
            return null;
        }

        // Only the near field stops us. The old check looked at the entire remaining corridor, so
        // a blocker 30 m ahead held the vehicle at the start of the maneuver - and since the
        // re-decision points are all downstream of that, it also made re-planning unreachable.
        // No start grace here: at the vehicle's own pose an overlap is a real contact, not the
        // footprint-of-the-start-point artefact the planner's grace exists for.
        ParkingPath horizon = Slice(progress, progress + settings.ObstacleLookaheadM);
        int conflicts = Planner.CountCorridorConflicts(horizon, settings, obstacles, startGraceM: 0.0);
        if (conflicts == 0)
        {
            // Sticky: the swept footprint is longer than the vehicle, so the conflict count
            // flickers as the projection breathes by a sample step. Forgetting the wait every time
            // it dips made the status read "waited 0.3 s" forever.
            if (blockedSince != DateTime.MinValue && (vehicle.Utc - lastBlockedUtc).TotalSeconds > BlockedForgetSeconds)
                blockedSince = DateTime.MinValue;

            return null;
        }

        lastBlockedUtc = vehicle.Utc;

        if (blockedSince == DateTime.MinValue)
            blockedSince = vehicle.Utc;

        double waited = (vehicle.Utc - blockedSince).TotalSeconds;

        if (waited > BlockedReplanSeconds && vehicle.Utc >= nextReplanUtc)
        {
            nextReplanUtc = vehicle.Utc.AddSeconds(BlockedReplanSeconds);

            if (TryReplan(vehicle, obstacles, 0.0, $"前方 {conflicts} 处挡住"))
                return ControlDemand.BrakeOnly(0.4f);
        }

        status = $"等待障碍离开（已等 {waited:0.0} s，重规划 {Replans}/{settings.MaxReplans}）";
        return ControlDemand.Hold(0.6f);
    }

    /// <summary>
    ///  The part of the route between two arc lengths, re-based at zero so the corridor check can
    ///  reuse the same code as the planner.
    /// </summary>
    private ParkingPath Slice(double from, double to)
    {
        List<PathPoint> points = new();
        foreach (PathPoint p in path.Points)
        {
            if (p.DistanceAlong + 0.05 < from || p.DistanceAlong > to)
                continue;

            points.Add(p with { DistanceAlong = p.DistanceAlong - from });
        }

        if (points.Count == 0)
            points.Add(new PathPoint(path.Points[^1].Position, path.Points[^1].HeadingRad, 0.0, DriveDirection.Forward, 0.0));

        return new ParkingPath
        {
            Points = points,
            Source = path.Source,
            Cost = path.Cost,
            Description = path.Description,
            GearSwitches = path.GearSwitches
        };
    }

    private double CostOf(ParkingPath route)
        => route.Length + settings.GearSwitchPenaltyM * route.GearSwitches;

    /// <summary>
    ///  Asks for a new route from the pose we are standing at and swaps it in when it is worth
    ///  having. Both call sites are stopped states, and that is the point: a route re-solved while
    ///  rolling shares the pose but not the arc length, and the nearest-point projection cannot
    ///  tell the two references apart. <paramref name="improvementM"/> is how much better the
    ///  alternative has to be; zero takes anything usable, which is what a blocked route wants.
    /// </summary>
    private bool TryReplan(VehicleState vehicle, ObstacleSnapshot obstacles, double improvementM, string reason)
    {
        if (replanner == null || !settings.ReplanWhileStopped || Replans >= settings.MaxReplans)
            return false;

        Pose2 from = new(vehicle.Position.X, vehicle.Position.Y, vehicle.HeadingRad);
        ParkingPath? next = replanner.Invoke(from, obstacles);
        if (next == null || next.Points.Count < 2)
            return false;

        if (improvementM > 0.0 && CostOf(next) > CostOf(Slice(progress, path.Length)) - improvementM)
            return false;

        status = $"重规划 #{Replans + 1}（{reason}）→ {next.Description} {next.Length:0.0} m";
        Replans++;
        Adopt(next);
        return true;
    }

    private void Adopt(ParkingPath next)
    {
        path = next;
        BuildRuns();

        // The new route starts where we are standing, so the estimator restarts at zero and the
        // gearbox is confirmed again from scratch. The wheel is deliberately left alone: it has not
        // been commanded anywhere, and restarting the slew limiter from centre would throw a
        // steering jerk into the first tick of the new leg.
        index = 0;
        progress = 0.0;
        runIndex = 0;
        bestRemaining = double.MaxValue;
        recovering = false;
        integral = 0.0;
        lastError = 0.0;
        blockedSince = DateTime.MinValue;
        lastBlockedUtc = DateTime.MinValue;
        nextReplanUtc = DateTime.MinValue;
        confirmedGear = null;
        gearConfirmed = false;
        gearAttempts = 0;
        wantedGear = runs.Count > 0 ? runs[0].Travel : DriveDirection.Forward;
        stage = Stage.SelectGear;
    }

    /// <summary>
    ///  The only automatic intervention left: if the vehicle is faster than the ceiling allows,
    ///  brake until it is not. That is speed control, not a pause - it never ends the maneuver.
    /// </summary>
    private ControlDemand? HandleOverspeed(VehicleState vehicle)
    {
        double speed = Math.Abs(vehicle.SignedSpeed);
        double cap = UnitConversions(Math.Max(settings.ForwardSpeedKph, settings.ReverseSpeedKph)) + 1.0;

        if (speed <= cap)
            return null;

        status = $"超速 {speed * 3.6:0.0} km/h（上限 {cap * 3.6:0.0}），紧急制动";

        // Handbrake as well as the pedal: on the road the pedal alone has not been shown to
        // slow the vehicle, while the parking brake demonstrably holds it at the finish.
        return ControlDemand.Hold(1.0f);
    }

    private double UnitConversions(double kmPerHour) => kmPerHour / 3.6;

    /// <summary>
    ///  The leg being driven. Runs share their boundary and the boundary belongs to the later one,
    ///  so the change of direction is requested as soon as the projection can reach it - and the
    ///  index only ever moves forward, because the projection jitters by a sample step around that
    ///  boundary: a direction that flickers leaves the truck standing on the spot changing gear
    ///  until it runs out of time.
    /// </summary>
    private Run CurrentRun()
    {
        while (runIndex + 1 < runs.Count && progress >= runs[runIndex].End - 1e-6)
            runIndex++;

        return runs[runIndex];
    }

    private void UpdateProgress(VehicleState vehicle)
    {
        IReadOnlyList<PathPoint> points = path.Points;

        // Routes cross themselves by design: the reverse leg of a two-leg maneuver lies right on
        // top of the approach it came in along. Confining the projection to the leg being driven
        // is what keeps reversing into the spot from walking progress back over the approach
        // samples, which made the remaining distance grow the further in the truck got.
        Run leg = CurrentRun();
        double windowLow = leg.Start;
        double windowHigh = leg.End + LegWindowM;

        // Progress may only advance about as fast as the vehicle physically moves. Without this
        // a route that passes close to itself (tight arcs) lets the nearest-point projection hop
        // onto another part of the curve, which teleports progress and wrecks both error terms.
        double maxAdvance = Math.Max(0.75, Math.Abs(vehicle.SignedSpeed) * stepSeconds + 0.5);

        int best = index;
        double bestDistance = Geometry.Distance(points[index].Position, vehicle.Position);

        int low = Math.Max(0, index - 8);
        int high = Math.Min(points.Count - 1, index + 60);

        for (int i = index + 1; i <= high; i++)
        {
            if (points[i].DistanceAlong - progress > maxAdvance || points[i].DistanceAlong > windowHigh)
                break;

            double distance = Geometry.Distance(points[i].Position, vehicle.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        for (int i = low; i < index; i++)
        {
            if (progress - points[i].DistanceAlong > 2.0 || points[i].DistanceAlong < windowLow)
                continue;

            double distance = Geometry.Distance(points[i].Position, vehicle.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        index = best;
        progress = points[index].DistanceAlong;
    }
}
