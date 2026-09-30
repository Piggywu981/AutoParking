using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  Geometric self-checks that need no game: every planned route must actually end on the
///  requested pose, be sampled densely enough to follow, respect the turning radius, and
///  refuse routes through obstacles. Run it from the settings page; it only touches pure math.
/// </summary>
public static class PlannerSelfTest
{
    public sealed class Report
    {
        public int Cases;
        public int Passed;
        public readonly List<string> Failures = new();

        public string Summary => Failures.Count == 0
            ? $"{Passed}/{Cases} 通过"
            : $"{Passed}/{Cases} 通过，失败：{string.Join(" | ", Failures)}";
    }

    private readonly record struct Case(string Name, Pose2 Start, Pose2 Goal, bool ExpectPath,
                                        Vector2[][]? Obstacles = null);

    public static Report Run(AutoParkingSettings settings)
    {
        Report report = new();
        AutoParkingSettings cfg = Clone(settings);

        foreach (Case testCase in BuildCases())
        {
            report.Cases++;

            ObstacleSnapshot obstacles = new();
            if (testCase.Obstacles != null)
                obstacles.Polygons.AddRange(testCase.Obstacles);

            PlanResult result;
            try
            {
                result = Planner.Plan(testCase.Start, testCase.Goal, cfg, obstacles);
            }
            catch (Exception ex)
            {
                report.Failures.Add($"{testCase.Name}: 抛出 {ex.GetType().Name}");
                continue;
            }

            if (!testCase.ExpectPath)
            {
                if (result.Ok)
                    report.Failures.Add($"{testCase.Name}: 期望无解，却给出了可用路径（{result.Summary}）");
                else
                    report.Passed++;
                continue;
            }

            string? problem = CheckPath(result, testCase, cfg);
            if (problem == null)
                report.Passed++;
            else
                report.Failures.Add($"{testCase.Name}: {problem}");
        }

        return report;
    }

    private static string? CheckPath(PlanResult result, Case testCase, AutoParkingSettings cfg)
    {
        if (!result.Ok)
            return result.ConflictCount > 0 ? $"路径被障碍挡住（{result.ConflictCount} 处）" : $"无解（{result.Reason}）";

        ParkingPath path = result.Path!;
        IReadOnlyList<PathPoint> points = path.Points;

        if (points.Count < 2)
            return "采样点不足";

        PathPoint last = points[^1];
        double endDistance = Geometry.Distance(last.Position, testCase.Goal.Position);
        double endHeading = Math.Abs(Geometry.SmallestAngleDifference(last.HeadingRad, testCase.Goal.HeadingRad));
        if (endDistance > 0.06)
            return $"终点偏差 {endDistance:0.000} m";
        if (endHeading > 0.03)
            return $"终点航向偏差 {endHeading * 180 / Math.PI:0.0}°";

        double maxCurvature = 1.0 / cfg.WheelbaseM * Math.Tan(cfg.MaxSteerDeg * Math.PI / 180.0) * 1.5;
        double previousAlong = points[0].DistanceAlong;

        for (int i = 0; i < points.Count; i++)
        {
            PathPoint p = points[i];

            if (double.IsNaN(p.Position.X) || double.IsNaN(p.Position.Y) || double.IsNaN(p.HeadingRad) || double.IsNaN(p.DistanceAlong))
                return $"第 {i} 点出现 NaN";

            double gap = p.DistanceAlong - previousAlong;
            if (i > 0 && gap > 0.35)
                return $"第 {i} 点采样间隔 {gap:0.00} m 过大";

            if (i > 0 && Geometry.Distance(p.Position, points[i - 1].Position) > 0.35)
                return $"第 {i} 点空间跳变过大";

            if (Math.Abs(p.Curvature) > maxCurvature + 1e-3)
                return $"第 {i} 点曲率 {Math.Abs(p.Curvature):0.000} 超出最小转弯半径";

            previousAlong = p.DistanceAlong;
        }

        if (path.GearSwitches > 2)
            return $"换挡 {path.GearSwitches} 次，超过 2 次";

        // A construction regression normally survives every other check but shows up as a path
        // that loops the full circle where a short arc belongs. Legitimate wide maneuvers cost at
        // most about one extra circle over the straight line, so bound it that way - per-case
        // magic numbers here would just be flaky.
        double straightLine = Geometry.Distance(testCase.Start.Position, testCase.Goal.Position);
        double loopBound = straightLine + Geometry.TwoPi * Kinematics.MinTurnRadius(cfg) * 1.05;
        if (path.Length > loopBound)
            return $"路径 {path.Length:0.0} m 比直线 {straightLine:0.0} m 多绕了超过一圈（上限 {loopBound:0.0} m）";

        if (result.ConflictCount > 0)
            return $"路径与障碍冲突 {result.ConflictCount} 处";

        return null;
    }

