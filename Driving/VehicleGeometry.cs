using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoParking;

/// <summary>One axle, taken as the wheels that share a position along the vehicle.</summary>
public readonly record struct AxleGroup(int WheelCount, double AlongM, double LateralMinM, double LateralMaxM,
                                       bool Steerable, bool Liftable)
{
    /// <summary>Distance between the outermost wheels of this axle.</summary>
    public double TrackM => Math.Max(0.0, LateralMaxM - LateralMinM);

    public override string ToString()
        => $"{AlongM:0.00} m×{WheelCount} 轮距 {TrackM:0.00}{(Steerable ? " 转向" : "")}{(Liftable ? " 可举升" : "")}";
}

/// <summary>Result of reading the vehicle's own wheel layout out of telemetry configuration.</summary>
public readonly record struct WheelbaseMeasurement(bool Valid, string Reason, double WheelbaseM,
                                                  double FrontTrackM, double RearTrackM,
                                                  IReadOnlyList<AxleGroup> Axles, bool AlongIsZ)
{
    public static WheelbaseMeasurement Invalid(string reason)
        => new(false, reason, 0, 0, 0, Array.Empty<AxleGroup>(), false);
}

/// <summary>
///  Reads the wheelbase out of the per-wheel configuration the game already publishes
///  (configVector.truckWheelPositions plus configBool.truckWheelSteerable / truckWheelLiftable).
///
///  This is the numerator of <see cref="Kinematics.MinTurnRadius"/> and until now it was a number
///  typed into the settings file. It is not really a preference: the value is a fact about the truck
///  the player is standing in, and the truck changes when they buy a different chassis or cab, which
///  is exactly when a hand-typed 4.0 starts being wrong.
///
///  Pure geometry on plain arrays so the offline harness can assert it without the game.
/// </summary>
public static class VehicleGeometry
{
    /// <summary>Wheels this close together along the vehicle count as one axle.</summary>
    public const double AxleClusterToleranceM = 0.6;

    /// <summary>Sanity window for the resulting wheelbase. Outside it the measurement is refused.</summary>
    public const double MinPlausibleWheelbaseM = 2.0;

    public const double MaxPlausibleWheelbaseM = 8.0;

