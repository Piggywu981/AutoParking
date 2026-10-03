using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

public static class Kinematics
{
    /// <summary>
    ///  Minimum turning circle of the tractor: wheelbase / tan(max road wheel angle).
    /// </summary>
    public static double MinTurnRadius(AutoParkingSettings settings)
    {
        double maxSteer = Math.Clamp(settings.MaxSteerDeg * Math.PI / 180.0, 0.05, 1.2);
        return Math.Clamp(settings.WheelbaseM / Math.Tan(maxSteer), 2.0, 40.0);
    }

    /// <summary>
    ///  The radius routes are planned on: the vehicle's own limit scaled up by
    ///  <see cref="AutoParkingSettings.PlanRadiusMargin"/> so the commanded wheel angle stays
    ///  away from saturation and the follower keeps room to correct.
    /// </summary>
    public static double PlanningRadius(AutoParkingSettings settings)
    {
        return MinTurnRadius(settings) * Math.Clamp(settings.PlanRadiusMargin, 1.0, 2.5);
    }
}

/// <summary>
///  Turns a chosen spot pose into a drivable route, trying the cheapest maneuvers first:
///  straight reverse, Reeds-Shepp back-in, Reeds-Shepp drive-in, then the staged two-leg
///  maneuver. Every candidate is checked against the obstacle field before it can win.
/// </summary>
public static class Planner
{
    private const double StraightLateralTolerance = 0.35;
    private const double StraightHeadingToleranceRad = 8.0 * Math.PI / 180.0;
    private const double CorridorSampleM = 0.5;
    private const double StartGraceM = 2.0;
    private const double EndGraceM = 1.0;

    public static PlanResult Plan(Pose2 start, Pose2 goal, AutoParkingSettings settings, ObstacleSnapshot obstacles)
    {
        double radius = Kinematics.PlanningRadius(settings);

        double alreadyThere = Geometry.Distance(start.Position, goal.Position);
        double alreadyAligned = Math.Abs(Geometry.SmallestAngleDifference(start.HeadingRad, goal.HeadingRad));
        if (alreadyThere <= settings.ToleranceLateralM && alreadyAligned <= settings.ToleranceHeadingDeg * Math.PI / 180.0)
        {
            return new PlanResult
            {
                Reason = "车已经在车位里了",
                MinRadiusM = radius
            };
        }

        double penalty = settings.GearSwitchPenaltyM;
        double maxLength = Math.Max(settings.MaxTakeoverDistanceM, 10.0) * 2.0 + settings.EntryDistanceM;

        List<ParkingPath> candidates = Collect(start, goal, settings, radius, penalty, maxLength);

        int evaluated = candidates.Count;
        ParkingPath? best = null;
        int bestConflicts = int.MaxValue;

        foreach (ParkingPath candidate in candidates)
        {
            int conflicts = CountCorridorConflicts(candidate, settings, obstacles);

            if (conflicts == 0 && (best == null || candidate.Cost < best.Cost))
            {
                best = candidate;
                bestConflicts = 0;
            }
            else if (best == null && conflicts < bestConflicts)
            {
                best = candidate;
                bestConflicts = conflicts;
            }
        }

        if (best == null)
        {
            return new PlanResult
            {
                Reason = evaluated == 0
                    ? $"以最小转弯半径 {radius:0.0} m 找不到可行路径，试试把车位挪近一点或减小进库角"
                    : "所有候选路径都超出了长度限制",
                ConflictCount = 0,
                MinRadiusM = radius,
                CandidatesEvaluated = evaluated
            };
        }

        if (bestConflicts > 0)
        {
            return new PlanResult
            {
                Path = best,
                Reason = $"路径上有 {bestConflicts} 处障碍冲突",
                ConflictCount = bestConflicts,
                MinRadiusM = radius,
                CandidatesEvaluated = evaluated
            };
        }

        return new PlanResult
        {
            Path = best,
            ConflictCount = 0,
            MinRadiusM = radius,
            CandidatesEvaluated = evaluated
        };
    }