    private static List<Case> BuildCases()
    {
        const double Deg = Math.PI / 180.0;
        List<Case> cases = new();

        cases.Add(new Case("正前方同向", new Pose2(0, 0, 0), new Pose2(0, -20, 0), true));
        cases.Add(new Case("正前方同向偏移", new Pose2(0, 0, 0), new Pose2(4, -18, 0), true));
        cases.Add(new Case("左前方 90 度", new Pose2(0, 0, 0), new Pose2(-8, -10, 90 * Deg), true));
        cases.Add(new Case("右前方 90 度", new Pose2(0, 0, 0), new Pose2(8, -10, -90 * Deg), true));
        cases.Add(new Case("正后方 180 度", new Pose2(0, 0, 0), new Pose2(0, 12, 180 * Deg), true));
        cases.Add(new Case("左后方斜入库", new Pose2(0, 0, 0), new Pose2(-6, 8, 45 * Deg), true));
        cases.Add(new Case("右后方斜入库", new Pose2(0, 0, 0), new Pose2(6, 8, -45 * Deg), true));
        cases.Add(new Case("直线倒车入库", new Pose2(0, 0, 0), new Pose2(0, 8, 0), true));
        cases.Add(new Case("侧方平移入库", new Pose2(0, 0, 0), new Pose2(3.2, 6, 0), true));
        cases.Add(new Case("近距离小角度", new Pose2(0, 0, 0), new Pose2(1.0, -2.0, 10 * Deg), true));
        cases.Add(new Case("目标在车侧 90 度", new Pose2(0, 0, 0), new Pose2(-7, 0, 90 * Deg), true));
        cases.Add(new Case("目标在车侧 -90 度", new Pose2(0, 0, 0), new Pose2(7, 0, -90 * Deg), true));
        cases.Add(new Case("较远斜向目标", new Pose2(0, 0, 0), new Pose2(14, -14, 30 * Deg), true));
        cases.Add(new Case("车头顶墙式目标", new Pose2(0, 0, 0), new Pose2(0, -6, 180 * Deg), true));
        cases.Add(new Case("大转角目标", new Pose2(0, 0, 0), new Pose2(-3, -3, 135 * Deg), true));
        cases.Add(new Case("反向小距离", new Pose2(0, 0, 0), new Pose2(2.0, 3.0, 20 * Deg), true));

        // Obstacle handling: a wall of parked cars across the only sensible route must be refused.
        Vector2[] Wall(int index)
        {
            Pose2 pose = new(-1.5 + index * 3.0, 6.0, 0);
            return Geometry.RectangleCorners(pose, 4.6, 1.9);
        }

        cases.Add(new Case("路径被车挡住",
                            new Pose2(0, 0, 0),
                            new Pose2(0, 14, 0),
                            false,
                            new[] { Wall(0), Wall(1), Wall(2), Wall(3) }));

        cases.Add(new Case("绕开侧方障碍",
                            new Pose2(0, 0, 0),
                            new Pose2(0, 12, 0),
                            true,
                            new[] { Wall(4) }));

        // Degenerate and invalid inputs must not produce a route or throw.
        cases.Add(new Case("起点等于终点", new Pose2(3, 3, 0.5), new Pose2(3, 3, 0.5), false));
        cases.Add(new Case("目标贴脸", new Pose2(0, 0, 0), new Pose2(0.05, 0.02, 0), false));
        cases.Add(new Case("超远目标", new Pose2(0, 0, 0), new Pose2(0, -400, 0), false));
        cases.Add(new Case("航向超范围", new Pose2(0, 0, 0), new Pose2(0, -15, 10 * Deg - 4 * Math.PI), true));
        cases.Add(new Case("负坐标远端", new Pose2(-1200, 3400, 0.3), new Pose2(-1195, 3390, 0.3), true));
        cases.Add(new Case("垂直入库深位", new Pose2(0, 0, 0), new Pose2(-5.5, 10.5, 90 * Deg), true));

        return cases;
    }

    private static AutoParkingSettings Clone(AutoParkingSettings source)
    {
        return new AutoParkingSettings
        {
            SettingsVersion = source.SettingsVersion,
            MaxTakeoverDistanceM = Math.Max(source.MaxTakeoverDistanceM, 40.0),
            EntryDistanceM = source.EntryDistanceM,
            WheelbaseM = source.WheelbaseM,
            MaxSteerDeg = source.MaxSteerDeg,
            VehicleLengthM = source.VehicleLengthM,
            VehicleWidthM = source.VehicleWidthM,
            GearSwitchPenaltyM = source.GearSwitchPenaltyM,
            PathSampleM = source.PathSampleM,
            ObstacleMarginM = source.ObstacleMarginM
        };
    }
}
