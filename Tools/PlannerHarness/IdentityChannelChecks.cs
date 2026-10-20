using System;
using AutoParking;

/// <summary>
///  Checks for the sidecar identity channel — and only for the half of it that can be checked
///  offline. <see cref="IdentityChannel.ParseTsv"/> is a pure static function over the probe's
///  machine-readable records, so the wire format is assertable without a game process; the cache /
///  TTL / timeout / subprocess half of <c>TryGet</c> deliberately has no case here, because the only
///  honest way to test it would be to launch MemoryProbe.exe from the harness, which needs the game
///  running and would make the offline gate depend on a live 0.3–0.6 s memory scan.
///
///  The fixture below is a live capture, not an imagined one: recorded 2026-10-20 with
///  <c>Tools\MemoryProbe\bin\Release\net10.0\win-x64\MemoryProbe.exe eurotrucks2 --deep-tsv
///  2194.40 1804.12 90</c> (395 ITEM + 820 STR rows). Five of its seven lines are verbatim lines
///  1213–1215 and 3–4 of that stdout, trigger first so the assertions read top-down; the other two are
///  synthetic and the comment on <c>Fixture</c> says exactly which format quirk each pins and how it was
///  measured. Two consequences of the real capture are pinned here:
///    - records end with CRLF, the way <c>Console.Out.NewLine</c> writes on Windows, so ParseTsv owns
///      the per-line <c>'\r'</c> trim. What it protects is the LAST field of a STR line — the identity
///      run, which would otherwise be stored inside <c>Strings</c> with a carriage return in it. The
///      numeric fields survive an untrimmed <c>'\r'</c> either way (NumberStyles.Float and HexNumber
///      both allow trailing whitespace), so the assertion below checks the text, not a parse failure;
///      dropping the trim in IdentityChannel turns that check's ✗, which is how it is known to be
///      load-bearing rather than decorative.
///    - a pointer-window run arrives labelled with its provenance inside the text
///      (<c>@+0x78 state park_trigger_on_stopped</c>), so identity is matched by substring and a
///      consumer that compared whole strings would find nothing.
/// </summary>
internal static class IdentityChannelChecks
{
    // Verbatim from the live --deep-tsv stdout, CRLF included — plus two SYNTHETIC lines that pin format
    // quirks this five-line window happens not to contain. They are not invented:
    //   - the STR multiset is measured, counted just now from the capture this fixture is cut from:
    //     14 duplicate (uid, run) STR pairs among its 1215 lines. Line 4 therefore repeats the trigger's
    //     state run.
    //   - a repeated uid across ITEM rows is structural rather than observed: the walk's dedupe key is
    //     the ELEMENT POINTER (`!seen.Add(elem)` in DoDeep), not the uid, so one uid reached through two
    //     entries prints twice. Counted in this capture: 0 repeated ITEM uids, so the last line is the
    //     synthetic one, the same trigger uid at a slightly different position.
    // Everything below asserts what ParseTsv owes both: one record per ITEM ROW, and the run list
    // aggregated identically onto each row of that uid with its duplicates preserved.
    private const string Fixture =
        "ITEM\t34\t0x5BBAC4CC239C0000\t0x80006\t2166.301\t-4.352\t1801.732\t10.164\t4.047\t6.684\r\n" +
        "STR\t0x5BBAC4CC239C0000\t@+0x78 state park_trigger_on_stopped\r\n" +
        "STR\t0x5BBAC4CC239C0000\t@+0xE8 /automat/f6/f6d4ace54bc8\r\n" +
        "STR\t0x5BBAC4CC239C0000\t@+0x78 state park_trigger_on_stopped\r\n" +
        "ITEM\t47\t0x4B2B18C8ACD40000\t0x3100\t2216.77\t-2.915\t1790.643\t7.25\t4.44\t2.98\r\n" +
        "STR\t0x4B2B18C8ACD40000\t@+0x58 academy.parked.truck\r\n" +
        "ITEM\t34\t0x5BBAC4CC239C0000\t0x80006\t2166.512\t-4.352\t1801.948\t10.164\t4.047\t6.684\r\n";

