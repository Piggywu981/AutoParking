using System;
using System.Collections.Generic;
using System.Numerics;
using ETS2LA.Logging;
using ETS2LA.Overlay;
using Hexa.NET.ImGui;

namespace AutoParking;

/// <summary>
///  The flat map the user picks a parking spot on. Drawn in ETS2LA's overlay window,
///  which means mouse input only arrives while the overlay interaction key (RightAlt by
///  default) is held - the window says so on screen.
/// </summary>
internal sealed class MapOverlay
{
    public const string WindowTitle = "AutoParking Map";

    public const int WindowWidth = 560;
    public const int WindowHeight = 560;
    private const float Padding = 8f;

    /// <summary>
    ///  How far the cursor has to leave the spot before a drag counts as "this orientation". The
    ///  pivot is under the cursor when the button goes down, so without a dead zone the first pixel
    ///  of jitter picks the heading - see Geometry.TryHeadingFromDrag.
    /// </summary>
    private const double HeadingDeadZonePx = 12.0;

    // Packed the same way Dear ImGui's IM_COL32 does (r | g<<8 | b<<16 | a<<24) so these
    // constants are safe to compute before the ImGui context exists.
    private static uint Color(float r, float g, float b, float a = 1f)
    {
        uint ri = (uint)(Math.Clamp(r, 0f, 1f) * 255f + 0.5f);
        uint gi = (uint)(Math.Clamp(g, 0f, 1f) * 255f + 0.5f);
        uint bi = (uint)(Math.Clamp(b, 0f, 1f) * 255f + 0.5f);
        uint ai = (uint)(Math.Clamp(a, 0f, 1f) * 255f + 0.5f);
        return ri | (gi << 8) | (bi << 16) | (ai << 24);
    }

    private static readonly uint ColorLaneLeft = Color(0.62f, 0.42f, 0.42f);
    private static readonly uint ColorLaneRight = Color(0.42f, 0.62f, 0.42f);
    private static readonly uint ColorCurve = Color(0.50f, 0.50f, 0.42f, 0.9f);
    private static readonly uint ColorObstacle = Color(0.90f, 0.35f, 0.35f, 0.9f);

    // Static map content, deliberately far from the vehicle red in hue: the whole point of drawing
    // it is to tell "the building next to me" apart from "the car that just moved into me".
    private static readonly uint ColorBuilding = Color(0.85f, 0.45f, 0.80f);
    private static readonly uint ColorModel = Color(0.80f, 0.88f, 0.35f);
    private static readonly uint ColorSign = Color(0.55f, 0.65f, 0.95f);
    private static readonly uint ColorArea = Color(0.45f, 0.80f, 0.70f);
    private static readonly uint ColorStaticOther = Color(0.75f, 0.75f, 0.75f, 0.9f);

    /// <summary>
    ///  Per-class colors for the static layer. Anything not named here still gets drawn - in gray -
    ///  because "what is that cluster of grey dots" is answered by the map inventory, and the whole
    ///  point of this layer is to surface classes nobody thought to whitelist.
    /// </summary>
    private static uint ShapeColor(string kind)
    {
        return kind switch
        {
            "Buildings" => ColorBuilding,
            "Model" => ColorModel,
            "Sign" => ColorSign,
            "TrafficArea" or "MapArea" or "Trigger" => ColorArea,
            _ => ColorStaticOther
        };
    }
    private static readonly uint ColorTruck = Color(0.30f, 0.95f, 0.45f);
    private static readonly uint ColorTarget = Color(0.95f, 0.85f, 0.30f);
    private static readonly uint ColorTargetLocked = Color(0.35f, 0.90f, 0.95f);
    private static readonly uint ColorGrid = Color(1f, 1f, 1f, 0.07f);
    private static readonly uint ColorCanvasEdge = Color(1f, 1f, 1f, 0.22f);
    private static readonly uint ColorText = Color(0.92f, 0.92f, 0.92f);
    private static readonly uint ColorRouteForward = Color(0.35f, 0.85f, 0.95f);
    private static readonly uint ColorRouteReverse = Color(0.98f, 0.62f, 0.20f);
    private static readonly uint ColorGearSwitch = Color(1f, 1f, 1f);

    private readonly AutoParkingPlugin plugin;
    private WindowDefinition definition;
    private bool registered;
    private bool draggingHeading;

