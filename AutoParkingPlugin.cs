using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using ETS2LA.Backend.Events;
using ETS2LA.Controls;
using ETS2LA.Game;
using ETS2LA.Game.Data;
using ETS2LA.Game.Telemetry;
using ETS2LA.Logging;
using ETS2LA.Notifications;
using ETS2LA.Settings;
using ETS2LA.Shared;
using ETS2LA.State;

namespace AutoParking;

public enum ParkingPhase
{
    Idle,
    Selecting,
    Engaging,
    Following,
    GearHold,
    HoldingBrake,
    Paused,
    Aborted
}

public sealed class AutoParkingPlugin : Plugin
{
    public static AutoParkingPlugin? Instance { get; private set; }

    public AutoParkingPlugin()
    {
        Instance = this;
    }

    private const string PluginId = "local.autoparking";
    private const string SettingsFileName = "AutoParking.json";
    private static readonly TimeSpan TelemetryStaleLimit = TimeSpan.FromMilliseconds(500);

    private readonly object sync = new();
    private SettingsHandler? settingsHandler;
    private AutoParkingSettings settings = new();

    private GameTelemetryData latestTelemetry = new();
    private DateTime lastTelemetryUtc = DateTime.MinValue;
    private bool telemetryStale = true;
    private bool previousReadTrailerData;

    private ParkingPhase phase = ParkingPhase.Idle;
    private string lastRejectReason = "";
    private string lastActionMessage = "";
    private string lastRawUserInputs = "";
    private bool settingsDirty;
    private bool lastActionFailed;

    private readonly MapGeometryBuilder geometryBuilder = new();
    private MapGeometry? mapGeometry;
    private Pose2? target;
    private DateTime nextGeometryBuildUtc = DateTime.MinValue;
    private MapOverlay? mapOverlay;
    private ArOverlay? arOverlay;

    /// <summary>
    ///  Reads the game's runtime-spawned map items (roadwork cones above all) from the game
    ///  process on its own background task - never on this tick thread (§39/§40) and never on the
    ///  render thread. Read-only, degrade-to-status-line by design; see Memory\KdopReader.cs.
    /// </summary>
    private KdopReader? kdopReader;

    /// <summary>
    ///  Control terms for the last ~17 s of tracking. Owned by the plugin, written by the follower on
    ///  the tick thread, read by <see cref="controlPlot"/> on the overlay thread; the trace locks
    ///  itself so nothing here has to.
    /// </summary>
    private readonly ControlTrace controlTrace = new();
    private ControlPlot? controlPlot;

    /// <summary>
    ///  Last wheel layout read out of telemetry, plus the signature it was read from. The signature
    ///  is what makes a truck swap in a garage show up as new data instead of a stale measurement.
    /// </summary>
    private string wheelbaseSignature = "";

    /// <summary>
    ///  Must never stay <c>default</c>: the record struct's reference member (Axles) is null in a
    ///  default value, and StatusRows reads it unconditionally - opening the settings page before
    ///  the first telemetry event crashed the host (hit for real 2026-10-16, both sessions in the
    ///  log, always right after a restart). Invalid("尚未测量") carries an empty Axles list and a
    ///  Reason the status table can show as-is.
    /// </summary>
    private WheelbaseMeasurement wheelbaseMeasurement = WheelbaseMeasurement.Invalid("尚未测量");
    private string mapStatus = "尚未构建";
    private PlanResult? planResult;
    private string selfTestSummary = "尚未运行";
    private DateTime groundOffsetCacheUtc = DateTime.MinValue;
    private double cachedGroundOffset;

    private readonly ControlOutput output = new();
    private Follower? follower;
    private DateTime phaseDeadlineUtc = DateTime.MinValue;
    private DateTime lastBrakeProbeUtc = DateTime.MinValue;
    private double lastProbeSpeed;
    private DateTime lastProbeSpeedUtc = DateTime.MinValue;
    private DateTime lastControlTickUtc = DateTime.MinValue;
    private double maxControlGapSinceProbeMs;
    private string lastOverrideState = "";

    // The game-side plugin publishes whether it is currently overriding the truck at
    // Local\ETS2LAPluginStatus (int version, bool steering, bool acceleration, 6 bytes -
    // core.hpp:15-21), and nothing in the host reads it. It is the only direct answer to "does the
    // truck listen to our pedal right now": the user*/game* echoes cannot say it, because under the
    // memory transport our values are not on the device surface the echoes are sampled from.
    private const string PluginStatusMapName = "Local\\ETS2LAPluginStatus";
    private MemoryMappedFile? statusFile;
    private MemoryMappedViewAccessor? statusAccessor;
    private bool statusTried;
    private DateTime lastGearProbeUtc = DateTime.MinValue;
    private int lastReplanCount;
    private string lastCostSignature = "";
    private bool warnedNoAir;

    // Reverse distance inside which the parking brake may be used to stop the truck.
    private const double ParkingBrakeStopDistanceM = 2.0;

    // Pedal fallback: the memory transport writes the game's input-mix output value, which the
    // game re-evaluates every frame, and a write that stops reaching the truck is not reported
    // anywhere - the game side keeps its override claim on the timestamp alone (core.cpp:213-239),
    // so the status map still says 踏板on while the wheels see nothing. Measured 2026-10-15:
    // full brake at 5.1 km/h for minutes, a实测≈0, air pinned at 123.25 psi, game_brake=0.00,
    // the truck creeping on the automatic's idle. The one proof available is physical: a full
    // brake demand that neither slows the truck nor drains the service line is a pedal that
    // never reached it.
    private const double PedalFallbackMinSpeedKph = 2.0;
    private const float PedalFallbackBrakeDemand = 0.5f;
    private const double PedalFallbackWindowSeconds = 2.0;
    private const double PedalFallbackMinDecelMps2 = 0.4;
    private const float PedalFallbackMinAirDropPsi = 2.0f;
    private const float PedalFallbackMinEchoBrake = 0.1f;

    private DateTime pedalSuspicionUtc = DateTime.MinValue;
    private float pedalSuspicionSpeedKph;
    private float pedalSuspicionAir;
    private bool pedalFallbackEngaged;

    /// <summary>
    ///  Is there enough air for the service brake to do anything? The emergency threshold comes
    ///  from the truck config; when it has not been read (zero) a low fixed floor is used, because
    ///  "no braking at all" is the one failure that must not be masked by a missing config value.
    /// </summary>
    private bool HasBrakeAir()
    {
        float emergency = latestTelemetry.configFloat.airPressureEmergency;

        return latestTelemetry.truckFloat.airPressure > (emergency > 0.0f ? emergency : 20.0f);
    }

    /// <summary>
    ///  Which transport the host used for our pedals during this run. The two are different memory
    ///  surfaces, so the same log line means something different depending on this: on `legacy` our
    ///  value lands on the virtual gamepad and reads back through `user_*`, on `memory` it goes into
    ///  the SDK analog slot. Gear and handbrake pulses always take the legacy path.
    /// </summary>
    private static string PedalTransport => ETS2LA.Game.GameSettings.Current.EnableModernOutputForPedals ? "memory" : "legacy";

    // Restored when the maneuver ends, unless the user changed them in the meantime.
    private bool hadAssistSnapshot;
    private bool savedEnableAssists;
    private float savedDesiredSpeed;
    private DrivingMode savedDrivingMode;

    private Vector2 lastPosition;
    private DateTime lastPositionUtc = DateTime.MinValue;

    private readonly ControlDefinition toggleControl = new()
    {
        Id = "local.autoparking.Toggle",
        Name = "Auto Parking: 触发/暂停/继续",
        Description = "空闲时开始泊车；运行中暂停（拉手刹）；已暂停时继续。用热键而不是点按钮：点 overlay 会让游戏失焦，SCS 虚拟手柄在失焦时不生效。",
        DefaultKeybind = "",
        Type = ControlType.Boolean
    };

    private readonly ControlDefinition abortControl = new()
    {
        Id = "local.autoparking.Abort",
        Name = "Auto Parking: 中止泊车",
        Description = "立即刹停、释放通道并交还控制权",
        DefaultKeybind = "",
        Type = ControlType.Boolean
    };

    private static readonly TimeSpan GeometryInterval = TimeSpan.FromMilliseconds(500);

    // The log holds the full inventory; the map window only ever shows the one-line summary,
    // because the canvas inside it is the point of the window.
    private const int MapProbeLogTypes = 8;

    public override PluginInformation Info { get; } = new()
    {
        Id = PluginId,
        Name = "Auto Parking",
        Description = "Park the tractor unit into a spot picked on a flat map, with gear, throttle, brake and steering control.",
        Version = "0.1.0",
        SupportedETS2LA = ">=2026.8.4900",
        AuthorName = "local",
        Tags = new[] { "Parking", "Overlay", "AR" }
    };

    public override float TickRate => 60.0f;

    public AutoParkingSettings Settings
    {
        get
        {
            lock (sync) return settings;
        }
    }

    /// <summary>
    ///  Runs once at load. Controls are registered here, not in OnEnable, because that is the
    ///  pattern the binding page expects: OvertakeAssistant does the same, and registering later
    ///  left our keys missing from 设置 -> 控制 entirely.
    /// </summary>
    public override void Init()
    {
        base.Init();

        ControlsBackend.Current.RegisterControl(toggleControl);
        ControlsBackend.Current.RegisterControl(abortControl);
        Logger.Info("AutoParking: 控件已注册（local.autoparking.Toggle / .Abort），请到 设置 -> 控制 绑定");
    }

