using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for the point-in-polygon primitive. It exists because the footprint and trust rasterizers
///  ask "is this 1 m cell's centre inside that ring" hundreds of times, and the SAT overlap test in
///  Geometry would build two polygons and four axis projections to answer it.
/// </summary>
internal static class GeometryPointChecks
{
    public static int Run()
    {
        int failures = 0;

        Vector2[] square =
        {
            new(-3f, -2f), new(3f, -2f), new(3f, 2f), new(-3f, 2f)
        };

        Report(ref failures, Geometry.PointInPolygon(square, new Vector2(0, 0)), "方框内的点", "(0,0) 在 ±3×±2 方框内");
        Report(ref failures, !Geometry.PointInPolygon(square, new Vector2(4, 0)), "方框外的点", "(4,0) 在框外");
        Report(ref failures, !Geometry.PointInPolygon(square, new Vector2(0, 3)), "方框外的点（纵向）", "(0,3) 在框外");

        // A clockwise ring must read the same as a counter-clockwise one: the map's own rings come in
        // whichever order the format wrote them.
        Vector2[] flipped = new[] { square[3], square[2], square[1], square[0] };
        bool same = Geometry.PointInPolygon(flipped, new Vector2(0, 0))
                    && !Geometry.PointInPolygon(flipped, new Vector2(4, 0));
        Report(ref failures, same, "顶点顺序无关", "反向环绕的同一方框读数一致");

        // Concave: an L-shaped ring, so a bbox test alone would be wrong.
        Vector2[] lShape =
        {
            new(0f, 0f), new(6f, 0f), new(6f, 2f), new(2f, 2f), new(2f, 6f), new(0f, 6f)
        };
        Report(ref failures, Geometry.PointInPolygon(lShape, new Vector2(1, 5)), "凹形内部（竖臂）", "(1,5) 在 L 的竖臂里");
        Report(ref failures, Geometry.PointInPolygon(lShape, new Vector2(5, 1)), "凹形内部（横臂）", "(5,1) 在 L 的横臂里");
        Report(ref failures, !Geometry.PointInPolygon(lShape, new Vector2(5, 5)), "凹形缺口不算内部", "(5,5) 在 L 的缺口里");

        // Degenerate input must answer "no" rather than throw or divide by zero: the map hands over
        // two-point segments and single nodes routinely.
        Report(ref failures, !Geometry.PointInPolygon(new[] { new Vector2(1, 1) }, new Vector2(1, 1)),
            "单点不是多边形", "1 个顶点 → false");
        Report(ref failures, !Geometry.PointInPolygon(new Vector2[0], new Vector2(0, 0)),
            "空输入不是多边形", "0 个顶点 → false");

        return failures;
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
