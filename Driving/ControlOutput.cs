using System;
using ETS2LA.Backend.Events;
using ETS2LA.Game.Output;

namespace AutoParking;

/// <summary>
///  The only place in the plugin that publishes driving commands.
///
///  GameOutput averages every channel that touches the same field, weighted by
///  <see cref="ControlProperties.Weight"/>, then clamps to [-1, 1] and writes the result. Only two
///  buckets exist by name: <c>steering</c> and <c>acceleration</c> (which <c>aforward</c> and
///  <c>abackward</c> are folded into), and both are averaged identically, so a competing plugin
///  smears our wheel rather than losing to us. Dropping a channel is not instant: an unrefreshed
///  channel survives until its timeout, so the drive channel must be published every tick.
///  Booleans always go to the legacy segment, and <c>TrueToToggle</c> fires as a pulse on a task,
///  which is why gear pulses must be sent once and the handbrake must be explicitly released or it
///  stays engaged after we go away.
/// </summary>
public sealed class ControlOutput
{
    private const string ChannelPrefix = "local.autoparking.";

    /// <summary>Set from the settings page every tick; see <c>AutoParkingSettings.ControlWeight</c>.</summary>
    public float Weight = 20.0f;

    // The game-side reader drops its own claim on the pedal after 0.2 s without a fresh write
    // (`core.cpp:184-187`), and when it drops it does not clear the pedal value it last injected
    // (`core.cpp:231-239`). A channel that times out therefore does not return the truck to the
    // driver, it returns it to a stale pedal, so surviving a tick stall is the safer failure.
    private readonly ControlChannelDefinition driveChannel = new() { Id = ChannelPrefix + "drive", Timeout = 1.0f };
    private readonly ControlChannelDefinition gearChannel = new() { Id = ChannelPrefix + "gear", Timeout = 0.5f };
    private readonly ControlChannelDefinition holdChannel = new() { Id = ChannelPrefix + "hold", Timeout = 0.5f };

    private bool holdPublished;

    public bool DryRun { get; set; }

    public int Published { get; private set; }

    public int Suppressed { get; private set; }

    /// <summary>What the controller last asked for, for display and diagnostics.</summary>
    public float LastSteer { get; private set; }
    public float LastThrottle { get; private set; }
    public float LastBrake { get; private set; }
    public bool LastHandbrake { get; private set; }

    /// <summary>The signed acceleration actually published: positive throttle, negative brake.</summary>
    public float LastAcceleration { get; private set; }

    /// <summary>
    ///  The published acceleration floor used in place of an exact zero. On the memory transport the
    ///  host claims the game-side override only while the averaged value is non-zero
    ///  (<c>Output.cs:304</c> writes <c>override_acceleration = weightedValue != 0</c>), and giving it
    ///  up does not clear the pedal the game was last handed (<c>core.cpp:231-239</c> only logs). An
    ///  exact zero from us is therefore not "coast" - it is "stop refreshing, keep whatever pedal was
    ///  last latched". A creep brake value keeps the override held and the wheel state known.
    /// </summary>
    private const float OverrideFloorAcceleration = -0.01f;

    public void Apply(ControlDemand demand)
    {
        LastSteer = demand.Steer;
        LastThrottle = demand.Throttle;
        LastBrake = demand.Brake;
        LastHandbrake = demand.HoldBrake;

        PublishDrive(demand);
        PublishHold(demand.HoldBrake);
        PublishGear(demand.Gear);
    }

    /// <summary>One gearbox action, for the settings-page self-check.</summary>
    public void PulseGear(GearRequest request) => PublishGear(request);

    private void PublishDrive(ControlDemand demand)
    {
        float acceleration = Math.Clamp(demand.Throttle - demand.Brake, -1f, 1f);

        if (acceleration == 0.0f)
            acceleration = OverrideFloorAcceleration;

        LastAcceleration = acceleration;

        ControlVariables variables = new() { steering = Math.Clamp(demand.Steer, -1f, 1f) };

        // GameOutput folds aforward and abackward into one "acceleration" bucket and averages
        // every contribution in it, then writes the pedal by sign: positive to aforward,
        // negative to abackward (negated). Publishing both fields therefore halves the demand
        // and flips braking into throttle - exactly what a real run showed, where commanding
        // abackward=1.0 produced a steady user_throttle=0.5 and 50 km/h. Send one signed field.
        // The official ACC does the same thing with an if/else on one ControlVariables, so this is
        // the intended shape of the API rather than a workaround of ours.
        if (acceleration >= 0.0f)
            variables.aforward = acceleration;
        else
            variables.abackward = acceleration;

        Publish(driveChannel, new ControlProperties { BooleanType = ControlBooleanType.Direct, Weight = Weight }, variables);
    }

    private void PublishHold(bool hold)
    {
        if (hold)
        {
            Publish(holdChannel, new ControlProperties { BooleanType = ControlBooleanType.Direct, Weight = Weight },
                    new ControlVariables { parkingbrake = true });
            holdPublished = true;
            return;
        }

        if (holdPublished)
        {
            // Write the release explicitly before dropping the channel, otherwise the game keeps
            // the last value it was given and the handbrake stays on.
            Publish(holdChannel, new ControlProperties { BooleanType = ControlBooleanType.Direct, Weight = Weight },
                    new ControlVariables { parkingbrake = false });
            ReleaseChannel(holdChannel);
            holdPublished = false;
        }
    }

    /// <summary>
    ///  One event per pulse. The host re-fires a TrueToToggle on every refresh of the channel,
    ///  so republishing this from a 60 Hz loop would hammer the gearbox action.
    /// </summary>
    private void PublishGear(GearRequest request)
    {
        if (request == GearRequest.None)
            return;

        ControlVariables variables = request switch
        {
            GearRequest.Drive => new ControlVariables { geardrive = true },
            GearRequest.Reverse => new ControlVariables { gearreverse = true },
            GearRequest.Neutral => new ControlVariables { gear0 = true },
            _ => new ControlVariables()
        };

        Publish(gearChannel, new ControlProperties { BooleanType = ControlBooleanType.TrueToToggle, Weight = Weight },
                variables);
    }

    /// <summary>
    ///  Let go of everything. Called on abort, on finish and on plugin disable.
    /// </summary>
    public void Release()
    {
        if (holdPublished)
        {
            Publish(holdChannel, new ControlProperties { BooleanType = ControlBooleanType.Direct, Weight = Weight },
                    new ControlVariables { parkingbrake = false });
            holdPublished = false;
        }

        ReleaseChannel(driveChannel);
        ReleaseChannel(gearChannel);
        ReleaseChannel(holdChannel);

        LastSteer = 0f;
        LastAcceleration = 0f;
        LastThrottle = 0f;
        LastBrake = 0f;
        LastHandbrake = false;
    }

    private void Publish(ControlChannelDefinition channel, ControlProperties properties, ControlVariables variables)
    {
        if (DryRun)
        {
            Suppressed++;
            return;
        }

        Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent
        {
            ChannelDefinition = channel,
            Properties = properties,
            Variables = variables
        });

        Published++;
    }

    private void ReleaseChannel(ControlChannelDefinition channel)
    {
        if (DryRun)
        {
            Suppressed++;
            return;
        }

        // An event with empty variables makes GameOutput remove the channel.
        Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent
        {
            ChannelDefinition = channel,
            Properties = new ControlProperties(),
            Variables = new ControlVariables()
        });
    }
}
