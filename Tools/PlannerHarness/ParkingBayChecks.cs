using System.Linq;
using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for the parking-bay reader. It exists because the game's own bay markings arrived in the
///  memory list as two 8×8 m Movers, and §43's "block everything the classifier recognises" then
///  refused the exact bay the job asks for. Detection is geometry over cone positions, so it can be
///  asserted without the game - including against the coordinates read off the live course.
/// </summary>
internal static class ParkingBayChecks
{
    public static int Run()
    {
        int failures = 0;

        // The shape the game uses: two facing rows, capped at one end, one row running on past the
        // other end so traffic can enter. Coordinates read live off the ATS course on 2026-10-16.
        Vector2[] southRow =
        {
            At(1948.0, 2141.4), At(1948.7, 2140.7), At(1950.9, 2140.7), At(1953.1, 2140.7),
            At(1955.3, 2140.7), At(1957.8, 2140.7), At(1960.7, 2140.7), At(1963.3, 2140.7),
            At(1966.0, 2140.7), At(1968.7, 2140.7), At(1971.5, 2140.7), At(1974.3, 2140.7)
        };
        Vector2[] northRow =
        {
            At(1948.1, 2144.3), At(1948.7, 2144.9), At(1951.0, 2144.9), At(1953.1, 2144.9),
            At(1955.4, 2144.9), At(1957.4, 2144.9), At(1960.6, 2144.9), At(1963.5, 2144.9)
        };
        Vector2[] westCap = { At(1948.0, 2142.4), At(1948.0, 2143.3) };
        Vector2[] realCourse = Joined(southRow, northRow, westCap);

        List<ParkingBay> bays = ParkingBays.Detect(realCourse);
        Report(ref failures, bays.Count == 1, "真实现场只认出一个车位", $"{bays.Count} 个（判据 1）");
        if (bays.Count == 1)
        {
            ParkingBay bay = bays[0];
            Report(ref failures, Math.Abs(bay.Center.X - 1955.8) < 1.2 && Math.Abs(bay.Center.Y - 2142.8) < 0.3,
                   "车位中心落在库内", $"({bay.Center.X:0.0}, {bay.Center.Y:0.0}) 判据 ≈(1955.8, 2142.8)");
            Vector2 nose = Geometry.ForwardFromHeading(bay.HeadingRad);
            Report(ref failures, nose.X < -0.98f && Math.Abs(nose.Y) < 0.05f,
                   "朝向 = 从开口指向封口（车头朝西）", $"forward=({nose.X:0.000}, {nose.Y:0.000})");
            Report(ref failures, bay.InteriorWidthM is > 2.5 and < 4.0,
                   "内部净宽与实测 3.4 m 一致", $"{bay.InteriorWidthM:0.00} m");

            // The bay's own markings sit on its centreline; the cone rows that form the walls do not.
            Report(ref failures, bay.Contains(At(1953.1, 2142.8), -0.6),
                   "内缩后库中心算内部", "标记物 (1953.1, 2142.8) → 内部（不当障碍）");
            Report(ref failures, !bay.Contains(At(1955.3, 2140.7), -0.6),
                   "边界锥桶仍是障碍", "南排 (1955.3, 2140.7) → 外部");

            bool snapped = ParkingBays.TrySnap(bays, At(1953.4, 2142.8), 6.0, out Pose2 pose);
            Report(ref failures, snapped && Math.Abs(pose.X - bay.Center.X) < 1e-3
                     && Math.Abs(pose.Z - bay.Center.Y) < 1e-3
                     && Math.Abs(pose.HeadingRad - bay.HeadingRad) < 1e-6,
                   "库里点选 → 吸附到库中心与库朝向", $"({pose.X:0.0}, {pose.Z:0.0}) {pose.YawDegrees:0.0}°");
            Report(ref failures, !ParkingBays.TrySnap(bays, At(1998.9, 2161.9), 6.0, out _),
                   "远处点选不误吸", "车当前位置 (1998.9, 2161.9) → 不吸附");
        }

        // A driving lane is two rows open at both ends: not a bay.
        Vector2[] lane = Joined(
            new[] { At(1975.5, 2159.0), At(1980.0, 2159.0), At(1985.0, 2159.0),
                    At(1990.0, 2159.0), At(1995.0, 2159.0), At(2000.0, 2159.0) },
            new[] { At(1981.9, 2164.8), At(1986.0, 2164.8), At(1991.0, 2164.8),
                    At(1996.0, 2164.8), At(2001.0, 2164.8), At(2006.0, 2164.8) });
        Report(ref failures, ParkingBays.Detect(lane).Count == 0, "两端都开口的是车道不是车位", "0 个");

        // Two equal rows capped at both ends: a yard, not a bay.
        Vector2[] yard = Joined(
            new[] { At(1900.0, 2100.0), At(1905.0, 2100.0), At(1910.0, 2100.0), At(1915.0, 2100.0) },
            new[] { At(1900.0, 2104.0), At(1905.0, 2104.0), At(1910.0, 2104.0), At(1915.0, 2104.0) });
        Report(ref failures, ParkingBays.Detect(yard).Count == 0, "两端都封口的是院子不是车位", "0 个");

        Vector2[] tooWide = Joined(
            new[] { At(1900.0, 2100.0), At(1905.0, 2100.0), At(1910.0, 2100.0) },
            new[] { At(1900.0, 2112.0), At(1905.0, 2112.0), At(1910.0, 2112.0) });
        Report(ref failures, ParkingBays.Detect(tooWide).Count == 0, "排间距 12 m 不算车位", "0 个");

        Report(ref failures, ParkingBays.Detect(new[] { At(1900.0, 2100.0), At(1902.0, 2100.0) }).Count == 0,
               "两个锥桶不成排", "0 个");
        Report(ref failures, ParkingBays.Detect(Array.Empty<Vector2>()).Count == 0, "空输入不崩", "0 个");

        failures += FarRowDoesNotPolluteNearBay();

        return failures;
    }

