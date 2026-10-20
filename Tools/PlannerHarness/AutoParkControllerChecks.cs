using System;
using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for the pure marker-following controller: the leg bookkeeping the plugin's phase machine
///  will hang off (Task 8), with no game process, no planner and no clock reads in sight - every
///  timestamp below is passed in by the caller.
///
///  The two-leg sequence below is the one the academy job actually produces: acquire the 0xC17 pair,
///  arrive, wait out the settle window, then acquire the framing gap once the game has moved the
///  target. Each expected number is the value the marker protocol handed over, not a preference.
/// </summary>
internal static class AutoParkControllerChecks
{
    public static int Run()
    {
        int failures = 0;

        var c = new AutoParkController();
        var t1 = new TargetResolver.Target(new Pose2(2166.175, 1801.835, Math.PI / 2),
                                           "mover-pair:0xA", 0.9, 0xA, null, null);
        c.OnAcquired(t1);
        bool okA = c.Legs == 1 && c.MarkerUid == 0xA
                && c.Active is { } a1 && a1.Provenance == "mover-pair:0xA"
                && c.LastMarkerPos == t1.Pose.Position;
        Report(ref failures, okA,
               "Controller: OnAcquired 记腿数与标记 uid",
               $"legs {c.Legs} · uid 0x{c.MarkerUid:X} · last ({c.LastMarkerPos.X:0.000}, {c.LastMarkerPos.Y:0.000}) "
               + $"· {c.Active?.Provenance ?? "null"}（判据 legs 1 · uid 0xA · 位置=目标位置）");

        c.OnArrived(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));
        // The third term is the pre-arrival guard: a freshly constructed controller has no arrival
        // stamp at all, and `utcNow - default` is ~55 years, so the naive subtraction would report
        // "settled" before the truck ever arrived and chain the next leg on the spot. The last two
        // terms pin the comparison itself: exactly 3.0 s is settled (>=, not >), and 3.1 s tested
        // against a 5 s window is not - otherwise a hardcoded 2 s window or a `>` comparison would
        // survive every check while Task 8 feeds a user setting into this parameter.
        var fresh = new AutoParkController();
        bool okB = !fresh.Settled(DateTime.UtcNow, 3.0)
                && !c.Settled(c.ArrivedUtc.AddSeconds(1.0), 3.0)
                && c.Settled(c.ArrivedUtc.AddSeconds(3.0), 3.0)
                && !c.Settled(c.ArrivedUtc.AddSeconds(3.1), 5.0)
                && c.Settled(c.ArrivedUtc.AddSeconds(3.1), 3.0);
        Report(ref failures, okB,
               "Controller: SettleTimeS 未满不放行、满了放行",
               $"未到达 {fresh.Settled(DateTime.UtcNow, 3.0)} · 1.0s@{3.0} "
               + $"{c.Settled(c.ArrivedUtc.AddSeconds(1.0), 3.0)} "
               + $"· 3.0s@{3.0} {c.Settled(c.ArrivedUtc.AddSeconds(3.0), 3.0)} "
               + $"· 3.1s@{5.0} {c.Settled(c.ArrivedUtc.AddSeconds(3.1), 5.0)} "
               + $"· 3.1s@{3.0} {c.Settled(c.ArrivedUtc.AddSeconds(3.1), 3.0)}（判据 false / false / true / false / true）");

        // The last two terms pin MarkerMoved as a state transition, not a query: after the accepted
        // 1.83 m move, a sample 0.40 m from the NEW position must read as still (it is 2.23 m from the
        // old one), and only a later move beyond tolerance may fire again.
        bool okC = !c.MarkerMoved(new Vector2(2166.30f, 1802.00f), 0.5)
                && c.MarkerMoved(new Vector2(2168.0f, 1802.0f), 0.5)
                && !c.MarkerMoved(new Vector2(2168.4f, 1802.0f), 0.5)
                && c.MarkerMoved(new Vector2(2170.0f, 1802.0f), 0.5);
        Report(ref failures, okC,
               "Controller: 标记位移阈值",
               $"0.21m 忽略 · 1.83m 触发 · 距新位 0.40m 忽略 · 从采纳点再移 2.0m 触发（阈值 0.5 m）· "
               + $"last ({c.LastMarkerPos.X:0.000}, {c.LastMarkerPos.Y:0.000})");

