using System;
using System.Collections.Generic;
using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for the target resolver: the pure marker-to-pose chain that decides where the game wants
///  the truck parked, with no planner and no game process in sight.
///
///  It exists because choosing the spot by hand is what cannot cover a multi-stage academy job - the
///  game moves the target between stages. The fixtures below are the memory items read live on
///  2026-10-19, so every expected number here is a measurement of what the markers actually say,
///  not a preference about what they should say.
/// </summary>
internal static class TargetResolverChecks
{
    // 2026-10-19 驾校 book1_2_09 实测（--deep 2194.40 1804.12 90）：0xC17 对 + 三台 framing 车。
    private static readonly TargetResolver.Item[] Scene =
    {
        new(0x09, 0x512DA87F50000000, new Vector2(2166.03f, 1801.67f), 8.75f, 8.75f, 0xC17),
        new(0x09, 0x3C11E12C59800001, new Vector2(2166.32f, 1802.00f), 7.94f, 7.94f, 0xC17),
        new(0x2F, 0x50E541F01C9C0001, new Vector2(2166.19f, 1797.41f), 7.50f, 3.23f, 0x200),
        new(0x2F, 0x51B24FF0D91C0000, new Vector2(2182.47f, 1797.42f), 7.50f, 3.23f, 0x100),
        new(0x2F, 0x50E5468F7C9C0000, new Vector2(2190.58f, 1797.42f), 7.50f, 3.23f, 0x100),
    };
    private static readonly Pose2 Truck = new(2194.40, 1804.12, 1.295); // 74.2°=1.295 rad

    // Synthetic north-south row: four framing vehicles sharing one X and spread along Z, with a 0xC17
    // pair beside them. FramingGaps pairs along world X only, so this scene yields NO gap - yet
    // RowHeading still has to resolve, and the only honest answer is the other world axis.
    private static readonly TargetResolver.Item[] RowNorthSouth =
    {
        new(0x09, 0x512DA87F500000A1, new Vector2(1000.00f, 1000.00f), 8.75f, 8.75f, 0xC17),
        new(0x09, 0x3C11E12C598000A2, new Vector2(1000.30f, 1000.30f), 7.94f, 7.94f, 0xC17),
        new(0x2F, 0x50E541F01C9C00A1, new Vector2(1008.00f,  985.00f), 7.50f, 3.23f, 0x200),
        new(0x2F, 0x51B24FF0D91C00A2, new Vector2(1008.00f,  995.00f), 7.50f, 3.23f, 0x100),
        new(0x2F, 0x50E5468F7C9C00A3, new Vector2(1008.00f, 1005.00f), 7.50f, 3.23f, 0x10C),
        new(0x2F, 0x50E5468F7C9C00A4, new Vector2(1008.00f, 1015.00f), 7.50f, 3.23f, 0x100),
    };

    // Two rows the resolver can see at once: the academy's 8.78 m gap ~21 m from the truck and a
    // roomier 20 m gap ~63 m away, on its own Y band so the two rows cannot pair with each other
    // (the 1.5 m same-row test). Widest-first used to return the 20 m one - i.e. a leg could be
    // driven to a bay the truck was not standing at, purely because it was roomier.
    private static readonly TargetResolver.Item[] NearAndFar =
    {
        new(0x2F, 0x50E541F01C9C00B1, new Vector2(2166.19f, 1797.41f), 7.50f, 3.23f, 0x200),
        new(0x2F, 0x51B24FF0D91C00B2, new Vector2(2182.47f, 1797.42f), 7.50f, 3.23f, 0x100),
        new(0x2F, 0x50E5468F7C9C00B3, new Vector2(2244.00f, 1805.00f), 7.50f, 3.23f, 0x100),
        new(0x2F, 0x50E5468F7C9C00B4, new Vector2(2271.50f, 1805.00f), 7.50f, 3.23f, 0x100),
    };

    // The same roomy row parked 200 m away - outside the truck-local window the resolver and the
    // plugin both apply (80 m: the planner's longest candidate is 72 m at the defaults).
    private static readonly TargetResolver.Item[] DistantRow =
    {
        new(0x2F, 0x50E5468F7C9C00C1, new Vector2(2400.00f, 1805.00f), 7.50f, 3.23f, 0x100),
        new(0x2F, 0x50E5468F7C9C00C2, new Vector2(2427.50f, 1805.00f), 7.50f, 3.23f, 0x100),
    };

