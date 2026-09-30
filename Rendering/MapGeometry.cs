using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    public ObstacleSnapshot Obstacles = new();

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

    public MapGeometry Build(MapData map, Vector2 center, double radiusM)
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

            HashSet<Road> roads = new();
            HashSet<Prefab> prefabs = new();
            foreach (Node node in nodes)
            {
                Collect(node.ForwardItem, roads, prefabs);
                Collect(node.BackwardItem, roads, prefabs);
            }

            foreach (Road road in roads)
            {
                AddRoad(road, geometry);
            }

            foreach (Prefab prefab in prefabs)
            {
                AddPrefab(prefab, geometry);
            }

            geometry.Obstacles = ObstacleScanner.Scan(center, radiusM);
            geometry.Status = $"道路 {roads.Count} 段 / 建筑 {prefabs.Count} 个 / 障碍 {geometry.Obstacles.Count}";
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
