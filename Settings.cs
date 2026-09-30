using System;

namespace AutoParking;

public enum ReverseLateralLaw
{
    CrossTrackPd,
    ReversePurePursuit
}

[Serializable]
public sealed class AutoParkingSettings
{
    public int SettingsVersion { get; set; } = 1;

    // General
    public bool DryRun { get; set; } = true;
    public bool RefuseWithTrailer { get; set; } = true;

    // Engagement limits
    public double MaxTakeoverDistanceM { get; set; } = 30.0;
    public double EntryDistanceM { get; set; } = 12.0;
    public double OvershootM { get; set; } = 0.5;

    // Vehicle geometry
    public double WheelbaseM { get; set; } = 4.0;
    public double MaxSteerDeg { get; set; } = 33.0;
    public double VehicleLengthM { get; set; } = 6.5;
    public double VehicleWidthM { get; set; } = 2.6;
    public bool AutoCalibrateRadius { get; set; } = true;

    // Speeds
    public double ForwardSpeedKph { get; set; } = 6.0;
    public double ReverseSpeedKph { get; set; } = 3.0;
    public double ComfortDecel { get; set; } = 0.5;
    public double MaxAccel { get; set; } = 1.0;
    public double MaxBrakeDecel { get; set; } = 1.2;
    public double LaunchAccelMps2 { get; set; } = 0.6;

    // Longitudinal PID
    public double PidKp { get; set; } = 0.35;
    public double PidKi { get; set; } = 0.05;
    public double PidKd { get; set; } = 0.02;

    // Lateral
    public double ReverseKxCross { get; set; } = 0.06;
    public double ReverseKhHeading { get; set; } = 1.2;
    public double LookaheadBaseM { get; set; } = 1.5;
    public double LookaheadGainMps { get; set; } = 0.6;
    public ReverseLateralLaw ReverseLateral { get; set; } = ReverseLateralLaw.ReversePurePursuit;

    // Planning
    public double GearSwitchPenaltyM { get; set; } = 6.0;
    public double PathSampleM { get; set; } = 0.25;

    // Obstacles
    public double ObstacleMarginM { get; set; } = 0.5;
    public double ObstacleWaitS { get; set; } = 8.0;

    // Tolerances and aborts
    public double MaxCrossErrorM { get; set; } = 2.0;
    public double ToleranceLateralM { get; set; } = 0.20;
    public double ToleranceHeadingDeg { get; set; } = 4.0;
    public double MaxDurationS { get; set; } = 180.0;

    public bool UserOverrideEnabled { get; set; } = true;
    public double UserSteerThreshold { get; set; } = 0.15;
    public double UserThrottleThreshold { get; set; } = 0.10;
    public double UserBrakeThreshold { get; set; } = 0.05;

    // Finish behaviour
    public bool HandbrakeOnFinish { get; set; } = true;
    public bool HazardOnFinish { get; set; } = false;
    public bool EngineOffOnFinish { get; set; } = false;
    public bool RestoreAssistsOnFinish { get; set; } = true;

    // Visualisation
    public bool ShowMapWindow { get; set; } = true;
    public bool ShowArOverlay { get; set; } = true;
    public double ArGroundTrimM { get; set; } = 0.0;
    public double MapScalePxPerM { get; set; } = 1.25;
    public double MapZoom { get; set; } = 1.0;
    public double MapViewRadiusM { get; set; } = 120.0;
    public bool SnapToNavCurve { get; set; } = true;

    // Saved so reloading the plugin does not silently lose the spot you picked.
    public bool HasSavedSpot { get; set; } = false;
    public double SavedSpotX { get; set; }
    public double SavedSpotZ { get; set; }
    public double SavedSpotHeadingRad { get; set; }

    public AutoParkingSettings Clone() => (AutoParkingSettings)MemberwiseClone();

    public double EffectiveMapPixelsPerMeter => Math.Clamp(MapScalePxPerM * MapZoom, 0.25, 6.0);

    public double MapDataRadiusM(double windowSizePx)
    {
        double visibleRadius = windowSizePx / (2.0 * EffectiveMapPixelsPerMeter);
        return Math.Max(MapViewRadiusM, visibleRadius + 20.0);
    }

    public void Clamp()
    {
        if (SettingsVersion < 1)
            SettingsVersion = 1;

        MaxTakeoverDistanceM = Math.Clamp(MaxTakeoverDistanceM, 5.0, 100.0);
        EntryDistanceM = Math.Clamp(EntryDistanceM, 6.0, 30.0);
        OvershootM = Math.Clamp(OvershootM, 0.0, 2.0);

        WheelbaseM = Math.Clamp(WheelbaseM, 2.5, 7.5);
        MaxSteerDeg = Math.Clamp(MaxSteerDeg, 20.0, 45.0);
        VehicleLengthM = Math.Clamp(VehicleLengthM, 4.0, 12.0);
        VehicleWidthM = Math.Clamp(VehicleWidthM, 1.8, 3.2);

        ForwardSpeedKph = Math.Clamp(ForwardSpeedKph, 2.0, 15.0);
        ReverseSpeedKph = Math.Clamp(ReverseSpeedKph, 1.0, 8.0);
        ComfortDecel = Math.Clamp(ComfortDecel, 0.2, 1.5);
        MaxAccel = Math.Clamp(MaxAccel, 0.3, 3.0);
        MaxBrakeDecel = Math.Clamp(MaxBrakeDecel, 0.3, 3.0);
        LaunchAccelMps2 = Math.Clamp(LaunchAccelMps2, 0.1, 1.5);

        PidKp = Math.Clamp(PidKp, 0.0, 2.0);
        PidKi = Math.Clamp(PidKi, 0.0, 1.0);
        PidKd = Math.Clamp(PidKd, 0.0, 1.0);

        ReverseKxCross = Math.Clamp(ReverseKxCross, 0.01, 0.30);
        ReverseKhHeading = Math.Clamp(ReverseKhHeading, 0.2, 4.0);
        LookaheadBaseM = Math.Clamp(LookaheadBaseM, 0.5, 4.0);
        LookaheadGainMps = Math.Clamp(LookaheadGainMps, 0.0, 2.0);

        GearSwitchPenaltyM = Math.Clamp(GearSwitchPenaltyM, 0.0, 20.0);
        PathSampleM = Math.Clamp(PathSampleM, 0.1, 1.0);

        ObstacleMarginM = Math.Clamp(ObstacleMarginM, 0.2, 1.5);
        ObstacleWaitS = Math.Clamp(ObstacleWaitS, 2.0, 30.0);

        MaxCrossErrorM = Math.Clamp(MaxCrossErrorM, 0.5, 5.0);
        ToleranceLateralM = Math.Clamp(ToleranceLateralM, 0.05, 1.0);
        ToleranceHeadingDeg = Math.Clamp(ToleranceHeadingDeg, 1.0, 15.0);
        MaxDurationS = Math.Clamp(MaxDurationS, 30.0, 600.0);

        UserSteerThreshold = Math.Clamp(UserSteerThreshold, 0.05, 0.5);
        UserThrottleThreshold = Math.Clamp(UserThrottleThreshold, 0.05, 0.5);
        UserBrakeThreshold = Math.Clamp(UserBrakeThreshold, 0.02, 0.5);

        MapScalePxPerM = Math.Clamp(MapScalePxPerM, 0.5, 3.0);
        MapZoom = Math.Clamp(MapZoom, 1.0, 4.0);
        MapViewRadiusM = Math.Clamp(MapViewRadiusM, 20.0, 200.0);
        ArGroundTrimM = Math.Clamp(ArGroundTrimM, -1.0, 1.0);
    }
}
