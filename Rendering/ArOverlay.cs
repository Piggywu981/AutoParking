using System;
using System.Collections.Generic;
using System.Numerics;
using ETS2LA.Overlay;
using ETS2LA.Overlay.AR;

namespace AutoParking;

/// <summary>
///  Shows the chosen spot in the in-game AR overlay: its footprint, its heading, the
///  straight-line preview until the real planner lands in M2, and a distance label.
/// </summary>
internal sealed class ArOverlay
{
    public const string CallbackName = "local.autoparking.ar";

    /// <summary>Distance between route points handed to the AR ribbon. The path is sampled at
    ///  PathSampleM (0.25 m) for planning; drawing every one of them is what made the AR too heavy.</summary>
    private const double DrawStepM = 1.5;

    // AR drawing takes ARGB; the overlay converts to ImGui's ABGR internally.
    private static uint Argb(float r, float g, float b, float a = 1f)
    {
        uint ri = (uint)(Math.Clamp(r, 0f, 1f) * 255f + 0.5f);
        uint gi = (uint)(Math.Clamp(g, 0f, 1f) * 255f + 0.5f);
        uint bi = (uint)(Math.Clamp(b, 0f, 1f) * 255f + 0.5f);
        uint ai = (uint)(Math.Clamp(a, 0f, 1f) * 255f + 0.5f);
        return (ri << 24) | (gi << 16) | (bi << 8) | ai;
    }

    private static readonly uint TargetColor = Argb(0.35f, 0.90f, 0.95f);
    private static readonly uint PreviewColor = Argb(0.95f, 0.85f, 0.30f, 0.85f);
    private static readonly uint PoleColor = Argb(0.95f, 0.55f, 0.20f);
    private static readonly uint TruckMarkColor = Argb(0.35f, 0.95f, 0.45f);

    // Route preview, same hue pair as the flat map so a cyan leg on the ground and a cyan leg on the map
    // are obviously the same leg. The ribbon is these colors at low alpha; the centerline is solid.
    private static readonly uint RouteForwardColor = Argb(0.35f, 0.85f, 0.95f, 0.55f);
    private static readonly uint RouteReverseColor = Argb(0.98f, 0.62f, 0.20f, 0.55f);

    // Magenta, filled: nothing else in the AR palette is close to it, and it has to hold against asphalt,
    // concrete and snow, where a white marker washed out in the real depot.
    private static readonly uint GearChangeColor = Argb(1.0f, 0.20f, 0.85f, 1.0f);

    private readonly AutoParkingPlugin plugin;
    private bool registered;

    public ArOverlay(AutoParkingPlugin plugin)
    {
        this.plugin = plugin;
    }

    public void Register()
    {
        if (registered)
            return;

        ARRenderer? renderer = OverlayHandler.Current.AR;
        if (renderer == null)
            return;

        renderer.RegisterRenderCallback(new ARRenderCallback
        {
            Definition = new ARRendererDefinition
            {
                Name = CallbackName,
                Alpha = 1.0f
            },
            Render3D = Render
        });

        registered = true;
    }

    public void Unregister()
    {
        if (!registered)
            return;

        OverlayHandler.Current.AR?.UnregisterRenderCallback(CallbackName);
        registered = false;
    }

    /// <summary>
    ///  The AR context may not exist yet right after enabling, so this is retried from the
    ///  plugin tick until it succeeds. Returns whether the callback is now live.
    /// </summary>
    public bool EnsureRegistered()
    {
        if (!registered)
            Register();

        return registered;
    }

    private void Render()
    {
        ARRenderer? ar = OverlayHandler.Current.AR;
        if (ar == null)
            return;

        AutoParkingSettings settings = plugin.Settings;
        Pose2 truck = plugin.CurrentPose;
        double groundY = plugin.GroundY;

        // Always drawn so the ground plane can be checked without a selected spot.
        DrawGroundMark(ar, truck.Position, groundY, TruckMarkColor, 1.2f);

        PlanResult? preview = plugin.PlanSnapshot;
        if (preview?.Path != null)
        {
            DrawRoutePreview(ar, preview.Path, settings, groundY);
        }

        Pose2? target = plugin.TargetPose;
        if (target == null)
            return;

        DrawFootprint(ar, target.Value, settings.VehicleLengthM, settings.VehicleWidthM, groundY, TargetColor);
        DrawHeadingArrow(ar, target.Value, groundY, TargetColor);
        DrawGroundMark(ar, target.Value.Position, groundY, TargetColor, 0.6f);
        DrawPoleAndLabel(ar, target.Value, truck, groundY, settings);
        DrawPreviewPath(ar, truck, target.Value, groundY);
    }