    public override void OnEnable()
    {
        settingsHandler = new SettingsHandler();
        settings = settingsHandler.Load<AutoParkingSettings>(SettingsFileName) ?? new AutoParkingSettings();
        settings.Clamp();
        settingsHandler.Save(SettingsFileName, settings);
        settingsHandler.RegisterListener<AutoParkingSettings>(SettingsFileName, OnSettingsChanged);

        previousReadTrailerData = GameTelemetry.Current.ReadTrailerData;
        GameTelemetry.Current.ReadTrailerData = true;

        Events.Current.Subscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);

        lock (sync)
        {
            latestTelemetry = GameTelemetry.Current.GetCurrentData();
            lastTelemetryUtc = DateTime.UtcNow;
            telemetryStale = false;
        }

        if (settings.HasSavedSpot)
        {
            target = new Pose2(settings.SavedSpotX, settings.SavedSpotZ, settings.SavedSpotHeadingRad);
            phase = ParkingPhase.Selecting;
        }

        mapOverlay = new MapOverlay(this);
        arOverlay = new ArOverlay(this);
        controlPlot = new ControlPlot(controlTrace);
        mapOverlay.Register(settings.ShowMapWindow);
        arOverlay.Register();
        controlPlot.SetVisible(settings.ShowControlPlot);
        kdopReader = new KdopReader(this);
        kdopReader.Start();

        ControlsBackend.Current.On(toggleControl.Id, OnToggleKeyPressed);
        ControlsBackend.Current.On(abortControl.Id, OnAbortKeyPressed);

