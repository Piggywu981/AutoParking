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

    private void PublishDrive(ControlDemand demand)
    {
        ControlVariables variables = new()
        {
            steering = Math.Clamp(demand.Steer, -1f, 1f),
            aforward = Math.Clamp(demand.Throttle, 0f, 1f),
            abackward = Math.Clamp(demand.Brake, 0f, 1f)
        };

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

    private void PublishGear(GearRequest request)
    {
        if (request == GearRequest.None)
            return;

        ControlVariables variables = new()
        {
            geardrive = request == GearRequest.Drive,
            gearreverse = request == GearRequest.Reverse,
            gear0 = request == GearRequest.Neutral
        };

        Publish(gearChannel, new ControlProperties { BooleanType = ControlBooleanType.TrueToToggle, Weight = Weight }, variables);
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
