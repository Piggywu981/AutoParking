using System;
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