    private const string StateRun = "@+0x78 state park_trigger_on_stopped";

    public static int Run()
    {
        int failures = 0;
        var items = IdentityChannel.ParseTsv(Fixture);
        var trigger = items.Count > 0 ? items[0] : null;
        var hookup = items.Count > 1 ? items[1] : null;
        var dupRow = items.Count > 2 ? items[2] : null;

        // The 0x22 trigger volume the game parks the truck on (type 34, printed as a decimal int; the
        // uid and the flags as 0x-prefixed hex), and the 0x2F framing vehicle that bounds the gap
        // (class 47, flags 0x3100). The record keeps the plane only — (SCS X, SCS Z) — so Pos.Y is the
        // world z, the same convention KdopReader.MemItem.PlanePos uses; the height column is parsed
        // and dropped on purpose. The last field of each line has to arrive with no carriage return
        // left on it, so the aggregated text is checked for a bare '\r' as well. The third record is the
        // SYNTHETIC repeat of the trigger's uid: it must be its own item at its own position, because a
        // resolver that dedupes by uid here would lose the second sighting of a moving or aliased item.
        bool parseOk = items.Count == 3
                    && trigger is { Uid: 0x5BBAC4CC239C0000UL, Type: 34, Flags: 0x80006 }
                    && Near(trigger.Pos.X, 2166.301f) && Near(trigger.Pos.Y, 1801.732f)
                    && Near(trigger.Dx, 10.164f) && Near(trigger.Dz, 6.684f)
                    && hookup is { Uid: 0x4B2B18C8ACD40000UL, Type: 47, Flags: 0x3100 }
                    && Near(hookup.Pos.X, 2216.77f) && Near(hookup.Pos.Y, 1790.643f)
                    && Near(hookup.Dx, 7.25f) && Near(hookup.Dz, 2.98f)
                    && dupRow is { Uid: 0x5BBAC4CC239C0000UL, Type: 34, Flags: 0x80006 }
                    && Near(dupRow.Pos.X, 2166.512f) && Near(dupRow.Pos.Y, 1801.948f)
                    && !Near(dupRow.Pos.X, trigger.Pos.X) && !Near(dupRow.Pos.Y, trigger.Pos.Y)
                    && !CarriageReturnSurvived(trigger) && !CarriageReturnSurvived(hookup)
                    && !CarriageReturnSurvived(dupRow);
        Report(ref failures, parseOk, "IdentityChannel: TSV 解析（uid/type/坐标/字符串聚合）",
               $"{items.Count} 条（判据 3，含 1 条重复 uid 行）· 首条 {Describe(trigger)} · 次条 {Describe(hookup)} · " +
               $"重复行 {Describe(dupRow)} · " +
               $"行尾 \\r 残留={CarriageReturnSurvived(trigger) || CarriageReturnSurvived(hookup) || CarriageReturnSurvived(dupRow)}（判据 无残留）");

        // Identity aggregation is the whole point of the sidecar: the trigger carries THREE STR rows for
        // one uid (its state name, the /automat path it only points at, and a repeat of the state name —
        // the multiset the live capture has 14 of) and all must land in the same record, substring-
        // testable with the @+0xNN provenance label still inside. The hookup's academy.parked.truck must
        // land on ITS uid — a merge that lost the key would pass the count check above, so both halves
        // are asserted here. Last, the repeat: aggregation is keyed by uid and independent of which row
        // asked, so the second ITEM row of the trigger carries the byte-identical run list, and a
        // duplicated run stays duplicated rather than being silently de-duped (a matcher may use
        // Contains, never equality, precisely because of this).
        bool stringOk = trigger != null && hookup != null && dupRow != null
                     && trigger.Strings.Contains("state park_trigger_on_stopped")
                     && trigger.Strings.Contains("/automat/f6/f6d4ace54bc8")
                     && hookup.Strings.Contains("academy.parked.truck")
                     && !hookup.Strings.Contains("park_trigger_on_stopped")
                     && RunCount(trigger) == 3 && RunCount(dupRow) == 3 && RunCount(hookup) == 1
                     && CountOf(trigger.Strings, StateRun) == 2
                     && dupRow.Strings == trigger.Strings;
        Report(ref failures, stringOk, "IdentityChannel: Hookup 身份串归到正确 uid",
               $"触发体 3 条 STR 归并（含 1 对重复）→ [{trigger?.Strings ?? "-"}] · 停放车 → [{hookup?.Strings ?? "-"}] · " +
               $"重复 uid 行与首行归并结果一致={dupRow?.Strings == trigger?.Strings}（判据 一致）");

        // The probe is a foreign process reading a third process, so a truncated line, a window that
        // found nothing, or stdout that is not its format at all must come back as an empty list
        // instead of throwing into the plugin's tick. The fourth case is the probe's OWN non-finite
        // token: Tools\MemoryProbe\Program.cs's Num() prints the literal "NaN" for any field that is not
        // finite, and NumberStyles.Float parses that token back happily — so without the IsFinite clause
        // in TryNum this row would be PUBLISHED, carrying NaN into Task 8's distance math where every
        // comparison against it is false and the item quietly stops being an obstacle. It has to drop.
        int garbage = Safe(() => IdentityChannel.ParseTsv("garbage\n\n").Count);
        int empty = Safe(() => IdentityChannel.ParseTsv("").Count);
        int strOnly = Safe(() => IdentityChannel.ParseTsv("STR\t0xDEADBEEF\torphan run\r\n").Count);
        int nanRow = Safe(() => IdentityChannel.ParseTsv(
            "ITEM\t34\t0x5BBAC4CC239C0000\t0x80006\tNaN\t-4.352\t1801.732\t10.164\t4.047\t6.684\r\n").Count);
        Report(ref failures, garbage == 0 && empty == 0 && strOnly == 0 && nanRow == 0,
               "IdentityChannel: 非法输入不抛异常返回空表",
               $"garbage → {garbage} · 空串 → {empty} · 无 ITEM 的孤行 STR → {strOnly} · 非有限坐标 NaN → {nanRow}（判据 0/0/0/0）");

        return failures;
    }

