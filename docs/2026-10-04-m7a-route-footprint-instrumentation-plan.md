# M7a 路线扫掠/未确认地面仪表 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** For every candidate route the planner generates, measure and display two new numbers — the ground
area the route *demands* (vehicle envelope swept over the ground) and how much of that area is ground the map
does not confirm as drivable — without changing which route is chosen.

**Architecture:** Two new pure classes. `GroundTrust` rasterizes the map's own evidence ("a nav curve or lane
passes here", "a collidable object sits here") into a 1 m cell grid. `RouteFootprint` rasterizes each candidate
route's demanded envelope over the same grid and classifies each covered cell against `GroundTrust`. The planner
stays a pure function; the host-side `MapGeometryBuilder` is the only thing that reads game data, and it builds
the `GroundTrust` once per map rebuild.

**Tech Stack:** .NET 10 / C# 13 (`net10.0`, nullable enabled, implicit usings). No NuGet packages (`NuGet.Config`
clears all sources); host assemblies come by `HintPath`. Offline verification is `Tools\PlannerHarness`, a console
project that **links** the plugin's pure sources instead of referencing the DLL; its process exit code is the gate.

**Spec:** `docs\2026-09-30-autoparking-design.md` §23–§25 (planner tiers and cost), §26–§32 (what map data can and
cannot provide; the decision that the static layer is drawn but does not block), and the four defaults approved on
2026-10-04: (1) minimize **intrusion into unconfirmed ground** as the primary new objective term, length and gear
changes secondary; (2) "confirmed ground" = inside the sample radius, within a corridor half-width of a nav curve
or road lane, and not occupied by a collidable map object; (3) more reverse legs and gear changes are acceptable,
but the gear-change penalty stays in the cost so five-segment zigzags stay excluded; (4) **M7a instruments only —
selection logic does not change.** M7b (missing candidate family) and M7c (new cost + unified gear accounting)
are separate plans, written after these numbers exist.

## Global Constraints

Verbatim from `AGENTS.md` in this repo root; every task's requirements implicitly include this section.

