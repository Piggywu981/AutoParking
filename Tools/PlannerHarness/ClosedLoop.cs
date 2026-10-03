using System.Numerics;
using AutoParking;

/// <summary>
///  Closed-loop check: a kinematic bicycle model driven by the real Follower, through the real
///  planner, with a delayed gearbox so the gear state machine actually has to wait and verify.
///
///  What this CAN catch: a controller that diverges, a gear machine that deadlocks, a speed
///  profile that overshoots, an end pose that misses. What it CANNOT catch: whether the game's
///  own steering pedal polarity matches our convention - both sides of that live in this sim,
///  so it has to be confirmed in the simulator (M4).
/// </summary>
public static class ClosedLoop
{
    public readonly record struct Outcome(string Name, bool Reached, double PositionError, double HeadingErrorDeg,
                                          double Seconds, int GearPulses, string Status,
                                          double FullLockSeconds = 0.0, int SteerReversals = 0, double SteerTravel = 0.0);

    public static List<Outcome> RunAll(AutoParkingSettings cfg)
    {
        const double Deg = Math.PI / 180.0;

        return new List<Outcome>
        {
            Run("直线倒车入库", new Pose2(0, 0, 0), new Pose2(0, 8, 0), cfg),
            Run("右前 45 度进库", new Pose2(0, 0, 0), new Pose2(6, -9, -45 * Deg), cfg),
            Run("左侧垂直库", new Pose2(0, 0, 0), new Pose2(-8, 0, 90 * Deg), cfg),
            Run("正前方同向", new Pose2(0, 0, 0), new Pose2(0, -20, 0), cfg),
            Run("左后斜入库", new Pose2(0, 0, 0), new Pose2(-6, 8, 45 * Deg), cfg)
        };
    }

