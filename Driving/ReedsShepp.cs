using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  Reeds-Shepp style paths for a car-like vehicle with a bounded turning radius.
///
///  Instead of transcribing the closed-form tables (easy to get subtly wrong and hard to
///  read), each family is built geometrically from the turning circles and then verified by
///  integrating it forward and comparing against the requested goal pose. A construction that
///  does not land on the goal is discarded, so a math mistake shows up as "no solution"
///  rather than as a path that drives the truck into a kerb.
///
///  Only forward-traversed paths are produced. Reverse-into-the-spot routes come from
///  <see cref="Invert"/>, which retraces a forward path backwards: the body heading along the
///  curve is unchanged, the vehicle simply travels opposite to it.
/// </summary>
public static class ReedsShepp
{
    private const double Epsilon = 1e-6;

    private enum SegmentKind
    {
        Line,
        Arc
    }

    private readonly record struct Primitive(SegmentKind Kind, int Turn, Vector2 Start, Vector2 End,
                                             double HeadingStart, double HeadingEnd, double Length, double Radius);

    public static List<PathPoint> Sample(Pose2 from, Pose2 to, double radius, double stepM, DriveDirection travel)
    {
        List<PathPoint> points = new();
        foreach (List<Primitive> path in Paths(from, to, radius))
        {
            points.AddRange(SamplePath(path, from, radius, stepM, travel));
        }

        return points;
    }

    /// <summary>
    ///  All geometrically valid forward paths from `from` to `to`, cheapest first.
    /// </summary>
    public static List<ParkingPath> Solve(Pose2 from, Pose2 to, double radius, double sampleM, double gearSwitchPenaltyM)
    {
        List<ParkingPath> results = new();

        foreach (List<Primitive> path in Paths(from, to, radius))
        {
            ParkingPath? sampled = Build(path, from, to, radius, sampleM, DriveDirection.Forward, gearSwitchPenaltyM,
                                         path.Count == 1 ? "直线" : $"RS {Name(path)}");
            if (sampled != null)
                results.Add(sampled);
        }

        results.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        return results;
    }

    /// <summary>
    ///  Retrace a solved route backwards, so the vehicle backs into `to` from `from`.
    /// </summary>
    public static ParkingPath? Invert(ParkingPath forward, double gearSwitchPenaltyM)
    {
        IReadOnlyList<PathPoint> points = forward.Points;
        if (points.Count < 2)
            return null;

        List<PathPoint> reversed = new(points.Count);
        double along = 0.0;
        Vector2 previous = points[^1].Position;

        for (int i = points.Count - 1; i >= 0; i--)
        {
            PathPoint p = points[i];
            along += Geometry.Distance(previous, p.Position);
            reversed.Add(p with { Travel = DriveDirection.Reverse, DistanceAlong = along });
            previous = p.Position;
        }

        return new ParkingPath
        {
            Points = reversed,
            Source = PlanSource.ReedsSheppReverse,
            Cost = forward.Length + gearSwitchPenaltyM,
            GearSwitches = 1,
            Description = "倒车入库（RS 反向遍历）"
        };
    }

    private static List<List<Primitive>> Paths(Pose2 from, Pose2 to, double radius)
    {
        List<List<Primitive>> found = new();
        if (radius <= 0.05 || !IsSane(from) || !IsSane(to))
            return found;

        AddStraight(found, from, to);

        for (int s1 = -1; s1 <= 1; s1 += 2)
        {
            for (int s2 = -1; s2 <= 1; s2 += 2)
            {
                AddCsc(found, from, to, radius, s1, s2);
            }
        }

        AddCcc(found, from, to, radius, 1);
        AddCcc(found, from, to, radius, -1);

        return found;
    }

    private static bool IsSane(Pose2 pose)
    {
        return !double.IsNaN(pose.X) && !double.IsNaN(pose.Z) && !double.IsNaN(pose.HeadingRad)
            && Math.Abs(pose.X) < 1e7 && Math.Abs(pose.Z) < 1e7;
    }

    private static void AddStraight(List<List<Primitive>> found, Pose2 from, Pose2 to)
    {
        Vector2 delta = to.Position - from.Position;
        double distance = delta.Length();
        if (distance < Epsilon)
            return;

        double headingOfDelta = Geometry.HeadingFromForward(delta);
        if (Math.Abs(Geometry.SmallestAngleDifference(headingOfDelta, from.HeadingRad)) > 1e-3)
            return;

        if (Math.Abs(Geometry.SmallestAngleDifference(from.HeadingRad, to.HeadingRad)) > 1e-3)
            return;

        found.Add(new List<Primitive>
        {
            new Primitive(SegmentKind.Line, 0, from.Position, to.Position, from.HeadingRad, to.HeadingRad, distance, 0.0)
        });
    }