    public static int Run()
    {
        int failures = 0;

        // The 0xC17 pair is the marker the game itself moves between stages, so it outranks the
        // framing gap even though the gap is a bigger object. The stopmid zone sits out in the open
        // lane 4.4 m north of the parked row, so it must NOT borrow that row's lateral room - the
        // insert envelope comes only from a pair genuinely inside a gap.
        TargetResolver.Target? pair = TargetResolver.Resolve(Scene, Truck, new Pose2(0, 0, 0), 6.5);
        Report(ref failures, pair.HasValue
               && Math.Abs(pair.Value.Pose.X - 2166.175) < 0.01
               && Math.Abs(pair.Value.Pose.Z - 1801.835) < 0.01
               && Math.Abs(pair.Value.Confidence - 0.9) < 0.001
               && !pair.Value.LateralClearM.HasValue
               && pair.Value.Provenance.StartsWith("mover-pair", StringComparison.Ordinal),
               "0xC17 对优先",
               pair is null
                   ? "无解（判据 中点 2166.175,1801.835 · conf 0.9 · 不借缺口净宽）"
                   : $"({pair.Value.Pose.X:0.000}, {pair.Value.Pose.Z:0.000}) conf {pair.Value.Confidence:0.00} · "
                     + $"clear {(pair.Value.LateralClearM is null ? "无" : $"{pair.Value.LateralClearM.Value:0.00}")} · "
                     + $"{pair.Value.Provenance}");

        // kdop boxes carry no yaw, so the row axis is the only heading available - but an axis has two
        // signs and the wrong one points the truck backwards. Nose west must read +90°, nose east -90°:
        // the sign itself is asserted, not just the magnitude, because a constant axis (or a bare
        // |heading| test) keeps every magnitude check green while shipping a 180°-wrong goal.
        // The two halves below then repeat the rule for a row the gap finder cannot see at all.
        TargetResolver.Target? west = TargetResolver.Resolve(Scene, Truck, null, 6.5);
        TargetResolver.Target? east = TargetResolver.Resolve(Scene,
            new Pose2(2194.40, 1804.12, 1.295 + Math.PI), null, 6.5);
        // Heading ±π sits on the atan2 branch cut, so the north-south half is asserted on the forward
        // vector the repo maps headings by (forward = (-sin h, -cos h)): a Z-axis row must land with
        // forward.X 0, i.e. heading 0 or ±π and never ±π/2, with forward.Y following the nose.
        TargetResolver.Target? plusZ = TargetResolver.Resolve(RowNorthSouth,
            new Pose2(1020.00, 1000.00, 2.0), null, 6.5);            // forward.Y = -cos 2 > 0
        TargetResolver.Target? minusZ = TargetResolver.Resolve(RowNorthSouth,
            new Pose2(1020.00, 1000.00, 2.0 - Math.PI), null, 6.5);   // same row, nose reversed
        Vector2 plusZFwd = Geometry.ForwardFromHeading(plusZ?.Pose.HeadingRad ?? double.NaN);
        Vector2 minusZFwd = Geometry.ForwardFromHeading(minusZ?.Pose.HeadingRad ?? double.NaN);
        Report(ref failures, west.HasValue && Math.Abs(west.Value.Pose.HeadingRad - Math.PI / 2) < 0.01
               && east.HasValue && Math.Abs(east.Value.Pose.HeadingRad + Math.PI / 2) < 0.01
               && plusZ.HasValue && minusZ.HasValue
               && Math.Abs(plusZ.Value.Confidence - 0.9) < 0.001     // pair path, no gap to borrow
               && Math.Abs(plusZFwd.X) < 0.01 && plusZFwd.Y > 0
               && Math.Abs(minusZFwd.X) < 0.01 && minusZFwd.Y < 0,
               "对目标航向取排轴线中与车头相近的符号",
               $"朝西 {(west?.Pose.HeadingRad ?? double.NaN) * 180.0 / Math.PI:0.0}° · "
               + $"朝东 {(east?.Pose.HeadingRad ?? double.NaN) * 180.0 / Math.PI:0.0}°（判据 +90 / -90）· "
               + $"南北排 {(plusZ?.Pose.HeadingRad ?? double.NaN) * 180.0 / Math.PI:0.0}° / "
               + $"{(minusZ?.Pose.HeadingRad ?? double.NaN) * 180.0 / Math.PI:0.0}°"
               + "（判据 forward.X 0 · forward.Y 随车头）");

        // Without the pair the framing row alone still says where the truck goes: the widest gap the
        // 8.78 m clear length leaves between two parked vehicles, with its 3.23 m depth as the room.
        TargetResolver.Item[] noPair = new TargetResolver.Item[Scene.Length - 2];
        Array.Copy(Scene, 2, noPair, 0, noPair.Length);
        TargetResolver.Target? gap = TargetResolver.Resolve(noPair, Truck, null, 6.5);
        Report(ref failures, gap.HasValue
               && Math.Abs(gap.Value.Pose.X - 2174.33) < 0.02
               && Math.Abs(gap.Value.Pose.Z - 1797.42) < 0.02
               && Math.Abs(gap.Value.Confidence - 0.6) < 0.001
               && gap.Value.LateralClearM.HasValue && Math.Abs(gap.Value.LateralClearM.Value - 3.23) < 0.01,
               "无对时解 framing 缺口",
               gap is null
                   ? "无解（判据 2174.33,1797.42 · clear 3.23 · conf 0.6）"
                   : $"({gap.Value.Pose.X:0.00}, {gap.Value.Pose.Z:0.00}) "
                     + $"clear {(gap.Value.LateralClearM ?? double.NaN):0.00} conf {gap.Value.Confidence:0.00}");

        // Nothing in the map but a saved spot: fall back to it, at a confidence the caller's
        // MinConfidence gate can refuse. The truck pose here is far from (100, 200), so no gap or
        // pair can be invented out of an empty item list.
        TargetResolver.Target? saved = TargetResolver.Resolve(Array.Empty<TargetResolver.Item>(), Truck,
                                                              new Pose2(100, 200, 1.0), 6.5);
        Report(ref failures, saved.HasValue && saved.Value.Pose.X == 100
               && Math.Abs(saved.Value.Confidence - 0.4) < 0.001,
               "空场景退到 SavedSpot",
               saved is null ? "无解（判据 conf 0.4）"
                             : $"X {saved.Value.Pose.X:0.0} conf {saved.Value.Confidence:0.00}");

        Report(ref failures,
               TargetResolver.Resolve(Array.Empty<TargetResolver.Item>(), Truck, null, 6.5) == null,
               "无对无缺口无存档 → null", "空输入无解（判据 null，调用方据此拒绝起步）");

        // Critical 4, tier 2: the gap must be the one the truck is standing at, not the widest one in
        // the snapshot. `nearAndFar` keeps both rows in view (~21 m and ~63 m) so this discriminates
        // the sort itself; `distant` is the same roomy row 200 m away, outside the 80 m local window.
        TargetResolver.Target? nearest = TargetResolver.Resolve(NearAndFar, Truck, null, 6.5);
        List<TargetResolver.Item> localPlusDistant = new(NearAndFar.Length + DistantRow.Length);
        localPlusDistant.AddRange(NearAndFar);
        localPlusDistant.AddRange(DistantRow);
        TargetResolver.Target? withinWindow = TargetResolver.Resolve(localPlusDistant, Truck, null, 6.5);
        Report(ref failures,
               nearest.HasValue && withinWindow.HasValue
               && Math.Abs(nearest.Value.Pose.X - 2174.33) < 0.02
               && Math.Abs(withinWindow.Value.Pose.X - 2174.33) < 0.02
               && nearest.Value.Provenance.StartsWith("framing-gap:8.78", StringComparison.Ordinal),
               "缺口取离车最近的一个（近 8.78 m 胜过远处 20 m）",
               $"近/远同屏 → {(nearest is null ? "无解" : $"({nearest.Value.Pose.X:0.00}, {nearest.Value.Pose.Z:0.00}) {nearest.Value.Provenance}")} · " +
               $"再加 200 m 外 20 m 缺口 → {(withinWindow is null ? "无解" : $"({withinWindow.Value.Pose.X:0.00}, {withinWindow.Value.Pose.Z:0.00}) {withinWindow.Value.Provenance}")}" +
               "（判据 X 2174.33 · 出处 framing-gap:8.78）");

        // The saved-spot tier only becomes reachable once the distant row stops counting: with the
        // local row absent, a 20 m gap 200 m away is not the truck's business and the user-picked
        // pose must win at its 0.4 confidence (R36 then exempts it from the confidence gate).
        Pose2 savedPose = new(2194.0, 1800.0, 0.5);
        TargetResolver.Target? fallback = TargetResolver.Resolve(DistantRow, Truck, savedPose, 6.5);
        Report(ref failures,
               fallback.HasValue && fallback.Value.Provenance == "saved-spot"
               && Math.Abs(fallback.Value.Confidence - 0.4) < 0.001
               && fallback.Value.Pose.X == savedPose.X && fallback.Value.Pose.Z == savedPose.Z,
               "只有远处缺口时退到存点（conf 0.4）",
               fallback is null
                   ? "无解（判据 出处 saved-spot · conf 0.4）"
                   : $"{fallback.Value.Provenance} conf {fallback.Value.Confidence:0.00} " +
                     $"({fallback.Value.Pose.X:0.0}, {fallback.Value.Pose.Z:0.0})（判据 2194.0, 1800.0）");

        // The pad is the whole point of the dynamic envelope: truck-wide gaps get nothing, roomy
        // gaps get capped at what the planner was validated in.
        Report(ref failures, Math.Abs(TargetResolver.InsertPad(3.24, 2.60, 0.10) - 0.22) < 1e-9
               && Math.Abs(TargetResolver.InsertPad(2.80, 2.60, 0.10) - 0.05) < 1e-9
               && Math.Abs(TargetResolver.InsertPad(4.00, 2.60, 0.10) - 0.30) < 1e-9,
               "InsertPad clamp 公式",
               $"3.24→{TargetResolver.InsertPad(3.24, 2.60, 0.10):0.000} · "
               + $"2.80→{TargetResolver.InsertPad(2.80, 2.60, 0.10):0.000} · "
               + $"4.00→{TargetResolver.InsertPad(4.00, 2.60, 0.10):0.000}（判据 0.22 / 下限 0.05 / 上限 0.30）");

        return failures;
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