    public static Outcome Run(string name, Pose2 start, Pose2 goal, AutoParkingSettings cfg, bool debug = false,
                              bool gearboxDeaf = false, ObstacleSnapshot? obstacles = null, bool replan = false,
                              bool automaticNeutral = false)
    {
        // The abort conditions that depend on the vehicle actually responding are gated off
        // while DryRun is on, so a harness that leaves it on would silently skip them.
        cfg.DryRun = false;

        // Planned with an empty field on purpose: the blocker handed to Run is one that appears
        // after the maneuver started, which is the situation re-planning exists for.
        ObstacleSnapshot field = obstacles ?? new ObstacleSnapshot();

        PlanResult plan = Planner.Plan(start, goal, cfg, new ObstacleSnapshot());
        if (!plan.Ok)
            return new Outcome(name, false, double.NaN, double.NaN, 0, 0, "规划失败：" + plan.Reason);

        Follower follower = new(plan.Path!, cfg, cfg.WheelbaseM, replan
            ? (from, blocked) =>
              {
                  PlanResult next = Planner.Plan(from, goal, cfg, blocked);
                  return next.Ok ? next.Path : null;
              }
            : null);

        const double dt = 1.0 / 60.0;
        DateTime clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double maxSteer = Math.Clamp(cfg.MaxSteerDeg * Math.PI / 180.0, 0.05, 1.2);

        Pose2 pose = start;
        double velocity = 0.0;
        int gear = 0;
        int selector = 0;
        int pendingGear = 0;
        DateTime gearAppliesAt = DateTime.MinValue;
        int gearPulses = 0;
        bool finished = false;
        string status = "";

        // Steering quality: how much of the run is spent pinned at full lock, how often the
        // wheel changes direction, and the total wheel travel. Saturation and chatter are what
        // make a geometrically correct path undrivable in the game.
        double fullLock = 0.0, steerTravel = 0.0, previousSteer = 0.0;
        int reversals = 0;

        for (double t = 0.0; t < 300.0 && !finished; t += dt)
        {
            clock = clock.AddSeconds(dt);

            // An automatic drops to neutral whenever the truck is stationary, while the stick keeps
            // the selected position (the real log shows gear=0 with dash=-1 at every standstill).
            int reported = automaticNeutral && Math.Abs(velocity) < 0.02 ? 0 : gear;

            VehicleState state = new(clock, pose.Position, pose.HeadingRad, velocity, reported, selector);
            ControlDemand demand = follower.Step(state, field);
            status = follower.Status;

            if (Math.Abs(demand.Steer) > 0.95f) fullLock += dt;
            if (demand.Steer * previousSteer < 0f && Math.Abs(demand.Steer - previousSteer) > 0.05f) reversals++;
            steerTravel += Math.Abs(demand.Steer - previousSteer);
            previousSteer = demand.Steer;

            if (demand.Gear != GearRequest.None)
            {
                gearPulses++;
                pendingGear = demand.Gear switch
                {
                    GearRequest.Drive => 1,
                    GearRequest.Reverse => -1,
                    _ => 0
                };
                gearAppliesAt = clock.AddMilliseconds(350);
            }

            if (gearAppliesAt != DateTime.MinValue && clock >= gearAppliesAt && !gearboxDeaf)
            {
                gear = pendingGear;
                selector = pendingGear;
                gearAppliesAt = DateTime.MinValue;
            }

            // The direction of travel still follows the stick, because that is what the torque
            // converter transmits through.
            double drive = demand.Throttle * cfg.MaxAccel * (selector < 0 ? -1.0 : 1.0);
            double braking = demand.Brake * cfg.MaxBrakeDecel + (demand.HoldBrake ? cfg.MaxBrakeDecel : 0.0);

            velocity += drive * dt;

            if (braking > 0.0)
            {
                double drop = braking * dt;
                velocity = Math.Abs(velocity) <= drop ? 0.0 : velocity - Math.Sign(velocity) * drop;
            }

            // Rolling resistance, so the launch boost is actually exercised rather than flattered.
            const double Resistance = 0.15;
            if (Math.Abs(velocity) > 0.001)
            {
                double drop = Resistance * dt;
                velocity = Math.Abs(velocity) <= drop ? 0.0 : velocity - Math.Sign(velocity) * drop;
            }
            else if (drive == 0.0)
            {
                velocity = 0.0;
            }

            if (gear == 0 && drive == 0.0)
                velocity *= 0.97;

            velocity = Math.Clamp(velocity, -2.5, 2.5);
            if (Math.Abs(velocity) < 0.002 && drive == 0.0)
                velocity = 0.0;

            double roadWheel = Math.Clamp(demand.Steer, -1f, 1f) * maxSteer;

            pose = pose with
            {
                X = pose.X + velocity * -Math.Sin(pose.HeadingRad) * dt,
                Z = pose.Z + velocity * -Math.Cos(pose.HeadingRad) * dt,
                HeadingRad = Geometry.NormalizeRadians(pose.HeadingRad + velocity * Math.Tan(roadWheel) / cfg.WheelbaseM * dt)
            };

            if (demand.Finished)
                finished = true;

            if (debug && ((int)(t * 60) % 30 == 0 || follower.RemainingDistance < 3.0))
            {
                Console.WriteLine($"      t={t:0.00} gear={gear} v={velocity:0.000} pos=({pose.X:0.00},{pose.Z:0.00}) " +
                                  $"剩={follower.RemainingDistance:0.00} " +
                                  $"航向差={follower.HeadingErrorDegrees:0.00}° 横={follower.CrossTrackErrorMeters:0.000} " +
                                  $"steer={demand.Steer:0.00} thr={demand.Throttle:0.00} brk={demand.Brake:0.00} hold={demand.HoldBrake} | {follower.Status}");
            }
        }

        double positionError = Geometry.Distance(pose.Position, goal.Position);
        double headingError = Math.Abs(Geometry.SmallestAngleDifference(pose.HeadingRad, goal.HeadingRad)) * 180.0 / Math.PI;

        // Lateral and heading accuracy are what put the vehicle in the bay; being 40 cm long or
        // short along the bay axis is fine, so judging them with one scalar would be wrong.
        Vector2 goalForward = Geometry.ForwardFromHeading(goal.HeadingRad);
        Vector2 offset = pose.Position - goal.Position;
        double longitudinal = Math.Abs(offset.X * goalForward.X + offset.Y * goalForward.Y);
        double lateral = Math.Abs(Geometry.SignedLateral(goal.Position, goalForward, pose.Position));

        // A bay is 5 m deep and the truck has to sit straight in it: 4° of nose angle at the
        // moment we declare "done" is what puts the driver out of the bay on the next attempt,
        // so the acceptance threshold is one degree, not the eight that used to pass.
        bool reached = finished && lateral <= 0.25 && longitudinal <= 0.6 && headingError <= 1.0;

        return new Outcome(name, reached, lateral, headingError, 0, gearPulses,
                           (finished ? "" : "超时未完成：") + status + $" · 重规划 {follower.Replans} 次",
                           fullLock, reversals, steerTravel);
    }
}
