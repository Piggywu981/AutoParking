using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for the smooth forward family (§53): lead-in straight, then arc - straight - arc with the
///  two arcs allowed to have different radii.
///
///  It exists because on the live ATS course the driver asked for one smooth curve instead of the
///  two-shift maneuver the planner kept returning, and every equal-radius, no-lead-in shape available
///  at the time pressed the cone at the end of the lane's north row. These assertions are pure
///  geometry over the coordinates read out of game memory, so they run without the game.
/// </summary>
internal static class SmoothForwardChecks
{
    private const double Deg = Math.PI / 180.0;

    public static int Run()
    {
        int failures = 0;

        AutoParkingSettings settings = new()
        {
            WheelbaseM = 4.95,          // measured off the live 6x2 (§49)
            MaxSteerDeg = 33.0,
            PlanRadiusMargin = 1.35,
            VehicleWidthM = 2.47,       // measured body box (§51/§52)
            VehicleLengthM = 8.43,
            ObstacleMarginM = 0.05,
            EnvelopePadM = 0.05,
            SmoothForwardLeadInM = 12.0
        };
        double radius = Kinematics.PlanningRadius(settings);
        Report(ref failures, Math.Abs(radius - 10.29) < 0.05,
               "现场车的规划半径 ≈10.29 m", $"{radius:0.00} m（轴距 4.95 / tan 33° × 1.35）");

        // The course pose, read live: truck at (1998.94, 2161.90) nose west, bay centre at
        // (1954.33, 2142.85) with the bay's own heading (nose west). The spot is 44.6 m ahead and
        // 19.0 m to the driver's right.
        Pose2 start = new(1998.94, 2161.90, 90 * Deg);
        Pose2 goal = new(1954.33, 2142.85, 90 * Deg);

        List<ParkingPath> routes = ReedsShepp.SmoothForward(start, goal, radius, settings.PathSampleM,
                                                           settings.SmoothForwardLeadInM, 6.0,
                                                           ReedsShepp.SmoothCandidateMax * 4);

        Report(ref failures, routes.Count > 0, "现场位姿能解出全前进平滑候选", $"{routes.Count} 条");

        int badGear = routes.Count(p => p.GearSwitches != 0
                                        || p.Points.Any(v => v.Travel != DriveDirection.Forward));
        Report(ref failures, badGear == 0, "每条候选全程前进、0 换挡", $"违例 {badGear} 条");

        int badEnd = routes.Count(p => Geometry.Distance(p.Points[^1].Position, goal.Position) > 0.05
                                       || Math.Abs(Geometry.SmallestAngleDifference(p.Points[^1].HeadingRad,
                                                                                    goal.HeadingRad)) > 0.02);
        Report(ref failures, badEnd == 0, "每条候选终点都落在目标位姿上",
               $"违例 {badEnd} 条，最远 " +
               $"{(routes.Count == 0 ? 0 : routes.Max(p => Geometry.Distance(p.Points[^1].Position, goal.Position))):0.000} m");

        int overCurved = routes.Count(p => p.Points.Any(v => Math.Abs(v.Curvature) > 1.0 / radius + 1e-6));
        Report(ref failures, overCurved == 0, "没有一段弧比规划半径更紧",
               $"违例 {overCurved} 条，最紧 " +
               $"{(routes.Count == 0 ? 0 : 1.0 / routes.Max(p => p.Points.Max(v => Math.Abs(v.Curvature)))):0.0} m");

        // Radius measured over the arc points only: a route that is mostly straight has zero curvature
        // and would otherwise make "gentlest" read as infinite, which proves nothing.
        double Gentlest(ParkingPath p)
        {
            double tightest = p.Points.Where(v => Math.Abs(v.Curvature) > 1e-9)
                                      .Select(v => Math.Abs(v.Curvature)).DefaultIfEmpty(0.0).Min();
            return tightest <= 0 ? 0.0 : 1.0 / tightest;
        }

        double gentlest = routes.Count == 0 ? 0 : routes.Max(Gentlest);
        Report(ref failures, gentlest >= 2.0 * radius,
               "族里确实有比最小半径平缓得多的解（这就是要它的原因）",
               $"最平缓 {gentlest:0.0} m vs 规划半径 {radius:0.0} m");

        bool sorted = routes.Zip(routes.Skip(1), (a, b) => a.Cost <= b.Cost + 1e-9).All(v => v);
        Report(ref failures, sorted, "候选按代价升序返回", sorted ? "是" : "否");

        // The lead-in is the part the driver can see, so it must really be there at the front of the
        // route rather than only in the description.
        ParkingPath? withLeadIn = routes.FirstOrDefault(p => p.Description.Contains("前移"));
        int LeadingStraight(ParkingPath p) => p.Points.TakeWhile(v => v.Curvature == 0.0).Count();
        Report(ref failures, withLeadIn != null && LeadingStraight(withLeadIn) >= 4,
               "带前移的候选开头确实是直线段",
               withLeadIn == null ? "一条都没有" : $"{withLeadIn.Description}，起头 {LeadingStraight(withLeadIn)} 个零曲率点");

        // Mirror: the same displacement to the other side must produce the other handedness.
        Pose2 leftGoal = new(1954.33, 2180.95, 90 * Deg);
        List<ParkingPath> mirrored = ReedsShepp.SmoothForward(start, leftGoal, radius, settings.PathSampleM,
                                                             settings.SmoothForwardLeadInM, 6.0,
                                                             ReedsShepp.SmoothCandidateMax);
        double PeakLateral(ParkingPath p) => p.Points
            .Select(v => Geometry.SignedLateral(start.Position, start.Forward, v.Position))
            .OrderBy(Math.Abs).LastOrDefault();
        Report(ref failures, mirrored.Count > 0 && mirrored.Any(p => PeakLateral(p) > 1.0),
               "目标在另一侧时镜像成立（偏移反号）",
               $"{mirrored.Count} 条，最大横向偏移 " +
               $"{(mirrored.Count == 0 ? 0 : mirrored.Max(PeakLateral)):0.0} m（现场那条是 " +
               $"{(routes.Count == 0 ? 0 : routes.Max(PeakLateral)):0.0} m 在右手边）");

        // lead-in = 0 must leave only the d=0 members: the knob on the settings page is the switch.
        List<ParkingPath> noLeadIn = ReedsShepp.SmoothForward(start, goal, radius, settings.PathSampleM,
                                                             0.0, 6.0, ReedsShepp.SmoothCandidateMax);
        Report(ref failures, noLeadIn.Count > 0 && noLeadIn.All(p => !p.Description.Contains("前移")),
               "「平滑线允许前移」0 = 只剩无前移段的成员",
               $"{noLeadIn.Count} 条，其中带前移的 {noLeadIn.Count(p => p.Description.Contains("前移"))} 条");

        // The new defaults are the whole point of §53: 0.25 m per side refused every route on this
        // course, so a fresh install must not carry it.
        AutoParkingSettings fresh = new();
        Report(ref failures, Math.Abs(fresh.EnvelopeWidthM - (fresh.VehicleWidthM + 0.2)) < 1e-9,
               "出厂包络 = 车宽 + 每侧 0.10 m", $"{fresh.EnvelopeWidthM:0.00} m（车宽 {fresh.VehicleWidthM:0.00}）");
        fresh.ObstacleMarginM = 0.0;
        fresh.Clamp();
        Report(ref failures, fresh.ObstacleMarginM >= 0.05,
               "障碍余量的夹取下限已放到 0.05", $"{fresh.ObstacleMarginM:0.00} m");

        AutoParkingSettings familyOff = new()
        {
            WheelbaseM = settings.WheelbaseM,
            MaxSteerDeg = settings.MaxSteerDeg,
            PlanRadiusMargin = settings.PlanRadiusMargin,
            VehicleWidthM = settings.VehicleWidthM,
            VehicleLengthM = settings.VehicleLengthM,
            ObstacleMarginM = settings.ObstacleMarginM,
            EnvelopePadM = settings.EnvelopePadM,
            SmoothForwardLeadInM = 0.0
        };

        // What a new candidate family is allowed to do to the planner's answer: nothing for the worse.
        // A conflict-free route that costs more than the previous best would mean the family is wired in
        // ahead of the obstacle test rather than inside it.
        PlanResult withFamily = Planner.Plan(start, goal, settings, new ObstacleSnapshot());
        PlanResult withoutFamily = Planner.Plan(start, goal, familyOff, new ObstacleSnapshot());
        ParkingPath? picked = withFamily.Path;
        ParkingPath? previous = withoutFamily.Path;
        bool neverWorse = picked != null && previous != null && picked.Cost <= previous.Cost + 1e-6;
        Report(ref failures, neverWorse, "加了这一族之后规划器的选择不可能变差",
               $"带族 {(picked == null ? "无解" : $"{picked.Cost:0.0} m · {picked.Description}")}" +
               $" vs 不带 {(previous == null ? "无解" : $"{previous.Cost:0.0} m · {previous.Description}")}");

        // And the honest limit of this section: on the live course the analytic planner has no
        // conflict-free candidate at all - smooth or otherwise - because its candidate universe cannot
        // detour (§45). The smooth routes that do exist there came from the offline sweep
        // (scratch/pad_table.py), not from this planner, and saying so is the point of the assertion.
        // The set is the corridor the maneuver drives through: lane rows, the cone curtain at x=1988.9,
        // the diagonal that becomes the bay's north wall, the south row and the west cap.
        ObstacleSnapshot course = new();
        foreach ((double x, double z) in CourseCones())
            course.AddBox(new Vector2((float)x, (float)z), 0.84f, 0.84f);
        PlanResult onCourse = Planner.Plan(start, goal, settings, course);
        Report(ref failures, !onCourse.Ok,
               "现场障碍集下解析规划器仍无 0 冲突候选（§45 盲区，未因这一族改变）",
               onCourse.Ok ? $"意外有解：{onCourse.Path?.Description}" : onCourse.Reason);

        return failures;
    }