- Run commands from this repo root (the directory holding `AGENTS.md`) on Windows, Git Bash or PowerShell.
  No absolute paths in project docs: `..\..\` is the workspace containing this repo under `ThirdPartyPlugins\`.
- Build: `dotnet build -c Release` → `bin\Release\AutoParking.dll`.
- Deploy: copy `bin\Release\AutoParking.dll` to `..\..\ETS2LA-win-release-Portable\current\Plugins\`, then
  **restart ETS2LA** — plugins are shadow-copy loaded and a running host keeps the old DLL.
- Offline check: `dotnet run --project Tools/PlannerHarness -c Release`.
- **Never lower a threshold in the harness to get a green run.** The harness already exits 1 by design on one
  case (右前 45 度 terminal heading 1.4° against a 1.0° bar); that verdict must be unchanged, not "fixed".
- Developer-only code lives under `Tools\`; the plugin csproj excludes `Tools\**`, and any new dev project must
  go there too (the batch `ThirdPartyPlugins\build_all.ps1` would otherwise ship a console app as a plugin).
- `..\..\SourceCode` (the ETS2LA host) is **read-only**; never fix a plugin problem there.
- `README.md` (English, source) and `README.zh-CN.md` are mirrors — edit both in the same change.
- Commits: **English only, never Chinese**; commit inside this repo only; **do not push unless explicitly asked**.
- **This project's rule overrides the skill's default commit-per-step cadence: commit only when the owner asks.**
  Each task below therefore ends with a verification step plus "stage nothing, do not commit" — the commit is a
  single explicit act at the end of the plan when he says so.
- Only the hotkeys may end a maneuver; tractor unit only; no new automatic aborts.
- Every claim about real-truck behavior must be labeled measured or not-yet-measured.

**Definition of done for the whole plan:** `dotnet build -c Release -t:Rebuild` → 0 errors and no new warnings
(the project carries 13 pre-existing); harness exit code and pass/fail set **unchanged** from before the edit
(adding assertions is allowed, changing an existing verdict is not); both READMEs and the design doc current.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| Modify `Geometry.cs` | Add `PointInPolygon` — the one primitive both rasterizers need and which does not exist yet (only SAT-based `PolygonsOverlap` does, at `Geometry.cs:160`) | 1 |
| Create `Driving/GroundTrust.cs` | Pure: which ground the map itself vouches for, as a 1 m cell grid (corridor cells minus blocker cells, gated by sample radius) | 2 |
| Modify `Driving/ParkingPath.cs` | Two measurement fields on `ParkingPath` (`SweptAreaM2`, `UnconfirmedAreaM2`, both `-1` = unmeasured) and the winner's numbers in `PlanResult.Summary` | 3 |
| Create `Driving/RouteFootprint.cs` | Pure: rasterize a route's demanded envelope over a `GroundTrust` grid and split the covered area into confirmed / unconfirmed | 3 |
| Modify `Driving/Planner.cs` | Optional `GroundTrust? trust` parameter; measure **all** candidates after `Collect`; expose them on `PlanResult` so the caller can log them. **Selection untouched.** | 4 |
| Modify `Rendering/MapGeometry.cs` | Build the `GroundTrust` from data the builder already holds (`DriveableCurves`, `RoadLanes`, collidable `StaticShapes`) | 5 |
| Modify `Settings.cs` | `ConfirmedCorridorHalfWidthM` + clamp | 5 |
| Modify `AutoParkingPlugin.cs` | Pass `geometry.Trust` at both `Planner.Plan` call sites; log one `[[路径代价]]` block per plan | 5 |
| Create `Tools/PlannerHarness/GroundTrustChecks.cs`, `Tools/PlannerHarness/RouteFootprintChecks.cs`; modify `Tools/PlannerHarness/PlannerHarness.csproj`, `Tools/PlannerHarness/Program.cs` | The assertions, including the one that guards the whole premise: instrumentation must not change the chosen route | 1–5 |
| Modify `docs\2026-09-30-autoparking-design.md` (§34), `README.md`, `README.zh-CN.md`, `AGENTS.md` | Record the instrument, what it means, and the measured numbers | 6 |

---

## Task 1: `Geometry.PointInPolygon`

**Files:**
- Modify: `Geometry.cs` (add one method after `PolygonsOverlap`, i.e. after line 163)
- Create: `Tools/PlannerHarness/GeometryPointChecks.cs`
- Modify: `Tools/PlannerHarness/Program.cs` (call the new checks; `Program.cs:121` is where `MapProbeChecks.Run()` is called and `Program.cs:141-143` is the exit gate)
- Modify: `Tools/PlannerHarness/PlannerHarness.csproj` — **every** new file needs a `<Compile Include>` entry.
  The harness sets `EnableDefaultCompileItems=false` and lists its sources by hand (`Program.cs`, `ClosedLoop.cs`,
  `MapProbeChecks.cs`, plus the linked plugin sources), so an unlisted file simply does not compile and the new
  checks silently never run. Add `<Compile Include="GeometryPointChecks.cs" />`.

**Interfaces:**
- Consumes: nothing.
- Produces: `public static bool PointInPolygon(Vector2[] polygon, Vector2 point)` in `AutoParking.Geometry`.
  Returns false for polygons with fewer than 3 vertices and for points exactly on the boundary in the +X ray
  tie case (documented, not asserted).

- [x] **Step 1: Write the failing checks**

Create `Tools/PlannerHarness/GeometryPointChecks.cs`:

```csharp
using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for the point-in-polygon primitive. It exists because the footprint and trust rasterizers
///  ask "is this 1 m cell's centre inside that ring" hundreds of times, and the SAT overlap test in
///  Geometry would build two polygons and four axis projections to answer it.
/// </summary>
internal static class GeometryPointChecks
{
    public static int Run()
    {
        int failures = 0;

        Vector2[] square =
        {
            new(-3f, -2f), new(3f, -2f), new(3f, 2f), new(-3f, 2f)
        };

        Report(ref failures, Geometry.PointInPolygon(square, new Vector2(0, 0)), "方框内的点", "(0,0) 在 ±3×±2 方框内");
        Report(ref failures, !Geometry.PointInPolygon(square, new Vector2(4, 0)), "方框外的点", "(4,0) 在框外");
        Report(ref failures, !Geometry.PointInPolygon(square, new Vector2(0, 3)), "方框外的点（纵向）", "(0,3) 在框外");

        // A clockwise ring must read the same as a counter-clockwise one: the map's own rings come in
        // whichever order the format wrote them.
        Vector2[] flipped = new[] { square[3], square[2], square[1], square[0] };
        bool same = Geometry.PointInPolygon(flipped, new Vector2(0, 0))
                    && !Geometry.PointInPolygon(flipped, new Vector2(4, 0));
        Report(ref failures, same, "顶点顺序无关", "反向环绕的同一方框读数一致");

        // Concave: an L-shaped ring, so a bbox test alone would be wrong.
        Vector2[] lShape =
        {
            new(0f, 0f), new(6f, 0f), new(6f, 2f), new(2f, 2f), new(2f, 6f), new(0f, 6f)
        };
        Report(ref failures, Geometry.PointInPolygon(lShape, new Vector2(1, 5)), "凹形内部（竖臂）", "(1,5) 在 L 的竖臂里");
        Report(ref failures, Geometry.PointInPolygon(lShape, new Vector2(5, 1)), "凹形内部（横臂）", "(5,1) 在 L 的横臂里");
        Report(ref failures, !Geometry.PointInPolygon(lShape, new Vector2(5, 5)), "凹形缺口不算内部", "(5,5) 在 L 的缺口里");

        // Degenerate input must answer "no" rather than throw or divide by zero: the map hands over
        // two-point segments and single nodes routinely.
        Report(ref failures, !Geometry.PointInPolygon(new[] { new Vector2(1, 1) }, new Vector2(1, 1)), "单点不是多边形", "1 个顶点 → false");
        Report(ref failures, !Geometry.PointInPolygon(new Vector2[0], new Vector2(0, 0)), "空输入不是多边形", "0 个顶点 → false");

        return failures;
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
```

- [x] **Step 2: Wire it into the harness and confirm it fails to compile for the right reason**

Run: `dotnet run --project Tools/PlannerHarness -c Release`
Expected: `error CS0117: "Geometry" does not contain a definition for "PointInPolygon"` — and nothing else.

- [x] **Step 3: Implement the minimal version**

Add to `Geometry.cs`, directly after the `PolygonsOverlap` method (which ends around line 163):

```csharp
    /// <summary>
    ///  Even-odd ray cast along +X. Cheaper than the SAT overlap test for the "is this cell centre
    ///  inside that ring" question the rasterizers ask hundreds of times, and it accepts a ring in
    ///  either winding order. Under 3 vertices there is no interior, so false.
    /// </summary>
    public static bool PointInPolygon(Vector2[] polygon, Vector2 point)
    {
        if (polygon == null || polygon.Length < 3)
            return false;

        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            Vector2 a = polygon[i];
            Vector2 b = polygon[j];

            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (double)(b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
```

- [x] **Step 4: Run the checks and confirm they pass**

Run: `dotnet run --project Tools/PlannerHarness -c Release --% 2>&1 | grep -A 12 "点在多边形"`
Expected: every line `✓`, `GeometryPointChecks` returning 0 failures. (If the grep label does not appear yet,
add the call in Step 5 first.)

- [x] **Step 5: Call it from the harness and add it to the exit gate**

In `Tools/PlannerHarness/Program.cs`, next to the existing `int probeFailures = MapProbeChecks.Run();`
(line 121) add:

```csharp
int pointFailures = GeometryPointChecks.Run();
Console.WriteLine();
Console.WriteLine("点在多边形（扫掠栅格要用它判定环内/环外）：");
```

and extend the final `return` expression (`Program.cs:141-143`) with `&& pointFailures == 0`, keeping every
existing term untouched.

- [x] **Step 6: Full verification**

Run: `dotnet build -c Release -t:Rebuild` → expect `0 个错误`, `13 个警告` (unchanged).
Run: `dotnet run --project Tools/PlannerHarness -c Release > /tmp/h.txt 2>&1; echo "exit=$?"`
Expected: `exit=1` **with the same ✗ set as before this change** — 右前 45 度 heading 1.4°, its table row, and the
"关：✗ 超时未完成" blocked-case line. Three ✗ total, no new ones. Compare against the baseline kept in
`/tmp/h2.txt` if still present.

- [x] **Step 7: Stage nothing, do not commit**

Per the Global Constraints, commits happen only when the owner asks.

---

## Task 2: `Driving/GroundTrust.cs` — what the map vouches for

**Files:**
- Create: `Driving/GroundTrust.cs`
- Modify: `AutoParking.csproj` — nothing (it globs the repo; `Driving\**` is already compiled)
- Create: `Tools/PlannerHarness/GroundTrustChecks.cs`
- Modify: `Tools/PlannerHarness/PlannerHarness.csproj` (add `<Compile Include="GroundTrustChecks.cs" />` **and**
  `<Compile Include="$(PluginRoot)Driving\GroundTrust.cs" />` — see Task 1's note on `EnableDefaultCompileItems=false`)
- Modify: `Tools/PlannerHarness/Program.cs` (call `GroundTrustChecks.Run()`, add to gate)

**Interfaces:**
- Consumes: `Geometry.ClosestPointOnSegment`, `Geometry.PointInPolygon`, `Geometry.Distance`.
- Produces:
  - `public sealed class GroundTrust` with
    `public const double CellM = 1.0`,
    `public Vector2 Center`, `public double RadiusM`, `public double CorridorHalfWidthM`,
    `public bool Truncated`,
    `public void AddCorridor(Vector2[] points)`,
    `public void AddBlocker(Vector2[] points, bool closed)`,
    `public bool IsConfirmed(Vector2 worldPoint)`,
    `public bool IsConfirmedCell(long key)`,
    `public long KeyAt(Vector2 worldPoint)`,
    `public int CorridorCells`, `public int BlockedCells`.
  - Blocker cells are stamped *after* corridor cells into a separate set; `IsConfirmed` = in corridor, not in
    blocked, inside radius. Order of `AddCorridor`/`AddBlocker` calls therefore does not matter, which is asserted.
  - `public int ConfirmedCells` — corridor cells with no blocker sitting on them, the number the map panel prints.

- [x] **Step 1: Write the failing checks**

Create `Tools/PlannerHarness/GroundTrustChecks.cs`:

```csharp
using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for GroundTrust — the map's own admission of which ground it can vouch for. The point of
///  keeping it as a rasterized cell set rather than a distance query is that the planner then asks
///  "how much of the route's demanded envelope sits on ground nobody vouched for" as a cell count,
///  and that number is what M7c is going to be weighted on. So the grid has to be deterministic and
///  independent of the order the map walk delivered its items in.
/// </summary>
internal static class GroundTrustChecks
{
    public static int Run()
    {
        int failures = 0;

        // A straight corridor along +X, 40 m long, half-width 5 m, sampled to 120 m.
        GroundTrust trust = new()
        {
            Center = new Vector2(0, 0),
            RadiusM = 120,
            CorridorHalfWidthM = 5.0
        };
        trust.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });

        Report(ref failures, trust.IsConfirmed(new Vector2(0, 0)), "曲线正上方已确认", "(0,0)");
        Report(ref failures, trust.IsConfirmed(new Vector2(10, 4.9f)), "半宽内已确认", "(10,4.9) 在 5 m 半宽内");
        Report(ref failures, !trust.IsConfirmed(new Vector2(10, 6.0f)), "超出半宽未确认", "(10,6.0)");
        Report(ref failures, !trust.IsConfirmed(new Vector2(30, 0)), "曲线端点之外未确认", "(30,0) 沿线但超出曲线");

        // The radius gate is the other half of "the map was even looked at out there".
        GroundTrust narrow = new() { Center = new Vector2(0, 0), RadiusM = 12, CorridorHalfWidthM = 5.0 };
        narrow.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });
        Report(ref failures, narrow.IsConfirmed(new Vector2(8, 0)), "半径内走廊已确认", "(8,0) 在 12 m 半径内");
        Report(ref failures, !narrow.IsConfirmed(new Vector2(16, 0)), "半径外一律未确认", "(16,0) 在走廊上但超出采样半径");

        // A collidable object takes the voucher away, in both stamping orders.
        GroundTrust withWallA = new() { Center = new Vector2(0, 0), RadiusM = 120, CorridorHalfWidthM = 5.0 };
        withWallA.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });
        withWallA.AddBlocker(new[] { new Vector2(2, -2), new Vector2(6, -2), new Vector2(6, 2), new Vector2(2, 2) }, true);

        GroundTrust withWallB = new() { Center = new Vector2(0, 0), RadiusM = 120, CorridorHalfWidthM = 5.0 };
        withWallB.AddBlocker(new[] { new Vector2(2, -2), new Vector2(6, -2), new Vector2(6, 2), new Vector2(2, 2) }, true);
        withWallB.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });

        Report(ref failures, !withWallA.IsConfirmed(new Vector2(4, 0)), "闭合障碍内部未确认", "(4,0) 在 4×4 墙里");
        Report(ref failures, withWallA.IsConfirmed(new Vector2(0, 0)), "障碍旁边仍然已确认", "(0,0) 离墙 2 m");
        Report(ref failures, withWallA.CorridorCells == withWallB.CorridorCells
                            && withWallA.BlockedCells == withWallB.BlockedCells
                            && withWallA.IsConfirmed(new Vector2(4, 0)) == withWallB.IsConfirmed(new Vector2(4, 0)),
            "盖章顺序无关", $"正序 {withWallA.CorridorCells}/{withWallA.BlockedCells} 与逆序 {withWallB.CorridorCells}/{withWallB.BlockedCells} 相同");

        // Confirmed cells are corridor cells no blocker sits on: a blocker outside the corridor must not
        // shrink the count, which is exactly what subtracting the two set sizes would have done.
        GroundTrust overlap = new() { Center = new Vector2(0, 0), RadiusM = 120, CorridorHalfWidthM = 5.0 };
        overlap.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });
        int before = overlap.ConfirmedCells;
        overlap.AddBlocker(new[] { new Vector2(2, -2), new Vector2(6, -2), new Vector2(6, 2), new Vector2(2, 2) }, true);
        int afterWall = overlap.ConfirmedCells;
        overlap.AddBlocker(new[] { new Vector2(60, 60), new Vector2(64, 60), new Vector2(64, 64), new Vector2(60, 64) }, true);
        Report(ref failures, before > afterWall && overlap.ConfirmedCells == afterWall && afterWall > 0,
            "只减真正重叠的格", $"{before} → 挡墙后 {afterWall} → 走廊外再加一块仍是 {overlap.ConfirmedCells}");

        // An open chain is a wall *segment*, not a filled region: it blocks a band, not an inside.
        GroundTrust segment = new() { Center = new Vector2(0, 0), RadiusM = 120, CorridorHalfWidthM = 5.0 };
        segment.AddCorridor(new[] { new Vector2(-20, 0), new Vector2(20, 0) });
        segment.AddBlocker(new[] { new Vector2(0, -10), new Vector2(0, 10) }, false);
        Report(ref failures, !segment.IsConfirmed(new Vector2(0.4f, 0)), "线段障碍带未确认", "(0.4,0) 落在 1.5 m 障碍带内");
        Report(ref failures, segment.IsConfirmed(new Vector2(3, 0)), "障碍带之外仍确认", "(3,0) 离线段 3 m");

        // Cell keys must not collide across quadrants — negative coordinates are the normal case — and
        // two points in the same cell must land on the same key, or areas would count one cell twice.
        long k1 = trust.KeyAt(new Vector2(1.5f, 1.5f));
        long k2 = trust.KeyAt(new Vector2(-1.5f, -1.5f));
        long k3 = trust.KeyAt(new Vector2(1.5f, -1.5f));
        Report(ref failures, k1 != k2 && k1 != k3 && k2 != k3, "单元格键不撞", "四个象限的格键互不相同");
        Report(ref failures, trust.KeyAt(new Vector2(1.0f, 1.0f)) == trust.KeyAt(new Vector2(1.4f, 1.9f)),
            "同一格内键相同", "(1.0,1.0) 与 (1.4,1.9) 同属一个 1 m 单元格");
        Report(ref failures, trust.KeyAt(new Vector2(1.0f, 1.0f)) != trust.KeyAt(new Vector2(2.0f, 1.0f)),
            "跨一格键就不同", "(1.0,1.0) 与 (2.0,1.0) 分属两格");

        // Cap: a pathological depot must not be able to stamp forever.
        GroundTrust capped = new() { Center = new Vector2(0, 0), RadiusM = 1200, CorridorHalfWidthM = 5.0 };
        for (int i = 0; i < 400; i++)
            capped.AddCorridor(new[] { new Vector2(-600, i * 3 - 600), new Vector2(600, i * 3 - 600) });
        // The first draft of this case used a 400 m sample and PASSED WITHOUT EXERCISING THE CAP: the
        // overlapping corridors deduplicated to 167,260 cells, so the `|| cells <= MaxCells` disjunct
        // carried it. Widened to 1200 m / 400 curves and the disjunction dropped to a conjunction, so
        // the case now fails unless Truncated actually flips. Measured: 300000 cells, Truncated=True.
        Report(ref failures, capped.Truncated && capped.CorridorCells <= GroundTrust.MaxCells,
            "盖章超预算就截断", $"{capped.CorridorCells} 格 / 上限 {GroundTrust.MaxCells}，截断标志={capped.Truncated}");

        return failures;
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
```

- [x] **Step 2: Link it and confirm the expected failure**

Add to `Tools/PlannerHarness/PlannerHarness.csproj`, after the existing
`<Compile Include="$(PluginRoot)Rendering\MapItemProbe.cs" />`:

```xml
    <Compile Include="GroundTrustChecks.cs" />
    <Compile Include="$(PluginRoot)Driving\GroundTrust.cs" />
