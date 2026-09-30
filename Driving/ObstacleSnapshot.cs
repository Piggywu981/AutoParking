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
}
