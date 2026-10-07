using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for GroundTrust — the map's own admission of which ground it can vouch for. The point of
///  keeping it as a rasterized cell set rather than a distance query is that the planner then asks
///  "how much of the route's demanded envelope sits on ground nobody vouched for" as a cell count,
///  and that number is what M7c is going to be weighted on. So the grid has to be deterministic and
///  independent of the order the map walk delivered its items in.
/// </summary>
internal static class GroundTrustChecks
{
    public static int Run()
    {
        int failures = 0;

        // A straight corridor along +X, 40 m long, half-width 5 m, sampled to 120 m.
        GroundTrust trust = new()
        {
            Center = new Vector2(0, 0),
            RadiusM = 120,
            CorridorHalfWidthM = 5.0
        };
        trust.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });

        Report(ref failures, trust.IsConfirmed(new Vector2(0, 0)), "曲线正上方已确认", "(0,0)");
        Report(ref failures, trust.IsConfirmed(new Vector2(10, 4.9f)), "半宽内已确认", "(10,4.9) 在 5 m 半宽内");
        Report(ref failures, !trust.IsConfirmed(new Vector2(10, 6.0f)), "超出半宽未确认", "(10,6.0)");
        Report(ref failures, !trust.IsConfirmed(new Vector2(30, 0)), "曲线端点之外未确认", "(30,0) 沿线但超出曲线");

        // The radius gate is the other half of "the map was even looked at out there".
        GroundTrust narrow = new() { Center = new Vector2(0, 0), RadiusM = 12, CorridorHalfWidthM = 5.0 };
        narrow.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });
        Report(ref failures, narrow.IsConfirmed(new Vector2(8, 0)), "半径内走廊已确认", "(8,0) 在 12 m 半径内");
        Report(ref failures, !narrow.IsConfirmed(new Vector2(16, 0)), "半径外一律未确认", "(16,0) 在走廊上但超出采样半径");

        // A collidable object takes the voucher away, in both stamping orders.
        GroundTrust withWallA = new() { Center = new Vector2(0, 0), RadiusM = 120, CorridorHalfWidthM = 5.0 };
        withWallA.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });
        withWallA.AddBlocker(new[] { new Vector2(2, -2), new Vector2(6, -2), new Vector2(6, 2), new Vector2(2, 2) }, true);

        GroundTrust withWallB = new() { Center = new Vector2(0, 0), RadiusM = 120, CorridorHalfWidthM = 5.0 };
        withWallB.AddBlocker(new[] { new Vector2(2, -2), new Vector2(6, -2), new Vector2(6, 2), new Vector2(2, 2) }, true);
        withWallB.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });

        Report(ref failures, !withWallA.IsConfirmed(new Vector2(4, 0)), "闭合障碍内部未确认", "(4,0) 在 4×4 墙里");
        Report(ref failures, withWallA.IsConfirmed(new Vector2(0, 0)), "障碍旁边仍然已确认", "(0,0) 离墙 2 m");
        Report(ref failures, withWallA.CorridorCells == withWallB.CorridorCells
                            && withWallA.BlockedCells == withWallB.BlockedCells
                            && withWallA.IsConfirmed(new Vector2(4, 0)) == withWallB.IsConfirmed(new Vector2(4, 0)),
            "盖章顺序无关",
            $"正序 {withWallA.CorridorCells}/{withWallA.BlockedCells} 与逆序 {withWallB.CorridorCells}/{withWallB.BlockedCells} 相同");

        // Confirmed cells are corridor cells no blocker sits on: a blocker outside the corridor must not
        // shrink the count, which is exactly what subtracting the two set sizes would have done.
        GroundTrust overlap = new() { Center = new Vector2(0, 0), RadiusM = 120, CorridorHalfWidthM = 5.0 };
        overlap.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });
        int before = overlap.ConfirmedCells;
        overlap.AddBlocker(new[] { new Vector2(2, -2), new Vector2(6, -2), new Vector2(6, 2), new Vector2(2, 2) }, true);
        int afterWall = overlap.ConfirmedCells;
        overlap.AddBlocker(new[] { new Vector2(60, 60), new Vector2(64, 60), new Vector2(64, 64), new Vector2(60, 64) }, true);
        Report(ref failures, before > afterWall && afterWall > 0 && overlap.ConfirmedCells == afterWall,
            "只减真正重叠的格", $"{before} → 挡墙后 {afterWall} → 走廊外再加一块仍是 {overlap.ConfirmedCells}");

        // An open chain is a wall *segment*, not a filled region: it blocks a band, not an inside.
        GroundTrust segment = new() { Center = new Vector2(0, 0), RadiusM = 120, CorridorHalfWidthM = 5.0 };
        segment.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });
        segment.AddBlocker(new[] { new Vector2(0, -10), new Vector2(0, 10) }, false);
        Report(ref failures, !segment.IsConfirmed(new Vector2(0.4f, 0)), "线段障碍带未确认", "(0.4,0) 落在 1.5 m 障碍带内");
        Report(ref failures, segment.IsConfirmed(new Vector2(3, 0)), "障碍带之外仍确认", "(3,0) 离线段 3 m");

        // Cell keys must not collide across quadrants — negative coordinates are the normal case — and
        // two points in the same cell must land on the same key, or areas would count one cell twice.
        long k1 = trust.KeyAt(new Vector2(1.5f, 1.5f));
        long k2 = trust.KeyAt(new Vector2(-1.5f, -1.5f));
        long k3 = trust.KeyAt(new Vector2(1.5f, -1.5f));
        Report(ref failures, k1 != k2 && k1 != k3 && k2 != k3, "单元格键不撞", "四个象限的格键互不相同");
        Report(ref failures, trust.KeyAt(new Vector2(1.0f, 1.0f)) == trust.KeyAt(new Vector2(1.4f, 1.9f)),
            "同一格内键相同", "(1.0,1.0) 与 (1.4,1.9) 同属一个 1 m 单元格");
        Report(ref failures, trust.KeyAt(new Vector2(1.0f, 1.0f)) != trust.KeyAt(new Vector2(2.0f, 1.0f)),
            "跨一格键就不同", "(1.0,1.0) 与 (2.0,1.0) 分属两格");

        // Cap: a pathological depot must not be able to stamp forever. The input has to be wide enough
        // that the cells cannot deduplicate into the budget — the first draft of this case used a 400 m
        // sample and passed on `cells <= MaxCells` alone, which proved nothing about the cap firing.
        GroundTrust capped = new() { Center = new Vector2(0, 0), RadiusM = 1200, CorridorHalfWidthM = 5.0 };
        for (int i = 0; i < 400; i++)
            capped.AddCorridor(new[] { new Vector2(-600, i * 3 - 600), new Vector2(600, i * 3 - 600) });
        Report(ref failures, capped.Truncated && capped.CorridorCells <= GroundTrust.MaxCells,
            "盖章超预算就截断", $"{capped.CorridorCells} 格 / 上限 {GroundTrust.MaxCells}，截断标志={capped.Truncated}");

        return failures;
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