```

Run: `dotnet run --project Tools/PlannerHarness -c Release`
Expected: `error CS2001: Source file ... Driving\GroundTrust.cs could not be found` — **not** CS0246, because the
csproj links the plugin source by path and the path does not exist yet. (Recorded from the actual run: the
plan's original expectation of CS0246 was wrong about the shape of the red state.)

- [x] **Step 3: Implement `GroundTrust`**

Create `Driving/GroundTrust.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  Which ground the map itself will vouch for — as a 1 m cell grid, not a distance query, so a
///  route's demand can be answered by counting cells.
///
///  "Confirmed" means all three of: inside the radius the map walk actually sampled, within
///  <see cref="CorridorHalfWidthM"/> of a navigation curve or road lane (the game's own statement that
///  vehicles are expected to drive there), and not underneath a collidable map object. Anything else is
///  ground we have no evidence about, which after design doc §32 is precisely where the unrecognizable
///  obstacles live: prefab interior geometry is not published, so a depot floor reads as empty data and
///  a crate in the middle of it is invisible to every layer we have.
///
///  This is deliberately NOT an obstacle model. It is the input to a measurement (M7a); whether it gets
///  to veto a route is M7c's decision, made on the numbers this produces.
/// </summary>
public sealed class GroundTrust
{
    /// <summary>Grid resolution. 1 m is coarse enough to keep a 120 m sample at a few tens of thousands
    ///  of cells and fine enough that a 2.6 m wide vehicle is resolved to three columns.</summary>
    public const double CellM = 1.0;

    /// <summary>Stamping budget per set. A depot with thousands of overlapping curves must not be able
    ///  to occupy the tick thread; when the budget bites, <see cref="Truncated"/> says so and the areas
    ///  reported from this map are understated rather than wrong-headed.</summary>
    public const int MaxCells = 300_000;

    /// <summary>How far a wall-like map item takes trust from its surroundings: it is the object's own
    ///  footprint plus a band, because the geometry we can see is a line while the thing has depth.</summary>
    public const double BlockerBandM = 1.5;

    public Vector2 Center;
    public double RadiusM;
    public double CorridorHalfWidthM = 5.0;
    public bool Truncated;

