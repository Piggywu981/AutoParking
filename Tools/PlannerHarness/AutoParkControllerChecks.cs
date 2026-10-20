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
        // "settled" before the truck ever arrived and chain the next leg on the spot.
        var fresh = new AutoParkController();
        bool okB = !fresh.Settled(DateTime.UtcNow, 3.0)
                && !c.Settled(c.ArrivedUtc.AddSeconds(1.0), 3.0)
                && c.Settled(c.ArrivedUtc.AddSeconds(3.1), 3.0);
        Report(ref failures, okB,
               "Controller: SettleTimeS 未满不放行、满了放行",
               $"未到达 {fresh.Settled(DateTime.UtcNow, 3.0)} · 1.0s {c.Settled(c.ArrivedUtc.AddSeconds(1.0), 3.0)} "
               + $"· 3.1s {c.Settled(c.ArrivedUtc.AddSeconds(3.1), 3.0)}（判据 false / false / true）");

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
        // second leg would inherit the first one's settle window and chain immediately.
        bool okD = c.Legs == 2 && c.MarkerUid == 0 && c.Active is { } a2 && a2.Confidence == 0.6
                && c.ArrivedUtc == default && !c.Settled(DateTime.UtcNow, 3.0)
                && c.LastMarkerPos == t2.Pose.Position;
        Report(ref failures, okD,
               "Controller: 第二腿无缝接管（framing 目标）",
               $"legs {c.Legs} · uid {c.MarkerUid} · conf {c.Active?.Confidence:0.00} · "
               + $"arrived {c.ArrivedUtc:O}（判据 legs 2 · uid 0 · conf 0.6 · 落地时刻已清空）");

        // Reset is what a reused controller relies on: every field must go back to its initial value,
        // and the stale arrival stamp in particular, or the next maneuver would start already settled.
        c.OnArrived(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        c.Reset();
        bool okE = c.Active == null && c.Legs == 0 && c.MarkerUid == 0
                && c.LastMarkerPos == Vector2.Zero && c.ArrivedUtc == default
                && !c.Settled(DateTime.UtcNow, 3.0);
        Report(ref failures, okE,
               "Controller: Reset 清空",
               $"active {(c.Active is null ? "null" : "非空")} · legs {c.Legs} · uid {c.MarkerUid} · "
               + $"last ({c.LastMarkerPos.X:0.0}, {c.LastMarkerPos.Y:0.0}) · arrived {c.ArrivedUtc:O}"
               + "（判据 全零 / null）");

        return failures;
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