    /// <summary>
    ///  A small cross lying flat on the ground plane. If this floats or sinks, the vertical
    ///  reference is wrong and the AR ground trim needs adjusting.
    /// </summary>
    private static void DrawGroundMark(ARRenderer ar, Vector2 position, double groundY, uint color, float armLength)
    {
        Vector2 forward = Geometry.ForwardFromHeading(0.0);
        ARCoordinate center = new(new Vector3((float)position.X, (float)groundY + 0.03f, (float)position.Y));
        ARCoordinate north = new(new Vector3((float)(position.X + forward.X * armLength), (float)groundY + 0.03f, (float)(position.Y + forward.Y * armLength)));
        ARCoordinate east = new(new Vector3((float)(position.X - forward.Y * armLength), (float)groundY + 0.03f, (float)(position.Y + forward.X * armLength)));

        ar.Draw3DLine(north, center, color, 2f);
        ar.Draw3DLine(center, east, color, 2f);
    }

    private static void DrawFootprint(ARRenderer ar, Pose2 pose, double length, double width, double groundY, uint color)
    {
        Vector2[] corners = Geometry.RectangleCorners(pose, length, width);
        ARCoordinate[] points = new ARCoordinate[corners.Length];

        for (int i = 0; i < corners.Length; i++)
        {
            points[i] = new ARCoordinate(new Vector3(corners[i].X, (float)groundY + 0.05f, corners[i].Y));
        }

        ar.Draw3DPolygon(points, color, false, 2.0f);
    }

    private static void DrawHeadingArrow(ARRenderer ar, Pose2 pose, double groundY, uint color)
    {
        Vector2 forward = pose.Forward;
        Vector2 nose = pose.Position + forward * 4f;

        ARCoordinate from = new(new Vector3((float)pose.Position.X, (float)groundY + 0.05f, (float)pose.Position.Y));
        ARCoordinate to = new(new Vector3(nose.X, (float)groundY + 0.05f, nose.Y));
        ar.Draw3DLine(from, to, color, 2.5f);

        Vector2 left = pose.Position + new Vector2(-forward.Y, forward.X) * 1.2f;
        Vector2 right = pose.Position + new Vector2(forward.Y, -forward.X) * 1.2f;
        ar.Draw3DLine(new ARCoordinate(new Vector3(left.X, (float)groundY + 0.05f, left.Y)), to, color, 2.0f);
        ar.Draw3DLine(new ARCoordinate(new Vector3(right.X, (float)groundY + 0.05f, right.Y)), to, color, 2.0f);
    }

    private static void DrawPoleAndLabel(ARRenderer ar, Pose2 target, Pose2 truck, double groundY, AutoParkingSettings settings)
    {
        ARCoordinate basePoint = new(new Vector3((float)target.X, (float)groundY, (float)target.Z));
        ARCoordinate topPoint = new(new Vector3((float)target.X, (float)groundY + 2.2f, (float)target.Z));
        ar.Draw3DLine(basePoint, topPoint, PoleColor, 2.5f);
        ar.Draw3DCircle(topPoint, 0.35f, PoleColor, true, 1.5f);

        double distance = Geometry.Distance(truck.Position, target.Position);
        double headingErrorDegrees = Math.Abs(Geometry.SmallestAngleDifference(truck.HeadingRad, target.HeadingRad)) * 180.0 / Math.PI;

        ar.Draw3DText(topPoint, $"{distance:0.0} m   {headingErrorDegrees:0}°", TargetColor);
    }