    private static List<ParkingPath> Collect(Pose2 start, Pose2 goal, AutoParkingSettings settings,
                                             double radius, double penalty, double maxLength)
    {
        List<ParkingPath> candidates = new();
        double tail = Math.Clamp(settings.TerminalStraightM, 0.0, 5.0);

        ParkingPath? straight = TryStraightReverse(start, goal, settings);
        if (straight != null)
            candidates.Add(straight);

        // Reeds-Shepp lands its final arc exactly on the goal pose, so the last heading is only
        // right at the last centimetre: a route stopped 0.35 m short walks out of the bay turned
        // by however much arc was left over. Planning to a standoff and driving the final metres
        // straight in makes every stopping point in that window correctly aligned.
        foreach (ParkingPath forward in ReedsShepp.Solve(start, Standoff(goal, tail, DriveDirection.Forward),
                                                         radius, settings.PathSampleM, penalty))
        {
            ParkingPath? withTail = AppendStraightTail(forward, goal, settings.PathSampleM);
            if (withTail != null && withTail.Length <= maxLength)
                candidates.Add(withTail);
        }

        List<ParkingPath> reversed = ReedsShepp.Solve(Standoff(goal, tail, DriveDirection.Reverse), start,
                                                      radius, settings.PathSampleM, penalty);
        if (reversed.Count > 0)
        {
            ParkingPath? backIn = ReedsShepp.Invert(reversed[0], penalty);
            backIn = AppendStraightTail(backIn, goal, settings.PathSampleM);
            if (backIn != null && backIn.Length <= maxLength)
                candidates.Add(backIn);
        }

        ParkingPath? staged = TryTwoLegStaging(start, goal, settings, radius, penalty, maxLength);
        if (staged != null)
            candidates.Add(staged);

        candidates.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        return candidates;
    }

    /// <summary>
    ///  Where the vehicle has to be to reach the spot in a straight line: <paramref name="tail"/>
    ///  metres along its own nose for a drive-in, or astern of it for a back-in.
    /// </summary>
    private static Pose2 Standoff(Pose2 goal, double tail, DriveDirection travel)
    {
        double sign = travel == DriveDirection.Forward ? -1.0 : 1.0;
        return new Pose2(goal.X + goal.Forward.X * (float)(tail * sign),
                         goal.Z + goal.Forward.Y * (float)(tail * sign),
                         goal.HeadingRad);
    }

    private static ParkingPath? AppendStraightTail(ParkingPath? route, Pose2 goal, double sampleM)
    {
        if (route == null || route.Points.Count == 0)
            return route;

        PathPoint last = route.Points[^1];
        ParkingPath? leg = StraightBetween(new Pose2(last.Position.X, last.Position.Y, goal.HeadingRad),
                                          goal, last.Travel, sampleM);
        if (leg == null)
            return route;

        List<PathPoint> points = new(route.Points.Count + leg.Points.Count);
        points.AddRange(route.Points);

        double offset = last.DistanceAlong;
        for (int i = 1; i < leg.Points.Count; i++)
        {
            points.Add(leg.Points[i] with { DistanceAlong = offset + leg.Points[i].DistanceAlong });
        }

        return new ParkingPath
        {
            Points = points,
            Source = route.Source,
            Cost = route.Cost + leg.Length,
            GearSwitches = route.GearSwitches,
            Description = route.Description + " + 直线入位"
        };
    }

    /// <summary>
    ///  The cheapest maneuver of all: the spot is directly behind the vehicle and already
    ///  aligned, so a single reverse straight line finishes the job.
    /// </summary>
    private static ParkingPath? TryStraightReverse(Pose2 start, Pose2 goal, AutoParkingSettings settings)
    {
        Vector2 forward = start.Forward;
        Vector2 delta = goal.Position - start.Position;
        double along = delta.X * forward.X + delta.Y * forward.Y;
        double lateral = Geometry.SignedLateral(start.Position, forward, goal.Position);
        double headingError = Math.Abs(Geometry.SmallestAngleDifference(start.HeadingRad, goal.HeadingRad));

        if (along > -0.3 || Math.Abs(lateral) > StraightLateralTolerance || headingError > StraightHeadingToleranceRad)
            return null;

        List<PathPoint> points = new();
        double distance = -along;
        int steps = Math.Max(2, (int)Math.Ceiling(distance / Math.Max(0.05, settings.PathSampleM)));

        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            Vector2 position = start.Position + forward * (float)(-distance * t);
            points.Add(new PathPoint(position, goal.HeadingRad, 0.0, DriveDirection.Reverse, distance * t));
        }