    public static WheelbaseMeasurement Measure(Vector3[]? positions, bool[]? steerable, bool[]? liftable,
                                               int wheelCount)
    {
        if (positions == null || positions.Length < 2)
            return WheelbaseMeasurement.Invalid("没有轮位数据");

        int count = Math.Clamp(wheelCount, 0, positions.Length);
        if (count < 2)
            return WheelbaseMeasurement.Invalid($"轮数不可信（{count}）");

        // The configuration frame is shared with the ground-offset code, which reads .Y as height
        // above the vehicle origin, so Y is up. Whether forward is X or Z is not documented anywhere
        // in what the host publishes, so pick whichever horizontal axis actually has the larger
        // spread rather than assuming, and say which one was picked.
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;

        for (int i = 0; i < count; i++)
        {
            Vector3 p = positions[i];
            if (!IsFinite(p))
                return WheelbaseMeasurement.Invalid($"第 {i} 个轮位不是有限值");

            // A wheel parked 40 m off the origin is not a wheel of this truck; refusing here keeps a
            // truncated or mis-indexed array from producing a confident wrong number.
            if (Math.Abs(p.X) > 10f || Math.Abs(p.Z) > 20f || Math.Abs(p.Y) > 5f)
                return WheelbaseMeasurement.Invalid($"第 {i} 个轮位离原点过远（{p.X:0.#},{p.Y:0.#},{p.Z:0.#}）");

            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minZ = Math.Min(minZ, p.Z);
            maxZ = Math.Max(maxZ, p.Z);
        }

        bool alongIsZ = maxZ - minZ >= maxX - minX;

        var groups = new List<AxleGroup>();

        for (int i = 0; i < count; i++)
        {
            Vector3 p = positions[i];
            double along = Axis(p, alongIsZ);
            double lateral = Axis(p, !alongIsZ);

            int at = groups.FindIndex(g => Math.Abs(g.AlongM - along) <= AxleClusterToleranceM);
            if (at < 0)
            {
                groups.Add(new AxleGroup(1, along, lateral, lateral,
                                        Flag(steerable, i), Flag(liftable, i)));
                continue;
            }

            AxleGroup near = groups[at];
            int n = near.WheelCount;
            groups[at] = near with
            {
                WheelCount = n + 1,
                AlongM = (near.AlongM * n + along) / (n + 1),
                LateralMinM = Math.Min(near.LateralMinM, lateral),
                LateralMaxM = Math.Max(near.LateralMaxM, lateral),
                Steerable = near.Steerable || Flag(steerable, i),
                Liftable = near.Liftable || Flag(liftable, i),
            };
        }

        groups.Sort((a, b) => a.AlongM.CompareTo(b.AlongM));

        if (groups.Count < 2)
            return WheelbaseMeasurement.Invalid($"只分出一个轴组（{groups.Count}），量不出轴距");

        // The pivot a truck turns around is the non-steering axle group that actually carries load.
        // Including a tag or lift axle would drag the centroid rearward and inflate the wheelbase, so
        // liftable groups are dropped unless doing so would leave nothing behind.
        List<AxleGroup> steered = groups.Where(g => g.Steerable && !g.Liftable).ToList();
        List<AxleGroup> pivots = groups.Where(g => !g.Steerable && !g.Liftable).ToList();

        if (steered.Count == 0)
            return WheelbaseMeasurement.Invalid("没有可转向轴组");

        if (pivots.Count == 0)
            pivots = groups.Where(g => !g.Steerable).ToList();

        if (pivots.Count == 0)
            return WheelbaseMeasurement.Invalid("没有非转向轴组");

        double front = steered.Average(g => g.AlongM);
        double rear = pivots.Average(g => g.AlongM);
        double wheelbase = Math.Abs(rear - front);

        if (wheelbase is < MinPlausibleWheelbaseM or > MaxPlausibleWheelbaseM)
            return WheelbaseMeasurement.Invalid($"轴距 {wheelbase:0.00} m 超出合理区间");

        // Track width comes from the axle nearest each pivot, not from the averaged centroid, since
        // a tandem's two axles can sit at different widths and the reference for a lane is the
        // outermost wheel that actually touches down.
        AxleGroup frontGroup = steered.OrderBy(g => Math.Abs(g.AlongM - front)).First();
        AxleGroup rearGroup = pivots.OrderBy(g => Math.Abs(g.AlongM - rear)).First();

        return new WheelbaseMeasurement(true, "测得", wheelbase, frontGroup.TrackM,
                                        rearGroup.TrackM, groups, alongIsZ);
    }

    /// <summary>
    ///  Body width implied by a measured wheel layout: the outermost wheel centre on either side,
    ///  plus a tyre allowance per side. The allowance has to be supplied - the telemetry publishes
    ///  wheel positions and no tyre dimension anywhere - so it is a setting, but everything else here
    ///  is measured. Kept as a function of the measurement so the harness can assert it without the
    ///  game (design doc §51: the hand-typed 2.6 m was over half a metre wider than the truck).
    /// </summary>
    public static double MeasuredWidthM(WheelbaseMeasurement measurement, double tireHalfWidthM)
    {
        if (!measurement.Valid || measurement.Axles.Count == 0)
            return 0.0;

        double outer = measurement.Axles.Max(g => Math.Max(Math.Abs(g.LateralMinM), Math.Abs(g.LateralMaxM)));
        return 2.0 * (outer + tireHalfWidthM);
    }

    /// <summary>
    ///  Whether a body box read out of game memory can be believed. It is the only source for the
    ///  vehicle's LENGTH, and it overwrites the box the planner sweeps along the route - a stale or
    ///  mis-pointed read that came back as 0.3 m would silently let the truck plan through a gap it
    ///  cannot fit, so anything outside a road-vehicle envelope is treated as "not read".
    /// </summary>
    public static bool PlausibleBodyBox(double lengthM, double widthM, double heightM)
        => double.IsFinite(lengthM) && double.IsFinite(widthM) && double.IsFinite(heightM)
        && lengthM is > 4.0 and < 16.0
        && widthM is >= 1.7 and <= 3.6
        && heightM is >= 2.0 and <= 4.6;

    private static double Axis(Vector3 p, bool useZ) => useZ ? p.Z : p.X;

    private static bool Flag(bool[]? flags, int index) => flags != null && index < flags.Length && flags[index];

    private static bool IsFinite(Vector3 v) =>
        !float.IsNaN(v.X) && !float.IsNaN(v.Y) && !float.IsNaN(v.Z)
        && !float.IsInfinity(v.X) && !float.IsInfinity(v.Y) && !float.IsInfinity(v.Z);
}
