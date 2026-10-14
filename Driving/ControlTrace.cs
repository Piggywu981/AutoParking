using System;
using System.Diagnostics;

namespace AutoParking;

/// <summary>
///  One control-cycle snapshot of everything the follower decided, taken where the decisions are made
///  rather than reconstructed from telemetry afterwards.
///
///  A single scalar in the status table cannot show *shape*: whether the cross-track error converged
///  or oscillated, whether the steer limiter is fighting the pursuit law, whether the integral wound
///  up before a slip. Those are only visible as a time series, and the official tuning aids agree -
///  PIDLib keeps ten seconds of P/I/D history and plots it (see
///  docs/2026-10-14-official-plugin-reverse-engineering-applied.md §3.2).
/// </summary>
public readonly struct ControlSample
{
    /// <summary>Seconds since the trace was reset. The plot's x axis, not wall clock.</summary>
    public double Elapsed { get; init; }

    public double CrossTrackM { get; init; }

    public double HeadingErrorDeg { get; init; }

    /// <summary>Pursuit/PD output before the rate limiter.</summary>
    public double SteerRaw { get; init; }

    /// <summary>What was actually published, after the rate limiter.</summary>
    public double Steer { get; init; }

    public double TargetSpeedMps { get; init; }

    public double SpeedMps { get; init; }

    public double PidP { get; init; }

    public double PidI { get; init; }

    public double PidD { get; init; }

    /// <summary>The returned m/s^2 demand, after the launch and approach-to-stop clamps.</summary>
    public double CommandMps2 { get; init; }

    public bool Reversing { get; init; }
}

/// <summary>
///  Fixed-capacity ring of <see cref="ControlSample"/>. Never allocates after construction, because
///  the 60 Hz tick and the overlay render thread both touch it every frame.
///
///  Reading is done under <see cref="Gate"/> into caller-owned buffers rather than by copying a
///  window out: 600 samples is roughly 60 KB per frame of garbage if copied, and the official
///  PIDLib monitor pays exactly that cost each render.
/// </summary>
public sealed class ControlTrace
{
    public const int Capacity = 1024;

    private readonly ControlSample[] samples = new ControlSample[Capacity];
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private int head;
    private int count;

    /// <summary>Hold this while reading <see cref="Samples"/>, <see cref="Head"/> and <see cref="Count"/>.</summary>
    public object Gate { get; } = new();

    public ControlSample[] Samples => samples;
    public int Head => head;
    public int Count => count;

    /// <summary>Seconds since the last <see cref="Reset"/>; the plot's right edge.</summary>
    public double NowSeconds => clock.Elapsed.TotalSeconds;

    public void Add(in ControlSample sample)
    {
        // Overwrite the caller's clock so Elapsed is always on this trace's timeline.
        ControlSample stamped = sample with { Elapsed = clock.Elapsed.TotalSeconds };

        lock (Gate)
        {
            samples[head] = stamped;
            head = (head + 1) % Capacity;
            if (count < Capacity)
                count++;
        }
    }

    /// <summary>Starts a fresh timeline. Call when a maneuver begins so the plot is not a smear of
    ///  two runs at different speeds.</summary>
    public void Reset()
    {
        lock (Gate)
        {
            head = 0;
            count = 0;
            clock.Restart();
        }
    }
}
