using System;
using System.Numerics;
using AutoParking;

/// <summary>
///  Checks that the parked vehicles framing a target gap reach the planner as obstacles.
///
///  The scene is the measured one (2026-10-19, 驾校 book1_2_09, scratch/deep_parallel.txt): the truck
///  at (2194.40, 1804.12) yaw 1.295 rad, the target gap centred on (2174.33, 1797.42) facing +90°, and
///  three 0x2F Hookups of 7.50 × 4.69 × 3.23 m standing along the row at z ≈ 1797.4. Those Hookups are
///  the only representation the academy's parked trucks have - the other 78 items nearby are 0x05 road
///  paint - so if the memory whitelist does not admit MemKind.ParkedVehicle, nothing in the planner's
///  obstacle set represents them and an insert leg may sweep through one (§43/§54, R16).
///
///  Two plans differ in nothing but the three boxes. The first reproduces the hole (a route accepted
///  against today's obstacle set); the second proves the same route is no longer accepted once the
///  boxes are present. Both numbers are printed so a failure says which side moved.
/// </summary>
internal static class ParkedObstacleChecks
{
    private static readonly Pose2 Start = new(2194.40, 1804.12, 1.295);

    // TargetResolver.Resolve puts the framing-gap centre midway between the two Hookups that bound
    // the gap: (2166.19 + 2182.47) / 2 with the row heading, nose west (+90°).
    private static readonly Pose2 Goal = new(2174.33, 1797.42, Math.PI / 2);

    // Measured k-DOP centres and ground footprint (dx, dz) of the three 0x2F academy.parked.truck
    // items. dy = 4.69 m is height and takes no part in the ground-plane obstacle set.
    private static readonly (float X, float Z)[] TruckRow =
    {
        (2166.19f, 1797.41f),
        (2182.47f, 1797.42f),
        (2190.58f, 1797.42f),
    };

    private const float TruckDx = 7.50f;
    private const float TruckDz = 3.23f;

    public static int Run()
    {
        int failures = 0;

        // Same settings on both runs; only the obstacle set differs.
        AutoParkingSettings cfg = new();

        PlanResult without = Planner.Plan(Start, Goal, cfg, new ObstacleSnapshot());

        ObstacleSnapshot withTrucks = new();
        foreach ((float x, float z) in TruckRow)
            withTrucks.AddBox(new Vector2(x, z), TruckDx, TruckDz);
        PlanResult with = Planner.Plan(Start, Goal, cfg, withTrucks);

        // (i) Today's set contains no representation of the framing vehicles, so this is the hole:
        // the route that exists can sweep the space the trucks occupy. The measured claim is that
        // it is accepted with zero conflicts, not merely "some route exists".
        bool hole = without.Ok && without.ConflictCount == 0;
        Report(ref failures, hole, "无停放车：实测场景规划出 0 冲突路线（漏洞）",
               $"conflicts={without.ConflictCount} · Ok={without.Ok} · Reason={without.Reason} · " +
               $"Route={without.Path?.Description ?? "无"}");

        // (ii) The closure. A planner that refuses the route outright (`Ok == false`) and one that
        // still returns it with conflicts are both correct here, because the plugin only drives an
        // Ok plan; the assertion is that the route is no longer accepted.
        bool blocked = !with.Ok || with.ConflictCount > 0;
        Report(ref failures, blocked, "三台停放车入障碍集后，同一场景不再被接受",
               $"conflicts={with.ConflictCount} · Ok={with.Ok} · Reason={with.Reason} · " +
               $"Route={with.Path?.Description ?? "无"}");

        return failures;
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
