using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  A parking bay found in the cone layout: the rectangle the two facing cone rows enclose, with
///  the heading the game asks for (the bay's own arrow points the same way - from the open end
///  toward the capped one, i.e. nose-first in through the opening).
/// </summary>
public readonly record struct ParkingBay(Vector2 Center, Vector2 HalfSize, double InteriorWidthM,
                                         double InteriorLengthM, double HeadingRad)
{
    public bool Contains(Vector2 point, double marginM = 0.0)
        => Math.Abs(point.X - Center.X) <= HalfSize.X + marginM
        && Math.Abs(point.Y - Center.Y) <= HalfSize.Y + marginM;

    public Pose2 Pose => new(Center.X, Center.Y, HeadingRad);
}

/// <summary>
///  Finds parking bays in a list of cone positions.
///
///  A bay is read out of the cones themselves rather than out of any game object: two facing rows of
///  cones, close enough together to be walls and far enough apart to drive between, capped at one end
///  and open at the other. That is the only form of this that needs no undocumented memory field -
///  the bay's own marker objects carry a flag bit that happens to differ here (0xC17 against 0x417 on
///  four samples in one scene), which is a correlation, not a contract.
///
///  Rows are taken along the world axes, which is what a cone course is built from; a bay rotated 30°
///  off-axis is not found and the caller reports nothing rather than guessing.
/// </summary>
public static class ParkingBays
{
    /// <summary>Cones this far apart sideways still belong to one row.</summary>
    public const double RowLateralToleranceM = 0.8;

    public const int MinConesPerRow = 3;
    public const double MinRowSpanM = 5.0;

    /// <summary>Row-to-row distance, measured centre to centre, that still counts as one bay.</summary>
    public const double MinRowGapM = 2.6;

    public const double MaxRowGapM = 7.0;

    /// <summary>How much of the two rows must overlap, and how long the resulting bay may be.</summary>
    public const double MinOverlapM = 5.0;

    public const double MinBayLengthM = 5.0;
    public const double MaxBayLengthM = 22.0;

    /// <summary>A row extending this far past the overlap makes that end open.</summary>
    public const double OpenEndMarginM = 1.0;

    /// <summary>Footprint width the classifier gives a cone, subtracted to get the clear interior.</summary>
    public const double ConeFootprintM = 0.85;

    private readonly record struct Row(bool AlongX, double Perp, double Start, double End, int Count);

    public static List<ParkingBay> Detect(IReadOnlyList<Vector2> cones)
    {
        List<ParkingBay> bays = new();
        if (cones.Count < 2 * MinConesPerRow)
            return bays;

        List<Row> rows = CollectRows(cones);

        for (int a = 0; a < rows.Count; a++)
        {
            for (int b = a + 1; b < rows.Count; b++)
            {
                Row left = rows[a], right = rows[b];
                if (left.AlongX != right.AlongX)
                    continue;

                double gap = Math.Abs(left.Perp - right.Perp);
                if (gap is < MinRowGapM or > MaxRowGapM)
                    continue;

                double from = Math.Max(left.Start, right.Start);
                double to = Math.Min(left.End, right.End);
                double length = to - from;
                if (length < Math.Min(MinOverlapM, MinBayLengthM) || length > MaxBayLengthM)
                    continue;

                // One row running past an end of the overlap means traffic continues there: open. Both
                // rows stopping together means the bay is walled off on that side: capped.
                bool lowOpen = Math.Min(left.Start, right.Start) < from - OpenEndMarginM;
                bool highOpen = Math.Max(left.End, right.End) > to + OpenEndMarginM;
                if (lowOpen == highOpen)
                    continue;

                double along = (from + to) * 0.5;
                double perp = (left.Perp + right.Perp) * 0.5;
                Vector2 center = left.AlongX ? new Vector2((float)along, (float)perp)
                                             : new Vector2((float)perp, (float)along);
                Vector2 half = left.AlongX ? new Vector2((float)(length * 0.5), (float)(gap * 0.5))
                                           : new Vector2((float)(gap * 0.5), (float)(length * 0.5));

                // Nose in through the opening: the direction from the open end toward the capped one.
                Vector2 nose = left.AlongX
                    ? new Vector2(lowOpen ? 1f : -1f, 0f)
                    : new Vector2(0f, lowOpen ? 1f : -1f);

                bays.Add(new ParkingBay(center, half, gap - ConeFootprintM, length,
                                        Geometry.HeadingFromForward(nose)));
            }
        }

        return bays;
    }

    /// <summary>
    ///  The bay nearest a picked point, if the pick is inside it or within reach of its centre. The
    ///  returned pose is the bay centre with the bay's own heading, which is what makes the spot
    ///  admissible to the game instead of merely close.
    /// </summary>
    public static bool TrySnap(IReadOnlyList<ParkingBay> bays, Vector2 pick, double maxDistanceM, out Pose2 snapped)
    {
        snapped = default;
        ParkingBay best = default;
        double bestDistance = double.MaxValue;
        bool found = false;

        foreach (ParkingBay bay in bays)
        {
            double distance = bay.Contains(pick) ? 0.0 : DistanceToRectangle(bay, pick);
            if (distance > maxDistanceM || distance >= bestDistance)
                continue;
            best = bay;
            bestDistance = distance;
            found = true;
        }

        if (!found)
            return false;

        snapped = best.Pose;
        return true;
    }

    private static double DistanceToRectangle(ParkingBay bay, Vector2 p)
    {
        double dx = Math.Max(0.0, Math.Abs(p.X - bay.Center.X) - bay.HalfSize.X);
        double dy = Math.Max(0.0, Math.Abs(p.Y - bay.Center.Y) - bay.HalfSize.Y);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static List<Row> CollectRows(IReadOnlyList<Vector2> cones)
    {
        List<Row> rows = new();

        for (int axis = 0; axis < 2; axis++)
        {
            bool alongX = axis == 0;
            List<(double along, double perp)> projected = new(cones.Count);
            foreach (Vector2 c in cones)
                projected.Add(alongX ? (c.X, c.Y) : (c.Y, c.X));

            projected.Sort((l, r) => l.perp.CompareTo(r.perp));

            int start = 0;
            for (int i = 1; i <= projected.Count; i++)
            {
                if (i < projected.Count && projected[i].perp - projected[i - 1].perp <= RowLateralToleranceM)
                    continue;

                if (i - start >= MinConesPerRow)
                {
                    double minAlong = double.MaxValue, maxAlong = double.MinValue, sumPerp = 0;
                    for (int k = start; k < i; k++)
                    {
                        minAlong = Math.Min(minAlong, projected[k].along);
                        maxAlong = Math.Max(maxAlong, projected[k].along);
                        sumPerp += projected[k].perp;
                    }

                    if (maxAlong - minAlong >= MinRowSpanM)
                        rows.Add(new Row(alongX, sumPerp / (i - start), minAlong, maxAlong, i - start));
                }

                start = i;
            }
        }

        return rows;
    }
}
