using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  Resolves where the game wants the truck parked, following the marker protocol measured on
///  2026-10-19 (design doc §54): a Mover pair with flags == 0xC17 marks the active stage's
///  target; the parked Hookup row (flags family 0x300) around it defines the physical gap and
///  the heading axis. Pure math over caller-supplied items so the offline harness can test it
///  without a game process; the plugin maps KdopReader.MemItem into Item at the call site.
///
///  Priority: 0xC17 pair (0.9) → framing-vehicle gap (0.6) → SavedSpot (0.4) → null.
///  Confidence below the plugin's MinConfidence gate refuses the start; the caller decides.
/// </summary>
public static class TargetResolver
{
    public readonly record struct Item(int Type, ulong Uid, Vector2 Pos, float Dx, float Dz, uint Flags);

    public readonly record struct Target(Pose2 Pose, string Provenance, double Confidence, ulong Uid,
                                         double? LateralClearM, Vector2? GapCenter);

    private const int MoverType = 0x09;
    private const int HookupType = 0x2F;
    private const uint C17Flags = 0xC17;
    private const double PairRadiusM = 2.5;
    private const double RowBandM = 1.5;
    private const double MinGapOverTruckM = 0.4;   // spec §4.5：纵向净长 >= 车长 + 0.4 否则拒绝
    private const double ConfMover = 0.9;
    private const double ConfFraming = 0.6;
    private const double ConfSaved = 0.4;

    public static Target? Resolve(IReadOnlyList<Item> items, Pose2 truck, Pose2? savedSpot,
                                  double vehicleLengthM)
    {
        List<Item> parked = new();
        List<Item> c17 = new();
        foreach (Item it in items)
        {
            if (it.Type == MoverType && it.Flags == C17Flags)
                c17.Add(it);
            else if (it.Type == HookupType && (it.Flags & 0x300u) != 0)
                parked.Add(it);
        }

        List<(Vector2 a, Vector2 b, double clear, double depth)> gaps = FramingGaps(parked, vehicleLengthM);

        if (c17.Count > 0)
        {
            // Pairs closer together than PairRadiusM form one marker; the nearest pair wins.
            (Vector2 mid, ulong uid)? best = null;
            double bestDist = double.MaxValue;
            for (int i = 0; i < c17.Count; i++)
            for (int j = i + 1; j < c17.Count; j++)
            {
                if (Vector2.Distance(c17[i].Pos, c17[j].Pos) > PairRadiusM) continue;
                Vector2 mid = (c17[i].Pos + c17[j].Pos) / 2f;
                double d = Vector2.Distance(mid, truck.Position);
                if (d < bestDist) { bestDist = d; best = (mid, c17[i].Uid); }
            }
            if (best is not null)
            {
                var (mid, uid) = best.Value;
                double? clear = null; Vector2? gapCenter = null;
                foreach (var g in gaps)
                {
                    // A pair INSIDE a framing gap (rect test: ±clear/2 along the row, ±depth/2
                    // across it) inherits that gap's lateral room for the insert envelope; a pair
                    // out in the open lane (the stopmid zone sits 4.4 m north of the row) does not.
                    Vector2 c = (g.a + g.b) / 2f;
                    Vector2 axis = g.b - g.a;
                    float len = axis.Length();
                    if (len < 1e-3f) continue;
                    axis /= len;
                    Vector2 n = new(-axis.Y, axis.X);
                    Vector2 d = mid - c;
                    if (Math.Abs(Vector2.Dot(d, axis)) <= g.clear / 2.0 + 0.5
                     && Math.Abs(Vector2.Dot(d, n)) <= g.depth / 2.0 + 0.5)
                    { clear = g.depth; gapCenter = c; break; }
                }
                Pose2 pose = new(mid.X, mid.Y, RowHeading(parked, mid, truck));
                return new Target(pose, $"mover-pair:0x{uid:X}", ConfMover, uid, clear, gapCenter);
            }
        }

        if (gaps.Count > 0)
        {
            var g = gaps[0]; // FramingGaps already returns the widest first.
            Vector2 c = (g.a + g.b) / 2f;
            Pose2 pose = new(c.X, c.Y, RowHeading(parked, c, truck));
            return new Target(pose, $"framing-gap:{g.clear:0.00}m", ConfFraming, 0, g.depth, c);
        }

        if (savedSpot is Pose2 s)
            return new Target(s, "saved-spot", ConfSaved, 0, null, null);

        return null;
    }

