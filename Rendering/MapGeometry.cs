using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using ETS2LA.Game;
using ETS2LA.Game.Data;
using ETS2LA.Game.PpdFiles;
using ETS2LA.Game.Utils;
using TruckLib;
using TruckLib.HashFs;
using TruckLib.Models.Ppd;
using TruckLib.ScsMap;

namespace AutoParking;

/// <summary>
///  Ground-plane polylines around one center point, ready for drawing and for
///  snapping a chosen spot to nearby navigation curves.
/// </summary>
public sealed class MapGeometry
{
    public Vector2 Center;
    public double RadiusM;
    public DateTime BuiltUtc = DateTime.UtcNow;
    public double BuildMilliseconds;
    public string Status = "尚未构建";

    /// <summary>
    ///  Lane centerlines. `Left` is the lane's side on the road item, used to color
    ///  opposing directions differently so the map reads like the game map.
    /// </summary>
    public readonly List<LaneLine> RoadLanes = new();

    public readonly record struct LaneLine(Vector2[] Points, bool Left);

    /// <summary>Prefab navigation curves, drawn as thin lines.</summary>
    public readonly List<Vector2[]> DriveableCurves = new();

    /// <summary>
    ///  Ground geometry of every map item class that has any, one entry per item: a point for
    ///  single-node items, a segment for polyline items, a chain for path items, a closed ring for
    ///  polygon items. <see cref="StaticShape.Kind"/> is the map item type, so the renderer can tell
    ///  a wall apart from a street lamp without a second lookup.
    /// </summary>
    public readonly List<StaticShape> StaticShapes = new();

    public readonly record struct StaticShape(Vector2[] Points, string Kind, string Token, bool Collision, bool Closed);

    /// <summary>
    ///  Convex hull of a prefab's control nodes. The map publishes no footprint for a prefab, so this
    ///  is an over-approximation drawn for orientation only - and it is deliberately not an obstacle,
    ///  because the parking spot usually sits inside the very prefab being outlined.
    /// </summary>
    public readonly List<PrefabOutline> PrefabOutlines = new();

    public readonly record struct PrefabOutline(Vector2[] Ring, Vector2 Center, string Token, int NodeCount, double AreaM2);

    /// <summary>True when a cap below cut collection short, so the picture is not the whole sample.</summary>
    public bool StaticTruncated;

    public ObstacleSnapshot Obstacles = new();

    /// <summary>
    ///  What the same node walk found but nothing here draws or plans with yet: buildings, signs,
    ///  models, POI areas. Read-only measurement, see <see cref="MapItemProbe"/>.
    /// </summary>
    public MapItemProbe Probe = new();

    /// <summary>
    ///  Which ground inside this sample the map itself vouches for, and which it does not. Built from the
    ///  same pass that fills the drawing lists, so the picture on screen and the number in the plan cost can
    ///  never come from different data. Null when the build failed.
    /// </summary>
    public GroundTrust? Trust;

    public bool IsFresh(TimeSpan maxAge) => DateTime.UtcNow - BuiltUtc <= maxAge;

    /// <summary>
    ///  Snap a free click to the nearest driveable curve within `maxDistanceM`, taking that
    ///  curve's tangent as the spot heading. The tangent direction closest to
    ///  `referenceHeadingRad` wins so the spot does not flip 180 degrees at random.
    /// </summary>
    public bool TrySnapToCurve(Vector2 point, double maxDistanceM, double referenceHeadingRad, out Pose2 snapped)
    {
        snapped = default;
        double bestDistance = maxDistanceM;
        bool found = false;

        for (int c = 0; c < DriveableCurves.Count; c++)
        {
            Vector2[] line = DriveableCurves[c];
            for (int i = 0; i + 1 < line.Length; i++)
            {
                Vector2 closest = Geometry.ClosestPointOnSegment(line[i], line[i + 1], point);
                double distance = Geometry.Distance(closest, point);
                if (distance >= bestDistance)
                    continue;

                Vector2 tangent = line[i + 1] - line[i];
                if (tangent.X == 0f && tangent.Y == 0f)
                    continue;

                double heading = Geometry.HeadingFromForward(tangent);
                if (Math.Abs(Geometry.SmallestAngleDifference(heading, referenceHeadingRad)) > PiOverTwo)
                    heading = Geometry.NormalizeRadians(heading + Math.PI);

                bestDistance = distance;
                snapped = new Pose2(closest.X, closest.Y, heading);
                found = true;
            }
        }

        return found;
    }