    private readonly HashSet<long> corridor = new();
    private readonly HashSet<long> blocked = new();

    public int CorridorCells => corridor.Count;
    public int BlockedCells => blocked.Count;

    /// <summary>Cells the map vouches for: corridor minus blockers. Counted on demand because the two sets
    ///  are stamped independently, so their sizes alone say nothing about how much they overlap.</summary>
    public int ConfirmedCells
    {
        get
        {
            int count = 0;
            foreach (long key in corridor)
            {
                if (!blocked.Contains(key))
                    count++;
            }

            return count;
        }
    }

    public long KeyAt(Vector2 worldPoint)
    {
        return Key((int)Math.Floor(worldPoint.X / CellM), (int)Math.Floor(worldPoint.Y / CellM));
    }

    private static long Key(int ix, int iz) => ((long)ix << 32) | (uint)iz;

    public bool IsConfirmedCell(long key)
    {
        return corridor.Contains(key) && !blocked.Contains(key);
    }

    public bool IsConfirmed(Vector2 worldPoint)
    {
        return IsConfirmedCell(KeyAt(worldPoint));
    }

    /// <summary>A polyline the game expects vehicles to drive on: a prefab navigation curve or a road lane
    ///  centerline. Its buffer is the corridor half-width, so a curve down the middle of a 10 m aisle
    ///  vouches for the whole aisle.</summary>
    public void AddCorridor(Vector2[] points)
    {
        Stamp(points, CorridorHalfWidthM, corridor);
    }

    /// <summary>A collidable map item. Closed rings block their interior; segments and chains block a band,
    ///  which is the honest reading of a wall drawn as one line.</summary>
    public void AddBlocker(Vector2[] points, bool closed)
    {
        if (closed && points.Length >= 3)
        {
            StampPolygon(points);
            return;
        }

        Stamp(points, BlockerBandM, blocked);
    }

    private void StampPolygon(Vector2[] ring)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (Vector2 p in ring)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }

        for (double x = Math.Floor(minX); x <= maxX; x += CellM)
        {
            for (double z = Math.Floor(minY); z <= maxY; z += CellM)
            {
                if (blocked.Count >= MaxCells)
                {
                    Truncated = true;
                    return;
                }

                Vector2 centre = new((float)(x + 0.5 * CellM), (float)(z + 0.5 * CellM));
                if (Geometry.PointInPolygon(ring, centre))
                    blocked.Add(KeyAt(centre));
            }
        }
    }

    private void Stamp(Vector2[] points, double halfWidth, HashSet<long> target)
    {
        if (points == null || points.Length == 0 || halfWidth <= 0.0)
            return;

        if (points.Length == 1)
        {
            StampSegment(points[0], points[0], halfWidth, target);
            return;
        }

        for (int i = 0; i + 1 < points.Length; i++)
            StampSegment(points[i], points[i + 1], halfWidth, target);
    }

    private void StampSegment(Vector2 from, Vector2 to, double halfWidth, HashSet<long> target)
    {
        double minX = Math.Min(from.X, to.X) - halfWidth;
        double maxX = Math.Max(from.X, to.X) + halfWidth;
        double minZ = Math.Min(from.Y, to.Y) - halfWidth;
        double maxZ = Math.Max(from.Y, to.Y) + halfWidth;

        for (double x = Math.Floor(minX); x <= maxX; x += CellM)
        {
            for (double z = Math.Floor(minZ); z <= maxZ; z += CellM)
            {
                if (target.Count >= MaxCells)
                {
                    Truncated = true;
                    return;
                }

                Vector2 centre = new((float)(x + 0.5 * CellM), (float)(z + 0.5 * CellM));

                // The radius is a property of the sample, not of the cell stamping, and it is cheapest
                // to enforce here: cells outside it never enter either set.
                if (Geometry.Distance(Center, centre) > RadiusM)
                    continue;

                if (Geometry.Distance(centre, Geometry.ClosestPointOnSegment(from, to, centre)) <= halfWidth)
                    target.Add(KeyAt(centre));
            }
        }
    }
}
```

- [x] **Step 4: Run and confirm the trust checks pass**

Run: `dotnet run --project Tools/PlannerHarness -c Release > /tmp/h.txt 2>&1; echo "exit=$?"; grep -n "✗" /tmp/h.txt`
Expected: no `✗` on any GroundTrust line. The pre-existing three `✗` lines stay; exit stays 1.

- [x] **Step 5: Add the call site and gate term**

In `Program.cs` after the PointInPolygon block:

```csharp
Console.WriteLine();
Console.WriteLine("地面可信度栅格（哪些地面地图自己担保能开）：");
int trustFailures = GroundTrustChecks.Run();
```

and add `&& trustFailures == 0` to the final `return` expression.

- [x] **Step 6: Verification**

Run: `dotnet build -c Release -t:Rebuild` → 0 errors, 13 warnings.
Run: the harness again → same three `✗`, exit 1.
Stage nothing, do not commit.

---

## Task 3: `RouteFootprint` + the measurement fields on `ParkingPath`

**Files:**
- Create: `Driving/RouteFootprint.cs`
- Modify: `Driving/ParkingPath.cs` (`ParkingPath` gets `SweptAreaM2`, `UnconfirmedAreaM2`, `FootprintTruncated`; `PlanResult.Summary` gains the readout)
- Modify: `Geometry.cs` (`PointInPolygon` already landed in Task 1; nothing further here)
- Create: `Tools/PlannerHarness/RouteFootprintChecks.cs`
- Modify: `Tools/PlannerHarness/PlannerHarness.csproj` (link `Driving\RouteFootprint.cs` **and** add
  `<Compile Include="RouteFootprintChecks.cs" />`)
- Modify: `Tools/PlannerHarness/Program.cs`

**Interfaces:**
- Consumes: `GroundTrust` (Task 2), `ParkingPath.Points` / `PathPoint.Position` / `PathPoint.HeadingRad`, `Geometry.RectangleCorners`, `AutoParkingSettings.VehicleLengthM/VehicleWidthM/ObstacleMarginM`.
- Produces:
  - `public static class RouteFootprint` with
    `public static void Measure(ParkingPath route, AutoParkingSettings settings, GroundTrust trust)`,
    `public static Vector2[] Envelope(AutoParkingSettings settings)` (returns `double width, double length` as a tuple? — no: it returns `EnvelopeSize(double LengthM, double WidthM)` record struct, because Task 4 and the docs both name the inflation).
  - `ParkingPath.SweptAreaM2` / `.UnconfirmedAreaM2` (`double`, default `-1` = not measured) and
    `.FootprintTruncated` (`bool`, default false).
  - The envelope equals the one `Planner.CountCorridorConflicts` already uses (`Planner.cs:320-321`):
    length `VehicleLengthM + 0.6 + 2*ObstacleMarginM`, width `VehicleWidthM + 0.5 + 2*ObstacleMarginM`.
    That equality is asserted, so the area and the collision test can never describe two different vehicles.

- [x] **Step 1: Add the fields (they must exist before the checks compile)**

In `Driving/ParkingPath.cs`, inside `public sealed class ParkingPath`, after `public int GearSwitches { get; init; }`:

```csharp
    /// <summary>
    ///  Ground area this route demands, in square metres, as whole 1 m cells — the vehicle envelope
    ///  swept along the path, not the bare body. -1 until RouteFootprint.Measure has run.
    /// </summary>
    public double SweptAreaM2 { get; set; } = -1.0;

    /// <summary>
    ///  Of that, the part sitting on ground the map does not vouch for (see GroundTrust). This is the
    ///  number the unrecognizable obstacles live in, so it is the one M7c will weight. -1 = unmeasured.
    /// </summary>
    public double UnconfirmedAreaM2 { get; set; } = -1.0;

    /// <summary>True when the rasterizer hit its cell budget, so the two areas above are understated.</summary>
    public bool FootprintTruncated { get; set; }