        var t2 = new TargetResolver.Target(new Pose2(2174.33, 1797.42, Math.PI / 2),
                                           "framing-gap:8.78m", 0.6, 0, 3.23, new Vector2(2174.33f, 1797.42f));
        c.OnAcquired(t2);
        // The arrival stamp belongs to the leg that ended: taking over leg 2 must clear it, or the
        // second leg would inherit the first one's settle window and chain immediately. The settle
        // probe is derived from ArrivedUtc, not the clock, so the regression is caught on any day:
        // with the stamp cleared it is 10 s after `default` (never settled), and with the stamp kept
        // it is 10 s past the 2026 arrival (settled) - no dependence on when the harness runs.
        bool okD = c.Legs == 2 && c.MarkerUid == 0 && c.Active is { } a2 && a2.Confidence == 0.6
                && c.ArrivedUtc == default && !c.Settled(c.ArrivedUtc.AddSeconds(10.0), 3.0)
                && c.LastMarkerPos == t2.Pose.Position;
        Report(ref failures, okD,
               "Controller: 第二腿无缝接管（framing 目标）",
               $"legs {c.Legs} · uid {c.MarkerUid} · conf {c.Active?.Confidence:0.00} · "
               + $"arrived {c.ArrivedUtc:O}（判据 legs 2 · uid 0 · conf 0.6 · 落地时刻已清空）");

        // Reset is what a reused controller relies on: every field must go back to its initial value,
        // and the stale arrival stamp in particular, or the next maneuver would start already settled.
        // Leg 2 carries uid 0 (a framing gap), so the uid-bearing t1 is re-acquired first: with uid 0
        // already in place the `MarkerUid == 0` term below is vacuous, and a Reset that forgets
        // `MarkerUid = 0` would still print ✓ - that is the field Task 8 gates its ~1 Hz poll on, and a
        // stale uid after Reset would un-gate the poll with no leg active. Re-acquisition clears
        // ArrivedUtc (a new leg has not arrived), so the 2020 stamp is planted afterwards; Reset then
        // has all three things to clear: a non-zero uid, a non-zero LastMarkerPos and a real stamp.
        var staleStamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        c.OnAcquired(t1);
        c.OnArrived(staleStamp);
        c.Reset();
        // After Reset the controller is pre-acquisition again, so both of its pre-acquisition guards
        // must hold: OnArrived is ignored (Settled cannot report a window for a maneuver with no leg)
        // and MarkerMoved refuses the sample instead of measuring from Vector2.Zero and resurrecting
        // LastMarkerPos. The settle probe uses the planted stamp, never the clock, so it is the same
        // answer on every run.
        c.OnArrived(staleStamp);
        var sample = new Vector2(2166.30f, 1802.00f);
        bool preAcqMoved = c.MarkerMoved(sample, 0.5);
        bool okE = c.Active == null && c.Legs == 0 && c.MarkerUid == 0
                && c.LastMarkerPos == Vector2.Zero && c.ArrivedUtc == default
                && !c.Settled(staleStamp.AddSeconds(10.0), 3.0)
                && !preAcqMoved && c.LastMarkerPos == Vector2.Zero;
        Report(ref failures, okE,
               "Controller: Reset 清空",
               $"active {(c.Active is null ? "null" : "非空")} · legs {c.Legs} · uid {c.MarkerUid} · "
               + $"last ({c.LastMarkerPos.X:0.0}, {c.LastMarkerPos.Y:0.0}) · arrived {c.ArrivedUtc:O} · "
               + $"无腿 OnArrived 后 Settled {c.Settled(staleStamp.AddSeconds(10.0), 3.0)} · 无腿 MarkerMoved {preAcqMoved}"
               + "（判据 全零 / null / false / false）");

        return failures;
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