    private const double PiOverTwo = Math.PI / 2.0;
}

/// <summary>
///  Builds MapGeometry from ETS2LA's parsed map data. Only ever called from the plugin
///  tick thread; the ImGui and Blazor threads read the finished snapshot.
/// </summary>
public sealed class MapGeometryBuilder
{
    private const int MaxRoadPointsPerLane = 240;
    private const int MaxCurvePoints = 120;
    private const int ParsedRoadCacheLimit = 4000;

    // Display caps only, and deliberately not planning caps: near a city a 120 m circle holds
    // thousands of loose models, and the sample is rebuilt twice a second on the tick thread.
    // When a cap bites, MapGeometry.StaticTruncated says so instead of quietly under-drawing.
    private const int MaxStaticShapes = 4000;
    private const int MaxPrefabOutlines = 400;

    // Roads and terrain already have a better rendering (lane centerlines), and a compound's own node
    // sits on top of the children that are now drawn individually.
    private static readonly HashSet<string> SkippedShapeKinds = new(StringComparer.Ordinal)
    {
        "Road", "Terrain", "Compound"
    };

    private readonly Dictionary<Road, ParsedRoad> parsedRoads = new();
    private readonly HashSet<string> prefabModelsWithoutDescriptor = new();
    private object? boundFileSystem;

    public MapData? FindMapData(out string status)
    {
        try
        {
            List<Installation>? installations = GameHandler.Current.Installations;
            if (installations == null || installations.Count == 0)
            {
                status = "未发现游戏安装";
                return null;
            }

            for (int i = 0; i < installations.Count; i++)
            {
                Installation installation = installations[i];
                if (!installation.IsParsed)
                    continue;

                MapData? map = installation.GetMapData();
                if (map == null)
                    continue;

                var fileSystem = installation.GetFileSystem();
                if (fileSystem != null && !ReferenceEquals(fileSystem, boundFileSystem))
                {
                    PpdFileHandler.Current.SetFileSystem(fileSystem);
                    boundFileSystem = fileSystem;
                }

                status = $"地图已就绪（{installation.Version}）";
                return map;
            }

            status = "地图解析中，平面地图要等解析完才有内容";
            return null;
        }
        catch (Exception ex)
        {
            status = $"读取地图数据失败：{ex.GetType().Name}";
            return null;
        }
    }

    public MapGeometry Build(MapData map, Vector2 center, double radiusM, double corridorHalfWidthM)
    {
        Stopwatch sw = Stopwatch.StartNew();
        MapGeometry geometry = new()
        {
            Center = center,
            RadiusM = radiusM,
            BuiltUtc = DateTime.UtcNow
        };

        try
        {
            IReadOnlyList<Node> nodes = map.Nodes.Within(center.X - radiusM, center.Y - radiusM,
                                                         center.X + radiusM, center.Y + radiusM);

            MapItemProbe probe = new()
            {
                Center = center,
                RadiusM = radiusM,
                NodesScanned = nodes.Count
            };

            // Attached before any of the drawing work runs: if one odd road throws later, the
            // inventory of what was already walked is still the thing we came here to read.
            geometry.Probe = probe;

            HashSet<Road> roads = new();
            HashSet<Prefab> prefabs = new();
            HashSet<ulong> staticTaken = new();
            foreach (Node node in nodes)
            {
                Collect(node.ForwardItem, roads, prefabs);
                Collect(node.BackwardItem, roads, prefabs);
                Visit(node.ForwardItem, node, geometry, probe, staticTaken, false);
                Visit(node.BackwardItem, node, geometry, probe, staticTaken, false);
            }

            foreach (Road road in roads)
            {
                AddRoad(road, geometry);
            }

            foreach (Prefab prefab in prefabs)
            {
                AddPrefab(prefab, geometry);
                AddPrefabOutline(prefab, geometry);
            }

            geometry.Obstacles = ObstacleScanner.Scan(center, radiusM);

            // Same walk, same lists: the corridors drawn as navigation curves are what vouches for the
            // ground, and the items the map flags collidable take that voucher back. Read-only for now —
            // the numbers it produces decide M7c's weights (design doc §34).
            GroundTrust trust = new()
            {
                Center = center,
                RadiusM = radiusM,
                CorridorHalfWidthM = corridorHalfWidthM
            };

            foreach (Vector2[] curve in geometry.DriveableCurves)
                trust.AddCorridor(curve);

            foreach (MapGeometry.LaneLine lane in geometry.RoadLanes)
                trust.AddCorridor(lane.Points);

            foreach (MapGeometry.StaticShape shape in geometry.StaticShapes)
            {
                if (shape.Collision)
                    trust.AddBlocker(shape.Points, shape.Closed);
            }

            geometry.Trust = trust;

            geometry.Status = $"道路 {roads.Count} 段 / 场站 {prefabs.Count} 个 / 障碍 {geometry.Obstacles.Count}" +
                              $" / 静态 {geometry.StaticShapes.Count} 项 / 场站轮廓 {geometry.PrefabOutlines.Count}" +
                              (geometry.StaticTruncated ? "（静态内容已截断）" : "") +
                              $" · 可信地面 {trust.ConfirmedCells} m²" +
                              (trust.Truncated ? "（栅格已截断）" : "");
        }
        catch (Exception ex)
        {
            geometry.Status = $"构建失败：{ex.GetType().Name}: {ex.Message}";
        }

        sw.Stop();
        geometry.BuildMilliseconds = sw.Elapsed.TotalMilliseconds;
        return geometry;
    }