        base.OnEnable();
    }

    public override void Tick()
    {
        lock (sync)
        {
            telemetryStale = (DateTime.UtcNow - lastTelemetryUtc) > TelemetryStaleLimit;
        }


        // Building map geometry costs 10-50 ms, so only do it while the map window is on.
        if (mapOverlay != null && settings.ShowMapWindow && DateTime.UtcNow >= nextGeometryBuildUtc)
        {
            nextGeometryBuildUtc = DateTime.UtcNow.Add(GeometryInterval);
            RefreshMapGeometry();
        }

        // The AR context can lag behind plugin enable, so keep retrying until it takes.
        if (arOverlay != null && settings.ShowArOverlay)
        {
            arOverlay.EnsureRegistered();
        }

        FlushSettingsIfDirty();
        StepControlLoop();
    }

    /// <summary>
    ///  Writing the settings file fires a change callback and takes a filesystem lock, so it
    ///  must not happen while holding the plugin lock or on the render/UI threads.
    /// </summary>
    private void FlushSettingsIfDirty()
    {
        AutoParkingSettings snapshot;

        lock (sync)
        {
            if (!settingsDirty)
                return;

            settingsDirty = false;
            snapshot = settings.Clone();
        }

        settingsHandler?.Save(SettingsFileName, snapshot);
    }

    private void RefreshMapGeometry()
    {
        MapData? map = geometryBuilder.FindMapData(out string status);
        if (map == null)
        {
            lock (sync)
            {
                mapStatus = status;
                mapGeometry = null;
            }
            return;
        }

        Pose2 truck = CurrentPose;
        double radius = settings.MapDataRadiusM(MapOverlay.WindowHeight);
        MapGeometry geometry = geometryBuilder.Build(map, truck.Position, radius,
                                                      settings.ConfirmedCorridorHalfWidthM);
        int bayCount = DetectParkingBays(geometry);
        int memoryObstacles = AddMemoryObstacles(geometry, truck.Position, radius);

        lock (sync)
        {
            mapGeometry = geometry;
            mapStatus = $"{geometry.Status} · {geometry.BuildMilliseconds:0} ms" +
                        (bayCount > 0 ? $" · 车位 {bayCount}" : "") +
                        (memoryObstacles > 0 ? $" · 内存障碍 {memoryObstacles}" : "");
        }

        Replan(geometry);
    }

    /// <summary>
    ///  Reads the parking bays out of the cones the memory reader already collected, before the
    ///  obstacle merge: a bay's own markings sit inside the bay, and merging them first would refuse
    ///  the very spot the game asks for (design doc §50).
    /// </summary>
    private int DetectParkingBays(MapGeometry geometry)
    {
        KdopReader.Snapshot? memory = MemoryItemSnapshot;
        if (memory == null || !settings.ShowMemoryItems)
            return 0;

        List<Vector2> cones = new();
        foreach (KdopReader.MemItem item in memory.Items)
            if (item.Kind == MemKind.Cone)
                cones.Add(item.PlanePos);

        geometry.Bays.AddRange(ParkingBays.Detect(cones));
        return geometry.Bays.Count;
    }

    /// <summary>
    ///  Memory items become planner obstacles: cones, boards, movers and buildings inside the
    ///  sampled radius go into <see cref="MapGeometry.Obstacles"/> as their own k-DOP ground
    ///  boxes, so Planner, Follower, the route-cost log and the map's red obstacle polygons all
    ///  treat them like any other blocker (owner decision §43 - near-lot objects must not be hit
    ///  whatever exactly they are). Merging here means the engaged maneuver's captured geometry
    ///  carries the set from its last rebuild (≤0.5 s old), while HandleObstacles sees fresh data
    ///  every tick - the same freshness split the traffic obstacles already live with.
    ///
    ///  The kind test below is a whitelist, so a kind the classifier learns afterwards is not a
    ///  blocker by construction. That is deliberate for Trigger (an invisible volume) and
    ///  ParkedVehicle (the game's own framing vehicles, which the target resolver reads as the gap
    ///  the truck is asked to use): making either of them block needs its own evidence, not a
    ///  rename (§54).
    /// </summary>
    private int AddMemoryObstacles(MapGeometry geometry, Vector2 truckPlane, double radiusM)
    {
        if (!settings.ShowMemoryItems || !settings.BlockRouteWithMemoryItems)
            return 0;

        KdopReader.Snapshot? memory = MemoryItemSnapshot;
        if (memory == null)
            return 0;

        int added = 0;
        foreach (KdopReader.MemItem item in memory.Items)
        {
            if (item.Kind is not (MemKind.Cone or MemKind.Board
                or MemKind.Mover or MemKind.Building))
                continue;

            if (Math.Abs(item.PlanePos.X - truckPlane.X) > radiusM
                || Math.Abs(item.PlanePos.Y - truckPlane.Y) > radiusM)
                continue;

            // Anything inside a detected bay is the bay's own furniture, not a blocker. The rectangle is
            // shrunk by BayInteriorInsetM first so the cone rows that form the bay's walls stay obstacles -
            // without the inset the truck would be allowed to drive through the walls it parks between.
            if (InsideBay(geometry, item.PlanePos))
                continue;

            geometry.Obstacles.AddBox(item.PlanePos, item.Dx, item.Dz);
            added++;
        }

        return added;
    }

    /// <summary>
    ///  How far inside the bay's own cone rows an item has to sit before it counts as the bay's
    ///  furniture. The rows are ON the rectangle's edge, so an inset smaller than half a cone footprint
    ///  would delete the wall the truck is supposed to stop against.
    /// </summary>
    private const double BayInteriorInsetM = 0.6;

    private static bool InsideBay(MapGeometry geometry, Vector2 point)
    {
        foreach (ParkingBay bay in geometry.Bays)
            if (bay.Contains(point, -BayInteriorInsetM))
                return true;
        return false;
    }

    /// <summary>
    ///  Writes the measured inventory to the log. The count of distinct tokens per class is the
    ///  number that decides whether real model footprints are affordable later: parsing one PMD
    ///  per token is what cost the upstream visualization plugin a ~20 s stall.
    /// </summary>
    private void DumpMapInventory()
    {
        MapGeometry? geometry;
        lock (sync) geometry = mapGeometry;

        if (geometry == null)
        {
            lock (sync)
            {
                lastActionFailed = true;
                lastActionMessage = "地图数据还没就绪（平面地图窗口要开着，游戏地图要解析完）";
            }
            return;
        }

        MapItemProbe probe = geometry.Probe;
        string fidelity = DataSettings.Current.DataFidelity.ToString();

        Logger.Info($"AutoParking: [[地图清单]] 中心=({probe.Center.X:0}, {probe.Center.Y:0}) " +
                    $"半径={probe.RadiusM:0} m 节点={probe.NodesScanned} 条目={probe.TotalItems} " +
                    $"构建={geometry.BuildMilliseconds:0} ms 宿主保真度={fidelity}");

        foreach (string line in probe.Lines(MapProbeLogTypes))
            Logger.Info($"AutoParking: [[地图清单]]   {line}");

        lock (sync)
        {
            lastActionFailed = false;
            lastActionMessage = $"已打印清单：{probe.Summary}（保真度 {fidelity}，见日志）";
        }

        DumpWholeMapCensus();
    }

    /// <summary>
    ///  The whole-map census walks every parsed item - millions in the base map - so it runs on its
    ///  own thread and reports into the log. It answers the question the sampled inventory cannot:
    ///  whether a class is absent from the data, or merely unreachable from the node index.
    /// </summary>
    private void DumpWholeMapCensus()
    {
        MapData? map = geometryBuilder.FindMapData(out _);
        if (map == null)
            return;

        Task.Run(() =>
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                Dictionary<string, int> census = MapGeometryBuilder.Census(map);
                sw.Stop();

                string fidelity = DataSettings.Current.DataFidelity.ToString();
                Logger.Info($"AutoParking: [[全图清点]] 类型 {census.Count} 种 · 条目 {census.Values.Sum()} " +
                            $"· 用时 {sw.ElapsedMilliseconds} ms · 宿主保真度={fidelity}");

                foreach (KeyValuePair<string, int> pair in census.OrderByDescending(p => p.Value))
                    Logger.Info($"AutoParking: [[全图清点]]   {pair.Key} {pair.Value}");
            }
            catch (Exception ex)
            {
                Logger.Error($"AutoParking: 全图清点失败 {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>
    ///  Re-plan for the current spot. Runs on the tick thread only, right after fresh
    ///  obstacle data arrives, so the route the user sees is the route the follower will get.
    /// </summary>
    private void Replan(MapGeometry geometry)
    {
        Pose2? spot;
        AutoParkingSettings snapshot;

        lock (sync)
        {
            spot = target;
            snapshot = settings;
        }

        if (spot == null)
        {
            lock (sync) planResult = null;
            return;
        }

        PlanResult result = Planner.Plan(CurrentPose, spot.Value, snapshot, geometry.Obstacles, geometry.Trust);

        lock (sync) planResult = result;

        LogCandidateCosts(result);
    }

    /// <summary>
    ///  One block per plan: every candidate the planner considered with the four numbers M7c will be
    ///  weighted on. Deliberately not one line for the winner — the whole question is whether the loser
    ///  that costs less ground was available at all, and that cannot be read off a winner.
    /// </summary>
    /// <summary>
    ///  One block per *decision*, not per plan: the map window rebuilds geometry every 500 ms and
    ///  re-plans with it, so logging every pass would bury the log under ~18 lines per second of an
    ///  unchanged answer. The signature is what the readout is for - family, length, conflicts, exposure -
    ///  so any change a driver could act on still prints.
    /// </summary>
    private void LogCandidateCosts(PlanResult result)
    {
        if (result.Candidates == null || result.Candidates.Count == 0)
            return;

        string signature = $"{result.Path?.Source}|{result.Path?.Description}|{result.Path?.Length:0.0}" +
                           $"|{result.ConflictCount}|{result.Candidates.Count}";
        if (signature == lastCostSignature)
            return;

        lastCostSignature = signature;
        int shown = 0;
        foreach (ParkingPath candidate in result.Candidates)
        {
            if (shown++ == 8)
            {
                Logger.Info($"AutoParking: [[路径代价]] 其余 {result.Candidates.Count - 8} 条已省略");
                break;
            }

            Logger.Info($"AutoParking: [[路径代价]] {(ReferenceEquals(candidate, result.Path) ? "选中" : "    ")} " +
                        $"{candidate.Description} · 长 {candidate.Length:0.0} m · 换挡 {candidate.GearSwitches} · " +
                        $"占地 {candidate.SweptAreaM2:0} m² · 未确认 {candidate.UnconfirmedAreaM2:0} m²" +
                        (candidate.FootprintTruncated ? "（栅格截断）" : ""));
        }
    }

    public PlanResult? PlanSnapshot
    {
        get
        {
            lock (sync) return planResult;
        }
    }

    // MARK: Control loop

    private void StepControlLoop()
    {
        ParkingPhase current;
        lock (sync) current = phase;

        switch (current)
        {
            case ParkingPhase.Engaging:
            case ParkingPhase.Following:
            case ParkingPhase.GearHold:
                StepManeuver();
                break;

            case ParkingPhase.Paused:
                // Paused still holds the vehicle, so it must not go blind: without telemetry we
                // cannot tell a parked truck from a rolled-away one, and re-asserting the brake
                // into a dead SDK would never end.
                if (telemetryStale || !latestTelemetry.sdkActive || latestTelemetry.paused)
                {
                    Abort("遥测中断或游戏已暂停");
                    break;
                }

                // Hold the spot: keep re-asserting the handbrake, touch nothing else.
                output.Apply(new ControlDemand(0f, 0f, 0f, true, GearRequest.None, false));
                break;

            case ParkingPhase.HoldingBrake:
            case ParkingPhase.Aborted:
                // Keep the brake applied while the deadline runs, then hand back.
                output.Apply(new ControlDemand(0f, 0f, 0.6f, settings.HandbrakeOnFinish || current == ParkingPhase.Aborted,
                                               GearRequest.None, false));
                if (DateTime.UtcNow >= phaseDeadlineUtc)
                    CompletePhase(current);
                break;
        }
    }

    private void StepManeuver()
    {
        Follower? active = follower;
        if (active == null)
            return;

        // Blind, or the game paused itself: hold the vehicle and wait for it to come back rather
        // than ending the maneuver. Only the hotkey ends it.
        if (telemetryStale || !latestTelemetry.sdkActive || latestTelemetry.paused)
        {
            output.Apply(new ControlDemand(0f, 0f, 0.6f, true, GearRequest.None, false));
            return;
        }

        VehicleState state = ReadVehicleState();
        ObstacleSnapshot obstacles = MapGeometrySnapshot?.Obstacles ?? new ObstacleSnapshot();

        output.DryRun = settings.DryRun;
        output.Weight = (float)settings.ControlWeight;
        ControlDemand demand = active.Step(state, obstacles);

        // Below the emergency threshold the service brake has nothing to act on - the pedal is
        // published, the pads do nothing, and the truck coasts. The parking brake works on a
        // different circuit, so near the end of the route it is the only thing that can still
        // stop us. Only near the end: yanking it at speed would lock the rear wheels.
        if (demand.Brake > 0.1f && !HasBrakeAir() && active.RemainingDistance < ParkingBrakeStopDistanceM)
        {
            if (!warnedNoAir)
            {
                warnedNoAir = true;
                Logger.Warn($"AutoParking: 气压 {latestTelemetry.truckFloat.airPressure:0.0} bar 低于应急值，行车制动无效，改用驻车制动停车");
            }

            demand = demand with { HoldBrake = true };
        }

        output.Apply(demand);

        // Publish cadence is what the game side's 0.2 s freshness gate actually measures, so the gap
        // between two of our own publishes is the first suspect when it drops the override. The probe
        // reports the worst gap since its last line, not the instantaneous one.
        DateTime controlUtc = DateTime.UtcNow;
        if (lastControlTickUtc != DateTime.MinValue)
        {
            double gap = (controlUtc - lastControlTickUtc).TotalMilliseconds;
            if (gap > maxControlGapSinceProbeMs)
                maxControlGapSinceProbeMs = gap;
        }
        lastControlTickUtc = controlUtc;

        // Worth its own line whenever it moves: losing the pedal claim mid-maneuver is invisible in
        // every other field, and the brake probe only fires while a brake demand is out.
        string controlState = ReadOverrideState();
        if (controlState != lastOverrideState)
        {
            lastOverrideState = controlState;
            Logger.Info($"AutoParking: {controlState}（v={Math.Abs(state.SignedSpeed) * 3.6:0.0} km/h " +
                        $"sent={output.LastAcceleration:0.00} 剩={active.RemainingDistance:0.0} m · {active.Status}）");
        }

        CheckPedalFallback(demand, state);
        ProbeBrakeChannel(demand, state, active);
        ProbeGearChannel(demand, state, active);

        if (active.Replans != lastReplanCount)
        {
            lastReplanCount = active.Replans;
            Logger.Info($"AutoParking: replanned {active.Replans}/{settings.MaxReplans} - {active.Status}");
        }

        lock (sync)
        {
            if (phase == ParkingPhase.Engaging)
                phase = ParkingPhase.Following;

            // A pulse that goes out on its own is the gearbox stage; one sent while driving is
            // only a repeat and must not change what the banner says.
            if (demand.Gear != GearRequest.None && demand.Throttle == 0f)
                phase = ParkingPhase.GearHold;
            else if (phase == ParkingPhase.GearHold)
                phase = ParkingPhase.Following;

            if (demand.Finished)
            {
                phase = ParkingPhase.HoldingBrake;
                phaseDeadlineUtc = DateTime.UtcNow.AddSeconds(2.0);
            }
        }
    }

    private VehicleState ReadVehicleState()
    {
        GameTelemetryData data;
        DateTime sampleUtc;
        lock (sync)
        {
            data = latestTelemetry;
            sampleUtc = lastTelemetryUtc;
        }

        Vector2 position = new((float)data.truckPlacement.coordinate.X, (float)data.truckPlacement.coordinate.Z);
        double heading = Geometry.HeadingFromRotationComponent(data.truckPlacement.rotation.X);
        int gear = data.truckInt.gear;
        double speed = Math.Abs(data.truckFloat.speed);

        // Timestamp the sample, not the tick. The tick runs at 60 Hz while telemetry arrives far
        // slower, so feeding one sample into the PID repeatedly inflated the integral and the
        // derivative by the ratio between the two rates - a throttle kick whenever the measured
        // speed dipped, which then overshot straight into the overspeed abort.
        DateTime now = sampleUtc;
        double signedSpeed = speed * Math.Sign(gear);

        if (gear == 0)
        {
            // Neutral takes away the gear sign, so fall back to how the position is moving.
            double dt = (now - lastPositionUtc).TotalSeconds;
            if (dt > 0.02 && dt < 1.0)
            {
                Vector2 delta = position - lastPosition;
                double axial = delta.X * (float)Math.Sin(heading) + delta.Y * (float)Math.Cos(heading);
                signedSpeed = speed * Math.Sign(-axial);
            }
        }

        lastPosition = position;
        lastPositionUtc = now;

        // Human takeover is no longer an abort condition, but keep showing the raw player inputs
        // next to what we sent: it is the only way to see on screen who is actually moving the
        // truck when the game is being driven from two places at once.
        lastRawUserInputs = $"user=({data.truckFloat.userSteer:0.00},{data.truckFloat.userThrottle:0.00},{data.truckFloat.userBrake:0.00}) " +
                            $"sent=({output.LastSteer:0.00},{output.LastThrottle:0.00},{output.LastBrake:0.00}) " +
                            $"game=({data.truckFloat.gameSteer:0.00},{data.truckFloat.gameThrottle:0.00},{data.truckFloat.gameBrake:0.00}) " +
                            $"v={speed * 3.6:0.0}km/h gear={gear}/{data.truckInt.gearDashboard} shifter={data.configString.shifterType}";

        return new VehicleState(now, position, heading, signedSpeed, gear, data.truckInt.gearDashboard);
    }

    /// <summary>
    ///  What the game-side plugin says it is controlling, read straight out of its status mapping.
    ///  Missing mapping (plugin not loaded, or not on Windows) reports `?` rather than failing the run.
    /// </summary>
    private string ReadOverrideState()
    {
        if (!statusTried && OperatingSystem.IsWindows())
        {
            statusTried = true;
            try
            {
                statusFile = MemoryMappedFile.OpenExisting(PluginStatusMapName);
                statusAccessor = statusFile.CreateViewAccessor(0, 6, MemoryMappedFileAccess.Read);
            }
            catch
            {
                statusFile = null;
                statusAccessor = null;
            }
        }

        if (statusAccessor == null)
            return "接管=?";

        try
        {
            return $"接管=转向{(statusAccessor.ReadBoolean(4) ? "on" : "off")} 踏板{(statusAccessor.ReadBoolean(5) ? "on" : "off")}";
        }
        catch
        {
            return "接管=?";
        }
    }

    /// <summary>
    ///  Watches the brake path for the one failure the status map cannot show: the game-side plugin
    ///  claiming the pedal while the write never reaches the wheels. Any of the three physical
    ///  echoes (deceleration, service-line pressure drop, game-side brake echo) clears the
    ///  suspicion window; none of them for the whole window with a full brake demand out and the
    ///  truck still moving is a dead memory channel, and we degrade to the legacy virtual gamepad
    ///  for the rest of the session. That device is already trusted with the gear and handbrake
    ///  pulses, and the switch costs nothing - the memory pedal was doing nothing anyway.
    /// </summary>
    private void CheckPedalFallback(ControlDemand demand, VehicleState state)
    {
        if (pedalFallbackEngaged || settings.DryRun)
            return;

        // Only the memory write can fail silently this way; the legacy gamepad's health shows up
        // in the same echoes and the gear channel has been riding it all along.
        if (!ETS2LA.Game.GameSettings.Current.EnableModernOutputForPedals)
            return;

        float speedKph = (float)(Math.Abs(state.SignedSpeed) * 3.6);
        if (demand.Brake < PedalFallbackBrakeDemand || speedKph < PedalFallbackMinSpeedKph)
        {
            // No demand, or already stopped - the healthy end state, not evidence of a dead pedal.
            pedalSuspicionUtc = DateTime.MinValue;
            return;
        }

        float gameBrake, air;
        lock (sync)
        {
            gameBrake = latestTelemetry.truckFloat.gameBrake;
            air = latestTelemetry.truckFloat.airPressure;
        }

        if (pedalSuspicionUtc == DateTime.MinValue)
        {
            pedalSuspicionUtc = state.Utc;
            pedalSuspicionSpeedKph = speedKph;
            pedalSuspicionAir = air;
            return;
        }

        double seconds = (state.Utc - pedalSuspicionUtc).TotalSeconds;
        if (seconds < 0.05)
            return;

        double decelMps2 = (pedalSuspicionSpeedKph - speedKph) / 3.6 / seconds;
        bool airDropped = pedalSuspicionAir - air > PedalFallbackMinAirDropPsi;
        bool echoPresent = gameBrake >= PedalFallbackMinEchoBrake;

        if (decelMps2 > PedalFallbackMinDecelMps2 || airDropped || echoPresent)
        {
            pedalSuspicionUtc = state.Utc;
            pedalSuspicionSpeedKph = speedKph;
            pedalSuspicionAir = air;
            return;
        }

        if (seconds < PedalFallbackWindowSeconds)
            return;

        pedalFallbackEngaged = true;
        pedalSuspicionUtc = DateTime.MinValue;

        // Deliberately not GameSettings.Save(): this is a session-long degradation of a channel
        // that just proved dead, not a preference change. The user's saved choice comes back on
        // the next app start, and the host picks the new value up on its next output tick.
        ETS2LA.Game.GameSettings.Current.EnableModernOutputForPedals = false;

        Logger.Warn($"AutoParking: 满刹车 {PedalFallbackWindowSeconds:0}s 无减速（a={decelMps2:0.00} m/s²）、" +
                    $"气压 {pedalSuspicionAir:0.0}→{air:0.0}、game_brake={gameBrake:0.00}，判定 memory 踏板通道未生效，" +
                    $"已切回 legacy 虚拟手柄（本次运行有效，重启后恢复你的设置）");
        Notify(NotificationLevel.Danger, "检测到 memory 踏板未生效（满刹车无减速），已自动切换到 legacy 虚拟手柄。", 10);
    }

    /// <summary>
    ///  Samples the brake path while we are asking for full braking. A demand that does not slow
    ///  the vehicle is the one failure the status table cannot show after the fact - the maneuver
    ///  is over by then - so it goes to the log as it happens. The sent value against a实测 and the
    ///  game-side claim is what settles whether the pedal reached the wheels at all.
    /// </summary>
    private void ProbeBrakeChannel(ControlDemand demand, VehicleState state, Follower active)
    {
        if (demand.Brake < 0.5f || settings.DryRun)
            return;

        if ((state.Utc - lastBrakeProbeUtc).TotalSeconds < 0.25)
            return;

        lastBrakeProbeUtc = state.Utc;

        float userBrake, userThrottle, airPressure, brakeTemp, gameBrake, gameThrottle, cruise;
        bool parkingBrake;
        int gear;
        lock (sync)
        {
            userBrake = latestTelemetry.truckFloat.userBrake;
            userThrottle = latestTelemetry.truckFloat.userThrottle;
            gameBrake = latestTelemetry.truckFloat.gameBrake;
            gameThrottle = latestTelemetry.truckFloat.gameThrottle;
            cruise = latestTelemetry.truckFloat.cruiseControlSpeed;
            airPressure = latestTelemetry.truckFloat.airPressure;
            brakeTemp = latestTelemetry.truckFloat.brakeTemperature;
            parkingBrake = latestTelemetry.truckBool.parkingBrake;
            gear = latestTelemetry.truckInt.gear;
        }

        double sampleSpeed = Math.Abs(state.SignedSpeed);
        double probeGap = (state.Utc - lastProbeSpeedUtc).TotalSeconds;
        string measured = probeGap > 0.05 && probeGap < 2.0
            ? $"a实测={(sampleSpeed - lastProbeSpeed) / probeGap:0.00} m/s²"
            : "a实测=-";
        lastProbeSpeed = sampleSpeed;
        lastProbeSpeedUtc = state.Utc;

        // Measured on the legacy transport: the SCS virtual controller arrives as a player device, so
        // game_brake stayed zero no matter how hard we braked and user_brake was the echo to read.
        // Under `memory` output that has not been re-measured, which is why the transport is printed
        // here. Air pressure is transport-independent - it falls only when the brake is actually
        // applied at the wheels, so it stays the hardest evidence of the three.
        // a实测 is the tie-breaker between those: sent -1.0 with a实测 near 0 while air falls and the
        // discs heat means the pedal is moving somewhere that is not this truck's wheels.
        // 接管 and stall then say which half failed: 踏板off with a small stall is the host or the
        // game-side reader losing the claim, 踏板off with a stall over ~200 ms is our own tick.
        string overrideState = ReadOverrideState();
        double stallMs = maxControlGapSinceProbeMs;
        maxControlGapSinceProbeMs = 0.0;

        Logger.Info($"AutoParking: 制动探针 transport={PedalTransport} v={Math.Abs(state.SignedSpeed) * 3.6:0.0} km/h " +
                    $"{measured} {overrideState} stall={stallMs:0}ms 已发={output.Published} 屏蔽={output.Suppressed} " +
                    $"sent_accel={output.LastAcceleration:0.00} (brake={demand.Brake:0.00} throttle={demand.Throttle:0.00}) " +
                    $"steer={output.LastSteer:0.00} 剩={active.RemainingDistance:0.0} 横={active.CrossTrackErrorMeters:0.00} " +
                    $"航向差={active.HeadingErrorDegrees:0.0} " +
                    $"user_brake={userBrake:0.00} user_throttle={userThrottle:0.00} " +
                    $"game_brake={gameBrake:0.00} game_throttle={gameThrottle:0.00} cruise={cruise:0.0} " +
                    $"air={airPressure:0.00} brake_temp={brakeTemp:0.0} hand={parkingBrake} gear={gear}");
    }

    /// <summary>
    ///  Samples the gearbox action every time a pulse goes out, with the stage that asked for it.
    ///  Repeated pulses for a gear the telemetry already reports mean the confirmation is reading
    ///  the wrong signal, and only the status text says which stage is looping.
    /// </summary>
    private void ProbeGearChannel(ControlDemand demand, VehicleState state, Follower active)
    {
        if (demand.Gear == GearRequest.None || settings.DryRun)
            return;

        if ((state.Utc - lastGearProbeUtc).TotalSeconds < 0.25)
            return;

        lastGearProbeUtc = state.Utc;

        GameTelemetryData data;
        lock (sync)
        {
            data = latestTelemetry;
        }

        Logger.Info($"AutoParking: 挡位探针 请求={demand.Gear} → gear={data.truckInt.gear} " +
                    $"dash={data.truckInt.gearDashboard} slot={data.truckUI.shifterSlot} " +
                    $"shifter_type={data.configString.shifterType} rpm={data.truckFloat.engineRpm:0} " +
                    $"hand={data.truckBool.parkingBrake} v={Math.Abs(state.SignedSpeed) * 3.6:0.0} km/h " +
                    $"剩={active.RemainingDistance:0.0} m · {active.Status}");
    }

    private void CompletePhase(ParkingPhase from)
    {
        output.Release();
        RestoreAssists();

        lock (sync)
        {
            follower = null;
            phase = ParkingPhase.Idle;
        }

        Notify(NotificationLevel.Success, from == ParkingPhase.HoldingBrake ? "泊车完成。" : "已中止并交还控制。", 6);
    }

    private void TakeOverAssists()
    {
        ApplicationState state = ApplicationState.Current;
        savedEnableAssists = state.EnableAssists;
        savedDesiredSpeed = state.DesiredSpeed;
        savedDrivingMode = state.DrivingMode;
        hadAssistSnapshot = true;

        state.EnableAssists = false;
    }

    private void RestoreAssists()
    {
        if (!hadAssistSnapshot)
            return;

        hadAssistSnapshot = false;
        if (!settings.RestoreAssistsOnFinish)
            return;

        ApplicationState state = ApplicationState.Current;

        // We turned assists off, so an enabled flag now means the user took charge - leave it.
        if (state.EnableAssists)
            return;

        state.DesiredSpeed = savedDesiredSpeed;
        state.DrivingMode = savedDrivingMode;
        state.EnableAssists = savedEnableAssists;
    }

    private void OnToggleKeyPressed(object? sender, ControlChangeEventArgs e)
    {
        if (e.NewValue is bool pressed && pressed)
            HandleToggle();
    }

    private void OnAbortKeyPressed(object? sender, ControlChangeEventArgs e)
    {
        if (e.NewValue is bool pressed && pressed)
            Abort("快捷键中止");
    }

    public override void OnDisable()
    {
        base.OnDisable();

        // Never leave the vehicle mid-maneuver or the assists switched off because we went away.
        output.Release();
        RestoreAssists();

        ControlsBackend.Current.UnregisterListener(toggleControl.Id, OnToggleKeyPressed);
        ControlsBackend.Current.UnregisterListener(abortControl.Id, OnAbortKeyPressed);

        mapOverlay?.Unregister();
        mapOverlay = null;
        arOverlay?.Unregister();
        arOverlay = null;
        controlPlot?.SetVisible(false);
        controlPlot = null;
        kdopReader?.Stop();
        kdopReader = null;

        Events.Current.Unsubscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);
        GameTelemetry.Current.ReadTrailerData = previousReadTrailerData;

        settingsHandler?.UnregisterListener<AutoParkingSettings>(SettingsFileName, OnSettingsChanged);
        settingsHandler?.Dispose();
        settingsHandler = null;

        lock (sync)
        {
            phase = ParkingPhase.Idle;
        }
    }

    public override void Shutdown()
    {
        OnDisable();
    }

    // MARK: State exposed to the overlays

    public ParkingPhase Phase
    {
        get
        {
            lock (sync) return phase;
        }
    }

    public Pose2 CurrentPose
    {
        get
        {
            lock (sync)
            {
                return new Pose2(latestTelemetry.truckPlacement.coordinate.X,
                                 latestTelemetry.truckPlacement.coordinate.Z,
                                 Geometry.HeadingFromRotationComponent(latestTelemetry.truckPlacement.rotation.X));
            }
        }
    }

    public Pose2? TargetPose
    {
        get
        {
            lock (sync) return target;
        }
    }

    public MapGeometry? MapGeometrySnapshot
    {
        get
        {
            lock (sync) return mapGeometry;
        }
    }

    /// <summary>
    ///  Latest game-memory item snapshot (runtime cones etc.), or null before the first read.
    ///  Null also when the reader is disabled or the game is absent - the map just skips the layer.
    /// </summary>
    internal KdopReader.Snapshot? MemoryItemSnapshot => kdopReader?.Current;

    /// <summary>
    ///  Measured contents of the sampled circle, as one line: the map window is 560 px tall and the
    ///  canvas inside it is the point of the window, so the per-class detail goes to the log instead
    ///  (设置 -> 地图清单).
    /// </summary>
    public string MapProbeSummary
    {
        get
        {
            lock (sync) return mapGeometry?.Probe.Summary ?? "";
        }
    }

    /// <summary>
    ///  Height of the ground plane under the vehicle. The telemetry truck origin is the
    ///  vehicle's own origin point, not its contact patch, so drawing at that height puts
    ///  everything about a metre in the air - and the gap differs between a car and a truck.
    ///  Derived from the lowest wheel that is on the ground: hub height minus wheel radius.
    /// </summary>
    public double GroundY
    {
        get
        {
            lock (sync)
            {
                return latestTelemetry.truckPlacement.coordinate.Y + GroundContactOffsetNoLock + settings.ArGroundTrimM;
            }
        }
    }

    public double GroundContactOffsetM
    {
        get
        {
            lock (sync) return GroundContactOffsetNoLock;
        }
    }

    private double GroundContactOffsetNoLock
    {
        get
        {
            if (groundOffsetCacheUtc == lastTelemetryUtc && lastTelemetryUtc != DateTime.MinValue)
                return cachedGroundOffset;

            cachedGroundOffset = ComputeGroundContactOffset(latestTelemetry);
            groundOffsetCacheUtc = lastTelemetryUtc;
            return cachedGroundOffset;
        }
    }

    private static double ComputeGroundContactOffset(GameTelemetryData data)
    {
        float[] radii = data.configFloat.truckWheelRadius;
        Vector3[] wheels = data.configVector.truckWheelPositions;
        bool[] onGround = data.truckBool.truckWheelOnGround;

        if (radii == null || wheels == null)
            return 0.0;

        double grounded = double.NaN;
        double any = double.NaN;

        for (int i = 0; i < 16; i++)
        {
            if (i >= radii.Length || i >= wheels.Length)
                break;

            float radius = radii[i];
            if (radius <= 0.05f || float.IsNaN(radius))
                continue;

            double contact = wheels[i].Y - radius;
            if (float.IsNaN((float)contact))
                continue;

            if (double.IsNaN(any) || contact < any)
                any = contact;

            bool touching = onGround != null && i < onGround.Length && onGround[i];
            if (touching && (double.IsNaN(grounded) || contact < grounded))
                grounded = contact;
        }

        double offset = double.IsNaN(grounded) ? any : grounded;
        if (double.IsNaN(offset))
            return 0.0;

        // A real vehicle sits between ground level and a couple of metres below its origin.
        return Math.Clamp(offset, -3.0, 0.0);
    }

    /// <summary>
    ///  Move the spot without touching the settings file. Used while dragging to set the
    ///  heading, which fires every frame - persisting there would rewrite the file dozens of
    ///  times per drag and re-enter through the settings watcher.
    /// </summary>
    public void SetTargetTransient(Pose2 pose)
    {
        lock (sync)
        {
            target = pose;
            phase = ParkingPhase.Selecting;
        }
    }

    public void SetTarget(Pose2? pose)
    {
        lock (sync)
        {
            target = pose;
            phase = pose == null ? ParkingPhase.Idle : ParkingPhase.Selecting;

            settings.HasSavedSpot = pose != null;
            if (pose != null)
            {
                settings.SavedSpotX = pose.Value.X;
                settings.SavedSpotZ = pose.Value.Z;
                settings.SavedSpotHeadingRad = pose.Value.HeadingRad;
            }

            // Only flag it: the settings handler writes the file and calls back into our
            // listener, and this runs on the ImGui render thread while holding the lock the
            // UI thread needs to paint. The tick thread does the write instead.
            settingsDirty = true;
        }
    }

    public void AdjustZoom(double factor)
    {
        lock (sync)
        {
            settings.MapZoom = Math.Clamp(settings.MapZoom * factor, 1.0, 4.0);
            settingsDirty = true;
        }
    }

    private void OnTelemetry(GameTelemetryData data)
    {
        lock (sync)
        {
            latestTelemetry = data;
            lastTelemetryUtc = DateTime.UtcNow;
            telemetryStale = false;
        }

        CalibrateWheelbase(data);
    }

    /// <summary>
    ///  Turn the wheel positions the game publishes into a wheelbase, so the minimum turning radius
    ///  stops being a number typed into a settings file.
    ///
    ///  Recomputed whenever the layout signature moves rather than latched once: switching trucks in
    ///  a garage changes the wheel configuration while the process keeps running, and a value measured
    ///  from the previous truck would otherwise be used forever. The arrays hold a handful of wheels,
    ///  so this is not a per-tick cost even at telemetry rate.
    /// </summary>
    private void CalibrateWheelbase(GameTelemetryData data)
    {
        Vector3[]? wheels = data.configVector.truckWheelPositions;
        int count = data.configUI.truckWheelCount;

        string signature = wheels == null
            ? "none"
            : $"{count}|{wheels.Length}|"
              + string.Join(";", wheels.Take(Math.Min(count == 0 ? 0 : Math.Min(count, wheels.Length), 8))
                                 .Select(p => $"{p.X:0.##},{p.Y:0.##},{p.Z:0.##}"));

        WheelbaseMeasurement measurement = wheelbaseMeasurement;

        lock (sync)
        {
            if (signature != wheelbaseSignature)
            {
                wheelbaseSignature = signature;
                measurement = VehicleGeometry.Measure(wheels, data.configBool.truckWheelSteerable,
                                                      data.configBool.truckWheelLiftable, count);
                wheelbaseMeasurement = measurement;

                if (!measurement.Valid)
                    Logger.Info($"AutoParking: 轴距未能实测（{measurement.Reason}），继续用设置里的 {settings.WheelbaseM:0.00} m");
            }

            if (!measurement.Valid)
                return;

            if (settings.AutoCalibrateRadius
                && Math.Abs(settings.WheelbaseM - measurement.WheelbaseM) >= 0.02)
            {
                double previous = settings.WheelbaseM;
                settings.WheelbaseM = measurement.WheelbaseM;
                settings.Clamp();

                Logger.Info($"AutoParking: 轴距实测 {measurement.WheelbaseM:0.00} m，覆盖设置里的 {previous:0.00} m"
                            + $"（{measurement.Axles.Count} 轴，前向轴={measurement.AlongIsZ}，"
                            + $"轮距 {measurement.FrontTrackM:0.00}/{measurement.RearTrackM:0.00} m，"
                            + $"轴位 {string.Join(" | ", measurement.Axles)}）");
            }

            // The width comes from the same wheel positions: the outermost centre plus a tyre
            // allowance. The hand-typed 2.6 m was wider than this tractor by more than half a metre,
            // and the obstacle envelope is built straight off it (design doc §51).
            // The box the planner sweeps, sized from the truck that is actually being driven: the
            // game's own local AABB when the memory reader has it (and that is the only source for the
            // length at all), otherwise wheel centres plus the tyre allowance for the width and no
            // change to the length. Design doc §52 - the typed 6.5 × 2.6 m was 1.9 m shorter and 0.13 m
            // wider than the 6x2 standing on the course.
            if (settings.AutoCalibrateBody)
            {
                KdopReader.TruckBody body = MemoryItemSnapshot?.Body ?? default;
                double measuredWidth = body.Available ? body.WidthM
                                                     : VehicleGeometry.MeasuredWidthM(measurement, settings.TireHalfWidthM);
                double measuredLength = body.Available ? body.LengthM : 0.0;

                double previousWidth = settings.VehicleWidthM, previousLength = settings.VehicleLengthM;
                bool widthChanged = measuredWidth >= 1.7 && Math.Abs(previousWidth - measuredWidth) >= 0.02;
                bool lengthChanged = measuredLength > 4.0 && Math.Abs(previousLength - measuredLength) >= 0.02;
                if (widthChanged)
                    settings.VehicleWidthM = measuredWidth;
                if (lengthChanged)
                    settings.VehicleLengthM = measuredLength;

                if (widthChanged || lengthChanged)
                {
                    settings.Clamp();
                    Logger.Info($"AutoParking: 车体实测 {(body.Available ? "游戏包围盒" : "轮心+胎宽")} "
                                + $"{settings.VehicleLengthM:0.00} × {settings.VehicleWidthM:0.00} m"
                                + (body.Available ? $"（高 {body.HeightM:0.00} m）" : $"（胎宽 {settings.TireHalfWidthM:0.00}/侧）")
                                + $"，覆盖设置里的 {previousLength:0.00} × {previousWidth:0.00} m"
                                + $"；障碍包络 {settings.EnvelopeLengthM:0.00} × {settings.EnvelopeWidthM:0.00} m");
                }
            }
        }
    }

    private void OnSettingsChanged(AutoParkingSettings newSettings)
    {
        lock (sync)
        {
            settings = newSettings ?? new AutoParkingSettings();
            settings.Clamp();
        }
    }

    // MARK: Commands

    public void HandleAction(string actionId, object? value)
    {
        Logger.Info($"AutoParking: command {actionId}");

        bool changed = false;

        lock (sync)
        {
            switch (actionId)
            {
                case "dryRun":
                    settings.DryRun = ToBool(value, settings.DryRun);
                    changed = true;
                    break;
                case "controlWeight":
                    settings.ControlWeight = ToDouble(value, settings.ControlWeight);
                    changed = true;
                    break;
                case "refuseWithTrailer":
                    settings.RefuseWithTrailer = ToBool(value, settings.RefuseWithTrailer);
                    changed = true;
                    break;
                case "restoreAssists":
                    settings.RestoreAssistsOnFinish = ToBool(value, settings.RestoreAssistsOnFinish);
                    changed = true;
                    break;
                case "maxTakeoverDistance":
                    settings.MaxTakeoverDistanceM = ToDouble(value, settings.MaxTakeoverDistanceM);
                    changed = true;
                    break;
                case "showMapWindow":
                    settings.ShowMapWindow = ToBool(value, settings.ShowMapWindow);
                    changed = true;
                    break;
                case "showArOverlay":
                    settings.ShowArOverlay = ToBool(value, settings.ShowArOverlay);
                    changed = true;
                    break;
                case "showControlPlot":
                    settings.ShowControlPlot = ToBool(value, settings.ShowControlPlot);
                    changed = true;
                    break;
                case "showMemoryItems":
                    settings.ShowMemoryItems = ToBool(value, settings.ShowMemoryItems);
                    changed = true;
                    break;
                case "blockRouteWithMemoryItems":
                    settings.BlockRouteWithMemoryItems = ToBool(value, settings.BlockRouteWithMemoryItems);
                    changed = true;
                    break;
                case "envelopePad":
                    settings.EnvelopePadM = ToDouble(value, settings.EnvelopePadM);
                    changed = true;
                    break;
                case "smoothForwardLeadIn":
                    settings.SmoothForwardLeadInM = ToDouble(value, settings.SmoothForwardLeadInM);
                    changed = true;
                    break;
                case "snapToParkingBay":
                    settings.SnapToParkingBay = ToBool(value, settings.SnapToParkingBay);
                    changed = true;
                    break;
                case "snapToNavCurve":
                    settings.SnapToNavCurve = ToBool(value, settings.SnapToNavCurve);
                    changed = true;
                    break;
                case "corridorHalfWidth":
                    settings.ConfirmedCorridorHalfWidthM = ToDouble(value, settings.ConfirmedCorridorHalfWidthM);
                    changed = true;
                    break;
                case "lockMapWindow":
                    settings.LockMapWindow = ToBool(value, settings.LockMapWindow);
                    changed = true;
                    break;
                case "vehicleLength":
                    settings.VehicleLengthM = ToDouble(value, settings.VehicleLengthM);
                    changed = true;
                    break;
                case "vehicleWidth":
                    settings.VehicleWidthM = ToDouble(value, settings.VehicleWidthM);
                    changed = true;
                    break;
                case "autoCalibrateRadius":
                    settings.AutoCalibrateRadius = ToBool(value, settings.AutoCalibrateRadius);
                    changed = true;
                    break;
                case "autoCalibrateBody":
                    settings.AutoCalibrateBody = ToBool(value, settings.AutoCalibrateBody);
                    changed = true;
                    break;
                case "tireHalfWidth":
                    settings.TireHalfWidthM = ToDouble(value, settings.TireHalfWidthM);
                    changed = true;
                    break;
                case "wheelbase":
                    settings.WheelbaseM = ToDouble(value, settings.WheelbaseM);
                    changed = true;
                    break;
                case "maxSteerDeg":
                    settings.MaxSteerDeg = ToDouble(value, settings.MaxSteerDeg);
                    changed = true;
                    break;
                case "arGroundTrim":
                    settings.ArGroundTrimM = ToDouble(value, settings.ArGroundTrimM);
                    changed = true;
                    break;
                case "forwardSpeed":
                    settings.ForwardSpeedKph = ToDouble(value, settings.ForwardSpeedKph);
                    changed = true;
                    break;
                case "reverseSpeed":
                    settings.ReverseSpeedKph = ToDouble(value, settings.ReverseSpeedKph);
                    changed = true;
                    break;
                case "comfortDecel":
                    settings.ComfortDecel = ToDouble(value, settings.ComfortDecel);
                    changed = true;
                    break;
                case "launchAccel":
                    settings.LaunchAccelMps2 = ToDouble(value, settings.LaunchAccelMps2);
                    changed = true;
                    break;
                case "pidKp":
                    settings.PidKp = ToDouble(value, settings.PidKp);
                    changed = true;
                    break;
                case "pidKi":
                    settings.PidKi = ToDouble(value, settings.PidKi);
                    changed = true;
                    break;
                case "pidKd":
                    settings.PidKd = ToDouble(value, settings.PidKd);
                    changed = true;
                    break;
                case "toleranceLateral":
                    settings.ToleranceLateralM = ToDouble(value, settings.ToleranceLateralM);
                    changed = true;
                    break;
                case "toleranceHeading":
                    settings.ToleranceHeadingDeg = ToDouble(value, settings.ToleranceHeadingDeg);
                    changed = true;
                    break;
                case "obstacleLookahead":
                    settings.ObstacleLookaheadM = ToDouble(value, settings.ObstacleLookaheadM);
                    changed = true;
                    break;
                case "replanWhileStopped":
                    settings.ReplanWhileStopped = ToBool(value, settings.ReplanWhileStopped);
                    changed = true;
                    break;
                case "maxReplans":
                    settings.MaxReplans = (int)ToDouble(value, settings.MaxReplans);
                    changed = true;
                    break;
                case "handbrakeOnFinish":
                    settings.HandbrakeOnFinish = ToBool(value, settings.HandbrakeOnFinish);
                    changed = true;
                    break;
                case "planRadiusMargin":
                    settings.PlanRadiusMargin = ToDouble(value, settings.PlanRadiusMargin);
                    changed = true;
                    break;
                case "terminalStraight":
                    settings.TerminalStraightM = ToDouble(value, settings.TerminalStraightM);
                    changed = true;
                    break;
                case "steerGain":
                    settings.SteerGain = ToDouble(value, settings.SteerGain);
                    changed = true;
                    break;
                case "steerDeadband":
                    settings.SteerDeadband = ToDouble(value, settings.SteerDeadband);
                    changed = true;
                    break;
                case "steerRateLimit":
                    settings.SteerRateLimitPerSecond = ToDouble(value, settings.SteerRateLimitPerSecond);
                    changed = true;
                    break;
                case "lookaheadBase":
                    settings.LookaheadBaseM = ToDouble(value, settings.LookaheadBaseM);
                    changed = true;
                    break;
                case "lookaheadGain":
                    settings.LookaheadGainMps = ToDouble(value, settings.LookaheadGainMps);
                    changed = true;
                    break;
                case "reverseLaw":
                    settings.ReverseLateral = ToInt(value, 0) == 1 ? ReverseLateralLaw.CrossTrackPd : ReverseLateralLaw.ReversePurePursuit;
                    changed = true;
                    break;
                case "clearTarget":
                    bool hadTarget = target != null;
                    target = null;
                    planResult = null;
                    phase = ParkingPhase.Idle;
                    lastActionFailed = false;
                    lastActionMessage = hadTarget ? "已清除车位" : "本来就没有选车位";
                    break;
            }

            if (changed)
            {
                settings.Clamp();
                settingsDirty = true;
            }
        }

        // Commands are dispatched outside the lock: they reach into notifications, the
        // ApplicationState singleton and the overlay, while the 60 Hz tick thread and the
        // ImGui render thread are both waiting on the same lock every frame.
        switch (actionId)
        {
            case "start":
                StartParking();
                break;
            case "stop":
                Abort("stopped by user");
                break;
            case "selfTest":
                RunSelfTest();
                break;
            case "dumpMapInventory":
                DumpMapInventory();
                break;
            case "testGearDrive":
            case "testGearReverse":
                TestGearPulse(actionId == "testGearDrive" ? GearRequest.Drive : GearRequest.Reverse);
                break;
        }

        ApplyOverlayVisibility();
    }

    /// <summary>
    ///  Fires a single gearbox action while nothing is driving, so the answer to "can boolean
    ///  actions reach the game at all?" comes from the gear readout in the status table instead
    ///  of from a whole maneuver. Pedals are analog axes and are known to work; shifting is not
    ///  the same code path in the host, and the whole gear state machine depends on it.
    /// </summary>
    private void TestGearPulse(GearRequest request)
    {
        GameTelemetryData data;
        bool allowed;
        lock (sync)
        {
            allowed = follower == null && phase == ParkingPhase.Idle && !settings.DryRun;
            data = latestTelemetry;
            if (allowed)
            {
                lastActionFailed = false;
                lastActionMessage = $"已发出 {request}，看下面状态表的 gear 是否变号";
            }
            else
            {
                lastActionFailed = true;
                lastActionMessage = "换挡自检只能在未启用车库、且关掉 Dry-run 时按";
            }
        }

        if (!allowed)
            return;

        output.DryRun = settings.DryRun;
        output.PulseGear(request);

        Logger.Info($"AutoParking: 换挡自检 {request}：按下前 gear={data.truckInt.gear}/{data.truckInt.gearDashboard} " +
                    $"shifter_type={data.configString.shifterType} v={Math.Abs(data.truckFloat.speed) * 3.6:0.0} km/h，" +
                    $"1 秒后看状态表的 gear 是否变成 {(request == GearRequest.Reverse ? "负数" : "正数")}");
    }

    private void ApplyOverlayVisibility()
    {
        bool showMap;
        bool showAr;
        bool showPlot;
        MapOverlay? map;
        ArOverlay? ar;
        ControlPlot? plot;

        lock (sync)
        {
            showMap = settings.ShowMapWindow;
            showAr = settings.ShowArOverlay;
            showPlot = settings.ShowControlPlot;
            map = mapOverlay;
            ar = arOverlay;
            plot = controlPlot;
        }

        map?.SetOpen(showMap);

        if (showAr)
            ar?.EnsureRegistered();
        else
            ar?.Unregister();

        plot?.SetVisible(showPlot);
    }

    public string StartParking()
    {
        Logger.Info("AutoParking: start requested");

        lock (sync)
        {
            string? rejection = ValidateStart();
            if (rejection != null)
            {
                lastRejectReason = rejection;
                lastActionFailed = true;
                lastActionMessage = "开始被拒绝：" + rejection;
                Notify(NotificationLevel.Warning, rejection, 6);
                Logger.Warn($"AutoParking: start rejected: {rejection}");
                return rejection;
            }

            lastRejectReason = "";

            try
            {
                Pose2 spot = target.Value;
                AutoParkingSettings cfg = settings;

                // Captured once: a map rebuild mid-maneuver must not leave the closure comparing a new
                // route against a different trust grid than the one the engaged route was measured on.
                GroundTrust? planTrust = mapGeometry?.Trust;

                follower = new Follower(planResult!.Path!, settings, settings.WheelbaseM,
                    (from, field) =>
                    {
                        // Ask the planner the same question we asked at engage time, only from the
                        // pose we are actually standing at now. Null when there is nothing usable,
                        // which leaves the follower on the route it already has.
                        PlanResult next = Planner.Plan(from, spot, cfg, field, planTrust);
                        return next.Ok ? next.Path : null;
                    })
                {
                    Trace = controlTrace
                };

                // A fresh timeline per maneuver: leaving the previous run in the buffer makes the
                // plot a smear across two different routes and hides the thing being tuned.
                controlTrace.Reset();

                TakeOverAssists();
                phase = ParkingPhase.Engaging;
                warnedNoAir = false;
            }
            catch (Exception ex)
            {
                follower = null;
                phase = ParkingPhase.Idle;
                lastActionFailed = true;
                lastActionMessage = $"启动异常：{ex.GetType().Name}: {ex.Message}";
                Logger.Warn($"AutoParking: engage threw {ex}");
                return lastActionMessage;
            }

            lastActionFailed = false;
            // Progress comes from the real vehicle position, so a Dry-run engagement shows the
            // demands for the current sample and then holds there; it does not walk the route.
            lastActionMessage = settings.DryRun
                ? $"已开始（Dry-run）：{planResult!.Summary} —— 不发控制，车不动，路径进度停在起点，看控制器行确认拟发数值"
                : $"已开始：{planResult!.Summary}";
        }

        output.DryRun = settings.DryRun;
        string mode = settings.DryRun ? "（Dry-run：只演算不输出）" : "";
        Notify(NotificationLevel.Information, $"开始泊车：{planResult!.Summary}{mode}", 6);
        Logger.Info($"AutoParking: engaged: {planResult.Summary}, dry-run={settings.DryRun}");
        return "";
    }

    /// <summary>
    ///  One key for the whole lifecycle: idle -> start, running -> pause, paused -> resume.
    ///  Pause releases every channel and sets the handbrake, so the truck cannot roll away or
    ///  keep a stale steering angle while we are not in control.
    /// </summary>
    public void HandleToggle()
    {
        ParkingPhase current;
        lock (sync) current = phase;

        switch (current)
        {
            case ParkingPhase.Paused:
                Resume();
                return;

            case ParkingPhase.Engaging:
            case ParkingPhase.Following:
            case ParkingPhase.GearHold:
                Pause();
                return;

            default:
                HandleAction("start", null);
                return;
        }
    }

    private void Pause()
    {
        lock (sync)
        {
            phase = ParkingPhase.Paused;
            lastActionFailed = false;
            lastActionMessage = "已暂停：手刹已拉起，再按一次主热键继续";
        }

        output.Release();
        output.Apply(new ControlDemand(0f, 0f, 0f, true, GearRequest.None, false));
        Logger.Info("AutoParking: paused by hotkey");
    }

    private void Resume()
    {
        lock (sync)
        {
            if (follower == null)
            {
                lastActionFailed = true;
                lastActionMessage = "没有可继续的任务（已中止或已完成）";
                return;
            }

            phase = ParkingPhase.Following;
            lastActionFailed = false;
            lastActionMessage = "已继续泊车";
        }

        output.Apply(new ControlDemand(0f, 0f, 0f, false, GearRequest.None, false));
        Logger.Info("AutoParking: resumed by hotkey");
    }

    public void Abort(string reason)
    {
        lock (sync)
        {
            if (phase != ParkingPhase.Engaging && phase != ParkingPhase.Following
                && phase != ParkingPhase.GearHold && phase != ParkingPhase.Paused)
            {
                lastActionFailed = false;
                lastActionMessage = "当前没有在跑的泊车任务，无需中止";
                return;
            }

            phase = ParkingPhase.Aborted;
            phaseDeadlineUtc = DateTime.UtcNow.AddSeconds(3.0);
            lastRejectReason = $"aborted: {reason}";
            lastActionFailed = true;
            lastActionMessage = "已中止：" + reason + "（保持制动 3 秒后交还）";
        }

        Logger.Warn($"AutoParking: aborted: {reason}");
        Notify(NotificationLevel.Warning, $"泊车中止：{reason}（保持制动 3 秒后交还）", 8);
    }

    public string RunSelfTest()
    {
        AutoParkingSettings snapshot = Settings;
        PlannerSelfTest.Report report = PlannerSelfTest.Run(snapshot);

        lock (sync)
        {
            selfTestSummary = report.Summary;
            lastActionFailed = report.Failures.Count > 0;
            lastActionMessage = "规划自检：" + report.Summary;
        }

        if (report.Failures.Count == 0)
        {
            Logger.Info($"AutoParking: planner self-test passed: {report.Summary}");
        }
        else
        {
            Logger.Warn($"AutoParking: planner self-test FAILED: {report.Summary}");
            foreach (string failure in report.Failures)
            {
                Logger.Warn($"AutoParking:   - {failure}");
            }
        }

        return report.Summary;
    }

    private string? ValidateStart()
    {
        if (phase == ParkingPhase.Paused)
            return "有一个已暂停的泊车任务，先按主热键继续或按中止键放弃";

        if (phase != ParkingPhase.Idle && phase != ParkingPhase.Selecting)
            return "已有一个泊车任务在跑";

        if (target == null)
            return "还没在平面地图上选择车位";

        if (telemetryStale)
            return "遥测超时，请确认游戏在运行且 SDK 已连接";

        if (!latestTelemetry.sdkActive || latestTelemetry.paused)
            return "SDK 未激活或游戏已暂停";

        if (settings.RefuseWithTrailer && IsTrailerAttached())
            return "本版本不支持带挂车，请先摘挂";

        if (planResult == null)
            return "还没有规划结果（地图数据未就绪？）";

        if (!planResult.Ok)
            return planResult.ConflictCount > 0
                ? $"路径上有 {planResult.ConflictCount} 处障碍冲突，先清场或换个车位"
                : planResult.Reason;

        if (planResult.Path!.Length > settings.MaxTakeoverDistanceM * 2.0 + settings.EntryDistanceM)
            return "路径过长，请先把车开到车位附近";

        return null;
    }

    private bool IsTrailerAttached()
    {
        TrailerInfo[]? trailers = latestTelemetry.trailers;
        if (trailers == null || trailers.Length == 0)
            return false;

        for (int i = 0; i < trailers.Length; i++)
        {
            if (trailers[i]?.comBool?.attached == true)
                return true;
        }

        return false;
    }

    // MARK: Status

    public string LastActionMessage
    {
        get
        {
            lock (sync) return lastActionMessage;
        }
    }

    public bool LastActionFailed
    {
        get
        {
            lock (sync) return lastActionFailed;
        }
    }

    public string RawUserInputs
    {
        get
        {
            lock (sync) return lastRawUserInputs;
        }
    }

    public string SpeedUnitAbbreviation => UnitConversions.GetUnitAbbreviation(UnitType.Speed, ApplicationState.Current.DisplayUnits);

    public IReadOnlyList<string> ReverseLawOptions { get; } = new[] { "反向 Pure Pursuit", "交叉误差 PD" };

    public Dictionary<string, string> StatusRows
    {
        get
        {
            lock (sync)
            {
                double ageSeconds = lastTelemetryUtc == DateTime.MinValue
                    ? double.PositiveInfinity
                    : (DateTime.UtcNow - lastTelemetryUtc).TotalSeconds;

                int gear = latestTelemetry.truckInt.gear;
                double signedSpeed = Math.Abs(latestTelemetry.truckFloat.speed) * Math.Sign(gear);
                string gearLabel = gear > 0 ? $"D{gear}" : (gear < 0 ? $"R{Math.Abs(gear)}" : "N");

                string targetRow = "-";
                if (target != null)
                {
                    Vector2 truckPlane = new((float)latestTelemetry.truckPlacement.coordinate.X,
                                             (float)latestTelemetry.truckPlacement.coordinate.Z);
                    double distance = Geometry.Distance(truckPlane, target.Value.Position);
                    targetRow = $"{target.Value.X:0.0}, {target.Value.Z:0.0} / {target.Value.YawDegrees:0}° / {distance:0.0} m";
                }

                return new Dictionary<string, string>
                {
                    { "相位", phase.ToString() },
                    { "遥测", ageSeconds > 1.0 ? $"{ageSeconds:0.0}s 前（已过期）" : $"{ageSeconds:0.0}s 前" },
                    { "SDK", latestTelemetry.sdkActive ? (latestTelemetry.paused ? "已连接（游戏暂停）" : "已连接") : "未连接" },
                    { "挡位", gearLabel },
                    { "有符号速度", $"{FormatSpeed(signedSpeed)}（{SpeedUnitAbbreviation}）" },
                    { "挂车", IsTrailerAttached() ? "已挂（本版本拒绝执行）" : "空车" },
                    { "目标车位", targetRow },
                    { "规划结果", planResult?.Summary ?? "未规划" },
                    { "控制器", follower?.Status ?? "未启用" },
                    { "重规划", follower == null ? "未启用" : $"{follower.Replans} / {settings.MaxReplans} 次" },
                    { "拟发输出", $"转向 {output.LastSteer:0.00} · 油门 {output.LastThrottle:0.00} · 刹车 {output.LastBrake:0.00} · 手刹 {(output.LastHandbrake ? "on" : "off")}" },
                    { "通道计数", $"已发 {output.Published} / Dry-run 屏蔽 {output.Suppressed}" },
                    { "人工输入", lastRawUserInputs },
                    { "制动能力", $"{latestTelemetry.truckFloat.airPressure:0.0} bar（应急 {latestTelemetry.configFloat.airPressureEmergency:0.0}）" +
                                (HasBrakeAir() ? "" : " · 行车制动无效") },
                    { "踏板通路", $"{PedalTransport}（memory=直写内存，失焦仍生效；legacy=虚拟手柄，失焦即失效；挡位/手刹恒走 legacy）" },
                    { "踏板回退", pedalFallbackEngaged
                        ? "已触发：memory 刹车未生效，本次运行走 legacy（重启后恢复原设置）"
                        : (PedalTransport == "memory" ? "待命（满刹车 2s 无减速且气压不降时触发）" : "不需要（已在 legacy）") },
                    { "轴距", $"{settings.WheelbaseM:0.00} m（实测 " +
                             (wheelbaseMeasurement.Valid ? $"{wheelbaseMeasurement.WheelbaseM:0.00}" : wheelbaseMeasurement.Reason) +
                             $"，{wheelbaseMeasurement.Axles.Count} 轴，轮距 {wheelbaseMeasurement.FrontTrackM:0.00}/" +
                             $"{wheelbaseMeasurement.RearTrackM:0.00}，自动标定 {(settings.AutoCalibrateRadius ? "开" : "关")}）" },
                    { "车身", $"{settings.VehicleLengthM:0.00} × {settings.VehicleWidthM:0.00} m（" +
                             (MemoryItemSnapshot?.Body is { Available: true } measured
                                 ? $"游戏包围盒，高 {measured.HeightM:0.00}"
                                 : wheelbaseMeasurement.Valid
                                     ? $"轮心+胎宽 {settings.TireHalfWidthM:0.00}/侧，长无实测源"
                                     : "未实测") +
                             $"，自动适应 {(settings.AutoCalibrateBody ? "开" : "关")}）" +
                             $"→ 障碍包络 {settings.EnvelopeLengthM:0.00} × {settings.EnvelopeWidthM:0.00} m" },
                    { "最小转弯半径", $"{Kinematics.MinTurnRadius(settings):0.00} m" },
                    { "地面基准", $"{GroundContactOffsetNoLock:0.00} m（+微调 {settings.ArGroundTrimM:0.00}）" },
                    { "地图数据", mapStatus },
                    { "地图内容", mapGeometry?.Probe.Summary ?? "未构建（平面地图窗口要开着）" },
                    { "规划自检", selfTestSummary },
                    { "输出", settings.DryRun ? "Dry-run（不发控制）" : "真实输出" },
                    { "最近一次拒绝/中止", lastRejectReason == "" ? "-" : lastRejectReason }
                };
            }
        }
    }

    private string FormatSpeed(double scientificMetersPerSecond)
    {
        double display = UnitConversions.FromScientificUnits(UnitType.Speed, (float)scientificMetersPerSecond, ApplicationState.Current.DisplayUnits);
        return display.ToString("0.0", CultureInfo.CurrentCulture);
    }

    private void Notify(NotificationLevel level, string content, float closeAfter)
    {
        NotificationHandler.Current.SendNotification(new Notification
        {
            Id = $"{PluginId}.Status",
            Title = "Auto Parking",
            Content = content,
            Level = level,
            CloseAfter = closeAfter
        });
    }

    private static bool ToBool(object? value, bool fallback)
    {
        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out bool parsed) => parsed,
            _ => fallback
        };
    }

    private static int ToInt(object? value, int fallback)
    {
        return value switch
        {
            int i => i,
            float f => (int)f,
            double d => (int)d,
            string s when int.TryParse(s, out int parsed) => parsed,
            _ => fallback
        };
    }

    private static double ToDouble(object? value, double fallback)
    {
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            string s when double.TryParse(s, NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => fallback
        };
    }
}
