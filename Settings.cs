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

    /// <summary>
    ///  Weight on our control channels. The host averages every channel that touches the same
    ///  field and splits the result by sign, so a lower weight does not lose politely: another
    ///  plugin publishing full throttle turns our brake demand into a positive average, our pedal
    ///  never reaches the wheels, and the truck drains its air reservoirs fighting it. Measured in
    ///  the game at an opposing weight of about 14, hence a default well above that.
    /// </summary>
    public double ControlWeight { get; set; } = 20.0;

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

    // Steering shaping: the geometric curvature command is scaled, deadbanded and then slew
    // limited so the wheel tracks proportionally instead of snapping to full lock.
    public double SteerGain { get; set; } = 1.0;
    public double SteerDeadband { get; set; } = 0.02;
    public double SteerRateLimitPerSecond { get; set; } = 1.5;
    public ReverseLateralLaw ReverseLateral { get; set; } = ReverseLateralLaw.ReversePurePursuit;

    // Planning
    public double GearSwitchPenaltyM { get; set; } = 6.0;

    /// <summary>
    ///  Plans on a circle larger than the vehicle's own minimum turning circle. Planning at the
    ///  limit makes every arc demand full steering lock, which leaves the controller no authority
    ///  to correct anything - a saturated actuator is no longer a linear one.
    /// </summary>
    public double PlanRadiusMargin { get; set; } = 1.35;

    /// <summary>
    ///  Length of the straight leg driven into the spot. The final heading of an arc is only
    ///  reached at the end of the arc, so without this a route stopped short leaves the bay
    ///  turned by the leftover arc; inside a straight tail any stopping point is aligned.
    /// </summary>
    public double TerminalStraightM { get; set; } = 1.5;
    public double PathSampleM { get; set; } = 0.25;

    // Obstacles
    public double ObstacleMarginM { get; set; } = 0.5;

    /// <summary>
    ///  Only conflicts this far ahead of the vehicle stop it. Anything further is not a reason to
    ///  stand still at the start of the maneuver - it is a reason to re-solve at the next gear
    ///  change, which is what makes segmented re-planning reachable at all.
    /// </summary>
    public double ObstacleLookaheadM { get; set; } = 4.0;

    // Re-planning
    public bool ReplanWhileStopped { get; set; } = true;
    public int MaxReplans { get; set; } = 3;

    // Tolerances and aborts
    public double ToleranceLateralM { get; set; } = 0.20;
    public double ToleranceHeadingDeg { get; set; } = 4.0;

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

    /// <summary>
    ///  Freeze the map panel's position and size. ImGui grabs a window from anywhere on its border,
    ///  and the canvas reaches to within Padding of that border, so a press meant for the edge of the
    ///  map moved the rect under the pick. Worth a switch rather than a permanent flag because the
    ///  panel genuinely needs repositioning some of the time.
    /// </summary>
    public bool LockMapWindow { get; set; } = false;

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

        ControlWeight = Math.Clamp(ControlWeight, 1.0, 100.0);
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
        SteerGain = Math.Clamp(SteerGain, 0.1, 3.0);
        SteerDeadband = Math.Clamp(SteerDeadband, 0.0, 0.2);
        SteerRateLimitPerSecond = Math.Clamp(SteerRateLimitPerSecond, 0.2, 6.0);

        GearSwitchPenaltyM = Math.Clamp(GearSwitchPenaltyM, 0.0, 20.0);
        PlanRadiusMargin = Math.Clamp(PlanRadiusMargin, 1.0, 2.5);
        TerminalStraightM = Math.Clamp(TerminalStraightM, 0.0, 5.0);
        PathSampleM = Math.Clamp(PathSampleM, 0.1, 1.0);

        ObstacleMarginM = Math.Clamp(ObstacleMarginM, 0.2, 1.5);
        ObstacleLookaheadM = Math.Clamp(ObstacleLookaheadM, 1.5, 15.0);
        MaxReplans = Math.Clamp(MaxReplans, 0, 8);

        ToleranceLateralM = Math.Clamp(ToleranceLateralM, 0.05, 1.0);
        ToleranceHeadingDeg = Math.Clamp(ToleranceHeadingDeg, 1.0, 15.0);

        MapScalePxPerM = Math.Clamp(MapScalePxPerM, 0.5, 3.0);
        MapZoom = Math.Clamp(MapZoom, 1.0, 4.0);
        MapViewRadiusM = Math.Clamp(MapViewRadiusM, 20.0, 200.0);
        ArGroundTrimM = Math.Clamp(ArGroundTrimM, -1.0, 1.0);
    }
}
