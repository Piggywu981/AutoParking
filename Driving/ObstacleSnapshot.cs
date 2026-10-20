using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  Everything that can block the maneuver, as ground-plane polygons in world XZ meters.
///  Pure data so the planner and its self-test can run without the game.
/// </summary>
public sealed class ObstacleSnapshot
{
    public readonly List<Vector2[]> Polygons = new();

    public DateTime BuiltUtc = DateTime.UtcNow;

    public int Count => Polygons.Count;

    /// <summary>
    ///  Adds one axis-aligned box as an obstacle, from its center and side lengths. This is the
    ///  shape a kdop_item already is (min/max per axis), so memory items (§41) go in without any
    ///  geometry work. A degenerate side is raised to a floor so a zero-width reading still
    ///  blocks what stands there.
    /// </summary>
    public void AddBox(Vector2 center, float dx, float dz)
    {
        float hx = MathF.Max(dx, 0.3f) * 0.5f;
        float hz = MathF.Max(dz, 0.3f) * 0.5f;
        Polygons.Add(new[]
        {
            new Vector2(center.X - hx, center.Y - hz), new Vector2(center.X + hx, center.Y - hz),
            new Vector2(center.X + hx, center.Y + hz), new Vector2(center.X - hx, center.Y + hz),
        });
    }
}
