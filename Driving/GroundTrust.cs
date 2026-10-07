using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  Which ground the map itself will vouch for — as a 1 m cell grid, not a distance query, so a
///  route's demand can be answered by counting cells.
///
///  "Confirmed" means all three of: inside the radius the map walk actually sampled, within
///  <see cref="CorridorHalfWidthM"/> of a navigation curve or road lane (the game's own statement that
///  vehicles are expected to drive there), and not underneath a collidable map object. Anything else is
///  ground we have no evidence about, which after design doc §32 is precisely where the unrecognizable
///  obstacles live: prefab interior geometry is not published, so a depot floor reads as empty data and
///  a crate in the middle of it is invisible to every layer we have.
///
///  This is deliberately NOT an obstacle model. It is the input to a measurement (M7a); whether it gets
///  to veto a route is M7c's decision, made on the numbers this produces.
/// </summary>
public sealed class GroundTrust
{
    /// <summary>Grid resolution. 1 m is coarse enough to keep a 120 m sample at a few tens of thousands
    ///  of cells and fine enough that a 2.6 m wide vehicle is resolved to three columns.</summary>
    public const double CellM = 1.0;

    /// <summary>Stamping budget per set. A depot with thousands of overlapping curves must not be able
    ///  to occupy the tick thread; when the budget bites, <see cref="Truncated"/> says so and the areas
    ///  reported from this map are understated rather than wrong-headed.</summary>
    public const int MaxCells = 300_000;

    /// <summary>How far a wall-like map item takes trust from its surroundings: it is the object's own
    ///  footprint plus a band, because the geometry we can see is a line while the thing has depth.</summary>
    public const double BlockerBandM = 1.5;

    public Vector2 Center;
    public double RadiusM;
    public double CorridorHalfWidthM = 5.0;
    public bool Truncated;

    private readonly HashSet<long> corridor = new();
    private readonly HashSet<long> blocked = new();

    public int CorridorCells => corridor.Count;
    public int BlockedCells => blocked.Count;

    /// <summary>Cells the map vouches for: corridor minus blockers. Counted on demand because the two sets
    ///  are stamped independently, so their sizes alone say nothing about how much they overlap.</summary>
    public int ConfirmedCells
    {
        get
        {
            int count = 0;
            foreach (long key in corridor)
            {
                if (!blocked.Contains(key))
                    count++;
            }

            return count;
        }
    }

    public long KeyAt(Vector2 worldPoint)
    {
        return Key((int)Math.Floor(worldPoint.X / CellM), (int)Math.Floor(worldPoint.Y / CellM));
    }

    private static long Key(int ix, int iz) => ((long)ix << 32) | (uint)iz;

    public bool IsConfirmedCell(long key)
    {
        return corridor.Contains(key) && !blocked.Contains(key);
    }

    public bool IsConfirmed(Vector2 worldPoint)
    {
        return IsConfirmedCell(KeyAt(worldPoint));
    }

    /// <summary>A polyline the game expects vehicles to drive on: a prefab navigation curve or a road lane
    ///  centerline. Its buffer is the corridor half-width, so a curve down the middle of a 10 m aisle
    ///  vouches for the whole aisle.</summary>
    public void AddCorridor(Vector2[] points)
    {
        Stamp(points, CorridorHalfWidthM, corridor);
    }

    /// <summary>A collidable map item. Closed rings block their interior; segments and chains block a band,
    ///  which is the honest reading of a wall drawn as one line.</summary>
    public void AddBlocker(Vector2[] points, bool closed)
    {
        if (closed && points.Length >= 3)
        {
            StampPolygon(points);
            return;
        }

        Stamp(points, BlockerBandM, blocked);
    }

    private void StampPolygon(Vector2[] ring)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (Vector2 p in ring)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }

        for (double x = Math.Floor(minX); x <= maxX; x += CellM)
        {
            for (double z = Math.Floor(minY); z <= maxY; z += CellM)
            {
                if (blocked.Count >= MaxCells)
                {
                    Truncated = true;
                    return;
                }

                Vector2 centre = new((float)(x + 0.5 * CellM), (float)(z + 0.5 * CellM));
                if (Geometry.PointInPolygon(ring, centre))
                    blocked.Add(KeyAt(centre));
            }
        }
    }

    private void Stamp(Vector2[] points, double halfWidth, HashSet<long> target)
    {
        if (points == null || points.Length == 0 || halfWidth <= 0.0)
            return;

        if (points.Length == 1)
        {
            StampSegment(points[0], points[0], halfWidth, target);
            return;
        }

        for (int i = 0; i + 1 < points.Length; i++)
            StampSegment(points[i], points[i + 1], halfWidth, target);
    }

    private void StampSegment(Vector2 from, Vector2 to, double halfWidth, HashSet<long> target)
    {
        double minX = Math.Min(from.X, to.X) - halfWidth;
        double maxX = Math.Max(from.X, to.X) + halfWidth;
        double minZ = Math.Min(from.Y, to.Y) - halfWidth;
        double maxZ = Math.Max(from.Y, to.Y) + halfWidth;

        for (double x = Math.Floor(minX); x <= maxX; x += CellM)
        {
            for (double z = Math.Floor(minZ); z <= maxZ; z += CellM)
            {
                if (target.Count >= MaxCells)
                {
                    Truncated = true;
                    return;
                }

                Vector2 centre = new((float)(x + 0.5 * CellM), (float)(z + 0.5 * CellM));

                // The radius is a property of the sample, not of the cell stamping, and it is cheapest
                // to enforce here: cells outside it never enter either set.
                if (Geometry.Distance(Center, centre) > RadiusM)
                    continue;

                if (Geometry.Distance(centre, Geometry.ClosestPointOnSegment(from, to, centre)) <= halfWidth)
                    target.Add(KeyAt(centre));
            }
        }
    }
}
