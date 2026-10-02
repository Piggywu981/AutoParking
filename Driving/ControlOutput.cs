using System;
using ETS2LA.Backend.Events;
using ETS2LA.Game.Output;

namespace AutoParking;

/// <summary>
///  The only place in the plugin that publishes driving commands.
///
///  GameOutput merges every channel that touches the same field by weight, drops channels that
///  have not been refreshed within their timeout, and writes booleans straight into shared
///  memory - so the drive channel must be refreshed every tick, gear pulses must be sent once,
///  and the handbrake must be explicitly released or it stays engaged after we go away.
/// </summary>
public sealed class ControlOutput
{
    private const string ChannelPrefix = "local.autoparking.";
    private const float Weight = 5.0f;

    private readonly ControlChannelDefinition driveChannel = new() { Id = ChannelPrefix + "drive", Timeout = 0.3f };
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
        LastAcceleration = acceleration;

        ControlVariables variables = new() { steering = Math.Clamp(demand.Steer, -1f, 1f) };

        // GameOutput folds aforward and abackward into one "acceleration" bucket and averages
        // every contribution in it, then writes the pedal by sign: positive to aforward,
        // negative to abackward (negated). Publishing both fields therefore halves the demand
        // and flips braking into throttle - exactly what a real run showed, where commanding
        // abackward=1.0 produced a steady user_throttle=0.5 and 50 km/h. Send one signed field.
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
