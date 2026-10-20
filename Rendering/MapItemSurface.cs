using System;
using System.Linq;
using System.Numerics;
using TruckLib.ScsMap;

namespace AutoParking;

/// <summary>
///  What can be read off a map item with no game involved: its type name, the token that identifies
///  its model or scheme, whether the map format marks it collidable, and the ground geometry its
///  class exposes.
///
///  Kept free of host references on purpose, so <c>Tools\MapSectorDump</c> compiles this same file
///  against a raw <c>.scs</c>. The in-game inventory and the offline sector dump then cannot drift
///  apart on what a class is called or what counts as a shape - which is the whole value of the dump.
/// </summary>
public static class MapItemSurface
{
    /// <summary>
    ///  Type name, model/scheme token and collision flag. The named cases exist because the token
    ///  lives in a different property per class; everything else still has to be reported, since
    ///  counting the classes nobody thought to name is the point.
    /// </summary>
    public static bool Describe(IMapObject? item, out string kind, out string? token, out bool collision)
    {
        kind = "";
        token = null;
        collision = false;

        switch (item)
        {
            case Road road:
                kind = "Road";
                token = road.RoadType.ToString();
                return true;
            case Prefab prefab:
                kind = "Prefab";
                token = prefab.Model.ToString();
                collision = prefab.Collision;
                return true;
            case Buildings building:
                kind = "Buildings";
                token = building.Name.ToString();
                collision = building.Collision;
                return true;
            // Fully qualified: TruckLib.Models.Model (the PMD mesh) shares the short name.
            case TruckLib.ScsMap.Model model:
                kind = "Model";
                token = model.Name.ToString();
                collision = model.Collision;
                return true;
            case Sign sign:
                kind = "Sign";
                token = sign.Model.ToString();
                // Cones are Sign items (927 of 959 in ETS2, 7293 of 7514 in ATS), and the installed
                // TruckLib does publish Sign.Collision - not reading it drew every cone as a hollow
                // point and kept the whole family out of the ground-truth blockers.
                collision = sign.Collision;
                return true;
            // The class that repeats a model along a path: most of the shipped map's barriers, fences,
            // construction props and even depot buildings live here, and the identity is on the
            // subcurve, not on the item. Reporting nothing for Curve is what hid 108k barrier items.
            case Curve curve:
                kind = "Curve";
                token = curve.Subcurves.FirstOrDefault(s => s != null)?.Model.ToString() ?? "";
                collision = curve.Collision;
                return true;
            case Compound compound:
                kind = "Compound";
                collision = compound.Collision;
                return true;
            case MapItem other:
                kind = other.ItemType.ToString();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    ///  The item's own ground footprint, read out of the map's k-DOP.
    ///
    ///  `MapItem.Kdop` is an internal property, but it returns a `KdopItem` whose public base
    ///  `KdopBounds` exposes `Minimums[5]`/`Maximums[5]` - the same 25-DOP the game holds in memory.
    ///  Measured offline: a traffic cone (`sign.dlc_fr_219`) reads min [-394.3, 53.1, 1279.6, …] /
    ///  max [-393.7, 54.0, 1280.2, …], i.e. 0.6 m × 0.6 m on the ground and 0.9 m tall. So real extents
    ///  do not need a PMD parse per token after all - the sector file already carries them (doc 26
    ///  open-question ①/29 assumed otherwise).
    ///
    ///  For an item that spans a chain (Curve) the box is the box of the whole chain, so its short axis
    ///  is the chain's width and its long axis is its length; that is only meaningful per axis pair.
    /// </summary>
    public static bool TryGroundBox(MapItem item, out Vector2 min, out Vector2 max)
    {
        min = Vector2.Zero;
        max = Vector2.Zero;

        if (KdopProperty == null)
            return false;

        if (KdopProperty.GetValue(item) is not KdopBounds bounds || bounds.Minimums == null || bounds.Maximums == null
            || bounds.Minimums.Length < 3 || bounds.Maximums.Length < 3)
            return false;

        min = new Vector2(bounds.Minimums[0], bounds.Minimums[2]);
        max = new Vector2(bounds.Maximums[0], bounds.Maximums[2]);
        return max.X > min.X && max.Y > min.Y;
    }

    private static readonly System.Reflection.PropertyInfo? KdopProperty =
        typeof(MapItem).GetProperty("Kdop", System.Reflection.BindingFlags.NonPublic
                                            | System.Reflection.BindingFlags.Instance
                                            | System.Reflection.BindingFlags.Public);

    /// <summary>
    ///  Where the item sits on the ground. The item's own nodes beat the node the walk arrived
    ///  through, which is only a fallback for classes without public node access.
    /// </summary>
    public static Vector2 GroundPosition(IMapObject item, Node reachedVia)
    {
        return item switch
        {
            SingleNodeItem { Node: not null } single => Geometry.ToPlane(single.Node.Position),
            PolylineItem { Node: not null } polyline => Geometry.ToPlane(polyline.Node.Position),
            _ => Geometry.ToPlane(reachedVia.Position)
        };
    }

    /// <summary>
    ///  Ground geometry of one map item in the shape its class defines: a point, a segment, a chain,
    ///  a ring. A polyline's endpoints are the item's own two nodes - not whichever node the walk
    ///  arrived through, which would draw half a wall.
    /// </summary>
    public static bool TryShape(IMapObject item, out Vector2[]? points, out bool closed)
    {
        points = null;
        closed = false;

        switch (item)
        {
            case SingleNodeItem { Node: not null } single:
                points = new[] { Geometry.ToPlane(single.Node.Position) };
                closed = true;
                return true;
            case PolylineItem { Node: not null, ForwardNode: not null } line:
                points = new[] { Geometry.ToPlane(line.Node.Position), Geometry.ToPlane(line.ForwardNode.Position) };
                return true;
            case PathItem { Nodes: { Count: > 1 } } path:
                points = path.Nodes.Select(node => Geometry.ToPlane(node.Position)).ToArray();
                return true;
            case PolygonItem { Nodes: { Count: > 2 } } area:
                points = area.Nodes.Select(node => Geometry.ToPlane(node.Position)).ToArray();
                closed = true;
                return true;
            default:
                return false;
        }
    }
}