```

In the same file, replace `PlanResult.Summary` (currently `$"{Path.Description} · {Path.Length:0.0} m · 换挡 {Path.GearSwitches} · 冲突 {ConflictCount}"`) with:

```csharp
    public string Summary => Path != null
        ? $"{Path.Description} · {Path.Length:0.0} m · 换挡 {Path.GearSwitches} · 冲突 {ConflictCount}"
          + (Path.SweptAreaM2 >= 0.0
              ? $" · 占地 {Path.SweptAreaM2:0} m²（未确认 {Path.UnconfirmedAreaM2:0} m²）"
              : "")
        : $"无解：{Reason}";
```

- [x] **Step 2: Write the failing checks**

Create `Tools/PlannerHarness/RouteFootprintChecks.cs`:

```csharp
using System.Numerics;
using AutoParking;

/// <summary>
///  Checks for RouteFootprint. The premise it has to defend is M7a's whole contract: measuring must not
///  change which route the planner picks. So the last case runs the planner twice over the same scenario,
///  with and without a trust map, and demands the same winner from the same source family.
/// </summary>
internal static class RouteFootprintChecks
{
    public static int Run()
    {
        int failures = 0;
        AutoParkingSettings settings = new();
        (double length, double width) = RouteFootprint.EnvelopeSize(settings);

        bool sameAsCollision = Math.Abs(length - (settings.VehicleLengthM + 0.6 + 2 * settings.ObstacleMarginM)) < 1e-9
                               && Math.Abs(width - (settings.VehicleWidthM + 0.5 + 2 * settings.ObstacleMarginM)) < 1e-9;
        Report(ref failures, sameAsCollision, "包络=冲突判定用的那个包络",
            $"{length:0.00} × {width:0.00} m（和 CountCorridorConflicts 一致）");

        ParkingPath straight = Route(new Pose2(0, 0, 0), new Pose2(10, 0, 0), DriveDirection.Forward, settings);
        GroundTrust wide = Trust(-20, 20, 5.0, 120);

        RouteFootprint.Measure(straight, settings, wide);
        double expected = (10.0 + length) * width;
        Report(ref failures, straight.SweptAreaM2 >= expected * 0.85 && straight.SweptAreaM2 <= expected * 1.20,
            "直线段占地≈长×宽", $"{straight.SweptAreaM2:0} m²（参照 {expected:0} m²，栅格量化允许 ±15/20%）");

        // Out and back over the same ground must not double-count: cell sets, not sums.
        ParkingPath thereAndBack = Route(new Pose2(0, 0, 0), new Pose2(10, 0, 0), DriveDirection.Forward, settings,
                                         extra: new Pose2(10, 0, Math.PI));
        RouteFootprint.Measure(thereAndBack, settings, wide);
        Report(ref failures, Math.Abs(thereAndBack.SweptAreaM2 - straight.SweptAreaM2) <= 2.0,
            "同一条走廊来回只算一次", $"去 {straight.SweptAreaM2:0} m² / 往返 {thereAndBack.SweptAreaM2:0} m²");

        // Inside the corridor everything is vouched for.
        Report(ref failures, straight.UnconfirmedAreaM2 == 0.0,
            "走廊内 → 未确认面积 0", $"{straight.UnconfirmedAreaM2:0} m²");

        // Push the same route sideways, off the curve: it is still the same area, now all of it unvouched.
        ParkingPath offset = Route(new Pose2(0, 9, 0), new Pose2(10, 9, 0), DriveDirection.Forward, settings);
        RouteFootprint.Measure(offset, settings, wide);
        Report(ref failures, offset.UnconfirmedAreaM2 > 0.6 * offset.SweptAreaM2,
            "偏出走廊 → 大部分未确认",
            $"{offset.UnconfirmedAreaM2:0} / {offset.SweptAreaM2:0} m²（y=9 m，半宽 5 m）");

        // Shrinking the sampled radius can only make a route look more exposed, never less.
        GroundTrust tight = Trust(-20, 20, 5.0, 6.0);
        ParkingPath copy = Route(new Pose2(0, 0, 0), new Pose2(10, 0, 0), DriveDirection.Forward, settings);
        RouteFootprint.Measure(copy, settings, tight);
        Report(ref failures, copy.UnconfirmedAreaM2 >= straight.UnconfirmedAreaM2,
            "半径越小越不确认", $"6 m 半径 {copy.UnconfirmedAreaM2:0} m² ≥ 120 m 半径 {straight.UnconfirmedAreaM2:0} m²");

        // A collidable object on the route's ground converts confirmed cells to unconfirmed.
        GroundTrust walled = Trust(-20, 20, 5.0, 120);
        walled.AddBlocker(new[] { new Vector2(4, -1), new Vector2(6, -1), new Vector2(6, 1), new Vector2(4, 1) }, true);
        ParkingPath through = Route(new Pose2(0, 0, 0), new Pose2(10, 0, 0), DriveDirection.Forward, settings);
        RouteFootprint.Measure(through, settings, walled);
        Report(ref failures, through.UnconfirmedAreaM2 >= 4.0,
            "障碍方块吃掉确认", $"{through.UnconfirmedAreaM2:0} m²（2×2 墙 + 包络重叠）");

        // Unmeasured stays distinguishable from zero-measured, because the map has to render both.
        ParkingPath untouched = Route(new Pose2(0, 0, 0), new Pose2(5, 0, 0), DriveDirection.Forward, settings);
        Report(ref failures, untouched.SweptAreaM2 < 0.0 && untouched.UnconfirmedAreaM2 < 0.0,
            "未测量 = -1，不是 0", $"占地 {untouched.SweptAreaM2} / 未确认 {untouched.UnconfirmedAreaM2}");

        // THE contract of M7a: instrumenting must not steer. Same scenario, trust on and off,
        // the planner must hand back the same winning family.
        Pose2 start = new(0, 0, 0);
        Pose2 goal = new(-8, 0, Math.PI / 2.0);
        ObstacleSnapshot none = new();
        PlanResult plain = Planner.Plan(start, goal, settings, none);
        PlanResult measured = Planner.Plan(start, goal, settings, none, wide);
        bool unchanged = plain.Path != null && measured.Path != null
                         && plain.Path.Source == measured.Path.Source
                         && Math.Abs(plain.Path.Length - measured.Path.Length) < 1e-6;
        Report(ref failures, unchanged, "仪表不改变选路",
            plain.Path == null || measured.Path == null
                ? "一侧无解"
                : $"{plain.Path.Source} / {measured.Path.Source}，长度 {plain.Path.Length:0.00} vs {measured.Path.Length:0.00} m");

        return failures;
    }

    private static GroundTrust Trust(double from, double to, double halfWidth, double radius)
    {
        GroundTrust trust = new()
        {
            Center = new Vector2(0, 0),
            RadiusM = radius,
            CorridorHalfWidthM = halfWidth
        };
        trust.AddCorridor(new[] { new Vector2(from, 0), new Vector2(to, 0) });
        return trust;
    }

    private static ParkingPath Route(Pose2 from, Pose2 to, DriveDirection travel, AutoParkingSettings settings,
                                     Pose2? extra = null)
    {
        List<PathPoint> points = new();
        double steps = Math.Max(2, Math.Ceiling(Geometry.Distance(from.Position, to.Position) / settings.PathSampleM));
        double along = 0.0;

        for (int i = 0; i <= steps; i++)
        {
            double t = i / steps;
            Vector2 position = from.Position + (to.Position - from.Position) * (float)t;
            points.Add(new PathPoint(position, from.HeadingRad, 0.0, travel, along));
            along += Geometry.Distance(from.Position, to.Position) / steps;
        }

        if (extra != null)
        {
            // Drive back the way we came, at the mirrored heading, so the same cells get demanded twice.
            Pose2 back = extra.Value;
            double offset = points[^1].DistanceAlong;
            double reverseSteps = Math.Max(2, Math.Ceiling(Geometry.Distance(to.Position, back.Position) / settings.PathSampleM));
            for (int i = 0; i <= reverseSteps; i++)
            {
                double t = i / reverseSteps;
                Vector2 position = back.Position + (to.Position - back.Position) * (float)t;
                points.Add(new PathPoint(position, back.HeadingRad, 0.0, DriveDirection.Reverse, offset
                         + Geometry.Distance(back.Position, to.Position) * t));
            }
        }

        return new ParkingPath
        {
            Points = points,
            Source = PlanSource.ReedsSheppForward,
            Cost = points[^1].DistanceAlong,
            GearSwitches = travel == DriveDirection.Reverse ? 1 : 0,
            Description = "测试路径"
        };
    }