    /// <summary>Cone centres read live off the ATS course (Tools/MemoryProbe --viz), corridor only.</summary>
    private static IEnumerable<(double, double)> CourseCones()
    {
        (double, double)[] laneNorth = { (1988.90, 2158.54), (1994.36, 2158.54), (1999.74, 2158.54), (2005.44, 2158.54) };
        (double, double)[] laneSouth = { (1988.90, 2165.04), (1994.36, 2165.04), (1999.74, 2165.04), (2005.44, 2165.04) };
        (double, double)[] curtain = { (1988.90, 2141.97), (1988.90, 2148.02), (1988.90, 2153.03) };
        (double, double)[] diagonal =
        {
            (1984.98, 2164.89), (1981.88, 2164.16), (1978.57, 2162.45), (1975.46, 2159.63), (1973.06, 2156.34),
            (1970.61, 2152.65), (1968.38, 2149.60), (1966.00, 2147.38), (1963.46, 2145.80), (1960.60, 2144.97)
        };
        (double, double)[] baySouth =
        {
            (1960.66, 2140.67), (1963.27, 2140.68), (1965.95, 2140.68), (1968.65, 2140.68), (1971.70, 2140.68),
            (1974.35, 2140.68), (1977.09, 2140.68), (1979.39, 2140.68), (1982.79, 2140.68), (1987.82, 2140.68)
        };
        (double, double)[] westCap = { (1948.0, 2142.4), (1948.0, 2143.3), (1948.7, 2140.7), (1948.7, 2144.9) };
        (double, double)[] southWall = { (1950.9, 2140.7), (1953.1, 2140.7), (1955.3, 2140.7), (1957.8, 2140.7) };
        (double, double)[] northWall = { (1951.0, 2144.9), (1953.1, 2144.9), (1955.4, 2144.9), (1957.4, 2144.9) };

        return laneNorth.Concat(laneSouth).Concat(curtain).Concat(diagonal).Concat(baySouth)
                       .Concat(westCap).Concat(southWall).Concat(northWall);
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
