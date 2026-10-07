using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for RouteFootprint. The premise it has to defend is M7a's whole contract: measuring must not
///  change which route the planner picks. So the last case runs the planner twice over the same scenario,
///  with and without a trust map, and demands the same winner from the same source family.
/// </summary>
internal static class RouteFootprintChecks
{
    public static int Run()
    {
        int failures = 0;
        AutoParkingSettings settings = new();
        (double length, double width) = RouteFootprint.EnvelopeSize(settings);

        bool sameAsCollision = Math.Abs(length - (settings.VehicleLengthM + 0.6 + 2 * settings.ObstacleMarginM)) < 1e-9
                               && Math.Abs(width - (settings.VehicleWidthM + 0.5 + 2 * settings.ObstacleMarginM)) < 1e-9;
        Report(ref failures, sameAsCollision, "包络=冲突判定用的那个包络",
            $"{length:0.00} × {width:0.00} m（和 CountCorridorConflicts 一致）");

        ParkingPath straight = Route(new Pose2(0, 0, 0), new Pose2(10, 0, 0), DriveDirection.Forward, settings);
        GroundTrust wide = Trust(-20, 20, 5.0, 120);

        RouteFootprint.Measure(straight, settings, wide);
        double expected = (10.0 + length) * width;
        Report(ref failures, straight.SweptAreaM2 >= expected * 0.85 && straight.SweptAreaM2 <= expected * 1.20,
            "直线段占地≈长×宽", $"{straight.SweptAreaM2:0} m²（参照 {expected:0} m²，栅格量化允许 ±15/20%）");

        // Out and back over the same ground must not double-count: cell sets, not sums.
        ParkingPath thereAndBack = Route(new Pose2(0, 0, 0), new Pose2(10, 0, 0), DriveDirection.Forward, settings,
                                         backToStart: true);
        RouteFootprint.Measure(thereAndBack, settings, wide);
        Report(ref failures, Math.Abs(thereAndBack.SweptAreaM2 - straight.SweptAreaM2) <= 2.0,
            "同一条走廊来回只算一次", $"去 {straight.SweptAreaM2:0} m² / 往返 {thereAndBack.SweptAreaM2:0} m²");

        // Inside the corridor everything is vouched for.
        Report(ref failures, straight.UnconfirmedAreaM2 == 0.0,
            "走廊内 → 未确认面积 0", $"{straight.UnconfirmedAreaM2:0} m²");

        // Push the same route sideways, off the curve: it is still the same area, now most of it unvouched.
        ParkingPath offset = Route(new Pose2(0, 9, 0), new Pose2(10, 9, 0), DriveDirection.Forward, settings);
        RouteFootprint.Measure(offset, settings, wide);
        Report(ref failures, offset.UnconfirmedAreaM2 > 0.6 * offset.SweptAreaM2,
            "偏出走廊 → 大部分未确认",
            $"{offset.UnconfirmedAreaM2:0} / {offset.SweptAreaM2:0} m²（y=9 m，半宽 5 m）");

        // Shrinking the sampled radius can only make a route look more exposed, never less.
        GroundTrust tight = Trust(-20, 20, 5.0, 6.0);
        ParkingPath copy = Route(new Pose2(0, 0, 0), new Pose2(10, 0, 0), DriveDirection.Forward, settings);
        RouteFootprint.Measure(copy, settings, tight);
        Report(ref failures, copy.UnconfirmedAreaM2 >= straight.UnconfirmedAreaM2,
            "半径越小越不确认", $"6 m 半径 {copy.UnconfirmedAreaM2:0} m² ≥ 120 m 半径 {straight.UnconfirmedAreaM2:0} m²");

        // A collidable object on the route's ground converts confirmed cells to unconfirmed.
        GroundTrust walled = Trust(-20, 20, 5.0, 120);
        walled.AddBlocker(new[] { new Vector2(4, -1), new Vector2(6, -1), new Vector2(6, 1), new Vector2(4, 1) }, true);
        ParkingPath through = Route(new Pose2(0, 0, 0), new Pose2(10, 0, 0), DriveDirection.Forward, settings);
        RouteFootprint.Measure(through, settings, walled);
        Report(ref failures, through.UnconfirmedAreaM2 >= 4.0,
            "障碍方块吃掉确认", $"{through.UnconfirmedAreaM2:0} m²（2×2 墙 + 包络重叠）");

        // Unmeasured stays distinguishable from zero-measured, because the map has to render both.
        ParkingPath untouched = Route(new Pose2(0, 0, 0), new Pose2(5, 0, 0), DriveDirection.Forward, settings);
        Report(ref failures, untouched.SweptAreaM2 < 0.0 && untouched.UnconfirmedAreaM2 < 0.0,
            "未测量 = -1，不是 0", $"占地 {untouched.SweptAreaM2} / 未确认 {untouched.UnconfirmedAreaM2}");

        // THE contract of M7a: instrumenting must not steer. Same scenario, trust on and off,
        // the planner must hand back the same winning family.
        Pose2 start = new(0, 0, 0);
        Pose2 goal = new(-8, 0, Math.PI / 2.0);
        ObstacleSnapshot none = new();
        PlanResult plain = Planner.Plan(start, goal, settings, none);
        PlanResult measured = Planner.Plan(start, goal, settings, none, wide);
        bool unchanged = plain.Path != null && measured.Path != null
                         && plain.Path.Source == measured.Path.Source
                         && Math.Abs(plain.Path.Length - measured.Path.Length) < 1e-6;
        Report(ref failures, unchanged, "仪表不改变选路",
            plain.Path == null || measured.Path == null
                ? "一侧无解"
                : $"{plain.Path.Source} / {measured.Path.Source}，长度 {plain.Path.Length:0.00} vs {measured.Path.Length:0.00} m");

        Console.WriteLine("   各候选（长度 / 换挡 / 占地 / 未确认）：");
        if (measured.Candidates != null)
        {
            foreach (ParkingPath candidate in measured.Candidates)
            {
                Console.WriteLine($"    {(ReferenceEquals(candidate, measured.Path) ? "选中" : "    ")} " +
                                  $"{candidate.Description,-28} {candidate.Length,5:0.0} m  换挡 {candidate.GearSwitches}  " +
                                  $"占地 {candidate.SweptAreaM2,5:0} m²  未确认 {candidate.UnconfirmedAreaM2,5:0} m²");
            }
        }

        return failures;
    }

