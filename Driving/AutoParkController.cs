using System;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  The pure state machine behind multi-leg marker following (design doc §54): which target the
///  truck is currently driving to, how many legs of the maneuver have been acquired, whether the
///  game has moved the marker since the last accepted sample, and whether the settle window after
///  an arrival has elapsed. No IO, no threads, no clock reads - every timestamp arrives as an
///  argument, so the offline harness drives it exactly as the plugin will.
///
///  A "leg" is one planned drive to one acquired target, not one marker: the game moves the target
///  between stages (a 0xC17 pair at the stop zone, then a framing gap at the bay), and a marker that
///  moves mid-leg costs another leg. Legs therefore counts OnAcquired calls, deliberately - a
///  re-plan caused by a marker move goes through OnAcquired again.
/// </summary>
internal sealed class AutoParkController
{
    /// <summary>The target of the current leg, or null before the first acquisition / after Reset.</summary>
    public TargetResolver.Target? Active { get; private set; }

    /// <summary>Legs acquired since construction or the last Reset; the first acquisition makes it 1.</summary>
    public int Legs { get; private set; }

    /// <summary>When the current leg arrived, in the caller's clock; <c>default</c> means "not arrived".</summary>
    public DateTime ArrivedUtc { get; private set; }

    /// <summary>The uid of the marker the current leg follows; 0 means "no marker identity" (e.g. a framing gap).</summary>
    public ulong MarkerUid { get; private set; }

    /// <summary>
    ///  The marker position the current leg was last measured against: the acquired target's position
    ///  until MarkerMoved accepts a new one. MarkerMoved advances it, so the ~1 Hz poll never re-fires
    ///  on displacement that has already been acted on.
    /// </summary>
    public Vector2 LastMarkerPos { get; private set; }

    /// <summary>
    ///  Starts a leg: counts it, adopts its target and marker identity, anchors LastMarkerPos at the
    ///  target's position and clears the arrival stamp - a leg that has just begun has not arrived,
    ///  and it must not inherit the previous leg's settle window.
    /// </summary>
    public void OnAcquired(TargetResolver.Target t)
    {
        Legs++;
        Active = t;
        MarkerUid = t.Uid;
        LastMarkerPos = t.Pose.Position;
        ArrivedUtc = default;
    }

    /// <summary>
    ///  Re-anchors <see cref="LastMarkerPos"/> at <paramref name="pos"/> without counting a leg and
    ///  without touching <see cref="ArrivedUtc"/>: a re-anchor for the caller's sample space, not a
    ///  new acquisition. A 0xC17 pair resolves to the pair's midpoint while the target's uid names one
    ///  member of it, and MarkerMoved matches that member by uid - comparing the member against the
    ///  midpoint reads half the pair spacing as a move on the first poll and re-aims the leg at one
    ///  cone. Ignored with no active leg, like OnArrived: there is no leg whose sample space to
    ///  re-anchor, and resurrecting LastMarkerPos before one would un-gate the caller's poll.
    /// </summary>
    public void SeedLastMarkerPosition(Vector2 pos)
    {
        if (Active is null)
            return;

        LastMarkerPos = pos;
    }

    /// <summary>
    ///  True when the marker has moved further than <paramref name="toleranceM"/> from the position the
    ///  current leg last accepted, and on that answer LastMarkerPos advances to <paramref name="markerPos"/>,
    ///  so the next call measures from the accepted position instead of re-triggering on the same
    ///  displacement every poll. False leaves LastMarkerPos untouched.
    ///
    ///  With no active leg it returns false and does not advance anything: before the first OnAcquired
    ///  (and after Reset) LastMarkerPos is Vector2.Zero, and measuring from that origin would report
    ///  "moved" for any real sample, firing a caller's ~1 Hz poll on the first tick. This class owns the
    ///  state that defines "a leg is active", so the refusal lives here; a caller's own poll gating is
    ///  belt-and-braces rather than the only defence.
    /// </summary>
    public bool MarkerMoved(Vector2 markerPos, double toleranceM)
    {
        if (Active is null)
            return false;

        if (Vector2.Distance(LastMarkerPos, markerPos) <= toleranceM)
            return false;

        LastMarkerPos = markerPos;
        return true;
    }

    /// <summary>
    ///  Stamps the arrival of the current leg. Settled reads this; nothing else does. With no active leg
    ///  the call is ignored - there is nothing to settle, and stamping one would let Settled report true
    ///  for a maneuver that has no leg, the same impossible state the "not arrived yet" default keeps out.
    ///  As with MarkerMoved, the guard belongs here because this class owns "a leg is active".
    /// </summary>
    public void OnArrived(DateTime utcNow)
    {
        if (Active is null)
            return;

        ArrivedUtc = utcNow;
    }

    /// <summary>
    ///  True once <paramref name="settleTimeS"/> has elapsed since the current leg arrived, and false
    ///  before any arrival: <c>default</c> is not a moment in the past but "no arrival yet", and
    ///  subtracting it would report settled the instant a controller is constructed.
    /// </summary>
    public bool Settled(DateTime utcNow, double settleTimeS)
        => ArrivedUtc != default && (utcNow - ArrivedUtc).TotalSeconds >= settleTimeS;

    /// <summary>
    ///  Returns the controller to its constructed state, so a reused instance cannot inherit the
    ///  previous maneuver's target, leg count, marker identity, last-seen marker position or settle window.
    /// </summary>
    public void Reset()
    {
        Active = null;
        Legs = 0;
        MarkerUid = 0;
        LastMarkerPos = Vector2.Zero;
        ArrivedUtc = default;
    }
}