    /// <summary>
    ///  arc(straight)arc with a shared tangent heading `phi` at both ends. The tangent point on
    ///  a turning circle of orientation s is C - s*R*l(phi), and requiring the middle segment to
    ///  be straight gives (C2-C1).l(phi) = R*(s2-s1).
    /// </summary>
    private static void AddCsc(List<List<Primitive>> found, Pose2 from, Pose2 to, double radius, int s1, int s2)
    {
        Vector2 c1 = Center(from, s1, radius);
        Vector2 c2 = Center(to, s2, radius);
        Vector2 d = c2 - c1;
        double dist = d.Length();
        if (dist < Epsilon)
            return;

        double k = radius * (s2 - s1);
        if (Math.Abs(k) > dist + 1e-9)
            return;

        Vector2 dhat = d / (float)dist;
        Vector2 perp = new(-dhat.Y, dhat.X);
        double cosB = k / dist;
        double sinB = Math.Sqrt(Math.Max(0.0, 1.0 - cosB * cosB));

        for (int side = -1; side <= 1; side += 2)
        {
            Vector2 m = dhat * (float)cosB + perp * (float)(side * sinB);
            if (m.LengthSquared() < 0.5)
                continue;

            m = Vector2.Normalize(m);
            double phi = Geometry.HeadingFromLeftNormal(m);

            Vector2 t1 = c1 - new Vector2((float)(s1 * radius), (float)(s1 * radius)) * m;
            Vector2 t2 = c2 - new Vector2((float)(s2 * radius), (float)(s2 * radius)) * m;

            Vector2 straight = t2 - t1;
            Vector2 forward = Geometry.ForwardFromHeading(phi);
            if (Vector2.Dot(straight, forward) <= 0f)
                continue;

            double lineLength = straight.Length();
            double sweep1 = Sweep(from.HeadingRad, phi, s1);
            double sweep2 = Sweep(phi, to.HeadingRad, s2);

            var path = new List<Primitive>(3)
            {
                new Primitive(SegmentKind.Arc, s1, from.Position, t1, from.HeadingRad, phi, radius * sweep1, radius)
            };

            if (lineLength > 1e-3)
                path.Add(new Primitive(SegmentKind.Line, 0, t1, t2, phi, phi, lineLength, 0.0));

            path.Add(new Primitive(SegmentKind.Arc, s2, t2, to.Position, phi, to.HeadingRad, radius * sweep2, radius));
            found.Add(path);
        }
    }

    /// <summary>
    ///  arc-arc-arc: the middle circle turns the other way, so its center sits 2R from both
    ///  neighbours - i.e. it is an intersection of two radius-2R circles around them.
    /// </summary>
    private static void AddCcc(List<List<Primitive>> found, Pose2 from, Pose2 to, double radius, int s1)
    {
        int s2 = -s1;
        Vector2 c1 = Center(from, s1, radius);
        Vector2 c3 = Center(to, s1, radius);
        Vector2 d = c3 - c1;
        double dist = d.Length();

        if (dist < Epsilon || dist > 4.0 * radius + 1e-9)
            return;

        Vector2 mid = (c1 + c3) * 0.5f;
        double halfDistance = dist * 0.5;
        double offset = Math.Sqrt(Math.Max(0.0, (2.0 * radius) * (2.0 * radius) - halfDistance * halfDistance));
        Vector2 dhat = d / (float)dist;
        Vector2 perp = new(-dhat.Y, dhat.X);

        for (int side = -1; side <= 1; side += 2)
        {
            Vector2 c2 = mid + perp * (float)(side * offset);

            Vector2 t1 = (c1 + c2) * 0.5f;
            Vector2 t2 = (c2 + c3) * 0.5f;

            Vector2 m1 = (c1 - t1) / (float)(s1 * radius);
            Vector2 m2 = (c2 - t2) / (float)(s2 * radius);
            if (m1.LengthSquared() < 0.5 || m2.LengthSquared() < 0.5)
                continue;

            double phi1 = Geometry.HeadingFromLeftNormal(Vector2.Normalize(m1));
            double phi2 = Geometry.HeadingFromLeftNormal(Vector2.Normalize(m2));

            double sweep1 = Sweep(from.HeadingRad, phi1, s1);
            double sweep2 = Sweep(phi1, phi2, s2);
            double sweep3 = Sweep(phi2, to.HeadingRad, s1);

            found.Add(new List<Primitive>
            {
                new Primitive(SegmentKind.Arc, s1, from.Position, t1, from.HeadingRad, phi1, radius * sweep1, radius),
                new Primitive(SegmentKind.Arc, s2, t1, t2, phi1, phi2, radius * sweep2, radius),
                new Primitive(SegmentKind.Arc, s1, t2, to.Position, phi2, to.HeadingRad, radius * sweep3, radius)
            });
        }
    }

    private static Vector2 Center(Pose2 pose, int turn, double radius)
    {
        return pose.Position + (float)(turn * radius) * Geometry.LeftFromHeading(pose.HeadingRad);
    }