    private static void Report(ref int failures, bool passed, string name, string detail)
    {
        Console.WriteLine($"  {(passed ? "✓" : "✗")} {name}：{detail}");
        if (!passed)
            failures++;
    }
}
```

- [x] **Step 3: Confirm it fails for the missing type**

Run: `dotnet run --project Tools/PlannerHarness -c Release`
Expected: `error CS0246` for `RouteFootprint` (and, until Task 4's parameter exists, `CS1501` on the 5-argument
`Planner.Plan` — that is expected at this point; Task 4 adds the overload).

- [x] **Step 4: Implement `RouteFootprint`**

Create `Driving/RouteFootprint.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoParking;

/// <summary>
///  How much ground a route demands, and how much of it nobody vouched for.
///
///  The quantity is the same envelope the collision test already uses (vehicle plus its own margin, see
///  Planner.CountCorridorConflicts), rasterized to 1 m cells, so "占地 430 m²" reads as the corridor we are
///  asking the map for rather than the paint the vehicle would spill. Rasterizing rather than integrating
///  analytically is what makes revisited ground count once: a two-leg maneuver backs over its own approach,
///  and a summed strip length would charge for it twice and hide exactly the exposure we are measuring.
/// </summary>
public static class RouteFootprint
{
    /// <summary>Cell budget per route. A 60 m route at this resolution is a few thousand cells; a route
    ///  that needs more than this is not a route, and we would rather flag it than stall the tick thread.</summary>
    public const int MaxCellsPerRoute = 40_000;

    /// <summary>The envelope the vehicle demands from the ground: body plus the margin the collision test
    ///  insists on, so the area and the conflicts describe one vehicle, not two.</summary>
    public static (double LengthM, double WidthM) EnvelopeSize(AutoParkingSettings settings)
    {
        return (settings.VehicleLengthM + 0.6 + 2.0 * settings.ObstacleMarginM,
                settings.VehicleWidthM + 0.5 + 2.0 * settings.ObstacleMarginM);
    }

    public static void Measure(ParkingPath route, AutoParkingSettings settings, GroundTrust trust)
    {
        if (route == null || settings == null || trust == null || route.Points.Count < 2)
            return;

        (double length, double width) = EnvelopeSize(settings);
        HashSet<long> covered = new();

        // PathSampleM is 0.25 m, which would stamp the same rectangle four times per cell. Advancing about
        // three quarters of a cell between stamps keeps consecutive envelopes overlapping, so no cell the
        // envelope really covers can slip between two samples.
        double stride = Math.Max(0.5, GroundTrust.CellM * 0.75);
        double lastStampedAt = -stride;

        foreach (PathPoint point in route.Points)
        {
            if (point.DistanceAlong - lastStampedAt < stride)
                continue;

            lastStampedAt = point.DistanceAlong;
            Pose2 pose = new(point.Position.X, point.Position.Y, point.HeadingRad);
            Stamp(Geometry.RectangleCorners(pose, length, width), trust, covered);

            if (covered.Count >= MaxCellsPerRoute)
            {
                route.FootprintTruncated = true;
                break;
            }
        }

        double cellArea = GroundTrust.CellM * GroundTrust.CellM;
        int unconfirmed = 0;
        foreach (long key in covered)
        {
            if (!trust.IsConfirmedCell(key))
                unconfirmed++;
        }

        route.SweptAreaM2 = covered.Count * cellArea;
        route.UnconfirmedAreaM2 = unconfirmed * cellArea;
    }

    private static void Stamp(Vector2[] rectangle, GroundTrust trust, HashSet<long> target)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (Vector2 corner in rectangle)
        {
            minX = Math.Min(minX, corner.X); maxX = Math.Max(maxX, corner.X);
            minY = Math.Min(minY, corner.Y); maxY = Math.Max(maxY, corner.Y);
        }

        for (double x = Math.Floor(minX); x <= maxX; x += GroundTrust.CellM)
        {
            for (double z = Math.Floor(minY); z <= maxY; z += GroundTrust.CellM)
            {
                if (target.Count >= MaxCellsPerRoute)
                    return;

                Vector2 cell = new((float)(x + 0.5 * GroundTrust.CellM), (float)(z + 0.5 * GroundTrust.CellM));
                if (Geometry.PointInPolygon(rectangle, cell))
                    target.Add(trust.KeyAt(cell));
            }
        }
    }
}
```

- [x] **Step 5: Make it compile and pass**

Add both to `PlannerHarness.csproj`:

```xml
    <Compile Include="RouteFootprintChecks.cs" />
    <Compile Include="$(PluginRoot)Driving\RouteFootprint.cs" />
```
Run: `dotnet run --project Tools/PlannerHarness -c Release > /tmp/h.txt 2>&1; echo "exit=$?"; grep -n "✗" /tmp/h.txt`
Expected at this task: every RouteFootprint line `✓` **except** the "仪表不改变选路" case, which cannot compile
until Task 4 adds the optional `trust` parameter. If that blocks compilation, land Task 4's Step 1 (the parameter
and its plumbing, which changes no selection) before Step 5 here, and note the interleaving in the design doc —
do not fake the assertion by dropping it.

- [x] **Step 6: Add the call site and gate term**

In `Program.cs`:

```csharp
Console.WriteLine();
Console.WriteLine("路线扫掠占地 / 未确认地面（M7a 仪表）：");
int footprintFailures = RouteFootprintChecks.Run();
```

plus `&& footprintFailures == 0` in the final `return`.

- [x] **Step 7: Verification**

Build with `-t:Rebuild` (0 errors / 13 warnings), harness exit unchanged at 1 with only the three known `✗`.
Stage nothing, do not commit.

---

## Task 4: Planner measures every candidate, and exposes them

**Files:**
- Modify: `Driving/Planner.cs` (`Plan` signature at line 42; candidate loop at lines 60-80; `PlanResult` construction)
- Modify: `Driving/ParkingPath.cs` (`PlanResult` gets `Candidates`)
- Modify: `Tools/PlannerHarness/RouteFootprintChecks.cs` (the 5-argument `Planner.Plan` call from Task 3 compiles now)

**Interfaces:**
- Consumes: `GroundTrust` (Task 2), `RouteFootprint.Measure` (Task 3), `Collect` (unchanged, `Planner.cs:116`).
- Produces:
  - `public static PlanResult Plan(Pose2 start, Pose2 goal, AutoParkingSettings settings, ObstacleSnapshot obstacles, GroundTrust? trust = null)`
    — the 4-argument form keeps compiling for `ClosedLoop`, `PlannerSelfTest` and the Follower lambda.
  - `PlanResult.Candidates` : `IReadOnlyList<ParkingPath>?` — the full sorted candidate list, already measured.
  - `public const int MaxLoggedCandidates = 8` on `Planner` is **not** introduced here; the plugin owns how many
    lines it prints.

- [x] **Step 1: Add the parameter and the measurement loop**

In `Driving/Planner.cs`, change the signature (`line 42`) to:

```csharp
    /// <param name="trust">When supplied, every candidate gets its swept footprint and unconfirmed-ground
    ///  area measured for the readout. It does not take part in the choice — M7a instruments, M7c decides.</param>
    public static PlanResult Plan(Pose2 start, Pose2 goal, AutoParkingSettings settings, ObstacleSnapshot obstacles,
                                  GroundTrust? trust = null)
```

immediately after `int evaluated = candidates.Count;` (`line 62`) insert:

```csharp
        if (trust != null)
        {
            foreach (ParkingPath candidate in candidates)
                RouteFootprint.Measure(candidate, settings, trust);
        }
