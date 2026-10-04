using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  A ground-plane pose in game world coordinates: X/Z are meters on the ground plane,
///  Y is elevation and is not part of the pose.
/// </summary>
public readonly record struct Pose2(double X, double Z, double HeadingRad)
{
    public Vector2 Position => new((float)X, (float)Z);

    public Vector2 Forward => Geometry.ForwardFromHeading(HeadingRad);

    public double YawDegrees => HeadingRad * 180.0 / Math.PI;

    public Vector3 ToVector3(double y = 0.0) => new((float)X, (float)y, (float)Z);

    public Pose2 MovedBy(Vector2 offset) => this with { X = X + offset.X, Z = Z + offset.Y };

    public Pose2 WithHeading(double headingRad) => this with { HeadingRad = headingRad };
}

public static class Geometry
{
    public const double Pi = Math.PI;
    public const double TwoPi = Math.PI * 2.0;

    public static double NormalizeRadians(double angle)
    {
        double a = angle % TwoPi;
        if (a > Pi) a -= TwoPi;
        if (a < -Pi) a += TwoPi;
        return a;
    }

    /// <summary>
    ///  Telemetry rotation components are fractions of a full turn, not radians.
    /// </summary>
    public static double HeadingFromRotationComponent(double rotationComponent)
    {
        return NormalizeRadians(rotationComponent * TwoPi);
    }

    public static Vector2 ForwardFromHeading(double headingRad)
    {
        // Matches the convention used by ETS2LA V2: yaw 0 points down -Z.
        return new Vector2((float)(-Math.Sin(headingRad)), (float)(-Math.Cos(headingRad)));
    }

    public static double HeadingFromForward(Vector2 forward)
    {
        double length = Math.Sqrt(forward.X * forward.X + forward.Y * forward.Y);
        if (length < 1e-6)
            return 0.0;

        return NormalizeRadians(Math.Atan2(-forward.X / length, -forward.Y / length));
    }

    /// <summary>
    ///  The orientation a pick-drag is asking for. The pivot sits under the cursor at press time, so
    ///  the first frames of every drag have a one-pixel delta - and the angle of a one-pixel vector is
    ///  noise. Below `minPixels` the gesture has not expressed an orientation yet and the caller must
    ///  keep the previous heading rather than take this one.
    /// </summary>
    public static bool TryHeadingFromDrag(Vector2 pivot, Vector2 cursor, double minPixels, out double headingRad)
    {
        headingRad = 0.0;
        if (Distance(pivot, cursor) < minPixels)
            return false;

        headingRad = HeadingFromForward(cursor - pivot);
        return true;
    }

    /// <summary>
    ///  Unit left normal of a heading. With forward = (-sin h, -cos h) the left direction is
    ///  (-cos h, sin h), which is also Cross(UnitY, forward) in the game's XZ plane.
    /// </summary>
    public static Vector2 LeftFromHeading(double headingRad)
    {
        return new Vector2((float)-Math.Cos(headingRad), (float)Math.Sin(headingRad));
    }

    /// <summary>
    ///  Inverse of <see cref="LeftFromHeading"/>: the heading whose left normal is `left`.
    /// </summary>
    public static double HeadingFromLeftNormal(Vector2 left)
    {
        return NormalizeRadians(Math.Atan2(left.Y, -left.X));
    }