    private static double Sweep(double fromHeading, double toHeading, int turn)
    {
        double delta = turn * (toHeading - fromHeading);
        delta %= Geometry.TwoPi;
        if (delta < 0.0)
            delta += Geometry.TwoPi;
        return delta;
    }

    private static ParkingPath? Build(List<Primitive> path, Pose2 from, Pose2 to, double radius,
                                      double sampleM, DriveDirection travel, double gearSwitchPenaltyM, string description)
    {
        if (!EndsAt(path, from, to, radius))
            return null;

        List<PathPoint> points = SamplePath(path, from, radius, sampleM, travel);
        if (points.Count < 2)
            return null;

        int switches = 0;
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].Travel != points[i - 1].Travel)
                switches++;
        }

        double length = points[^1].DistanceAlong;
        if (length <= 1e-3 || double.IsNaN(length) || double.IsInfinity(length))
            return null;

        return new ParkingPath
        {
            Points = points,
            Source = travel == DriveDirection.Forward ? PlanSource.ReedsSheppForward : PlanSource.ReedsSheppReverse,
            Cost = length + gearSwitchPenaltyM * switches,
            GearSwitches = switches,
            Description = description
        };
    }

    private static List<PathPoint> SamplePath(List<Primitive> path, Pose2 from, double radius, double sampleM, DriveDirection travel)
    {
        List<PathPoint> points = new();
        double along = 0.0;
        double step = Math.Max(0.05, sampleM);

        foreach (Primitive p in path)
        {
            if (p.Length <= 1e-6)
                continue;

            int steps = Math.Max(1, (int)Math.Ceiling(p.Length / step));

            for (int i = 1; i <= steps; i++)
            {
                double t = i / (double)steps;
                Vector2 position;
                double heading;

                if (p.Kind == SegmentKind.Line)
                {
                    position = p.Start + (p.End - p.Start) * (float)t;
                    heading = p.HeadingStart;
                }
                else
                {
                    Vector2 center = Center(new Pose2(p.Start.X, p.Start.Y, p.HeadingStart), p.Turn, p.Radius);
                    double swept = p.Turn * t * (p.Length / p.Radius);
                    Vector2 radiusVector = Geometry.LeftFromHeading(p.HeadingStart + swept) * (float)(-p.Turn * p.Radius);
                    position = center + radiusVector;
                    heading = Geometry.NormalizeRadians(p.HeadingStart + swept);
                }

                along += Geometry.Distance(points.Count == 0 ? from.Position : points[^1].Position, position);
                points.Add(new PathPoint(position, heading, p.Kind == SegmentKind.Arc ? (1.0 / p.Radius) * p.Turn : 0.0, travel, along));
            }
        }

        if (points.Count > 0)
        {
            points.Insert(0, new PathPoint(from.Position, from.HeadingRad,
                                           path.Count > 0 && path[0].Kind == SegmentKind.Arc ? path[0].Turn / radius : 0.0,
                                           travel, 0.0));
        }

        return points;
    }

    /// <summary>
    ///  Independent check: walk the primitives and confirm the route really finishes on the goal
    ///  pose with a continuous tangent everywhere. This is what keeps a wrong construction from
    ///  becoming a wrong maneuver.
    /// </summary>
    private static bool EndsAt(List<Primitive> path, Pose2 from, Pose2 to, double radius)
    {
        Vector2 position = from.Position;
        double heading = from.HeadingRad;

        foreach (Primitive p in path)
        {
            if (Geometry.Distance(position, p.Start) > 0.05)
                return false;

            if (Math.Abs(Geometry.SmallestAngleDifference(heading, p.HeadingStart)) > 0.02)
                return false;

            if (p.Kind == SegmentKind.Arc)
            {
                if (Math.Abs(p.Radius - radius) > 1e-6)
                    return false;

                double swept = p.Turn * (p.Length / p.Radius);
                Vector2 center = Center(new Pose2(position.X, position.Y, heading), p.Turn, p.Radius);
                Vector2 startRadius = Geometry.LeftFromHeading(heading) * (float)(-p.Turn * p.Radius);
                Vector2 endRadius = Geometry.LeftFromHeading(heading + swept) * (float)(-p.Turn * p.Radius);

                position = center + endRadius;
                heading = Geometry.NormalizeRadians(heading + swept);
            }
            else
            {
                position = position + Geometry.ForwardFromHeading(heading) * (float)p.Length;
            }
        }

        return Geometry.Distance(position, to.Position) < 0.05
            && Math.Abs(Geometry.SmallestAngleDifference(heading, to.HeadingRad)) < 0.02;
    }

    private static string Name(List<Primitive> path)
    {
        char[] letters = new char[path.Count];
        for (int i = 0; i < path.Count; i++)
        {
            letters[i] = path[i].Kind switch
            {
                SegmentKind.Line => 'S',
                SegmentKind.Arc when path[i].Turn > 0 => 'L',
                _ => 'R'
            };
        }

        return new string(letters);
    }
}