```

and add `Candidates = candidates,` to the **three** `PlanResult` initializers that come after `Collect`
(`Planner.cs:84`, `:97`, `:107`). The one at `Planner.cs:50` returns before any candidate exists — leave it alone;
`Candidates` is nullable and its default `null` is the honest value there.

- [x] **Step 2: Expose the list**

In `Driving/ParkingPath.cs`, in `PlanResult` after `public int CandidatesEvaluated { get; init; }`:

```csharp
    /// <summary>
    ///  Every candidate the planner considered, cheapest first, with the footprint fields filled in when a
    ///  GroundTrust was supplied. The plugin prints this: two routes with the same length and different
    ///  exposure are indistinguishable from the winner alone, and distinguishing them is the point of M7a.
    /// </summary>
    public IReadOnlyList<ParkingPath>? Candidates { get; init; }
```

- [x] **Step 3: Run the harness — the "instrumentation does not steer" assertion must now compile and pass**

Run: `dotnet run --project Tools/PlannerHarness -c Release > /tmp/h.txt 2>&1; echo "exit=$?"; sed -n '/M7a 仪表/,/^$/p' /tmp/h.txt`
Expected: all lines `✓`, including `仪表不改变选路：ReedsSheppForward / ReedsSheppForward，长度 … vs … m`.
Exit 1, only the three known `✗` elsewhere.

- [x] **Step 4: Print the winner in the harness for eyeballing the scale**

Append to `RouteFootprintChecks.Run()` before `return failures;`:

```csharp
        Console.WriteLine("   各候选（长度 / 换挡 / 占地 / 未确认）：");
        if (measured.Candidates != null)
        {
            foreach (ParkingPath candidate in measured.Candidates)
            {
                Console.WriteLine($"    {candidate.Description,-28} {candidate.Length,5:0.0} m  " +
                                  $"换挡 {candidate.GearSwitches}  占地 {candidate.SweptAreaM2,5:0} m²  " +
                                  $"未确认 {candidate.UnconfirmedAreaM2,5:0} m²");
            }
        }
```

Run the harness again and **record the printed table** — Task 6 puts the real numbers in the design doc.

- [x] **Step 5: Verification** — rebuild (`0 错误 / 13 警告`), harness verdicts unchanged. Stage nothing, do not commit.

---

## Task 5: Host wiring — build the trust map, pass it, log every candidate

**Files:**
- Modify: `Rendering/MapGeometry.cs` (`MapGeometry` fields near line 38; `MapGeometryBuilder.Build` at line 182, success path ending around line 233)
- Modify: `Settings.cs` (new knob + clamp)
- Modify: `AutoParkingPlugin.cs` (`Replan` at lines 354-374; the Follower replanner lambda at line 1175; `StatusRows` at line 1400 for the readout)
- Modify: `SettingsPage.razor` (slider for the half-width)

**Interfaces:**
- Consumes: `MapGeometry.DriveableCurves` / `.RoadLanes` / `.StaticShapes` (already collected), `Planner.Plan(..., trust)` (Task 4).
- Produces: `public GroundTrust? Trust;` on `MapGeometry`; `AutoParkingSettings.ConfirmedCorridorHalfWidthM`
  (default 5.0, clamped 2.0–20.0); a `[[路径代价]]` log block.

- [x] **Step 1: Add the knob**

In `Settings.cs`, in the Visualisation block after `SnapToNavCurve`:

```csharp
    /// <summary>
    ///  How far a navigation curve or lane centerline vouches for the ground beside it. Half of a typical
    ///  aisle: a curve runs down the middle of the drive lane, and the lane is what we want confirmed.
    ///  Too large and "unconfirmed" disappears; too small and the bay you are parking into counts as unknown.
    /// </summary>
    public double ConfirmedCorridorHalfWidthM { get; set; } = 5.0;
```

and in `Clamp()`: `ConfirmedCorridorHalfWidthM = Math.Clamp(ConfirmedCorridorHalfWidthM, 2.0, 20.0);`

- [x] **Step 2: Build the trust grid in the map builder**

In `Rendering/MapGeometry.cs`, add the field next to `Probe` (`line 68`):

```csharp
    /// <summary>
    ///  Which ground inside this sample the map itself vouches for, and which it does not. Built from the
    ///  same pass that fills the drawing lists, so the picture on screen and the number in the plan cost can
    ///  never come from different data. Null when the build failed.
    /// </summary>
    public GroundTrust? Trust;
```

Then in `Build`, immediately before `geometry.Status = ...` on the success path (around `line 231`), insert —
using the settings the builder already has for the half-width and the sample radius it already computed:

```csharp
            GroundTrust trust = new()
            {
                Center = center,
                RadiusM = radiusM,
                CorridorHalfWidthM = corridorHalfWidthM
            };

            foreach (Vector2[] curve in geometry.DriveableCurves)
                trust.AddCorridor(curve);

            foreach (MapGeometry.LaneLine lane in geometry.RoadLanes)
                trust.AddCorridor(lane.Points);

            foreach (MapGeometry.StaticShape shape in geometry.StaticShapes)
            {
                if (shape.Collision)
                    trust.AddBlocker(shape.Points, shape.Closed);
            }

            geometry.Trust = trust;
```

`corridorHalfWidthM` is a new parameter to `Build`, threaded from the settings the caller already holds. Give it
the signature `public MapGeometry Build(MapData map, Vector2 center, double radiusM, double corridorHalfWidthM)`
and update the one call site — `AutoParkingPlugin.cs:267`, two lines under
`double radius = settings.MapDataRadiusM(MapOverlay.WindowHeight);` at line 266:

```csharp
        MapGeometry geometry = geometryBuilder.Build(map, truck.Position, radius,
                                                      settings.ConfirmedCorridorHalfWidthM);
```

Change signature and call site in the same edit so the build is never left broken. Inside `Build` the plane is
`Vector2(X, Z)` — the existing code reads `center.Y` where it means world Z — so `Center = center` above inherits
that convention and must not be "corrected".

Also append to the `Status` line so the panel shows how much ground the grid actually vouches for, using
`GroundTrust.ConfirmedCells` (Task 2; it counts corridor cells that no blocker sits on, which is why subtracting
`CorridorCells - BlockedCells` would have been wrong):

```csharp
                              $" · 可信地面 {trust.ConfirmedCells} m²" +
                              (trust.Truncated ? "（栅格已截断）" : "");
```

- [x] **Step 3: Pass it, and log every candidate**

In `AutoParkingPlugin.cs`, `Replan` (`line 371`) becomes:

```csharp
        PlanResult result = Planner.Plan(CurrentPose, spot.Value, snapshot, geometry.Obstacles, geometry.Trust);
        lock (sync) planResult = result;

        LogCandidateCosts(result);
```

with the new private method placed next to `DumpMapInventory` (`line 283`), following its markup discipline —
`ETS2LA.Logging` swallows a bare `[Tag]`, hence the doubled brackets:

```csharp
    /// <summary>
    ///  One block per plan: every candidate the planner considered with the four numbers M7c will be
    ///  weighted on. Deliberately not one line for the winner — the whole question is whether the loser
    ///  that costs less ground was available at all, and that cannot be read off a winner.
    /// </summary>
    private void LogCandidateCosts(PlanResult result)
    {
        if (result.Candidates == null || result.Candidates.Count == 0)
            return;

        int shown = 0;
        foreach (ParkingPath candidate in result.Candidates)
        {
            if (shown++ == 8)
            {
                Logger.Info($"AutoParking: [[路径代价]] 其余 {result.Candidates.Count - 8} 条已省略");
                break;
            }

            Logger.Info($"AutoParking: [[路径代价]] {(ReferenceEquals(candidate, result.Path) ? "选中 " : "      ")}" +
                        $"{candidate.Description} · 长 {candidate.Length:0.0} m · 换挡 {candidate.GearSwitches} · " +
                        $"占地 {candidate.SweptAreaM2:0} m² · 未确认 {candidate.UnconfirmedAreaM2:0} m²" +
                        (candidate.FootprintTruncated ? "（栅格截断）" : ""));
        }
    }
```

The Follower's replanner lambda (`line 1175`) needs the same grid; capture it once before the lambda so a
mid-maneuver rebuild cannot hand the closure two different grids:

```csharp
                GroundTrust? planTrust = MapGeometrySnapshot?.Trust;
                follower = new Follower(planResult!.Path!, settings, settings.WheelbaseM,
                    (from, field) =>
                    {
                        PlanResult next = Planner.Plan(from, spot, cfg, field, planTrust);
                        return next.Ok ? next.Path : null;
                    });