    // The projection latched when the pick-drag started. The map is drawn centred on the truck and
    // the truck moves, so a drag measured against the live projection is a race: the spot slides
    // under the cursor and the gesture's angle follows the truck instead of the hand.
    private Vector2 dragCanvasCenter;
    private Vector2 dragTruckPlane;
    private float dragScale;
    private Vector2 dragCanvasMin;

    public MapOverlay(AutoParkingPlugin plugin)
    {
        this.plugin = plugin;
    }

    public void Register(bool open)
    {
        if (registered)
            return;

        definition = new WindowDefinition
        {
            Title = WindowTitle,
            // NoResize only. The whole window border is an ImGui resize grab and the canvas reaches
            // to within Padding of it, so a press meant for the map edge was grabbing the border
            // instead - and the window rect, which the projection reads every frame, moved under the
            // drag. Moving the panel stays allowed: ImGui drags a window from its title bar only,
            // which is above the canvas rectangle and therefore never read as a pick.
            Flags = ImGuiWindowFlags.NoResize,
            Width = WindowWidth,
            Height = WindowHeight,
            X = 24,
            Y = 180,
            Alpha = 0.86f
        };

        OverlayHandler.Current.RegisterWindow(definition, Render);
        ApplyOpenState(open);
        registered = true;
    }

    public void Unregister()
    {
        if (!registered)
            return;

        OverlayHandler.Current.UnregisterWindow(definition);
        registered = false;
        draggingHeading = false;
    }

    public void SetOpen(bool open)
    {
        if (registered)
            ApplyOpenState(open);
    }

    private static void ApplyOpenState(bool open)
    {
        if (open)
            OverlayHandler.Current.OpenWindow(WindowTitle);
        else
            OverlayHandler.Current.CloseWindow(WindowTitle);
    }