    public static double Distance(Vector2 a, Vector2 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static double SmallestAngleDifference(double a, double b)
    {
        return NormalizeRadians(a - b);
    }

    /// <summary>
    ///  Signed lateral offset of `point` from the ray starting at `origin` along `forward`.
    ///  Positive is to the vehicle's left, which is `Cross(UnitY, forward)` in the XZ plane.
    /// </summary>
    public static double SignedLateral(Vector2 origin, Vector2 forward, Vector2 point)
    {
        Vector2 delta = point - origin;
        return -(double)(forward.X * delta.Y - forward.Y * delta.X);
    }

    public static Vector2 ClosestPointOnSegment(Vector2 a, Vector2 b, Vector2 p)
    {
        Vector2 ab = b - a;
        double squaredLength = ab.X * ab.X + ab.Y * ab.Y;
        if (squaredLength < 1e-9)
            return a;

        double t = ((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / squaredLength;
        if (t < 0.0) t = 0.0;
        if (t > 1.0) t = 1.0;

        return a + ab * (float)t;
    }

    /// <summary>
    ///  The four ground corners of a rectangle centered on `pose`, measured along its
    ///  forward axis (length) and its left axis (width).
    /// </summary>
    public static Vector2[] RectangleCorners(Pose2 pose, double length, double width)
    {
        Vector2 forward = pose.Forward;
        Vector2 left = new(forward.Y, -forward.X);
        Vector2 halfLength = forward * (float)(length * 0.5);
        Vector2 halfWidth = left * (float)(width * 0.5);
        Vector2 center = pose.Position;

        return new[]
        {
            center - halfLength - halfWidth,
            center + halfLength - halfWidth,
            center + halfLength + halfWidth,
            center - halfLength + halfWidth
        };
    }

    public static Vector2 ToPlane(Vector3 value) => new(value.X, value.Z);

    public static Vector3 ToWorld(Vector2 planeValue, float y = 0f) => new(planeValue.X, y, planeValue.Y);

    /// <summary>
    ///  Separating-axis test for two convex ground polygons. Both come from vehicle boxes,
    ///  so they are convex by construction and no clipping is attempted.
    /// </summary>
    public static bool PolygonsOverlap(Vector2[] a, Vector2[] b)
    {
        return !SeparatedOnAxes(a, b) && !SeparatedOnAxes(b, a);
    }

    private static bool SeparatedOnAxes(Vector2[] poly, Vector2[] other)
    {
        for (int i = 0; i < poly.Length; i++)
        {
            Vector2 edge = poly[(i + 1) % poly.Length] - poly[i];
            if (edge.X == 0f && edge.Y == 0f)
                continue;

            Vector2 axis = new(-edge.Y, edge.X);
            if (!OverlapsOnAxis(poly, other, axis))
                return true;
        }

        return false;
    }

    private static bool OverlapsOnAxis(Vector2[] a, Vector2[] b, Vector2 axis)
    {
        double minA = double.PositiveInfinity, maxA = double.NegativeInfinity;
        double minB = double.PositiveInfinity, maxB = double.NegativeInfinity;

        foreach (Vector2 p in a)
        {
            double projection = p.X * axis.X + p.Y * axis.Y;
            if (projection < minA) minA = projection;
            if (projection > maxA) maxA = projection;
        }

        foreach (Vector2 p in b)
        {
            double projection = p.X * axis.X + p.Y * axis.Y;
            if (projection < minB) minB = projection;
            if (projection > maxB) maxB = projection;
        }

        return minA <= maxB && minB <= maxA;
    }

    /// <summary>
    ///  Smallest convex ring around a set of ground points. A prefab only publishes its control nodes,
    ///  so this is deliberately an over-approximation of its footprint: an L-shaped lot comes out as
    ///  the wedge that closes it. Fewer than three distinct points enclose nothing and are returned
    ///  unchanged, which is the caller's cue to draw the points instead of a polygon.
    /// </summary>
    public static Vector2[] ConvexHull(Vector2[] points)
    {
        List<Vector2> unique = new();
        foreach (Vector2 point in points)
        {
            bool seen = false;
            foreach (Vector2 other in unique)
            {
                if (other == point)
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
                unique.Add(point);
        }

        if (unique.Count < 3)
            return points;

        unique.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        Vector2[] hull = new Vector2[unique.Count * 2];
        int size = 0;

        for (int i = 0; i < unique.Count; i++)
        {
            while (size >= 2 && Cross(hull[size - 2], hull[size - 1], unique[i]) <= 0.0)
                size--;
            hull[size++] = unique[i];
        }

        int upper = size + 1;
        for (int i = unique.Count - 2; i >= 0; i--)
        {
            while (size >= upper && Cross(hull[size - 2], hull[size - 1], unique[i]) <= 0.0)
                size--;
            hull[size++] = unique[i];
        }

        return size - 1 > 0 ? hull[..(size - 1)] : Array.Empty<Vector2>();
    }

    /// <summary>Area of a ground polygon, positive regardless of vertex order.</summary>
    public static double PolygonArea(Vector2[] points)
    {
        double twice = 0.0;
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 next = points[(i + 1) % points.Length];
            twice += (double)points[i].X * next.Y - (double)next.X * points[i].Y;
        }

        return Math.Abs(twice) * 0.5;
    }

    private static double Cross(Vector2 origin, Vector2 a, Vector2 b)
    {
        return (double)(a.X - origin.X) * (b.Y - origin.Y) - (double)(a.Y - origin.Y) * (b.X - origin.X);
    }
}