```

- [x] **Step 4: Settings page control**

In `SettingsPage.razor`, after the 吸附到导航曲线 switch and before the 锁定地图窗口 switch, add the slider in the
same shape as the existing sliders (e.g. the AR 地面微调 one at `line 63`):

```razor
        <Slider Title="可信走廊半宽 (m)"
                Description="导航曲线/车道线向两侧确认多少地面算「已确认」。太大则未确认面积消失，太小则车位本身也算未知。只影响「占地/未确认」两个读数，暂不影响选路。"
                Min="@(2f)" Max="@(20f)" Step="@(0.5f)"
                Value="@(Convert.ToSingle(Plugin.Settings.ConfirmedCorridorHalfWidthM))"
                ValueChanged="@((float value) => Plugin.HandleAction("corridorHalfWidth", value))" />
```

and in `AutoParkingPlugin.HandleAction`, next to `case "snapToNavCurve"` (`line 938`):

```csharp
                case "corridorHalfWidth":
                    settings.ConfirmedCorridorHalfWidthM = ToDouble(value, settings.ConfirmedCorridorHalfWidthM);
                    changed = true;
                    break;
```

- [x] **Step 5: Verification**

`dotnet build -c Release -t:Rebuild` → 0 errors, 13 warnings.
`dotnet run --project Tools/PlannerHarness -c Release` → unchanged verdicts, and the new candidate table prints.
Deploy: copy `bin\Release\AutoParking.dll` to `..\..\ETS2LA-win-release-Portable\current\Plugins\` and tell the
owner ETS2LA must restart. Stage nothing, do not commit.

---

## Task 6: Documentation of the instrument and the numbers

**Files:**
- Modify: `docs\2026-09-30-autoparking-design.md` (append §34)
- Modify: `README.md` (new §6.6, plus a row in the §5 settings table)
- Modify: `README.zh-CN.md` (mirror of both)
- Modify: `AGENTS.md` (`§17–§33` pointer → `§17–§34`)

- [x] **Step 1: Design doc §34**

Append a section in the §17 style, with these facts stated in this order — each one is required, and the
numbers come from the harness table captured in Task 4 Step 4 plus one in-game plan:

1. Motivation, in one sentence: after §32 (static content is unknowable below the prefab boundary), the risk
   moved from "identify the obstacle" to "commit to less ground".
2. The four approved defaults, verbatim from the Spec block of this plan, and which task implemented what.
3. What is measured: the envelope is `CountCorridorConflicts`' envelope (asserted equal), the grid is 1 m,
   revisited ground counts once, `MaxCells`/`MaxCellsPerRoute` truncation flags exist and how they read.
4. What "confirmed ground" means here and what it deliberately does **not** mean (not an obstacle model; the
   bay itself may read as unconfirmed when no nav curve passes through it — say so, and record whether that
   happened in the measured case rather than assuming it did not).
5. The measured table: for each of the harness scenarios (直线倒库 / 右前45° / 左侧垂直库 / 两段式) the winner's
   长度 / 换挡 / 占地 / 未确认, and the same numbers for the runner-up. **If the in-game numbers have not been
   read yet, write "未实测" next to them** — that is the project rule, and this is the artifact that decides
   M7c's weights.
6. The explicit statement that selection did not change, with the harness assertion name (仪表不改变选路) as
   the evidence.
7. Next: M7b (the missing candidate family — short forward pre-align plus a *curved* reverse entry, because
   `TryTwoLegStaging` at `Planner.cs:238-277` demands arrival already aligned, which is the structural reason
   for the loop) and M7c (new cost + the gear-switch counting that is inconsistent across families today:
   `Planner.cs:267-273` counts `legA.GearSwitches + 1`, `ReedsShepp.cs:88-89` hard-codes 1, `AppendStraightTail`
   at `Planner.cs:192` adds length without recounting the boundary).

- [x] **Step 2: README §6.6 (English, then the Chinese mirror)**

English, after §6.5:

```markdown
### 6.6 Candidate route cost (the `路径代价` log block)

Every plan logs all the routes the planner considered, not just the winner: length, gear switches,
the ground area the route demands (vehicle envelope swept along the path, rasterized to 1 m cells)
and how much of that area sits on ground the map does not vouch for. "Vouched for" means: inside the
sampled radius, within **可信走廊半宽** of a prefab navigation curve or road lane, and not underneath a
collidable map object. It is a measurement, not a veto — the winner is still chosen by length plus the
gear-change penalty, and the map line `占地 A m²（未确认 B m²）` is what you can read the trade-off from.
Design doc §34 explains why exposure to *unconfirmed* ground is the thing worth minimizing now that the
prefab interior is unreachable (§32). Numbers measured offline in the harness; the in-game readout is
not yet measured.
```

Chinese §6.6 mirrors it with the same last sentence (未实测标注), and both READMEs get the settings-table row:

```markdown
| | `ConfirmedCorridorHalfWidthM` | 5.0 | How far a nav curve or lane vouches for the ground beside it (§6.6) |
```

- [x] **Step 3: Pointer and consistency**

`AGENTS.md` line 5: `§17–§33` → `§17–§34`. Then re-read both READMEs' §6 numbering for collisions and grep the
doc set for the claim "不改选优" so the design doc, README and harness assertion name all use the same words.

- [x] **Step 4: Final verification, then ask before committing**

`dotnet build -c Release -t:Rebuild`, `dotnet run --project Tools/PlannerHarness -c Release`, `git status --short`,
`git diff --stat`. Report the measured table and the diff summary to the owner; commit only on his word (English
message, named files, no push).

---

## Self-Review notes

- **Spec coverage:** default (1) primary objective → Tasks 2/3 measure unconfirmed area; default (2) definition
  of confirmed ground → Task 2 (`AddCorridor`/`AddBlocker`/radius) with the half-width knob in Task 5; default (3)
  gear penalty untouched → Task 4 changes no cost term (asserted by 仪表不改变选路); default (4) instruments only →
  same assertion. M7b/M7c are out of scope by the owner's own ordering and are recorded as follow-on work in §34
  rather than smuggled into a task here.
- **Placeholders:** grepped for `TBD` / `TODO` / "implement later" — none. Every code step carries the code. Two
  couplings are stated as ordering notes rather than hand-waved: Task 3's "instrumentation must not steer"
  assertion needs Task 4's parameter to compile (Task 3 Step 5 says to land Task 4 Step 1 first, and not to drop
  the assertion), and `ConfirmedCells` is owned by Task 2 because Task 2's checks assert it while Task 5 only
  prints it.
- **Type consistency:** `GroundTrust.KeyAt`/`IsConfirmedCell`/`IsConfirmed`, `RouteFootprint.Measure`/
  `EnvelopeSize` (returns a named tuple `(double LengthM, double WidthM)` — the checks destructure it as
  `(double length, double width)`), `ParkingPath.SweptAreaM2`/`UnconfirmedAreaM2`/`FootprintTruncated`,
  `PlanResult.Candidates`, `AutoParkingSettings.ConfirmedCorridorHalfWidthM` are the only new names introduced.

---

## Execution notes (2026-10-04)

Executed inline in this session. All 6 tasks landed; every checkbox above is now ticked.
Five deviations, each recorded with its evidence in design doc §34 ("执行期间推翻的东西"):
the harness csproj lists sources by hand so new check files must be registered (plan gap);
the red state for a brand-new plugin type is `CS2001`, not `CS0246`; the cap assertion as written
passed without exercising the cap; two of my own test cases were wrong (yaw 0 = -Z made the
"straight" route slide sideways, and the out-and-back case had a zero-length return leg); and
`Replan` runs every 500 ms, so the candidate block is logged per *decision*, not per plan.
Final: `dotnet build -c Release -t:Rebuild` 0 errors / 13 warnings, harness 69 pass / the same 3
known failures as the pre-change baseline (exit 1 by design), DLL deployed.
Not committed: this project commits only on the owner's word.