    private static void Collect(IMapObject? item, HashSet<Road> roads, HashSet<Prefab> prefabs)
    {
        switch (item)
        {
            case Road road:
                roads.Add(road);
                break;
            case Prefab prefab:
                prefabs.Add(prefab);
                break;
        }
    }

    /// <summary>
    ///  Counts every parsed item in the whole map by type, ignoring distance. This is the census
    ///  that answers "does the data even contain loose models here?" as opposed to "did our node
    ///  walk reach them" - the two need different fixes and only one of them is a code bug.
    ///  One pass over a dictionary that can hold millions of entries: call it off the tick thread.
    /// </summary>
    public static Dictionary<string, int> Census(MapData map)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (MapItem item in map.MapItems.Values)
        {
            string type = item.ItemType.ToString();
            counts.TryGetValue(type, out int seen);
            counts[type] = seen + 1;
        }

        return counts;
    }

    /// <summary>
    ///  Counts one map item and pulls whatever is drawable out of it, then steps inside compounds.
    /// </summary>
    private static void Visit(IMapObject? item, Node reachedVia, MapGeometry geometry, MapItemProbe probe,
                              HashSet<ulong> staticTaken, bool nested)
    {
        if (!MapItemSurface.Describe(item, out string itemType, out string? token, out bool collision))
            return;

        probe.Add(item!.Uid, itemType, token, collision, MapItemSurface.GroundPosition(item, reachedVia), nested);
        CollectShape(item, itemType, token, collision, geometry, staticTaken);

        // A compound keeps its children in its own dictionaries - CompoundSerializer fills
        // comp.MapItems/comp.Nodes and never the map's - so a walk that stops at the compound reports
        // a stack of crates as a single item. Bundled props (boxes, pallets, junction boxes) live
        // exactly there. One level only: compounds inside compounds are not something the format has.
        if (item is Compound { MapItems: not null } compound)
        {
            foreach (MapItem child in compound.MapItems.Values)
            {
                if (child is not Compound)
                    Visit(child, reachedVia, geometry, probe, staticTaken, true);
            }
        }
    }

    /// <summary>
    ///  Pulls the drawable ground geometry out of the item classes the probe only counts. Every one
    ///  of these is reached twice (once from each end node), hence the UID set. Nothing collected
    ///  here feeds the planner yet - 建筑线段和点位现在只是画出来给人看的.
    /// </summary>
    private static void CollectShape(IMapObject item, string kind, string? token, bool collision,
                                     MapGeometry geometry, HashSet<ulong> taken)
    {
        if (SkippedShapeKinds.Contains(kind) || !taken.Add(item.Uid))
            return;

        if (!MapItemSurface.TryShape(item, out Vector2[]? points, out bool closed) || points == null)
            return;

        if (geometry.StaticShapes.Count >= MaxStaticShapes)
        {
            geometry.StaticTruncated = true;
            return;
        }

        geometry.StaticShapes.Add(new MapGeometry.StaticShape(points, kind, token ?? "", collision, closed));
    }

    /// <summary>
    ///  A prefab's footprint, approximated as the convex hull of its control nodes: those are the only
    ///  ground positions the map publishes for it. Over-approximated on purpose and never an obstacle -
    ///  the spot being parked in normally sits inside the prefab, so blocking on this would block the
    ///  maneuver itself.
    /// </summary>
    private static void AddPrefabOutline(Prefab prefab, MapGeometry geometry)
    {
        if (geometry.PrefabOutlines.Count >= MaxPrefabOutlines)
        {
            geometry.StaticTruncated = true;
            return;
        }

        List<Vector2> nodes = new();
        foreach (INode node in prefab.Nodes)
        {
            if (node != null)
                nodes.Add(Geometry.ToPlane(node.Position));
        }

        if (nodes.Count == 0)
            return;

        Vector2[] points = nodes.ToArray();
        Vector2 center = new(points.Average(point => point.X), points.Average(point => point.Y));
        Vector2[] ring = Geometry.ConvexHull(points);

        geometry.PrefabOutlines.Add(new MapGeometry.PrefabOutline(ring, center, prefab.Model.ToString(),
                                                                  points.Length, Geometry.PolygonArea(ring)));
    }

    private void AddRoad(Road road, MapGeometry geometry)
    {
        if (road.Length <= 0f)
            return;

        try
        {
            ParsedRoad parsed = GetParsedRoad(road);
            float resolution = Math.Max(RoadUtils.GetRoadResolution(road), 2.0f);
            int steps = (int)Math.Clamp(road.Length / resolution, 4, MaxRoadPointsPerLane);

            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                Side side = sideIndex == 0 ? Side.Left : Side.Right;
                int laneCount = parsed.GetLaneCount(side);

                for (int lane = 0; lane < laneCount; lane++)
                {
                    List<Vector2> points = new(steps + 1);
                    for (int i = 0; i <= steps; i++)
                    {
                        float t = i / (float)steps;
                        points.Add(Geometry.ToPlane(parsed.InterpolateLane(t, side, lane).Position));
                    }

                    if (points.Count > 1)
                        geometry.RoadLanes.Add(new MapGeometry.LaneLine(points.ToArray(), side == Side.Left));
                }
            }
        }
        catch
        {
            // Odd road data (zero-length lanes, missing curves) - skip just this road.
        }
    }

    private ParsedRoad GetParsedRoad(Road road)
    {
        if (parsedRoads.TryGetValue(road, out ParsedRoad? cached) && cached != null)
            return cached;

        if (parsedRoads.Count >= ParsedRoadCacheLimit)
            parsedRoads.Clear();

        ParsedRoad parsed = new(road);
        parsedRoads[road] = parsed;
        return parsed;
    }

    private void AddPrefab(Prefab prefab, MapGeometry geometry)
    {
        string model = prefab.Model.ToString();
        if (prefabModelsWithoutDescriptor.Contains(model))
            return;

        // Map files only keep the model token; the PPD handler resolves it to a real descriptor.
        PrefabDescriptor? descriptor = PpdFileHandler.Current.GetPpdFile(model) as PrefabDescriptor;
        if (descriptor == null)
        {
            prefabModelsWithoutDescriptor.Add(model);
            return;
        }

        try
        {
            if (descriptor.Nodes.Count == 0 || descriptor.NavCurves.Count == 0 || prefab.Nodes.Count == 0)
            {
                prefabModelsWithoutDescriptor.Add(model);
                return;
            }

            int origin = Math.Clamp(prefab.Origin, 0, descriptor.Nodes.Count - 1);
            Vector3 prefabStart = prefab.Nodes[0].Position - descriptor.Nodes[origin].Position;
            Vector3 prefabRotation = prefab.Nodes[0].Rotation.ToEuler()
                                   - MathEx.GetNodeRotation(descriptor.Nodes[origin].Direction).ToEuler();
            Matrix4x4 rotation = Matrix4x4.CreateRotationY(prefabRotation.Y, prefab.Nodes[0].Position);

            foreach (NavCurve curve in descriptor.NavCurves)
            {
                if (curve.Length <= 0.1f)
                    continue;

                int steps = (int)Math.Clamp(curve.Length / 1.5f, 2, MaxCurvePoints);
                List<Vector2> points = new(steps + 1);

                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    Vector3 local = PrefabUtils.InterpolateNavCurve(curve, t);
                    Vector3 world = Vector3.Transform(local + prefabStart, rotation);
                    points.Add(Geometry.ToPlane(world));
                }

                geometry.DriveableCurves.Add(points.ToArray());
            }
        }
        catch
        {
            prefabModelsWithoutDescriptor.Add(model);
        }
    }
}
