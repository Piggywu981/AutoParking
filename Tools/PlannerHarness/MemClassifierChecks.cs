using System;
using AutoParking;

/// <summary>
///  Checks for the runtime k-DOP classifier, pulled out of KdopReader so it can be asserted at all:
///  KdopReader itself P/Invokes ReadProcessMemory and logs through ETS2LA.Logging, so the harness
///  cannot link it and every kind rule used to be invisible until the game was running.
///
///  The fixtures are the items read live off the 驾校 parking markers on 2026-10-19 (design doc
///  §38/§41/§54), so each expectation is a measurement of what the memory layer actually says, not a
///  preference about what it should say.
///
///  Scope note, deliberately visible here: the reader's "any extent over 20 m is not something this
///  layer draws" filter lives in KdopReader, not in the classifier. The Building fixture below has
///  dx 29.89 m and therefore never reaches a snapshot - it tests the classifier alone, which is the
///  only way to pin the archive-class rules next to the two new runtime kinds.
/// </summary>
internal static class MemClassifierChecks
{
    public static int Run()
    {
        int failures = 0;

        // The marker volume the game parks the truck on: 10.16 × 4.05 × 6.68 m, i.e. far outside every
        // cone-shape window. A runtime trigger is an invisible detection box, so the type number alone
        // has to decide - a size test here would silently turn markers into Other.
        Expect(ref failures, "0x22 → Trigger（无视尺寸）", 0x22, 0x80006, 10.16f, 4.05f, 6.68f, MemKind.Trigger);

        // The framing vehicles around the marker. Hookup class 0x2F is only a parked vehicle when the
        // flags carry the family measured live (0x100 / 0x10C / 0x200 / 0x3100 / 0x21120); 0x3100 is
        // the row head sitting next to the pair.
        Expect(ref failures, "0x2F+0x3100 → ParkedVehicle", 0x2F, 0x3100, 7.25f, 4.44f, 2.98f, MemKind.ParkedVehicle);

        // Same type, no parked bit: 0x2F also covers moving traffic and unhooked trailers, which must
        // not become planner targets or footprints.
        Expect(ref failures, "0x2F 无 parked 位 → Other", 0x2F, 0x0, 1.0f, 1.0f, 1.0f, MemKind.Other);

        // Regressions - the three kinds the planner and the map already block and draw on. A cone is a
        // Sign whose ground box is near-square and knee-high; both bounds come from the archive +
        // runtime samples in §38/§41.
        Expect(ref failures, "锥桶形状 Sign → Cone（回归）", 0x24, 0x0, 0.84f, 1.11f, 0.84f, MemKind.Cone);
        Expect(ref failures, "0x09 → Mover（回归）", 0x09, 0xC17, 8.75f, 8.75f, 8.75f, MemKind.Mover);
        Expect(ref failures, "0x02 → Building（回归）", 0x02, 0x20, 29.89f, 0.25f, 10.70f, MemKind.Building);

        return failures;
    }

    private static void Expect(ref int failures, string what, int type, uint flags,
                               float dx, float dy, float dz, MemKind want)
    {
        MemKind got = MemClassifier.Classify(type, flags, dx, dy, dz);
        Report(ref failures, got == want, what,
               $"type 0x{type:X2} flags 0x{flags:X} {dx:0.00}×{dy:0.00}×{dz:0.00} → {got}（判据 {want}）");
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
