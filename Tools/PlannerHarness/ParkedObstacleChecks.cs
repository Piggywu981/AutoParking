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
///
///  Scope note: this case exercises <see cref="Planner"/> and the whitelist it shares with the plugin,
///  <c>MemClassifier.BlocksMemoryRoutes</c> - so dropping parked vehicles from that predicate fails
///  here. It does not cover whether the runtime reader actually feeds these items into a snapshot,
///  which needs the game and belongs to the in-game check (Task 9).
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
        //
        // The predicate term is what pins the plugin change: AddMemoryObstacles decides "blocker"
        // with MemClassifier.BlocksMemoryRoutes, and this file cannot link the plugin, so asserting
        // the shared predicate is the only offline guard against the whitelist dropping this kind.
        // Without it the boxes above are fed to the planner by hand and this case stays green
        // through exactly the regression it exists to prevent.
        bool parkedBlocks = MemClassifier.BlocksMemoryRoutes(MemKind.ParkedVehicle, 0x2F, 0x200);
        bool hole = without.Ok && without.ConflictCount == 0 && parkedBlocks;
        Report(ref failures, hole, "无停放车：实测场景规划出 0 冲突路线（漏洞）",
               $"conflicts={without.ConflictCount} · Ok={without.Ok} · Reason={without.Reason} · " +
               $"Route={without.Path?.Description ?? "无"} · ParkedVehicle 阻挡={parkedBlocks}");

        // (ii) The closure. A planner that refuses the route outright (`Ok == false`) and one that
        // still returns it with conflicts are both correct here, because the plugin only drives an
        // Ok plan; the assertion is that the route is no longer accepted.
        //
        // The route itself is re-measured, not inferred. Without this term the comparison would rest
        // on Planner returning candidates[0] for both runs, and `without.ConflictCount == 0` is
        // tautological when the snapshot is empty - CountCorridorConflicts returns early on an empty
        // obstacle set, so the printed 0 would say nothing about the route. Sweeping the *without*-run's
        // path through the *with*-boxes snapshot turns that 0 into a measured 0-conflict route and the
        // 27 into a conflict count on that same route.
        int sameRouteConflicts = without.Path is { } route
            ? Planner.CountCorridorConflicts(route, cfg, withTrucks)
            : -1;

        // The predicate terms, in both directions. A 0xC17 Mover is the game's parking marker and must
        // NOT block: the resolver returns the pair's midpoint, so its own 8.75/7.94 m boxes contained
        // the goal and refused the earliest start (Critical 1). A plain Mover (flags 0x417, one bit
        // different) still blocks. A parked vehicle blocks; Trigger and Other stay out - so the helper
        // cannot pass by being "everything blocks" or "nothing does".
        bool markerBlocks = MemClassifier.BlocksMemoryRoutes(MemKind.Mover, 0x09, 0xC17);
        bool plainMoverBlocks = MemClassifier.BlocksMemoryRoutes(MemKind.Mover, 0x09, 0x417);
        bool triggerBlocks = MemClassifier.BlocksMemoryRoutes(MemKind.Trigger, 0x22, 0x80006);
        bool otherBlocks = MemClassifier.BlocksMemoryRoutes(MemKind.Other, 0x05, 0x0);

        // What a detected bay may exempt (Important 5): the bay's own furniture - its cone rows, its
        // boards and the 8x8 m Mover markings - but never a parked vehicle. "Furniture" is the whole
        // test now, so a Hookup inside an occupied bay goes into the obstacle set and the planner sees
        // the vehicle standing in the spot. The bay rectangle itself is the plugin's InsideBay call;
        // this pins the half that decides which kinds that call may drop.
        bool coneIsFurniture = MemClassifier.IsBayFurniture(MemKind.Cone);
        bool parkedIsNotFurniture = !MemClassifier.IsBayFurniture(MemKind.ParkedVehicle);

        bool blocked = (!with.Ok || with.ConflictCount > 0) && sameRouteConflicts > 0
                       && plainMoverBlocks && parkedBlocks && !markerBlocks
                       && !triggerBlocks && !otherBlocks
                       && coneIsFurniture && parkedIsNotFurniture;
        Report(ref failures, blocked, "三台停放车入障碍集后，同一场景不再被接受",
               $"conflicts={with.ConflictCount} · sameRoute={sameRouteConflicts} · Ok={with.Ok} · " +
               $"Reason={with.Reason} · Route={with.Path?.Description ?? "无"} · " +
               $"普通 Mover 阻挡={plainMoverBlocks} / 停放车 阻挡={parkedBlocks} / " +
               $"0xC17 标记对 阻挡={markerBlocks} / Trigger 阻挡={triggerBlocks} / Other 阻挡={otherBlocks} · " +
               $"库内豁免 锥桶={coneIsFurniture} / 停放车={MemClassifier.IsBayFurniture(MemKind.ParkedVehicle)}（判据 锥桶=True 停放车=False）");

        return failures;
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