    /// <summary>
    ///  The route the planner chose, drawn on the ground before it is driven: the corridor the vehicle
    ///  will occupy, its centerline, and where it changes gear. The map window has shown this since M1;
    ///  the point of putting it in the game view is that this is where it can be judged against the
    ///  depot actually in front of the truck, which is the one thing the flat map cannot do.
    ///
    ///  One batched ribbon call per gear run, not a polygon per pair of envelopes: filling every segment
    ///  was reported too heavy on a real truck. Lane assist's shape - left and right edge point lists -
    ///  is the cheap one, because the host batches it, clips it to the render distance and fades it by
    ///  distance for us.
    /// </summary>
    private static void DrawRoutePreview(ARRenderer ar, ParkingPath path, AutoParkingSettings settings, double groundY)
    {
        IReadOnlyList<PathPoint> points = path.Points;
        if (points.Count < 2)
            return;

        // The same envelope the collision test and the route-cost numbers use, so the corridor drawn on
        // the ground is the one the planner actually demanded rather than a prettier, narrower one.
        float halfWidth = (float)(RouteFootprint.EnvelopeSize(settings).WidthM * 0.5);
        float bandY = (float)(groundY + 0.04f);
        float lineY = (float)(groundY + 0.06f);

        int start = 0;
        for (int i = 1; i <= points.Count; i++)
        {
            if (i < points.Count && points[i].Travel == points[start].Travel)
                continue;

            DrawRun(ar, points, start, i, halfWidth, bandY, lineY);

            if (i < points.Count)
            {
                // The gear change is the decision worth seeing from the seat. Filled rather than a ring:
                // a hollow circle at ground level disappears into the texture of the depot floor.
                PathPoint change = points[i];
                ar.Draw3DCircle(new ARCoordinate(new Vector3(change.Position.X, lineY, change.Position.Y)),
                                1.2f, GearChangeColor, true, 2f);
                start = i;
            }
        }
    }

    private static void DrawRun(ARRenderer ar, IReadOnlyList<PathPoint> points, int from, int to,
                                float halfWidth, float bandY, float lineY)
    {
        uint color = points[from].Travel == DriveDirection.Forward ? RouteForwardColor : RouteReverseColor;
        List<Vector3> center = new();
        List<ARCoordinate> left = new();
        List<ARCoordinate> right = new();
        double lastTaken = -DrawStepM;

        for (int i = from; i < to; i++)
        {
            PathPoint point = points[i];

            // Decimate: the path is sampled at PathSampleM (0.25 m), which is planning resolution, not
            // drawing resolution. 1.5 m still resolves every corner of a bay entry, and the last point of
            // a run is always kept so the runs meet at the gear change instead of leaving a gap.
            if (i != to - 1 && point.DistanceAlong - lastTaken < DrawStepM)
                continue;

            lastTaken = point.DistanceAlong;
            Vector2 offset = Geometry.LeftFromHeading(point.HeadingRad) * halfWidth;
            Vector3 at = new(point.Position.X, bandY, point.Position.Y);

            left.Add(new ARCoordinate(at + new Vector3(offset.X, 0f, offset.Y)));
            right.Add(new ARCoordinate(at - new Vector3(offset.X, 0f, offset.Y)));
            center.Add(new Vector3(point.Position.X, lineY, point.Position.Y));
        }

        if (left.Count < 2)
            return;

        ar.Draw3DLineWithGradient(left, right, color);

        for (int i = 1; i < center.Count; i++)
        {
            ar.Draw3DLine(new ARCoordinate(center[i - 1]), new ARCoordinate(center[i]), color, 2f);
        }
    }

    private static void DrawPreviewPath(ARRenderer ar, Pose2 truck, Pose2 target, double groundY)
    {
        Vector2 delta = target.Position - truck.Position;
        double length = delta.Length();
        if (length < 0.5)
            return;

        ARCoordinate start = new(new Vector3((float)truck.X, (float)groundY + 0.1f, (float)truck.Z));
        ARCoordinate end = new(new Vector3((float)target.X, (float)groundY + 0.1f, (float)target.Z));
        ar.Draw3DLine(start, end, PreviewColor, 2.0f);

        // Tick marks every 5 m so the raw distance reads without a HUD.
        int ticks = (int)(length / 5.0);
        for (int i = 1; i <= ticks; i++)
        {
            float t = (float)(i * 5.0 / length);
            Vector2 point = truck.Position + delta * t;
            Vector2 perpendicular = new(-delta.Y / (float)length, delta.X / (float)length);
            Vector2 tickStart = point - perpendicular * 0.5f;
            Vector2 tickEnd = point + perpendicular * 0.5f;

            ar.Draw3DLine(new ARCoordinate(new Vector3(tickStart.X, (float)groundY + 0.1f, tickStart.Y)),
                          new ARCoordinate(new Vector3(tickEnd.X, (float)groundY + 0.1f, tickEnd.Y)),
                          PreviewColor,
                          1.5f);
        }
    }
}