    private static GroundTrust Trust(double from, double to, double halfWidth, double radius)
    {
        GroundTrust trust = new()
        {
            Center = new Vector2(0, 0),
            RadiusM = radius,
            CorridorHalfWidthM = halfWidth
        };
        trust.AddCorridor(new[] { new Vector2((float)from, 0f), new Vector2((float)to, 0f) });
        return trust;
    }

    private static ParkingPath Route(Pose2 from, Pose2 to, DriveDirection travel, AutoParkingSettings settings,
                                     bool backToStart = false)
    {
        // Every point carries the heading that points the vehicle AT its own direction of travel. The
        // convention here is yaw 0 = -Z (Geometry.ForwardFromHeading), so a route that "starts at heading 0"
        // and runs along +X would be sliding sideways — which is what the first draft of this helper did, and
        // it made the straight-line area assertion fail on a test bug rather than on the rasterizer.
        double heading = Geometry.HeadingFromForward(to.Position - from.Position);
        double span = Geometry.Distance(from.Position, to.Position);
        int steps = Math.Max(2, (int)Math.Ceiling(span / settings.PathSampleM));

        List<PathPoint> points = new();
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            Vector2 position = from.Position + (to.Position - from.Position) * (float)t;
            points.Add(new PathPoint(position, heading, 0.0, travel, span * t));
        }

        if (backToStart)
        {
            // Retrace the same ground in reverse, so the second pass must add no new cells. The first
            // draft of this case ended the return leg at the far point (zero length), which made the
            // dedupe assertion pass without ever revisiting anything.
            double offset = points[^1].DistanceAlong;
            double backSpan = Geometry.Distance(to.Position, from.Position);
            double backHeading = Geometry.HeadingFromForward(from.Position - to.Position);
            int backSteps = Math.Max(2, (int)Math.Ceiling(backSpan / settings.PathSampleM));
            for (int i = 0; i <= backSteps; i++)
            {
                double t = i / (double)backSteps;
                Vector2 position = to.Position + (from.Position - to.Position) * (float)t;
                points.Add(new PathPoint(position, backHeading, 0.0, DriveDirection.Reverse,
                                         offset + backSpan * t));
            }
        }

        return new ParkingPath
        {
            Points = points,
            Source = PlanSource.ReedsSheppForward,
            Cost = points[^1].DistanceAlong,
            GearSwitches = travel == DriveDirection.Reverse ? 1 : 0,
            Description = "测试路径"
        };
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