    /// <summary>Per-side envelope pad for an insert leg, metres. Spec §4.5: the framing row's
    ///  kdop depth is the lateral room the manoeuvre has; subtract the truck and a safety margin,
    ///  then clamp to the range the planner has been validated in.</summary>
    public static double InsertPad(double lateralClearM, double vehicleWidthM, double safetyMarginM)
        => Math.Clamp((lateralClearM - vehicleWidthM) / 2.0 - safetyMarginM, 0.05, 0.30);

    // Widest-gap-first so Resolve's "take gaps[0]" is deterministic.
    //
    // GAP PAIRING here is world-X only, and that is deliberate: the sort is on X and two neighbours
    // only count as a gap when the same-Y band test below lets them through, so a row of parked
    // vehicles running north-south yields no gap. Driving/ParkingBay.cs:31-32 documents the same
    // refusal for off-axis cone bays - a bay rotated away from the axes is silently not detected
    // rather than guessed at. Keep it: a caller that gets nothing back degrades to the saved spot,
    // while one that gets a guessed gap parks the truck across a row. Do not "fix" this into a
    // best-fit rotation.
    //
    // HEADING INFERENCE is the narrower thing and is NOT covered by the refusal above: RowHeading
    // snaps to whichever of the two world axes the parked spread is longer along (X or Z), never to
    // an arbitrary angle. An axis-only answer still cannot invent a rotated goal; see RowHeading.
    private static List<(Vector2 a, Vector2 b, double clear, double depth)> FramingGaps(
        List<Item> parked, double vehicleLengthM)
    {
        var result = new List<(Vector2 a, Vector2 b, double clear, double depth)>();
        List<Item> sorted = new(parked);
        sorted.Sort((l, r) => l.Pos.X != r.Pos.X ? l.Pos.X.CompareTo(r.Pos.X) : l.Pos.Y.CompareTo(r.Pos.Y));
        for (int i = 0; i + 1 < sorted.Count; i++)
        {
            Item l = sorted[i], r = sorted[i + 1];
            if (Math.Abs(l.Pos.Y - r.Pos.Y) > RowBandM) continue; // not the same row
            double clear = Vector2.Distance(l.Pos, r.Pos) - (l.Dx + r.Dx) / 2f;
            if (clear < vehicleLengthM + MinGapOverTruckM) continue;
            double depth = (l.Dz + r.Dz) / 2f;
            result.Add((l.Pos, r.Pos, clear, depth));
        }
        result.Sort((a, b) => b.clear.CompareTo(a.clear));
        return result;
    }

    private static double RowHeading(List<Item> parked, Vector2 at, Pose2 truck)
    {
        // kdop boxes carry no yaw; the row axis is the heading family. This is a world-axis SNAP, not a
        // rotation fit: the spread picks X or Z, never an arbitrary angle, which is the same refusal
        // Driving/ParkingBay.cs:31-32 records for off-axis bays. The Z branch is reachable and intended
        // even though FramingGaps pairs along world X only - a north-south row gives no gap, but its
        // axis is still a fact read off the parked vehicles, whereas falling back to the truck's own
        // heading would invent the goal from the vehicle instead of from the row.
        // Pick the sign whose forward is closer to the truck's own nose so the goal never flips 180° on
        // the user.
        Vector2 fwd = Geometry.ForwardFromHeading(truck.HeadingRad);
        double sx = 0, sy = 0;
        int near = 0;
        foreach (Item p in parked)
        {
            if (Vector2.Distance(p.Pos, at) >= 40.0) continue;
            near++;
            sx = Math.Max(sx, Math.Abs(p.Pos.X - at.X));
            sy = Math.Max(sy, Math.Abs(p.Pos.Y - at.Y));
        }
        if (near < 2) return truck.HeadingRad;

        Vector2 axis = sx >= sy ? new Vector2(1, 0) : new Vector2(0, 1);
        Vector2 dir = Vector2.Dot(axis, fwd) >= 0 ? axis : -axis;
        return Geometry.HeadingFromForward(dir);
    }
}
