using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for VehicleGeometry — the wheelbase read out of the game's own per-wheel configuration,
///  which replaces a hand-typed number in the settings file with a fact about the truck.
///
///  The case that matters most is the liftable tag axle. A 6x2 has three axles but turns around the
///  gap between the steered one and the driven one; averaging all non-steering axles instead would
///  drag the pivot rearward and inflate the wheelbase, which then inflates the planned minimum radius
///  and makes the planner refuse routes the truck can actually do.
/// </summary>
internal static class VehicleGeometryChecks
{
    public static int Run()
    {
        int failures = 0;

        // A 4x2: steered axle forward, one driving axle behind, 1.05 m either side.
        WheelbaseMeasurement twoAxle = VehicleGeometry.Measure(
            new[]
            {
                V(-1.05, 0.5f, 2.0f), V(1.05, 0.5f, 2.0f),
                V(-1.05, 0.5f, -2.2f), V(1.05, 0.5f, -2.2f)
            },
            new[] { true, true, false, false },
            new[] { false, false, false, false }, 4);

        Report(ref failures, twoAxle.Valid, "4x2 量得出轴距", twoAxle.Reason);
        Report(ref failures, Near(twoAxle.WheelbaseM, 4.2), "4x2 轴距 = 前后轴心差", $"实测 {twoAxle.WheelbaseM:0.00}，判据 4.20");
        Report(ref failures, twoAxle.AlongIsZ, "前后方向落在 Z 上", "Z 方向展开更大时选 Z");
        Report(ref failures, Near(twoAxle.FrontTrackM, 2.1) && Near(twoAxle.RearTrackM, 2.1),
            "轮距是左右轮之差不是绝对值", $"前 {twoAxle.FrontTrackM:0.00} / 后 {twoAxle.RearTrackM:0.00}，判据 2.10");

        // The 6x2 case: a liftable tag axle behind the driven one must not move the pivot.
        WheelbaseMeasurement withTag = VehicleGeometry.Measure(
            new[]
            {
                V(-1.05, 0.5f, 2.0f), V(1.05, 0.5f, 2.0f),
                V(-1.10, 0.5f, -1.6f), V(1.10, 0.5f, -1.6f),
                V(-1.10, 0.5f, -3.4f), V(1.10, 0.5f, -3.4f)
            },
            new[] { true, true, false, false, false, false },
            new[] { false, false, false, false, true, true }, 6);

        Report(ref failures, withTag.Valid, "6x2 带举升桥仍量得出", withTag.Reason);
        Report(ref failures, Near(withTag.WheelbaseM, 3.6),
            "举升桥不参与支点计算", $"实测 {withTag.WheelbaseM:0.00}，判据 3.60（若把举升桥算进去会是 4.50）");

        // Tandem driven axles pivot at their midpoint, so the wheelbase is front to that midpoint.
        WheelbaseMeasurement tandem = VehicleGeometry.Measure(
            new[]
            {
                V(-1.05, 0.5f, 2.0f), V(1.05, 0.5f, 2.0f),
                V(-1.10, 0.5f, -1.8f), V(1.10, 0.5f, -1.8f),
                V(-1.10, 0.5f, -3.0f), V(1.10, 0.5f, -3.0f)
            },
            new[] { true, true, false, false, false, false },
            new[] { false, false, false, false, false, false }, 6);

        Report(ref failures, Near(tandem.WheelbaseM, 4.4),
            "双驱动桥取中点作支点", $"实测 {tandem.WheelbaseM:0.00}，判据 4.40");
        Report(ref failures, tandem.Axles.Count == 3, "三根轴各成一组", $"{tandem.Axles.Count} 组：{string.Join(" | ", tandem.Axles)}");

        // Which horizontal axis is "along" is not documented by the host, so it is inferred from the
        // data. The same truck laid out along X has to give the same wheelbase.
        WheelbaseMeasurement alongX = VehicleGeometry.Measure(
            new[]
            {
                V(2.0f, 0.5f, -1.05f), V(2.0f, 0.5f, 1.05f),
                V(-2.2f, 0.5f, -1.05f), V(-2.2f, 0.5f, 1.05f)
            },
            new[] { true, true, false, false },
            new[] { false, false, false, false }, 4);

        Report(ref failures, !alongX.AlongIsZ && Near(alongX.WheelbaseM, 4.2),
            "前向落在 X 上时同样成立", $"AlongIsZ={alongX.AlongIsZ}，轴距 {alongX.WheelbaseM:0.00}");

        // Refusals. Each of these would otherwise hand the planner a confident wrong number.
        Report(ref failures, !VehicleGeometry.Measure(null, null, null, 0).Valid,
            "无数据直接判无效", "positions=null");

        Report(ref failures, !VehicleGeometry.Measure(
            new[] { V(-1.05f, 0.5f, 0f), V(1.05f, 0.5f, 0f),
                    V(-1.05f, 0.5f, 0.1f), V(1.05f, 0.5f, 0.1f) },
            new[] { true, true, false, false }, null, 4).Valid,
            "只有一根轴时拒绝", "四轮回同一位置，量不出轴距");

        Report(ref failures, !VehicleGeometry.Measure(
            new[] { V(float.NaN, 0.5f, 2f), V(1.05f, 0.5f, 2f),
                    V(-1.05f, 0.5f, -2f), V(1.05f, 0.5f, -2f) },
            new[] { true, true, false, false }, null, 4).Valid,
            "NaN 轮位拒绝", "第一项为 NaN");

        Report(ref failures, !VehicleGeometry.Measure(
            new[] { V(-1.05f, 0.5f, 2f), V(40f, 0.5f, 2f),
                    V(-1.05f, 0.5f, -2f), V(1.05f, 0.5f, -2f) },
            new[] { true, true, false, false }, null, 4).Valid,
            "离原点过远的轮拒绝", "第二项 X=40 m，不是本车车轮");

        Report(ref failures, !VehicleGeometry.Measure(
            new[] { V(-1.05f, 0.5f, 2f), V(1.05f, 0.5f, 2f),
                    V(-1.05f, 0.5f, -2f), V(1.05f, 0.5f, -2f) },
            new[] { false, false, false, false }, null, 4).Valid,
            "没有转向轴组时拒绝", "全部不可转向");

        // An array longer than the reported truck wheel count carries trailer wheels after it, and
        // including them would put the pivot somewhere down the trailer.
        WheelbaseMeasurement truncated = VehicleGeometry.Measure(
            new[]
            {
                V(-1.05, 0.5f, 2.0f), V(1.05, 0.5f, 2.0f),
                V(-1.05, 0.5f, -2.2f), V(1.05, 0.5f, -2.2f),
                V(-1.20, 0.5f, -9.0f), V(1.20, 0.5f, -9.0f)
            },
            new[] { true, true, false, false, false, false },
            new[] { false, false, false, false, false, false }, 4);

        Report(ref failures, truncated.Valid && Near(truncated.WheelbaseM, 4.2),
            "只取卡车自己的轮", $"count=4 时轴距 {truncated.WheelbaseM:0.00}（含拖车会是 5.50）");

        // The truck the ATS cone course was actually driven in, read out of Local\SCSTelemetry with
        // Tools\MemoryProbe --wheels (2026-10-16): three axles, outer wheel centres ±1.03 m at the
        // steered axle and ±0.91 m at the two rear axles. The obstacle envelope is built from the
        // width, and the hand-typed 2.6 m was 0.14 m wider than this tractor plus a full tyre - which
        // is most of the difference between "fits a 3.1 m bay" and "refused".
        WheelbaseMeasurement sixByTwoLive = VehicleGeometry.Measure(
            new[]
            {
                V(-1.03f, 0.51f, -2.86f), V(1.03f, 0.51f, -2.86f),
                V(-0.91f, 0.51f, 2.09f), V(0.91f, 0.51f, 2.09f),
                V(-0.91f, 0.51f, 3.40f), V(0.91f, 0.51f, 3.40f)
            },
            new[] { true, true, false, false, false, false },
            new[] { false, false, false, false, true, true }, 6);

        Report(ref failures, sixByTwoLive.Valid, "现场 6x2 量得出结果", sixByTwoLive.Reason);
        Report(ref failures, Near(VehicleGeometry.MeasuredWidthM(sixByTwoLive, 0.2), 2.46),
               "车宽取最外轮心 + 胎宽", $"{VehicleGeometry.MeasuredWidthM(sixByTwoLive, 0.2):0.00} m（判据 2.46 = 2×(1.03+0.20)）");
        Report(ref failures, Near(VehicleGeometry.MeasuredWidthM(sixByTwoLive, 0.05), 2.16),
               "胎宽余量按每侧计", $"{VehicleGeometry.MeasuredWidthM(sixByTwoLive, 0.05):0.00} m（判据 2.16，不是 2.21）");
        Report(ref failures, VehicleGeometry.MeasuredWidthM(WheelbaseMeasurement.Invalid("无数据"), 0.2) == 0.0,
               "量不出时返回 0，让设置值继续生效", "0 m");

        // The body box read out of game memory (§52) overwrites the box the planner sweeps, so a bad
        // read has to be refused rather than believed. 8.43 × 2.47 × 3.84 is the ATS course's 6x2.
        Report(ref failures, VehicleGeometry.PlausibleBodyBox(8.43, 2.47, 3.84),
               "现场 6x2 的包围盒算合理", "8.43 × 2.47 × 3.84 m");
        Report(ref failures, !VehicleGeometry.PlausibleBodyBox(0, 0, 0), "没读到就是没读到", "0³ → 拒绝");
        Report(ref failures, !VehicleGeometry.PlausibleBodyBox(0.3, 0.2, 0.1),
               "指针错位读出的小盒子被拒", "0.3 m → 拒绝（否则包络会缩到车外）");
        Report(ref failures, !VehicleGeometry.PlausibleBodyBox(8.43, 4.2, 3.84), "宽 4.2 m 超界被拒", "拒绝");
        Report(ref failures, !VehicleGeometry.PlausibleBodyBox(8.43, 2.47, 1.2), "高 1.2 m 超界被拒", "拒绝");
        Report(ref failures, !VehicleGeometry.PlausibleBodyBox(double.NaN, 2.47, 3.84), "NaN 被拒", "拒绝");

        return failures;
    }

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 0.01;

    // Vector3 only takes floats, and half of these literals want to be doubles, so build through a
    // helper rather than remembering an f suffix on twenty numbers.
    private static Vector3 V(double x, double y, double z) => new((float)x, (float)y, (float)z);

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
