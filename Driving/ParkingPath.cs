using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

public enum DriveDirection
{
    Forward,
    Reverse
}

public enum PlanSource
{
    StraightReverse,
    ReedsSheppForward,
    ReedsSheppReverse,
    TwoLegStaging
}

public readonly record struct PathPoint(Vector2 Position, double HeadingRad, double Curvature,
                                        DriveDirection Travel, double DistanceAlong);

/// <summary>
///  A sampled, drivable route into the spot. Immutable once built; the follower reads it
///  by arc length and never writes to it.
/// </summary>
public sealed class ParkingPath
{
    public required IReadOnlyList<PathPoint> Points { get; init; }

    public required PlanSource Source { get; init; }

    public required double Cost { get; init; }

    /// <summary>Short human readable description, shown on the map and settings page.</summary>
    public required string Description { get; init; }

    public double Length => Points.Count == 0 ? 0.0 : Points[^1].DistanceAlong;

    public int GearSwitches { get; init; }

    public DriveDirection FirstTravel => Points.Count == 0 ? DriveDirection.Forward : Points[0].Travel;

    /// <summary>
    ///  The point at `distance` metres along the route plus its tangent, used by the follower.
    /// </summary>
    public bool TryPointAt(double distance, out PathPoint point)
    {
        point = default;
        IReadOnlyList<PathPoint> pts = Points;
        if (pts.Count == 0)
            return false;

        if (distance <= 0.0)
        {
            point = pts[0];
            return true;
        }

        if (distance >= pts[^1].DistanceAlong)
        {
            point = pts[^1];
            return true;
        }

        int low = 0;
        int high = pts.Count - 1;
        while (low + 1 < high)
        {
            int mid = (low + high) / 2;
            if (pts[mid].DistanceAlong <= distance)
                low = mid;
            else
                high = mid;
        }

        PathPoint a = pts[low];
        PathPoint b = pts[high];
        double span = b.DistanceAlong - a.DistanceAlong;
        double t = span <= 1e-9 ? 0.0 : (distance - a.DistanceAlong) / span;

        point = new PathPoint(a.Position + (b.Position - a.Position) * (float)t,
                              Geometry.NormalizeRadians(a.HeadingRad + t * Geometry.SmallestAngleDifference(b.HeadingRad, a.HeadingRad)),
                              a.Curvature,
                              a.Travel,
                              distance);
        return true;
    }
}

/// <summary>
///  Outcome of a planning request: either a route, or why there isn't one.
/// </summary>
public sealed class PlanResult
{
    public ParkingPath? Path { get; init; }
    public string Reason { get; init; } = "";
    public int ConflictCount { get; init; }
    public double MinRadiusM { get; init; }
    public int CandidatesEvaluated { get; init; }

    /// <summary>
    ///  Usable to drive: a route exists and nothing on it overlaps an obstacle. Callers that
    ///  only want to preview a blocked route read <see cref="Path"/> directly.
    /// </summary>
    public bool Ok => Path != null && ConflictCount == 0;

    public string Summary => Path != null
        ? $"{Path.Description} · {Path.Length:0.0} m · 换挡 {Path.GearSwitches} · 冲突 {ConflictCount}"
        : $"无解：{Reason}";
}