    private static bool Near(float got, double want) => Math.Abs(got - want) < 0.001f;

    // A '\r' that reached Strings means ParseTsv stopped owning the probe's CRLF terminator. Checked on
    // the aggregated text rather than on a number because NumberStyles.Float / HexNumber both allow
    // trailing whitespace, so an untrimmed line still parses — the identity run is the field that shows it.
    private static bool CarriageReturnSurvived(IdentityChannel.ItemIdentity? item)
        => item?.Strings.Contains('\r') ?? false;

    // How many runs an aggregated record carries (the "; " join ParseTsv publishes). -1 for no record at
    // all, so a missing row reads as a failure rather than as a zero that could satisfy a "== 0" by luck.
    private static int RunCount(IdentityChannel.ItemIdentity item)
        => item.Strings.Length == 0 ? 0 : item.Strings.Split("; ").Length;

    // Occurrences of an exact run inside the joined text: this is what says the STR multiset survived
    // aggregation instead of being de-duped. -1 for a null record, same reason as RunCount.
    private static int CountOf(string? text, string needle)
    {
        if (text is null)
            return -1;
        int found = 0, at = 0;
        while (true)
        {
            int hit = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (hit < 0)
                return found;
            found++;
            at = hit + needle.Length;
        }
    }

    private static string Describe(IdentityChannel.ItemIdentity? item)
        => item is null ? "-"
           : $"0x{item.Uid:X12} type={item.Type} flags=0x{item.Flags:X} ({item.Pos.X:0.###}, {item.Pos.Y:0.###}) {item.Dx:0.###}×{item.Dz:0.###}";

    // A parse that throws must reach the ✗ column, not crash the harness into printing nothing.
    private static int Safe(Func<int> read)
    {
        try { return read(); } catch (Exception e) { return -1 - e.GetType().Name.Length; }
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
