using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    private string mapStatus = "尚未构建";
    private PlanResult? planResult;
    private string selfTestSummary = "尚未运行";
    private DateTime groundOffsetCacheUtc = DateTime.MinValue;
    private double cachedGroundOffset;

    private readonly ControlOutput output = new();
    private Follower? follower;
    private DateTime phaseDeadlineUtc = DateTime.MinValue;
    private DateTime lastBrakeProbeUtc = DateTime.MinValue;
    private DateTime lastGearProbeUtc = DateTime.MinValue;
    private int lastReplanCount;
    private bool warnedNoAir;

    // Reverse distance inside which the parking brake may be used to stop the truck.
    private const double ParkingBrakeStopDistanceM = 2.0;

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
        mapOverlay.Register(settings.ShowMapWindow);
        arOverlay.Register();

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
        MapGeometry geometry = geometryBuilder.Build(map, truck.Position, radius);

        lock (sync)
        {
            mapGeometry = geometry;
            mapStatus = $"{geometry.Status} · {geometry.BuildMilliseconds:0} ms";
        }

        Replan(geometry);
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

        PlanResult result = Planner.Plan(CurrentPose, spot.Value, snapshot, geometry.Obstacles);

        lock (sync) planResult = result;
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
    ///  Samples the brake path while we are asking for full braking. A demand that does not slow
    ///  the vehicle is the one failure the status table cannot show after the fact - the maneuver
    ///  is over by then - so it goes to the log as it happens. sent_brake high with game_brake
    ///  near zero means the abackward axis is not the brake pedal.
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

        // Measured on the legacy transport: the SCS virtual controller arrives as a player device, so
        // game_brake stayed zero no matter how hard we braked and user_brake was the echo to read.
        // Under `memory` output that has not been re-measured, which is why the transport is printed
        // here. Air pressure is transport-independent - it falls only when the brake is actually
        // applied at the wheels, so it stays the hardest evidence of the three.
        Logger.Info($"AutoParking: 制动探针 transport={PedalTransport} v={Math.Abs(state.SignedSpeed) * 3.6:0.0} km/h " +
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
                case "snapToNavCurve":
                    settings.SnapToNavCurve = ToBool(value, settings.SnapToNavCurve);
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
        MapOverlay? map;
        ArOverlay? ar;

        lock (sync)
        {
            showMap = settings.ShowMapWindow;
            showAr = settings.ShowArOverlay;
            map = mapOverlay;
            ar = arOverlay;
        }

        map?.SetOpen(showMap);

        if (showAr)
            ar?.EnsureRegistered();
        else
            ar?.Unregister();
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

                follower = new Follower(planResult!.Path!, settings, settings.WheelbaseM,
                    (from, field) =>
                    {
                        // Ask the planner the same question we asked at engage time, only from the
                        // pose we are actually standing at now. Null when there is nothing usable,
                        // which leaves the follower on the route it already has.
                        PlanResult next = Planner.Plan(from, spot, cfg, field);
                        return next.Ok ? next.Path : null;
                    });

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