        return new ParkingPath
        {
            Points = points,
            Source = PlanSource.StraightReverse,
            Cost = distance + settings.GearSwitchPenaltyM,
            GearSwitches = 1,
            Description = "直线倒车入库"
        };
    }

    /// <summary>
    ///  Drive to a staging point in front of the spot, then reverse straight into it. Used when
    ///  a single Reeds-Shepp maneuver is unavailable or too long.
    /// </summary>
    private static ParkingPath? TryTwoLegStaging(Pose2 start, Pose2 goal, AutoParkingSettings settings,
                                                 double radius, double penalty, double maxLength)
    {
        Pose2 staging = new(goal.X + goal.Forward.X * settings.EntryDistanceM,
                            goal.Z + goal.Forward.Y * settings.EntryDistanceM,
                            goal.HeadingRad);

        List<ParkingPath> approach = ReedsShepp.Solve(start, staging, radius, settings.PathSampleM, penalty);
        if (approach.Count == 0)
            return null;

        ParkingPath legA = approach[0];
        ParkingPath? legB = StraightBetween(staging, goal, DriveDirection.Reverse, settings.PathSampleM);
        if (legB == null)
            return null;

        List<PathPoint> points = new(legA.Points.Count + legB.Points.Count);
        points.AddRange(legA.Points);

        double offset = points[^1].DistanceAlong;
        for (int i = 1; i < legB.Points.Count; i++)
        {
            points.Add(legB.Points[i] with { DistanceAlong = offset + legB.Points[i].DistanceAlong });
        }

        double length = points[^1].DistanceAlong;
        if (length > maxLength)
            return null;

        int switches = legA.GearSwitches + 1;

        return new ParkingPath
        {
            Points = points,
            Source = PlanSource.TwoLegStaging,
            Cost = length + penalty * switches,
            GearSwitches = switches,
            Description = $"前进到预热点 {settings.EntryDistanceM:0} m 后直线倒库"
        };
    }

    private static ParkingPath? StraightBetween(Pose2 from, Pose2 to, DriveDirection travel, double sampleM)
    {
        double distance = Geometry.Distance(from.Position, to.Position);
        if (distance < 0.2)
            return null;

        List<PathPoint> points = new();
        int steps = Math.Max(2, (int)Math.Ceiling(distance / Math.Max(0.05, sampleM)));

        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            Vector2 position = from.Position + (to.Position - from.Position) * (float)t;
            points.Add(new PathPoint(position, from.HeadingRad, 0.0, travel, distance * t));
        }

        return new ParkingPath
        {
            Points = points,
            Source = PlanSource.TwoLegStaging,
            Cost = distance,
            GearSwitches = 0,
            Description = "直线段"
        };
    }

    /// <summary>
    ///  Sweeps the vehicle footprint along the route and counts obstacles that overlap it.
    ///  By default the first metres and the last metre are skipped: the vehicle starts inside its
    ///  own footprint and the spot is expected to be clear by definition. A caller that is already
    ///  driving asks a different question - "is anything in front of me" - and passes its own
    ///  grace, because the route-relative grace would hide the whole near field.
    /// </summary>
    public static int CountCorridorConflicts(ParkingPath path, AutoParkingSettings settings, ObstacleSnapshot obstacles,
                                             double? startGraceM = null, double? endGraceM = null)
    {
        if (obstacles.Polygons.Count == 0 || path.Points.Count < 2)
            return 0;

        double startGrace = startGraceM ?? StartGraceM;
        double endGrace = endGraceM ?? EndGraceM;
        double length = settings.VehicleLengthM + 0.6 + 2.0 * settings.ObstacleMarginM;
        double width = settings.VehicleWidthM + 0.5 + 2.0 * settings.ObstacleMarginM;
        double total = path.Length;

        int conflicts = 0;
        for (double along = startGrace; along <= total - endGrace; along += CorridorSampleM)
        {
            if (!path.TryPointAt(along, out PathPoint point))
                break;

            Pose2 pose = new(point.Position.X, point.Position.Y, point.HeadingRad);
            Vector2[] footprint = Geometry.RectangleCorners(pose, length, width);

            foreach (Vector2[] obstacle in obstacles.Polygons)
            {
                if (Geometry.PolygonsOverlap(footprint, obstacle))
                {
                    conflicts++;
                    break;
                }
            }
        }

        return conflicts;
    }
}