    private void Render()
    {
        AutoParkingSettings settings = plugin.Settings;
        Pose2 truck = plugin.CurrentPose;
        MapGeometry? geometry = plugin.MapGeometrySnapshot;
        Pose2? target = plugin.TargetPose;

        ImGui.TextUnformatted($"Auto Parking - 相位 {plugin.Phase}" + (plugin.Phase == ParkingPhase.Paused ? "  ⏸ 已暂停" : ""));
        RenderButtons(settings, target);

        bool interactive = false;
        try
        {
            interactive = OverlayHandler.Current.IsOverlayFocused;
        }
        catch
        {
        }

        if (interactive)
        {
            ImGui.TextUnformatted("点击放置车位，按住拖动定车头朝向，右键清除。");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.35f, 1f), "按住 RightAlt（ETS2LA 交互键）才能用鼠标点选。");
        }

        string feedback = plugin.LastActionMessage;
        if (!string.IsNullOrEmpty(feedback))
        {
            ImGui.TextColored(plugin.LastActionFailed ? new Vector4(0.95f, 0.45f, 0.4f, 1f)
                                                      : new Vector4(0.4f, 0.9f, 0.5f, 1f),
                feedback);
        }

        PlanResult? status = plugin.PlanSnapshot;
        if (status == null)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "在地图上点选车位开始规划路径。");
        }
        else
        {
            Vector4 color = status.Ok ? new Vector4(0.4f, 0.9f, 0.5f, 1f) : new Vector4(0.95f, 0.45f, 0.4f, 1f);
            ImGui.TextColored(color, status.Summary);
        }

        // One line, no more: this is the measurement step in front of any of it becoming an
        // obstacle, and the canvas below is what the window is for. 设置 -> 地图清单 logs the detail.
        string inventory = plugin.MapProbeSummary;
        if (!string.IsNullOrEmpty(inventory))
        {
            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), inventory);
        }

        if (geometry != null)
        {
            // The wording is the current contract, not a footnote: the static layer is drawn so it
            // can be judged, and it does not veto a route yet.
            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "静态（只画不拦）：");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.85f, 0.45f, 0.80f, 1f), "建筑");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.80f, 0.88f, 0.35f, 1f), "模型");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.55f, 0.65f, 0.95f, 1f), "牌");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.45f, 0.80f, 0.70f, 1f), "区域");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.75f, 0.75f, 0.75f, 1f), "其他条目");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), "· 实心=地图标记可碰");
        }

        Vector2 windowPos = ImGui.GetWindowPos();
        Vector2 windowSize = ImGui.GetWindowSize();
        Vector2 canvasMin = new(windowPos.X + Padding, ImGui.GetCursorScreenPos().Y + Padding * 0.5f);
        Vector2 canvasMax = new(windowPos.X + windowSize.X - Padding, windowPos.Y + windowSize.Y - Padding);
        Vector2 canvasCenter = (canvasMin + canvasMax) * 0.5f;

        float scale = (float)settings.EffectiveMapPixelsPerMeter;
        Vector2 truckPlane = truck.Position;

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRect(canvasMin, canvasMax, ColorCanvasEdge, 0f, thickness: 1f);

        DrawGrid(drawList, canvasMin, canvasMax, canvasCenter, truckPlane, scale);

        if (geometry == null)
        {
            drawList.AddText(canvasCenter + new Vector2(-90f, -8f), ColorText, "地图数据尚未就绪");
            return;
        }

        foreach (MapGeometry.LaneLine lane in geometry.RoadLanes)
        {
            DrawPolyline(drawList, lane.Points, lane.Left ? ColorLaneLeft : ColorLaneRight, 2f, canvasCenter, truckPlane, scale);
        }

        foreach (Vector2[] curve in geometry.DriveableCurves)
        {
            DrawPolyline(drawList, curve, ColorCurve, 1.5f, canvasCenter, truckPlane, scale);
        }

        foreach (MapGeometry.StaticShape shape in geometry.StaticShapes)
        {
            uint color = ShapeColor(shape.Kind);

            if (shape.Points.Length == 1)
            {
                Vector2 at = ToCanvas(shape.Points[0], canvasCenter, truckPlane, scale);
                if (shape.Collision)
                    drawList.AddCircleFilled(at, 2.2f, color);
                else
                    drawList.AddCircle(at, 2.2f, color, 0, 1f);
                continue;
            }

            if (shape.Closed)
            {
                DrawClosedPolygon(drawList, shape.Points, color, canvasCenter, truckPlane, scale);
                continue;
            }

            DrawPolyline(drawList, shape.Points, color, shape.Kind == "Buildings" ? 2.5f : 1.5f,
                         canvasCenter, truckPlane, scale);
        }

        foreach (Vector2[] polygon in geometry.Obstacles.Polygons)
        {
            DrawClosedPolygon(drawList, polygon, ColorObstacle, canvasCenter, truckPlane, scale);
        }

        DrawVehicleBox(drawList, truck, settings.VehicleLengthM, settings.VehicleWidthM, ColorTruck, canvasCenter, truckPlane, scale);

        if (target.HasValue)
        {
            uint color = plugin.Phase == ParkingPhase.Idle ? ColorTarget : ColorTargetLocked;
            DrawVehicleBox(drawList, target.Value, settings.VehicleLengthM, settings.VehicleWidthM, color, canvasCenter, truckPlane, scale);
            DrawHeadingArrow(drawList, target.Value, color, canvasCenter, truckPlane, scale);
        }

        PlanResult? plan = plugin.PlanSnapshot;
        if (plan?.Path != null)
        {
            DrawRoute(drawList, plan.Path, canvasCenter, truckPlane, scale);
        }

        DrawScaleBar(drawList, canvasMin, scale);
        HandleMouse(interactive, canvasMin, canvasMax, canvasCenter, truckPlane, scale, settings, geometry);
    }

    /// <summary>
    ///  The planned route: forward legs in cyan, reverse legs in orange, gear changes as white
    ///  dots. This is the user's chance to veto a route before it is ever driven.
    /// </summary>
    private static void DrawRoute(ImDrawListPtr drawList, ParkingPath path, Vector2 canvasCenter, Vector2 truckPlane, float scale)
    {
        IReadOnlyList<PathPoint> points = path.Points;
        for (int i = 1; i < points.Count; i++)
        {
            Vector2 from = ToCanvas(points[i - 1].Position, canvasCenter, truckPlane, scale);
            Vector2 to = ToCanvas(points[i].Position, canvasCenter, truckPlane, scale);
            uint color = points[i].Travel == DriveDirection.Forward ? ColorRouteForward : ColorRouteReverse;
            drawList.AddLine(from, to, color, 2.5f);
        }

        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].Travel == points[i - 1].Travel)
                continue;

            Vector2 at = ToCanvas(points[i].Position, canvasCenter, truckPlane, scale);
            drawList.AddCircle(at, 4f, ColorGearSwitch, 0, 2f);
        }
    }

    private void RenderButtons(AutoParkingSettings settings, Pose2? target)
    {
        if (ImGui.Button("Start"))
        {
            plugin.HandleAction("start", null);
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop"))
        {
            plugin.HandleAction("stop", null);
        }

        ImGui.SameLine();
        if (ImGui.Button("清除"))
        {
            plugin.HandleAction("clearTarget", null);
        }

        ImGui.SameLine();
        if (ImGui.Button("-"))
        {
            plugin.AdjustZoom(1f / 1.25f);
        }

        ImGui.SameLine();
        if (ImGui.Button("+"))
        {
            plugin.AdjustZoom(1.25f);
        }

        ImGui.SameLine();
        if (ImGui.Button("暂停/继续"))
        {
            plugin.HandleAction("toggle", null);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted($"比例 {settings.EffectiveMapPixelsPerMeter:0.00} px/m");

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
            "热键在 设置→控制 里绑定：Toggle=触发/暂停/继续，Abort=中止。用热键是因为点按钮会让游戏失焦、SDK 输入失效。");
    }

    private void HandleMouse(bool interactive, Vector2 canvasMin, Vector2 canvasMax, Vector2 canvasCenter,
                             Vector2 truckPlane, float scale, AutoParkingSettings settings, MapGeometry geometry)
    {
        Vector2 mouse = ImGui.GetMousePos();
        bool overCanvas = mouse.X >= canvasMin.X && mouse.X <= canvasMax.X
                       && mouse.Y >= canvasMin.Y && mouse.Y <= canvasMax.Y;

        if (!interactive || !overCanvas)
        {
            draggingHeading = false;
            return;
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            Vector2 world = truckPlane + (mouse - canvasCenter) / scale;
            double reference = plugin.CurrentPose.HeadingRad;
            Pose2 pose = new(world.X, world.Y, reference);

            bool snappedToCurve = false;
            if (settings.SnapToNavCurve && geometry.TrySnapToCurve(world, 3.0, reference, out Pose2 snapped))
            {
                pose = snapped;
                snappedToCurve = true;
            }

            plugin.SetTarget(pose);
            draggingHeading = true;
            dragCanvasCenter = canvasCenter;
            dragTruckPlane = truckPlane;
            dragScale = scale;
            dragCanvasMin = canvasMin;

            Logger.Info($"AutoParking: 选位开始 世界=({pose.X:0.00}, {pose.Z:0.00}) 吸附导航线={snappedToCurve} " +
                        $"朝向={pose.YawDegrees:0.0}° 画布左上=({canvasMin.X:0}, {canvasMin.Y:0}) 比例={scale:0.00}px/m");
            return;
        }

        if (draggingHeading && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            Pose2? current = plugin.TargetPose;
            if (current == null)
            {
                draggingHeading = false;
                return;
            }

            // Measured against the latched projection: whatever the truck or the window does between
            // these two frames, the pivot stays put under the cursor.
            Vector2 pivot = ToCanvas(current.Value.Position, dragCanvasCenter, dragTruckPlane, dragScale);
            if (Geometry.TryHeadingFromDrag(pivot, mouse, HeadingDeadZonePx, out double heading))
                plugin.SetTargetTransient(current.Value.WithHeading(heading));

            ImDrawListPtr canvas = ImGui.GetWindowDrawList();
            canvas.AddCircle(pivot, (float)HeadingDeadZonePx, ColorGrid, 0, 1f);
            canvas.AddLine(pivot, mouse, ColorTarget, 1.5f);
        }

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (draggingHeading && plugin.TargetPose.HasValue)
            {
                Pose2 released = plugin.TargetPose.Value;
                Vector2 pivot = ToCanvas(released.Position, dragCanvasCenter, dragTruckPlane, dragScale);
                double radius = Geometry.Distance(pivot, mouse);
                double mapSlid = Geometry.Distance(dragTruckPlane, truckPlane) * dragScale;
                double rectMoved = Geometry.Distance(dragCanvasMin, canvasMin);
                Logger.Info($"AutoParking: 选位结束 朝向={released.YawDegrees:0.0}° 拖动={radius:0} px（死区 {HeadingDeadZonePx:0}）" +
                            $" 期间地图滑动={mapSlid:0} px 画布位移={rectMoved:0} px");
                plugin.SetTarget(released);   // persist once, on release
            }

            draggingHeading = false;
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            plugin.SetTarget(null);
    }

    private static Vector2 ToCanvas(Vector2 world, Vector2 canvasCenter, Vector2 truckPlane, float scale)
    {
        return canvasCenter + (world - truckPlane) * scale;
    }

    private static void DrawGrid(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector2 center,
                                 Vector2 truckPlane, float scale)
    {
        float step = 10f * scale;
        if (step < 6f)
            return;

        for (float dx = 0f; center.X + dx < max.X; dx += step)
        {
            drawList.AddLine(new Vector2(center.X + dx, min.Y), new Vector2(center.X + dx, max.Y), ColorGrid, 1f);
            drawList.AddLine(new Vector2(center.X - dx, min.Y), new Vector2(center.X - dx, max.Y), ColorGrid, 1f);
        }

        for (float dy = 0f; center.Y + dy < max.Y; dy += step)
        {
            drawList.AddLine(new Vector2(min.X, center.Y + dy), new Vector2(max.X, center.Y + dy), ColorGrid, 1f);
            drawList.AddLine(new Vector2(min.X, center.Y - dy), new Vector2(max.X, center.Y - dy), ColorGrid, 1f);
        }
    }

    private static void DrawPolyline(ImDrawListPtr drawList, Vector2[] worldPoints, uint color, float thickness,
                                     Vector2 canvasCenter, Vector2 truckPlane, float scale)
    {
        Vector2 previous = ToCanvas(worldPoints[0], canvasCenter, truckPlane, scale);

        for (int i = 1; i < worldPoints.Length; i++)
        {
            Vector2 next = ToCanvas(worldPoints[i], canvasCenter, truckPlane, scale);
            drawList.AddLine(previous, next, color, thickness);
            previous = next;
        }
    }

    private static void DrawClosedPolygon(ImDrawListPtr drawList, Vector2[] worldPoints, uint color,
                                          Vector2 canvasCenter, Vector2 truckPlane, float scale)
    {
        Vector2 first = ToCanvas(worldPoints[0], canvasCenter, truckPlane, scale);
        Vector2 previous = first;

        for (int i = 1; i < worldPoints.Length; i++)
        {
            Vector2 next = ToCanvas(worldPoints[i], canvasCenter, truckPlane, scale);
            drawList.AddLine(previous, next, color, 1.5f);
            previous = next;
        }

        drawList.AddLine(previous, first, color, 1.5f);
    }

    private static void DrawVehicleBox(ImDrawListPtr drawList, Pose2 pose, double length, double width, uint color,
                                       Vector2 canvasCenter, Vector2 truckPlane, float scale)
    {
        Vector2[] corners = Geometry.RectangleCorners(pose, length, width);
        Vector2 first = ToCanvas(corners[0], canvasCenter, truckPlane, scale);
        Vector2 previous = first;

        for (int i = 1; i < corners.Length; i++)
        {
            Vector2 next = ToCanvas(corners[i], canvasCenter, truckPlane, scale);
            drawList.AddLine(previous, next, color, 2f);
            previous = next;
        }

        drawList.AddLine(previous, first, color, 2f);

        Vector2 center = ToCanvas(pose.Position, canvasCenter, truckPlane, scale);
        drawList.AddCircleFilled(center, 2.5f, color);
    }

    private static void DrawHeadingArrow(ImDrawListPtr drawList, Pose2 pose, uint color,
                                         Vector2 canvasCenter, Vector2 truckPlane, float scale)
    {
        Vector2 nose = pose.Position + pose.Forward * 4f;
        Vector2 from = ToCanvas(pose.Position, canvasCenter, truckPlane, scale);
        Vector2 to = ToCanvas(nose, canvasCenter, truckPlane, scale);
        drawList.AddLine(from, to, color, 2f);

        Vector2 left = pose.Position + new Vector2(-pose.Forward.Y, pose.Forward.X) * 1.4f;
        Vector2 right = pose.Position + new Vector2(pose.Forward.Y, -pose.Forward.X) * 1.4f;
        drawList.AddLine(ToCanvas(left, canvasCenter, truckPlane, scale), to, color, 1.5f);
        drawList.AddLine(ToCanvas(right, canvasCenter, truckPlane, scale), to, color, 1.5f);
    }

    private static void DrawScaleBar(ImDrawListPtr drawList, Vector2 canvasMin, float scale)
    {
        float meters = 10f;
        Vector2 start = canvasMin + new Vector2(10f, 14f);
        Vector2 end = start + new Vector2(meters * scale, 0f);
        drawList.AddLine(start, end, ColorText, 2f);
        drawList.AddLine(start - new Vector2(0f, 4f), start + new Vector2(0f, 4f), ColorText, 1.5f);
        drawList.AddLine(end - new Vector2(0f, 4f), end + new Vector2(0f, 4f), ColorText, 1.5f);
        drawList.AddText(end + new Vector2(4f, -6f), ColorText, "10 m");
    }
}