    /// <summary>
    ///  §54's miss, reproduced with the measured numbers: the sampled radius reaches a distant cone run
    ///  that shares the lateral coordinates of the bay's own rows, so the band used to become one 58 m
    ///  row, the overlap came out longer than MaxBayLengthM, and the near bay was reported as nothing.
    ///  Rows are now segmented along the axis, so the 42 m empty gap ends a row. Each run keeps one wall
    ///  a cone longer than the other: two equal rows are capped at both ends, which is a yard (asserted
    ///  above) and no amount of row splitting turns a yard into a bay.
    /// </summary>
    private static int FarRowDoesNotPolluteNearBay()
    {
        int failures = 0;

        List<Vector2> cones = new();
        // 近处真库位：两条 X 向排，z=103.7 跨 10→18，z=100 跨 8→18（口朝 −X）
        for (int i = 0; i < 5; i++) cones.Add(At(10 + i * 2, 103.7));
        for (int i = 0; i < 6; i++) cones.Add(At(8 + i * 2, 100));
        // 远排：同 z=100 与 z=103.7，x=60→68 / 60→70（42 m 空档，两条带上都是 42）
        for (int i = 0; i < 5; i++) cones.Add(At(60 + i * 2, 103.7));
        for (int i = 0; i < 6; i++) cones.Add(At(60 + i * 2, 100));

        List<ParkingBay> bays = ParkingBays.Detect(cones);
        bool near = bays.Exists(b => Math.Abs(b.Center.X - 14) < 0.5 && Math.Abs(b.Center.Y - 101.85) < 0.5);
        bool distant = bays.Exists(b => Math.Abs(b.Center.X - 64) < 0.5 && Math.Abs(b.Center.Y - 101.85) < 0.5);

        Report(ref failures, near && distant, "42 m 空档断排，近处库位仍可检出",
               $"{bays.Count} 个（判据 2）· 近排 (14, 101.85) {(near ? "检出" : "漏判")} · " +
               $"远排 (64, 101.85) {(distant ? "检出" : "漏判")}");

        return failures;
    }

    private static Vector2 At(double x, double y) => new((float)x, (float)y);

    private static Vector2[] Joined(params Vector2[][] rows) => rows.SelectMany(r => r).ToArray();

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
