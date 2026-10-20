namespace AutoParking;

/// <summary>
///  What a runtime k-DOP item read out of the game's memory actually is.
///
///  A runtime item carries no model path, no name and no node - only its item type byte, its flags
///  word and its k-DOP box - so this enum plus <see cref="MemClassifier"/> is the whole of its
///  identity. Kept apart from <c>KdopReader</c> because the reader P/Invokes ReadProcessMemory and
///  logs through ETS2LA.Logging, so the rules below could not be asserted offline while they lived
///  inside it (design doc §38 for the archive numbering, §41 for the runtime cones, §54 for the
///  parking markers).
/// </summary>
internal enum MemKind
{
    /// <summary>Sign, near-square footprint ~0.6–1.1 m wide, ~0.9–1.2 m tall — the shape of
    ///  every cone measured so far, archive (§38) and runtime (§41) alike.</summary>
    Cone,
    /// <summary>A bigger Sign: construction boards, gantry signs, the row-head signs.</summary>
    Board,
    /// <summary>Mover items - the game's dynamic/animated objects. The parking marker pair is two
    ///  Movers whose flags read 0xC17 (§54); the kind itself stays just "Mover", because which
    ///  mover is a marker is a flags question the target resolver answers.</summary>
    Mover,
    /// <summary>Buildings items. An archive class, but the host's filtered MapData does not
    ///  necessarily carry them at the current spot, so the memory layer draws and blocks them
    ///  too (§43, owner decision: near-lot structures must not be hit whatever they are).</summary>
    Building,
    /// <summary>Trigger volumes - invisible detection boxes the game places around a goal (the 驾校
    ///  goal volume measured 10.16 × 4.05 × 6.68 m with flags 0x80006; spec F10 also has a 7.36³
    ///  one). Loose by construction — F10 refuted "this box is the bay's outline" — so nothing here
    ///  is drawn and nothing blocks.</summary>
    Trigger,
    /// <summary>A Hookup item flagged as parked rather than moving traffic (§54): a vehicle already
    ///  standing in the lot. Its k-DOP is the real footprint of that vehicle, which is what bounds
    ///  the gap the truck has to fit into. Drawn as a gray footprint and, unlike Trigger, it does
    ///  block routes - but only while both <c>ShowMemoryItems</c> and <c>BlockRouteWithMemoryItems</c>
    ///  are on, the same gate every other memory obstacle sits behind (R16).</summary>
    ParkedVehicle,
    /// <summary>Any other small item class; drawn nowhere, blocks nothing.</summary>
    Other
}

/// <summary>
///  The memory layer's classifier: <c>type / flags / k-DOP extents → MemKind</c>. Pure by
///  construction (no host assembly, no game process, no reading of anything), which is what lets
///  <c>Tools\PlannerHarness</c> link this file and assert every rule offline.
///
///  Deliberately NOT here: the reader's "any extent over 20 m is not something this layer draws"
///  filter. That is a publishing decision of <c>KdopReader</c>, and moving it in here would change
///  what a snapshot contains while pretending to be a rename.
/// </summary>
internal static class MemClassifier
{
    /// <summary>
    ///  Classify one item. Numbering matches TruckLib ItemType (design doc §38): 0x02 Buildings,
    ///  0x09 Mover, 0x22 Trigger, 0x24 Sign, 0x2F Hookup.
    ///
    ///  The two kinds the marker feature adds are decided on different evidence. A 0x22 trigger is
    ///  a detection volume, so its size means nothing and the type number alone decides (§54: the
    ///  measured goal volume is 10.16 × 4.05 × 6.68 m, i.e. outside every cone window). A 0x2F
    ///  hookup is a vehicle only when its flags carry the parked family - the live samples were
    ///  0x100 / 0x10C / 0x200 / 0x3100 / 0x21120, all of which have a bit set inside 0x300, while
    ///  moving traffic and unhooked trailers in the same class read without one.
    /// </summary>
    public static MemKind Classify(int type, uint flags, float dx, float dy, float dz)
    {
        if (type == 0x02)
            return MemKind.Building;
        if (type == 0x09)
            return MemKind.Mover;
        if (type == 0x22)
            return MemKind.Trigger;
        if (IsParkedHookup(type, flags))
            return MemKind.ParkedVehicle;
        if (type == 0x24)
        {
            float ratio = dz > 0 ? dx / dz : 0;
            if (dx is >= 0.35f and <= 1.15f && dz is >= 0.35f and <= 1.15f
                && dy is >= 0.75f and <= 1.4f && ratio is >= 0.55f and <= 1.8f)
                return MemKind.Cone;
            return MemKind.Board;
        }
        return MemKind.Other;
    }

    /// <summary>
    ///  The one definition of "a memory item of this kind becomes a planner obstacle": cone, board,
    ///  mover, building, parked vehicle. It lives here, next to the kind rules, so
    ///  <c>AutoParkingPlugin.AddMemoryObstacles</c> and <c>Tools\PlannerHarness\ParkedObstacleChecks</c>
    ///  share it - the harness cannot link the plugin (host assemblies), and a second copy of this
    ///  whitelist in the check would stay green after the plugin's copy dropped a kind.
    ///
    ///  <see cref="MemKind.Trigger"/> is deliberately excluded: a trigger is an invisible detection
    ///  volume (measured 10.16 × 4.05 × 6.68 m), not something standing on the ground, so blocking it
    ///  would fence off the very goal the truck is parking in. Parked vehicles are in only since the
    ///  framing-truck measurement (three 7.50 × 3.23 m 0x2F Hookups bounding the academy gap): with the
    ///  kind left out the planner planned straight through that row - 0 conflicts on a route that sweeps
    ///  the trucks, 27 once they are in the set (§43/§54, R16).
    ///
    ///  A whitelist, not a blacklist, so a kind the classifier learns later is not a blocker by
    ///  construction; it has to be added here on purpose.
    /// </summary>
    internal static bool BlocksMemoryRoutes(MemKind kind)
        => kind is MemKind.Cone or MemKind.Board or MemKind.Mover or MemKind.Building or MemKind.ParkedVehicle;

    /// <summary>
    ///  The one predicate for "this 0x2F Hookup is a parked vehicle rather than moving traffic":
    ///  type 0x2F plus any bit of the parked flags family 0x300. It exists once because
    ///  <see cref="Classify"/> and <c>TargetResolver.Resolve</c> both need exactly this test, and two
    ///  copies of a protocol constant drift apart silently - the classifier would draw a gap the
    ///  resolver no longer reads, or worse, one would accept live traffic as a bay wall (R17).
    ///  Meaning is unchanged from both former copies: measured live samples read 0x100 / 0x10C /
    ///  0x200 / 0x3100 / 0x21120, all of which carry a bit inside 0x300 (§54).
    /// </summary>
    public static bool IsParkedHookup(int type, uint flags)
        => type == 0x2F && (flags & 0x300u) != 0;
}
