using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  How much ground a route demands, and how much of it nobody vouched for.
///
///  The quantity is the same envelope the collision test already uses (vehicle plus its own margin, see
///  Planner.CountCorridorConflicts), rasterized to 1 m cells, so "占地 430 m²" reads as the corridor we are
///  asking the map for rather than the paint the vehicle would spill. Rasterizing rather than integrating
///  analytically is what makes revisited ground count once: a two-leg maneuver backs over its own approach,
///  and a summed strip length would charge for it twice and hide exactly the exposure we are measuring.
/// </summary>
public static class RouteFootprint
{
    /// <summary>Cell budget per route. A 60 m route at this resolution is a few thousand cells; a route
    ///  that needs more than this is not a route, and we would rather flag it than stall the tick thread.</summary>
    public const int MaxCellsPerRoute = 40_000;

    /// <summary>The envelope the vehicle demands from the ground: body plus the margin the collision test
    ///  insists on, so the area and the conflicts describe one vehicle, not two.</summary>
    public static (double LengthM, double WidthM) EnvelopeSize(AutoParkingSettings settings)
    {
        return (settings.VehicleLengthM + 0.6 + 2.0 * settings.ObstacleMarginM,
                settings.VehicleWidthM + 0.5 + 2.0 * settings.ObstacleMarginM);
    }

    public static void Measure(ParkingPath route, AutoParkingSettings settings, GroundTrust trust)
    {
        if (route == null || settings == null || trust == null || route.Points.Count < 2)
            return;

        (double length, double width) = EnvelopeSize(settings);
        HashSet<long> covered = new();

        // PathSampleM is 0.25 m, which would stamp the same rectangle four times per cell. Advancing about
        // three quarters of a cell between stamps keeps consecutive envelopes overlapping, so no cell the
        // envelope really covers can slip between two samples.
        double stride = Math.Max(0.5, GroundTrust.CellM * 0.75);
        double lastStampedAt = -stride;

        foreach (PathPoint point in route.Points)
        {
            if (point.DistanceAlong - lastStampedAt < stride)
                continue;

            lastStampedAt = point.DistanceAlong;
            Pose2 pose = new(point.Position.X, point.Position.Y, point.HeadingRad);
            Stamp(Geometry.RectangleCorners(pose, length, width), trust, covered);

            if (covered.Count >= MaxCellsPerRoute)
            {
                route.FootprintTruncated = true;
                break;
            }
        }

        double cellArea = GroundTrust.CellM * GroundTrust.CellM;
        int unconfirmed = 0;
        foreach (long key in covered)
        {
            if (!trust.IsConfirmedCell(key))
                unconfirmed++;
        }

        route.SweptAreaM2 = covered.Count * cellArea;
        route.UnconfirmedAreaM2 = unconfirmed * cellArea;
    }

    private static void Stamp(Vector2[] rectangle, GroundTrust trust, HashSet<long> target)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (Vector2 corner in rectangle)
        {
            minX = Math.Min(minX, corner.X); maxX = Math.Max(maxX, corner.X);
            minY = Math.Min(minY, corner.Y); maxY = Math.Max(maxY, corner.Y);
        }

        for (double x = Math.Floor(minX); x <= maxX; x += GroundTrust.CellM)
        {
            for (double z = Math.Floor(minY); z <= maxY; z += GroundTrust.CellM)
            {
                if (target.Count >= MaxCellsPerRoute)
                    return;

                Vector2 cell = new((float)(x + 0.5 * GroundTrust.CellM), (float)(z + 0.5 * GroundTrust.CellM));
                if (Geometry.PointInPolygon(rectangle, cell))
                    target.Add(trust.KeyAt(cell));
            }
        }
    }
}
