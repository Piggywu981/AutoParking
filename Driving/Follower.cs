using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  Everything the controller needs about the vehicle, as plain values. Keeping the telemetry
///  types out of here is what lets the follower be driven by a simulator in the test harness.
/// </summary>
public readonly record struct VehicleState(DateTime Utc, Vector2 Position, double HeadingRad, double SignedSpeed,
                                           int Gear, double UserSteer, double UserThrottle, double UserBrake)
{
    public Pose2 Pose => new(Position.X, Position.Y, HeadingRad);
}

public enum GearRequest
{
    None,
    Drive,
    Reverse,
    Neutral
}

/// <summary>
///  What the controller wants this tick. The plugin turns it into game output channels; the
///  simulator turns it into wheel commands. Nothing else publishes controls.
/// </summary>
public readonly record struct ControlDemand(float Steer, float Throttle, float Brake, bool HoldBrake,
                                            GearRequest Gear, bool Finished, string? AbortReason)
{
    public static ControlDemand Idle => new(0f, 0f, 0f, false, GearRequest.None, false, null);

    public static ControlDemand Hold(float brake) => new(0f, 0f, brake, true, GearRequest.None, false, null);

    public static ControlDemand Aborted(string reason, float brake) => new(0f, 0f, brake, true, GearRequest.None, false, reason);
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

    private readonly ParkingPath path;
    private readonly AutoParkingSettings settings;
    private readonly double wheelbase;
    private readonly double maxCurvature;

    private readonly List<Run> runs = new();

    private Stage stage = Stage.SelectGear;
    private int index;
    private double progress;
    private DriveDirection wantedGear;
    private DriveDirection? confirmedGear;
    private DateTime gearPulseAt = DateTime.MinValue;
    private int gearAttempts;
    private DateTime startedAt = DateTime.MinValue;
    private DateTime lastStepUtc = DateTime.MinValue;
    private DateTime lastStepCallUtc = DateTime.MinValue;
    private double stepSeconds = 1.0 / 60.0;
    private double integral;
    private double lastError;
    private double lastSteer;
    private DateTime lastMovementCheckUtc = DateTime.MinValue;
    private bool demandingMovement;
    private DateTime blockedSince = DateTime.MinValue;
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

    public Follower(ParkingPath path, AutoParkingSettings settings, double wheelbase)
    {
        this.path = path;
        this.settings = settings;
        this.wheelbase = wheelbase;
        maxCurvature = 1.0 / Math.Max(0.5, Kinematics.MinTurnRadius(settings));

        BuildRuns();
        wantedGear = runs.Count > 0 ? runs[0].Travel : DriveDirection.Forward;
    }

    public string Status => status;

    public double RemainingDistance => Math.Max(0.0, path.Length - progress);

    public double CrossTrackErrorMeters { get; private set; }

    public double HeadingErrorDegrees { get; private set; }

    public DriveDirection CurrentTravel => confirmedGear ?? wantedGear;

    private void BuildRuns()
    {
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

            runs.Add(new Run(current, start, points[i].DistanceAlong));
            current = points[i].Travel;
            start = points[i].DistanceAlong;
        }

        runs.Add(new Run(current, start, points[^1].DistanceAlong));
    }

    public ControlDemand Step(VehicleState vehicle, ObstacleSnapshot obstacles)
    {
        if (path.Points.Count < 2)
            return ControlDemand.Aborted("路径为空", 0.6f);

        if (startedAt == DateTime.MinValue)
        {
            startedAt = vehicle.Utc;
            lastStepUtc = vehicle.Utc;
            lastStepCallUtc = vehicle.Utc;
            lastMovementCheckUtc = vehicle.Utc;
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
                lastMovementCheckUtc = vehicle.Utc;
                integral = 0.0;
                lastError = 0.0;
                lastStepUtc = vehicle.Utc;
                demandingMovement = false;
            }
        }

        ControlDemand? fault = CheckFaults(vehicle);
        if (fault != null)
            return fault.Value;

        UpdateProgress(vehicle);

        if (RemainingDistance <= Math.Max(0.25, settings.ToleranceLateralM) && Math.Abs(vehicle.SignedSpeed) < 0.12)
        {
            stage = Stage.Finished;
            status = "到位，拉手刹";
            return new ControlDemand(0f, 0f, 0.4f, settings.HandbrakeOnFinish, GearRequest.Neutral, true, null);
        }

        switch (stage)
        {
            case Stage.SelectGear:
            case Stage.StoppingForGearChange:
                return StopAndRequestGear(vehicle);

            case Stage.WaitingForGear:
                return WaitForGear(vehicle);

            case Stage.Finished:
                return new ControlDemand(0f, 0f, 0.4f, settings.HandbrakeOnFinish, GearRequest.None, true, null);
        }

        ControlDemand? blocked = HandleObstacles(vehicle, obstacles);
        if (blocked != null)
            return blocked.Value;

        return Drive(vehicle);
    }

    private ControlDemand StopAndRequestGear(VehicleState vehicle)
    {
        bool wasMoving = Math.Abs(vehicle.SignedSpeed) > 0.15;
        stage = Stage.StoppingForGearChange;
        status = wasMoving ? "换挡前刹停" : "请求挡位";

        if (wasMoving)
            return ControlDemand.Hold(0.5f);

        gearAttempts++;
        if (gearAttempts > 3)
        {
            status = "换挡失败";
            return ControlDemand.Aborted($"换挡 3 次未成功（目标 {wantedGear}）", 0.8f);
        }

        gearPulseAt = vehicle.Utc;
        stage = Stage.WaitingForGear;
        status = $"脉冲挡位 {wantedGear}（第 {gearAttempts} 次）";

        return new ControlDemand(0f, 0f, 0.5f, false,
                                 wantedGear == DriveDirection.Forward ? GearRequest.Drive : GearRequest.Reverse,
                                 false, null);
    }

    private ControlDemand WaitForGear(VehicleState vehicle)
    {
        bool confirmed = wantedGear == DriveDirection.Forward ? vehicle.Gear > 0 : vehicle.Gear < 0;
        if (confirmed)
        {
            confirmedGear = wantedGear;
            gearAttempts = 0;
            stage = Stage.Driving;
            integral = 0.0;
            lastError = 0.0;
            status = $"挡位已确认 {wantedGear}";
            return ControlDemand.Hold(0.2f);
        }

        if ((vehicle.Utc - gearPulseAt).TotalSeconds > 1.5)
        {
            stage = Stage.StoppingForGearChange;
            status = "挡位未确认，重试";
        }

        return ControlDemand.Hold(0.5f);
    }

    private ControlDemand Drive(VehicleState vehicle)
    {
        Run run = RunAt(progress);
        if (run.Travel != confirmedGear)
        {
            wantedGear = run.Travel;
            stage = Stage.SelectGear;
            status = $"即将反向，准备换 {run.Travel}";
            return ControlDemand.Hold(0.2f);
        }

        bool reversing = run.Travel == DriveDirection.Reverse;
        double speedLimit = UnitConversions(reversing ? settings.ReverseSpeedKph : settings.ForwardSpeedKph);

        path.TryPointAt(progress, out PathPoint anchor);
        CrossTrackErrorMeters = Geometry.SignedLateral(anchor.Position, Geometry.ForwardFromHeading(anchor.HeadingRad), vehicle.Position);
        HeadingErrorDegrees = Geometry.SmallestAngleDifference(vehicle.HeadingRad, anchor.HeadingRad) * 180.0 / Math.PI;

        double steer = reversing ? ReverseSteering(vehicle, anchor) : ForwardSteering(vehicle, run);
        steer = Math.Clamp(steer, -1f, 1f);
        steer = Smooth(steer);

        double reference = ReferenceSpeed(run, speedLimit);
        double acceleration = Longitudinal(vehicle, reference);

        float throttle = acceleration > 0 ? (float)Math.Clamp(acceleration / settings.MaxAccel, 0.0, 1.0) : 0f;
        float brake = acceleration < 0 ? (float)Math.Clamp(-acceleration / settings.MaxBrakeDecel, 0.0, 1.0) : 0f;

        demandingMovement = throttle > 0.4f;

        status = $"{(reversing ? "倒车" : "前进")} 剩 {RemainingDistance:0.0} m · 误差 {CrossTrackErrorMeters:0.00} m · 目标 {reference * 3.6:0.0} km/h";

        return new ControlDemand((float)steer, throttle, brake, false, GearRequest.None, false, null);
    }

    private double ForwardSteering(VehicleState vehicle, Run run)
    {
        double lookahead = Math.Clamp(settings.LookaheadBaseM + settings.LookaheadGainMps * Math.Abs(vehicle.SignedSpeed), 1.0, 6.0);
        double targetDistance = Math.Min(progress + lookahead, run.End);

        if (!path.TryPointAt(targetDistance, out PathPoint target))
            return 0.0;

        Vector2 delta = target.Position - vehicle.Position;
        if (delta.LengthSquared() < 1e-4f)
            return 0.0;

        double alpha = Geometry.SmallestAngleDifference(Geometry.HeadingFromForward(delta), vehicle.HeadingRad);
        double curvature = 2.0 * Math.Sin(alpha) / lookahead;
        return CurvatureToSteer(curvature);
    }

    private double ReverseSteering(VehicleState vehicle, PathPoint anchor)
    {
        if (settings.ReverseLateral == ReverseLateralLaw.ReversePurePursuit)
        {
            double lookahead = Math.Clamp(settings.LookaheadBaseM + settings.LookaheadGainMps * Math.Abs(vehicle.SignedSpeed), 1.0, 6.0);

            // The car travels towards larger arc length while its nose points the other way, so
            // the aim point is ahead along the route but the angle is measured from the tail.
            if (!path.TryPointAt(Math.Min(progress + lookahead, path.Length), out PathPoint target))
                return 0.0;

            Vector2 delta = target.Position - vehicle.Position;
            if (delta.LengthSquared() < 1e-4f)
                return 0.0;

            double tailHeading = Geometry.NormalizeRadians(vehicle.HeadingRad + Math.PI);
            double alpha = Geometry.SmallestAngleDifference(Geometry.HeadingFromForward(delta), tailHeading);

            // Heading rate is v*tan(steer)/L, and v is negative in reverse, so the wheel command
            // carries the opposite sign to the forward case for the same aim error.
            return CurvatureToSteer(-2.0 * Math.Sin(alpha) / lookahead);
        }

        double headingErrorRad = HeadingErrorDegrees * Math.PI / 180.0;
        double curvature = -(settings.ReverseKxCross * CrossTrackErrorMeters + settings.ReverseKhHeading * headingErrorRad);
        return CurvatureToSteer(curvature);
    }

    private double CurvatureToSteer(double curvature)
    {
        double clamped = Math.Clamp(curvature, -maxCurvature, maxCurvature);
        double steerAngle = Math.Atan(clamped * wheelbase);
        double maxSteer = Math.Clamp(settings.MaxSteerDeg * Math.PI / 180.0, 0.05, 1.2);
        return Math.Tan(steerAngle) / Math.Tan(maxSteer);
    }

    private double Smooth(double steer)
    {
        const double RateLimit = 0.08;
        double delta = Math.Clamp(steer - lastSteer, -RateLimit, RateLimit);
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
        double dt = Math.Clamp((vehicle.Utc - lastStepUtc).TotalSeconds, 0.005, 0.05);
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

        ParkingPath remainder = SliceFrom(progress);
        int conflicts = Planner.CountCorridorConflicts(remainder, settings, obstacles);
        if (conflicts == 0)
        {
            blockedSince = DateTime.MinValue;
            return null;
        }

        if (blockedSince == DateTime.MinValue)
            blockedSince = vehicle.Utc;

        double waited = (vehicle.Utc - blockedSince).TotalSeconds;
        if (waited > settings.ObstacleWaitS)
            return ControlDemand.Aborted($"路径被挡超过 {settings.ObstacleWaitS:0} s", 0.8f);

        status = $"等待障碍离开（{waited:0.0}/{settings.ObstacleWaitS:0.0} s）";
        return ControlDemand.Hold(0.6f);
    }

    /// <summary>
    ///  The not-yet-driven part of the route, re-based at zero so the corridor check can reuse
    ///  the same code as the planner.
    /// </summary>
    private ParkingPath SliceFrom(double from)
    {
        List<PathPoint> points = new();
        foreach (PathPoint p in path.Points)
        {
            if (p.DistanceAlong + 0.05 < from)
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

    private ControlDemand? CheckFaults(VehicleState vehicle)
    {
        double elapsed = (vehicle.Utc - startedAt).TotalSeconds;
        if (elapsed > settings.MaxDurationS)
            return ControlDemand.Aborted($"超过最长允许时间 {settings.MaxDurationS:0} s", 0.8f);

        // Only armed once the vehicle has committed to the route: the very first samples of a
        // curvy route can legitimately sit a little off the projection.
        if (progress > 0.5 && Math.Abs(CrossTrackErrorMeters) > settings.MaxCrossErrorM && stage == Stage.Driving)
            return ControlDemand.Aborted($"横向偏差 {CrossTrackErrorMeters:0.0} m 超过 {settings.MaxCrossErrorM:0.0} m", 0.8f);

        double speed = Math.Abs(vehicle.SignedSpeed);
        double cap = UnitConversions(Math.Max(settings.ForwardSpeedKph, settings.ReverseSpeedKph)) + 1.0;
        if (speed > cap)
            return ControlDemand.Aborted($"速度 {speed * 3.6:0.0} km/h 失控", 1.0f);

        // Give the drivetrain a moment after engaging: pedals settling and the first gear pulse
        // can otherwise look like a takeover before the route is even being followed.
        if (settings.UserOverrideEnabled && (vehicle.Utc - startedAt).TotalSeconds > 1.0)
        {
            if (Math.Abs(vehicle.UserSteer) > settings.UserSteerThreshold)
                return ControlDemand.Aborted($"人类动了方向盘（{vehicle.UserSteer:0.00}）", 0.6f);
            if (vehicle.UserThrottle > settings.UserThrottleThreshold)
                return ControlDemand.Aborted($"人类踩了油门（{vehicle.UserThrottle:0.00}）", 0.6f);
            if (vehicle.UserBrake > settings.UserBrakeThreshold)
                return ControlDemand.Aborted($"人类踩了刹车（{vehicle.UserBrake:0.00}）", 0.6f);
        }

        // Asked to move but staying still for seconds means a kerb, a locked wheel or a gear
        // the game never actually engaged. Abort rather than keep cooking the drivetrain.
        double speedNow = Math.Abs(vehicle.SignedSpeed);
        if (speedNow > 0.05)
        {
            lastMovementCheckUtc = vehicle.Utc;
        }
        else if (demandingMovement && (vehicle.Utc - lastMovementCheckUtc).TotalSeconds > 3.0)
        {
            return ControlDemand.Aborted("已给油门但车辆 3 s 未移动（可能卡住或挡位未生效）", 1.0f);
        }

        return null;
    }

    /// <summary>
    ///  True when the game itself is not reacting to our output: we command a pedal or the
    ///  wheel and the game-reported input stays at zero. The plugin owns the timer because only
    ///  it knows what was actually sent.
    /// </summary>
    public bool DemandingMovement => demandingMovement;

    private double UnitConversions(double kmPerHour) => kmPerHour / 3.6;

    private Run RunAt(double distance)
    {
        for (int i = 0; i < runs.Count; i++)
        {
            if (distance <= runs[i].End + 1e-6)
                return runs[i];
        }

        return runs[^1];
    }

    private void UpdateProgress(VehicleState vehicle)
    {
        IReadOnlyList<PathPoint> points = path.Points;

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
            if (points[i].DistanceAlong - progress > maxAdvance)
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
            if (progress - points[i].DistanceAlong > 2.0)
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
