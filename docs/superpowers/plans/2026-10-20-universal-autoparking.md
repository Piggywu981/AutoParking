# 通用自动泊车（MarkerFollower）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把驾校场景验证过的能力做进 AutoParking 插件——热键后插件跟随游戏 0xC17 Mover 标记自行解析目标、规划并执行泊车；影子模式默认开，实车按起步门放行。

**Architecture:** MarkerFollower 协议环：Acquiring（KdopReader 几何 + IdentityChannel sidecar 身份）→ 解析目标（0xC17 对 → framing 缺口 → SavedSpot）→ 规划一腿（接近=§53 平滑族松包络 / 倒库=RS+动态包络）→ Follower 执行 → 标记位移则重规划、到达停稳则重新解析。几何走现有 KdopReader（后台线程快照），身份走 MemoryProbe.exe sidecar（0.3–0.6 s 实测，仅解析时调用）。M4 边界不动：中止只有热键；起步检查是"门"不是"中止"。

**Tech Stack:** C# / net10.0，无 NuGet；离线验证 = Tools/PlannerHarness（纯数学，无游戏无宿主）。

**Spec:** `docs/superpowers/specs/2026-10-19-universal-autoparking-design.md`（已提交 d70966f）

**对 spec 的两处显式偏离（审阅时请注意）：**
1. 文件放平铺（`Driving/TargetResolver.cs` 等），不建 `Driving/Autoparking/` 子目录——仓库 Driving\ 现状即平铺。
2. 新增配置键 `GameProcessName`（默认 `"eurotrucks2"`）：sidecar 第一个参数是进程名（MemoryProbe 默认 `amtrucks`，ETS2 必须显式传，见项目记忆）。
3. 新增配置键 `AutoParkByMarker`（默认 **true**，关闭即回到手动选点的旧行为）——spec §6 未列，但保留现有工作流需要它。
4. spec §4.1 写 controller「输出 ControlOutput 或 Idle」；落地为 controller 是**纯决策核**（Task 7），控制输出仍走现有 ParkingPhase 机器与 Follower→ControlOutput 通路（Task 8 抽 EngageFollower 复用）——不复制一套相位机，避免两套状态漂移。

## Global Constraints

- **Harness 基线纪律**：当前 `dotnet run --project Tools/PlannerHarness -c Release` = exit 1 / 131 ✓ / 三条固定 ✗（右前45度横 0.01 m、闭环超时、§45 无0冲突候选）。新判据只允许**增加 ✓**；✗ 集合必须原样；**永不为变绿降门槛**。
- **触碰 `Tools\` 下任何文件后必须跑 harness**（插件 build 不编译 Tools，悬空引用不可见）。
- **Harness csproj 手工列文件**（`EnableDefaultCompileItems=false`）：每个新纯源文件与其 `*Checks.cs` 都要加 `<Compile Include>`，漏加=测试静默不跑。
- **M4 硬政策**：只有热键能中止机动；不得新增任何自动 pause/abort。起步门只在 Idle→Acquiring 时拒绝起步。
- **只支持牵引车**（`RefuseWithTrailer` 保持默认开）。Tractor unit only.
- **证据先于修复**：控制类改动必须有 harness 判据、探针或实测日志支撑；读代码不算证据。
- **Commits：英文 only**；不 push 除非明确要求。每任务至少一个 commit。
- **改动后更新设计文档** `docs/2026-09-30-autoparking-design.md`：追加 §54 起的日志条目（症状、线索、测量），含被否决的假设。README.md 与 README.zh-CN.md 同步改。
- **部署**：`dotnet build -c Release` → 把 `bin\Release\AutoParking.dll`（和 sidecar exe）拷进 `..\..\ETS2LA-win-release-Portable\current\Plugins\`（以本 checkout 的 HintPath 相对宿主目录为准）→ 重启 ETS2LA（shadow-copy，运行中宿主持旧 DLL）。
- **Known-good harness run output must be non-empty**：0 行 ✓/✗ 且 exit 1 = build 失败，不是绿灯。

## 关键接口速查（实现时的对接契约，均已从源码核实）

```csharp
// Driving/Planner.cs:52
public static PlanResult Plan(Pose2 start, Pose2 goal, AutoParkingSettings settings,
                              ObstacleSnapshot obstacles, GroundTrust? trust = null)
// PlanResult (Driving/ParkingPath.cs:113): Path( ParkingPath? ), Reason, ConflictCount, Ok, Summary

// Driving/Follower.cs:45,137,205
public Follower(ParkingPath path, AutoParkingSettings settings, double wheelbase,
                Func<Pose2, ObstacleSnapshot, ParkingPath?> replan) { Trace = ... }
public ControlDemand Step(VehicleState vehicle, ObstacleSnapshot obstacles)  // .Finished 到达

// Driving/Follower.cs:11
public readonly record struct VehicleState(DateTime Utc, Vector2 Position, double HeadingRad,
                                           double SignedSpeed, int Gear, int GearDashboard)

// Memory/KdopReader.cs:37,68,1094（插件侧）
public sealed record MemItem(int Type, MemKind Kind, ulong Uid, Vector2 PlanePos,
                             float Height, float Dx, float Dy, float Dz, uint Flags);
internal KdopReader.Snapshot? MemoryItemSnapshot => kdopReader?.Current;   // 后台刷新，读不阻塞

// AutoParkingPlugin.cs
public string StartParking()          // :1642，lock(sync) 内，先 ValidateStart()
private string? ValidateStart()       // :1829，返回非 null = 拒绝原因
public void HandleToggle()            // :1721，主热键
public void Abort(string reason)      // :1781，只被 OnAbortKeyPressed 调用
private void CompletePhase(ParkingPhase from)  // :956
// 相机字段：latestTelemetry.truckFloat.speed (m/s)、.engineRpm、truckInt.gear、
//           configString.shifterType；方法 HasBrakeAir()、IsTrailerAttached()
// 输出：output.DryRun = settings.DryRun;  在 StepManeuver ~:636

// Settings/Settings.cs：EnvelopeWidthM => VehicleWidthM + 2*(EnvelopePadM + ObstacleMarginM)
//   EnvelopePadM clamp [0,1]、ObstacleMarginM clamp [0.05,1.5]、VehicleWidthM 默认 2.60

// Tools/PlannerHarness/ClosedLoop.cs:33
public static Outcome Run(string name, Pose2 start, Pose2 goal, AutoParkingSettings cfg,
    bool debug = false, bool gearboxDeaf = false, ObstacleSnapshot? obstacles = null, ...)
```

---

### Task 1: TargetResolver（纯目标解析链 + harness fixture 判据）

**Files:**
- Create: `Driving/TargetResolver.cs`
- Test: `Tools/PlannerHarness/TargetResolverChecks.cs`
- Modify: `Tools/PlannerHarness/PlannerHarness.csproj`（加两个 `<Compile Include>`）

**Interfaces:**
- Consumes: `Pose2`、`Geometry.ForwardFromHeading`、`Geometry.Distance`（均在 Geometry.cs）
- Produces（后续任务依赖，签名锁定）:
```csharp
public static class TargetResolver
{
    public readonly record struct Item(int Type, ulong Uid, Vector2 Pos, float Dx, float Dz, uint Flags);
    public readonly record struct Target(Pose2 Pose, string Provenance, double Confidence, ulong Uid,
                                         double? LateralClearM, Vector2? GapCenter);
    public static Target? Resolve(IReadOnlyList<Item> items, Pose2 truck, Pose2? savedSpot,
                                  double vehicleLengthM);
    public static double InsertPad(double lateralClearM, double vehicleWidthM, double safetyMarginM);
}
```
- 判据常量（写死并注释来源）：`MoverType=0x09`、`HookupType=0x2F`、`C17Flags=0xC17u`、parked 族启发 `(flags & 0x300u) != 0`（覆盖实测 0x100/0x10C/0x200/0x3100/0x21120 全部）、`PairRadiusM=2.5`、`RowBandM=1.5`、`MinGapOverTruckM=1.0`、`ConfMover=0.9`、`ConfFraming=0.6`、`ConfSaved=0.4`。

- [ ] **Step 1: 写失败判据**（fixture = 2026-10-19 实测内存条目，数值逐字来自 `deep_parallel.txt`）

```csharp
// Tools/PlannerHarness/TargetResolverChecks.cs
using System.Numerics;
using AutoParking;

internal static class TargetResolverChecks
{
    // 2026-10-19 驾校 book1_2_09 实测（--deep 2194.40 1804.12 90）：0xC17 对 + 三台 framing 车。
    private static readonly TargetResolver.Item[] Scene =
    {
        new(0x09, 0x512DA87F50000000, new Vector2(2166.03f, 1801.67f), 8.75f, 8.75f, 0xC17),
        new(0x09, 0x3C11E12C59800001, new Vector2(2166.32f, 1802.00f), 7.94f, 7.94f, 0xC17),
        new(0x2F, 0x50E541F01C9C0001, new Vector2(2166.19f, 1797.41f), 7.50f, 3.23f, 0x200),
        new(0x2F, 0x51B24FF0D91C0000, new Vector2(2182.47f, 1797.42f), 7.50f, 3.23f, 0x100),
        new(0x2F, 0x50E5468F7C9C0000, new Vector2(2190.58f, 1797.42f), 7.50f, 3.23f, 0x100),
    };
    private static readonly Pose2 Truck = new(2194.40, 1804.12, 1.295); // 74.2°=1.295 rad

    public static void Run()
    {
        MoverPairWins();
        PairHeadingFollowsTruck();
        FramingGapWhenPairRemoved();
        SavedSpotFallback();
        NullWhenEmpty();
        InsertPadFormula();
    }

    private static void Check(bool ok, string what) =>
        System.Console.WriteLine($"  {(ok ? "✓" : "✗")} TargetResolver: {what}");

    private static void MoverPairWins()
    {
        var t = TargetResolver.Resolve(Scene, Truck, new Pose2(0, 0, 0), 6.5);
        bool ok = t.HasValue && Math.Abs(t.Value.Pose.X - 2166.175) < 0.01
                && Math.Abs(t.Value.Pose.Z - 1801.835) < 0.01
                && Math.Abs(t.Value.Confidence - 0.9) < 0.001
                && !t.Value.LateralClearM.HasValue   // 停稳区对在开阔车道上，8 m 外的缺口不许借包络
                && t.Value.Provenance.StartsWith("mover-pair");
        Check(ok, "0xC17 对优先（中点 2166.175,1801.835 · conf 0.9 · 不借缺口净宽）");
    }

    private static void PairHeadingFollowsTruck()
    {
        // 车头朝西（-X）时 X 轴排应解出 90°；把车调头朝东再解一次应得 270°。
        var west = TargetResolver.Resolve(Scene, Truck, null, 6.5);
        var east = TargetResolver.Resolve(Scene, new Pose2(2194.40, 1804.12, 1.295 + Math.PI), null, 6.5);
        bool ok = west.HasValue && Math.Abs(west.Value.Pose.HeadingRad - Math.PI / 2) < 0.01
                && east.HasValue && Math.Abs(Math.Abs(east.Value.Pose.HeadingRad) - Math.PI / 2) < 0.01;
        Check(ok, "对目标航向取排轴线中与车头相近的符号");
    }

    private static void FramingGapWhenPairRemoved()
    {
        var noPair = new TargetResolver.Item[Scene.Length - 2];
        Array.Copy(Scene, 2, noPair, 0, noPair.Length);
        var t = TargetResolver.Resolve(noPair, Truck, null, 6.5);
        bool ok = t.HasValue && Math.Abs(t.Value.Pose.X - 2174.33) < 0.02
                && Math.Abs(t.Value.Pose.Z - 1797.42) < 0.02
                && Math.Abs(t.Value.Confidence - 0.6) < 0.001
                && t.Value.LateralClearM.HasValue && Math.Abs(t.Value.LateralClearM.Value - 3.23) < 0.01;
        Check(ok, "无对时解 framing 缺口（2174.33,1797.42 · clear 3.23 · conf 0.6）");
    }

    private static void SavedSpotFallback()
    {
        var t = TargetResolver.Resolve(Array.Empty<TargetResolver.Item>(), Truck,
                                       new Pose2(100, 200, 1.0), 6.5);
        bool ok = t.HasValue && t.Value.Pose.X == 100 && Math.Abs(t.Value.Confidence - 0.4) < 0.001;
        Check(ok, "空场景退到 SavedSpot（conf 0.4）");
    }

    private static void NullWhenEmpty()
    {
        var t = TargetResolver.Resolve(Array.Empty<TargetResolver.Item>(), Truck, null, 6.5);
        Check(t == null, "无对无缺口无存档 → null");
    }

    private static void InsertPadFormula()
    {
        bool ok = Math.Abs(TargetResolver.InsertPad(3.24, 2.60, 0.10) - 0.22) < 1e-9
               && Math.Abs(TargetResolver.InsertPad(2.80, 2.60, 0.10) - 0.05) < 1e-9  // 下限夹取
               && Math.Abs(TargetResolver.InsertPad(4.00, 2.60, 0.10) - 0.30) < 1e-9; // 上限夹取
        Check(ok, "InsertPad clamp 公式（3.24→0.22 · 下限0.05 · 上限0.30）");
    }
}
```

- [ ] **Step 2: harness csproj 注册** — `PlannerHarness.csproj` 的 ItemGroup 加：
```xml
<Compile Include="TargetResolverChecks.cs" />
<Compile Include="$(PluginRoot)Driving\TargetResolver.cs" />
```

- [ ] **Step 3: 跑判据确认失败**
Run: `dotnet run --project Tools/PlannerHarness -c Release`
Expected: 编译失败（`TargetResolver` 不存在）——这是失败信号，不是绿灯。

- [ ] **Step 4: 实现 TargetResolver**

```csharp
// Driving/TargetResolver.cs
using System.Numerics;

namespace AutoParking;

/// <summary>
///  Resolves where the game wants the truck parked, following the marker protocol measured on
///  2026-10-19 (design doc §54): a Mover pair with flags == 0xC17 marks the active stage's
///  target; the parked Hookup row (flags family 0x300) around it defines the physical gap and
///  the heading axis. Pure math over caller-supplied items so the offline harness can test it
///  without a game process; the plugin maps KdopReader.MemItem into Item at the call site.
///
///  Priority: 0xC17 pair (0.9) → framing-vehicle gap (0.6) → SavedSpot (0.4) → null.
///  Confidence below the plugin's MinConfidence gate refuses the start; the caller decides.
/// </summary>
public static class TargetResolver
{
    public readonly record struct Item(int Type, ulong Uid, Vector2 Pos, float Dx, float Dz, uint Flags);

    public readonly record struct Target(Pose2 Pose, string Provenance, double Confidence, ulong Uid,
                                         double? LateralClearM, Vector2? GapCenter);

    private const int MoverType = 0x09;
    private const int HookupType = 0x2F;
    private const uint C17Flags = 0xC17;
    private const double PairRadiusM = 2.5;
    private const double RowBandM = 1.5;
    private const double MinGapOverTruckM = 0.4;   // spec §4.5：纵向净长 >= 车长 + 0.4 否则拒绝
    private const double ConfMover = 0.9;
    private const double ConfFraming = 0.6;
    private const double ConfSaved = 0.4;

    public static Target? Resolve(IReadOnlyList<Item> items, Pose2 truck, Pose2? savedSpot,
                                  double vehicleLengthM)
    {
        List<Item> parked = new();
        List<Item> c17 = new();
        foreach (Item it in items)
        {
            if (it.Type == MoverType && it.Flags == C17Flags)
                c17.Add(it);
            else if (it.Type == HookupType && (it.Flags & 0x300u) != 0)
                parked.Add(it);
        }

        List<(Vector2 a, Vector2 b, double clear, double depth)> gaps = FramingGaps(parked, vehicleLengthM);

        if (c17.Count > 0)
        {
            // Pairs closer together than PairRadiusM form one marker; the nearest pair wins.
            (Vector2 mid, ulong uid)? best = null;
            double bestDist = double.MaxValue;
            for (int i = 0; i < c17.Count; i++)
            for (int j = i + 1; j < c17.Count; j++)
            {
                if (Vector2.Distance(c17[i].Pos, c17[j].Pos) > PairRadiusM) continue;
                Vector2 mid = (c17[i].Pos + c17[j].Pos) / 2f;
                double d = Vector2.Distance(mid, truck.Position);
                if (d < bestDist) { bestDist = d; best = (mid, c17[i].Uid); }
            }
            if (best is not null)
            {
                var (mid, uid) = best.Value;
                double? clear = null; Vector2? gapCenter = null;
                foreach (var g in gaps)
                {
                    // A pair INSIDE a framing gap (rect test: ±clear/2 along the row, ±depth/2
                    // across it) inherits that gap's lateral room for the insert envelope; a pair
                    // out in the open lane (the stopmid zone sits 4.4 m north of the row) does not.
                    Vector2 c = (g.a + g.b) / 2f;
                    Vector2 axis = g.b - g.a;
                    double len = axis.Length();
                    if (len < 1e-3f) continue;
                    axis /= len;
                    Vector2 n = new(-axis.Y, axis.X);
                    Vector2 d = mid - c;
                    if (Math.Abs(Vector2.Dot(d, axis)) <= g.clear / 2.0 + 0.5
                     && Math.Abs(Vector2.Dot(d, n)) <= g.depth / 2.0 + 0.5)
                    { clear = g.depth; gapCenter = c; break; }
                }
                Pose2 pose = new(mid.X, mid.Y, RowHeading(parked, mid, truck));
                return new Target(pose, $"mover-pair:0x{uid:X}", ConfMover, uid, clear, gapCenter);
            }
        }

        if (gaps.Count > 0)
        {
            var g = gaps[0]; // FramingGaps already returns the widest first.
            Vector2 c = (g.a + g.b) / 2f;
            Pose2 pose = new(c.X, c.Y, RowHeading(parked, c, truck));
            return new Target(pose, $"framing-gap:{g.clear:0.00}m", ConfFraming, 0, g.depth, c);
        }

        if (savedSpot is Pose2 s)
            return new Target(s, "saved-spot", ConfSaved, 0, null, null);

        return null;
    }

    /// <summary>Per-side envelope pad for an insert leg, metres. Spec §4.5: the framing row's
    ///  kdop depth is the lateral room the manoeuvre has; subtract the truck and a safety margin,
    ///  then clamp to the range the planner has been validated in.</summary>
    public static double InsertPad(double lateralClearM, double vehicleWidthM, double safetyMarginM)
        => Math.Clamp((lateralClearM - vehicleWidthM) / 2.0 - safetyMarginM, 0.05, 0.30);

    // Widest-gap-first so Resolve's "take gaps[0]" is deterministic.
    private static List<(Vector2 a, Vector2 b, double clear, double depth)> FramingGaps(
        List<Item> parked, double vehicleLengthM)
    {
        var result = new List<(Vector2, Vector2, double, double)>();
        List<Item> sorted = new(parked);
        sorted.Sort((l, r) => l.Pos.X != r.Pos.X ? l.Pos.X.CompareTo(r.Pos.X) : l.Pos.Y.CompareTo(r.Pos.Y));
        for (int i = 0; i + 1 < sorted.Count; i++)
        {
            Item l = sorted[i], r = sorted[i + 1];
            if (Math.Abs(l.Pos.Y - r.Pos.Y) > RowBandM) continue; // not the same row
            double clear = Vector2.Distance(l.Pos, r.Pos) - (l.Dx + r.Dx) / 2f;
            if (clear < vehicleLengthM + MinGapOverTruckM) continue;
            double depth = (l.Dz + r.Dz) / 2f;
            result.Add((l.Pos, r.Pos, clear, depth));
        }
        result.Sort((a, b) => b.clear.CompareTo(a.clear));
        return result;
    }

    private static double RowHeading(List<Item> parked, Vector2 at, Pose2 truck)
    {
        // kdop boxes carry no yaw; the row axis is the heading family. Pick the sign whose
        // forward is closer to the truck's own nose so the goal never flips 180° on the user.
        Vector2 fwd = Geometry.ForwardFromHeading(truck.HeadingRad);
        double sx = 0, sy = 0;
        int near = 0;
        foreach (Item p in parked)
        {
            if (Vector2.Distance(p.Pos, at) >= 40.0) continue;
            near++;
            sx = Math.Max(sx, Math.Abs(p.Pos.X - at.X));
            sy = Math.Max(sy, Math.Abs(p.Pos.Y - at.Y));
        }
        if (near < 2) return truck.HeadingRad;

        Vector2 axis = sx >= sy ? new Vector2(1, 0) : new Vector2(0, 1);
        Vector2 dir = Vector2.Dot(axis, fwd) >= 0 ? axis : -axis;
        return Geometry.HeadingFromForward(dir);
    }
}
```

- [ ] **Step 5: 跑判据确认通过**
Run: `dotnet run --project Tools/PlannerHarness -c Release`
Expected: exit 1（三条固定 ✗ 不动），✓ 从 131 → **137**（6 条新判据），无新 ✗。若 RowHeading 的轴判定在 fixture 上翻车（例如 near[0]/near[^1] dx 相等导致 sx/sy 判错），修 Resolve 内部实现，**不改判据期望值**。

- [ ] **Step 6: Commit**
```bash
git add Driving/TargetResolver.cs Tools/PlannerHarness/TargetResolverChecks.cs Tools/PlannerHarness/PlannerHarness.csproj
git commit -m "feat: resolve parking targets from 0xC17 markers and framing-vehicle gaps"
```

---

### Task 2: MemClassifier 提取 + Trigger/ParkedVehicle 两个新 MemKind

**Files:**
- Create: `Memory/MemClassifier.cs`
- Modify: `Memory/KdopReader.cs`（删嵌套 enum 与私有 Classify，改用新类）
- Modify: `AutoParkingPlugin.cs`、`Rendering/MapOverlay.cs`（`KdopReader.MemKind.X` → `MemKind.X`，共约 19 处）
- Modify: `Tools/RouteSearch/Program.cs` 及其 csproj（若它链接 KdopReader.cs，补链 MemClassifier.cs）
- Test: `Tools/PlannerHarness/MemClassifierChecks.cs`；Modify: `Tools/PlannerHarness/PlannerHarness.csproj`

**Interfaces:**
- Produces:
```csharp
// Memory/MemClassifier.cs —— 零宿主依赖，可被 harness 链接
public enum MemKind { Cone, Board, Mover, Building, Trigger, ParkedVehicle, Other }
public static class MemClassifier
{
    // 判据（design doc §38/§41/§54 实测）：0x02 Buildings、0x09 Mover、0x22 Trigger、
    // 0x24 Sign（锥桶=近方形 0.35–1.15 m 宽 / 0.75–1.4 m 高 / 比例 0.55–1.8）、
    // 0x2F Hookup 且 flags 命中 parked 族 (flags & 0x300) != 0（实测 0x100/0x10C/0x200/0x3100/0x21120）。
    public static MemKind Classify(int type, uint flags, float dx, float dy, float dz);
}
```

- [ ] **Step 1: 写失败判据**
```csharp
// Tools/PlannerHarness/MemClassifierChecks.cs
internal static class MemClassifierChecks
{
    public static void Run()
    {
        Check(MemClassifier.Classify(0x22, 0x80006, 10.16f, 4.05f, 6.68f) == MemKind.Trigger,
              "0x22 → Trigger（无视尺寸）");
        Check(MemClassifier.Classify(0x2F, 0x3100, 7.25f, 4.44f, 2.98f) == MemKind.ParkedVehicle,
              "0x2F+0x3100 → ParkedVehicle");
        Check(MemClassifier.Classify(0x2F, 0x0, 1.0f, 1.0f, 1.0f) == MemKind.Other,
              "0x2F 无 parked 位 → Other");
        Check(MemClassifier.Classify(0x24, 0x0, 0.84f, 1.11f, 0.84f) == MemKind.Cone,
              "锥桶形状 Sign → Cone（回归）");
        Check(MemClassifier.Classify(0x09, 0xC17, 8.75f, 8.75f, 8.75f) == MemKind.Mover,
              "0x09 → Mover（回归）");
        Check(MemClassifier.Classify(0x02, 0x20, 29.89f, 0.25f, 10.70f) == MemKind.Building,
              "0x02 → Building（回归）");
    }
    private static void Check(bool ok, string what) =>
        System.Console.WriteLine($"  {(ok ? "✓" : "✗")} MemClassifier: {what}");
}
```
csproj 注册 `<Compile Include="MemClassifierChecks.cs" />` 与 `<Compile Include="$(PluginRoot)Memory\MemClassifier.cs" />`。

- [ ] **Step 2: 跑确认编译失败** — `dotnet run --project Tools/PlannerHarness -c Release` → 期望编译错误。

- [ ] **Step 3: 实现 + 迁移**
新建 `Memory/MemClassifier.cs`（enum + Classify，逻辑从 `KdopReader.Classify`（:371）原样搬入并加 flags 参数与两个新分支）。然后：
- `KdopReader.cs`：删除嵌套 `public enum MemKind`（:40）与私有 `Classify`（:371），调用点改 `MemClassifier.Classify(type, flags, dx, dy, dz)`（flags 与 uid 同一次读缓冲里，+0x34）。
- 全仓 `grep -rn "KdopReader.MemKind" --include=*.cs --exclude-dir=bin --exclude-dir=obj` 逐处改为 `MemKind`。
- `Tools/RouteSearch/Program.cs` 若引用了 KdopReader 源：其 csproj 补 `<Compile Include="$(PluginRoot)Memory\MemClassifier.cs" />`。
- `Rendering/MapOverlay.cs`：检查对 Kind 的 switch/判断，为 `Trigger`（隐形体积，不画）与 `ParkedVehicle`（画灰色框，与 Building 同样式）补分支；**不把新 Kind 加进 AddMemoryObstacles 的阻挡集合**——不改变现有路线阻挡行为（那要单独的证据）。

- [ ] **Step 4: 跑 harness** — Expected: exit 1、✓ 137→**143**、三条固定 ✗ 原样。
- [ ] **Step 5: 插件 build** — `dotnet build -c Release` → 0 错误。
- [ ] **Step 6: Commit**
```bash
git add Memory/MemClassifier.cs Memory/KdopReader.cs AutoParkingPlugin.cs Rendering/MapOverlay.cs Tools/PlannerHarness/MemClassifierChecks.cs Tools/PlannerHarness/PlannerHarness.csproj Tools/RouteSearch/
git commit -m "feat: classify runtime triggers and parked-vehicle hookups in the memory layer"
```

---

### Task 3: ParkingBays 行分组修复（远排不再并入）

**Files:**
- Modify: `Driving/ParkingBay.cs:150`（`CollectRows`）
- Test: `Tools/PlannerHarness/ParkingBayChecks.cs`（追加一个判据）

**Interfaces:**
- Consumes: 现有 `Row` record、常量 `MaxBayLengthM=22.0`、`MinConesPerRow=3`、`MinRowSpanM=5.0`。

- [ ] **Step 1: 写失败判据**（§54 病灶复刻：同垂坐标、隔 40 m 的两段锥并成一排 → 跨度 58 > 22 → 全拒）
```csharp
// ParkingBayChecks.cs 追加：
private static void FarRowDoesNotPolluteNearBay()
{
    List<Vector2> cones = new();
    // 近处真库位：两条 X 向排，z=100 / z=103.7，各 5 锥跨 10→18
    for (int i = 0; i < 5; i++) { cones.Add(new Vector2(10 + i * 2, 100f)); cones.Add(new Vector2(10 + i * 2, 103.7f)); }
    // 远排：同 z=100 与 z=103.7，x=60→68（42 m 空档）
    for (int i = 0; i < 5; i++) { cones.Add(new Vector2(60 + i * 2, 100f)); cones.Add(new Vector2(60 + i * 2, 103.7f)); }
    var bays = ParkingBays.Detect(cones);
    bool ok = bays.Exists(b => Math.Abs(b.Center.X - 14) < 0.5 && Math.Abs(b.Center.Y - 101.85) < 0.5);
    System.Console.WriteLine($"  {(ok ? "✓" : "✗")} ParkingBays: 42 m 空档断排，近处库位仍可检出（{bays.Count} 个）");
}
```
在 `Run()` 里调用它。跑 → 期望 ✗（旧代码合并跨度 58 m 被拒，0 个库位）。

- [ ] **Step 2: 修 CollectRows** — 在按垂坐标分组后、求 min/max along 之前，把组内点按 along 排序，遇到相邻 along 间距 > `MaxBayLengthM` 就切成新的一段；每段独立过 `MinConesPerRow`/`MinRowSpanM` 门槛成为 Row：
```csharp
// ParkingBay.cs CollectRows，替换「for k in start..i 求 min/max/sumPerp」那段：
List<(double along, double perp)> band = new();
for (int k = start; k < i; k++) band.Add(projected[k]);
band.Sort((l, r) => l.along.CompareTo(r.along));
int seg = 0;
for (int g = 1; g <= band.Count; g++)
{
    if (g < band.Count && band[g].along - band[g - 1].along <= MaxBayLengthM) continue;
    if (g - seg >= MinConesPerRow)
    {
        double minAlong = band[seg].along, maxAlong = band[g - 1].along, sumPerp = 0;
        for (int k = seg; k < g; k++) sumPerp += band[k].perp;
        if (maxAlong - minAlong >= MinRowSpanM)
            rows.Add(new Row(alongX, sumPerp / (g - seg), minAlong, maxAlong, g - seg));
    }
    seg = g;
}
```

- [ ] **Step 3: 跑 harness 确认通过** — Expected: 新判据 ✓，既有 ParkingBay 判据全部保持，✗ 集合不变。
- [ ] **Step 4: Commit**
```bash
git add Driving/ParkingBay.cs Tools/PlannerHarness/ParkingBayChecks.cs
git commit -m "fix: split cone rows across long axial gaps so distant rows stop polluting bays"
```

---

### Task 4: MemoryProbe `--deep-tsv` 机器可读输出

**Files:**
- Modify: `Tools/MemoryProbe/Program.cs`

**Interfaces:**
- Produces（Task 5 依赖的磁盘格式）：
```
ITEM\t<type:int>\t<uid:0xHEX>\t<flags:0xHEX>\t<x}\t<y>\t<z>\t<dx>\t<dy>\t<dz>
STR\t<uid:0xHEX>\t<ascii-run>
```
纯 ASCII、InvariantCulture 浮点（`"0.###"`）、每行一条；STR 行只含 0x20–0x7E 字符。`--deep-tsv <x> <z> [r]` 与 `--deep` 同一遍历路径，只是打印格式不同。

- [ ] **Step 1: 实现** — 在 `DoDeep`（Program.cs:459）加 `bool tsv` 参数（或并列模式开关），遍历逻辑复用；TSV 分支打印上述两行格式，字符串行对每个条目取 `ReadAsciiRuns(h, elem, 0x100, 4)` 的结果逐条输出；参数解析处加 `--deep-tsv`（与 `--deep` 相同的 x/z/r 解析）。
- [ ] **Step 2: 跑 harness（AGENTS 规则：动 Tools 必跑）** — Expected: exit 1、✓/✗ 集合与 Task 3 后一致。
- [ ] **Step 3: 若游戏在跑，实测一次** —
Run: `dotnet run --project Tools/MemoryProbe -c Release -- eurotrucks2 --deep-tsv 2194.40 1804.12 90 | head -20`
Expected: 每行 `ITEM\t...` 或 `STR\t...`，无非 ASCII 字节。游戏不在跑则记录"未实测，Task 5 的 fixture 先行"。
- [ ] **Step 4: Commit**
```bash
git add Tools/MemoryProbe/Program.cs
git commit -m "feat: add machine-readable TSV output to MemoryProbe deep scan"
```

---

### Task 5: IdentityChannel（sidecar 身份通道，解析可离线测）

**Files:**
- Create: `Memory/IdentityChannel.cs`
- Test: `Tools/PlannerHarness/IdentityChannelChecks.cs`
- Modify: `Tools/PlannerHarness/PlannerHarness.csproj`

**Interfaces:**
- Produces（Task 8 接线依赖）:
```csharp
public sealed class IdentityChannel : IDisposable
{
    public IdentityChannel(string exePath, string gameProcessName); // exePath 缺失不抛，TryGet 返回错误
    public sealed record ItemIdentity(ulong Uid, int Type, uint Flags, Vector2 Pos,
                                      float Dx, float Dz, string Strings);
    // 同步取缓存；缓存新鲜(<5 s)直接返回。过期返回旧值并后台刷新（不阻塞调用线程）。
    // exe 缺失/超时(3 s)/解析空 → items=null + error 描述。
    public bool TryGet(Vector2 center, double radiusM, out IReadOnlyList<ItemIdentity>? items, out string? error);
    public static List<ItemIdentity> ParseTsv(string text);   // 纯函数，harness 测这个
}
```

- [ ] **Step 1: 写失败判据**
```csharp
// Tools/PlannerHarness/IdentityChannelChecks.cs
internal static class IdentityChannelChecks
{
    // 逐字来自 2026-10-19 实测输出结构（Task 4 的 TSV 格式）。
    private const string Fixture =
        "ITEM\t22\t0x5BBAC4CC239C0000\t0x80006\t2166.30\t-4.35\t1801.73\t10.16\t4.05\t6.68\n" +
        "STR\t0x5BBAC4CC239C0000\tstate park_trigger_on_stopped\n" +
        "STR\t0x5BBAC4CC239C0000\t/automat/f6/f6d4ace54bc8\n" +
        "ITEM\t47\t0x50E5468F7C9C0000\t0x100\t2190.58\t-2.91\t1797.42\t7.50\t4.69\t3.23\n" +
        "STR\t0x50E5468F7C9C0000\tacademy.parked.truck\n";

    public static void Run()
    {
        var items = IdentityChannel.ParseTsv(Fixture);
        bool ok1 = items.Count == 2
                && items[0].Uid == 0x5BBAC4CC239C0000 && items[0].Type == 22
                && items[0].Strings.Contains("state park_trigger_on_stopped")
                && Math.Abs(items[0].Pos.Y - 1801.73f) < 0.001;
        System.Console.WriteLine($"  {(ok1 ? "✓" : "✗")} IdentityChannel: TSV 解析（uid/type/坐标/字符串聚合）");
        bool ok2 = items[1].Uid == 0x50E5468F7C9C0000
                && items[1].Strings.Contains("academy.parked.truck");
        System.Console.WriteLine($"  {(ok2 ? "✓" : "✗")} IdentityChannel: Hookup 身份串归到正确 uid");
        bool ok3 = IdentityChannel.ParseTsv("garbage\n\n").Count == 0;
        System.Console.WriteLine($"  {(ok3 ? "✓" : "✗")} IdentityChannel: 非法输入不抛异常返回空表");
    }
}
```
csproj 注册两个文件。跑 → 期望编译失败。

- [ ] **Step 2: 实现 IdentityChannel**
要点（写进代码注释）：`Process.Start` 用 `UseShellExecute=false, RedirectStandardOutput=true`；`WaitForExit(3000)` 超时 `Kill()`；stdout 用 `Encoding.ASCII` 读（Task 4 保证纯 ASCII）；缓存键 = `(Round(center), radiusM)`，TTL 5 s；刷新走 `Task.Run` + lock，**绝不从 tick 线程同步调用 exe**（本类只在 Acquiring 用，调用方保证线程）。`TryGet` 三分支要显式：缓存新鲜 → 返回真值；有旧值但过期 → **返回旧值**同时踢后台刷新；无任何缓存（冷启动第一呼）→ 返回 `(null, "warming up")` 并踢刷新——身份通道本来就只是增强（spec §4.3），调用方拿到 warming up 就按降级走，下一腿拿到真值。exe 路径不存在时构造不抛、TryGet 返回 `(false, "exe not found: <path>")`。`ParseTsv` 是纯静态函数：按 `\n` 切行、`\t` 切字段、`ulong.Parse(field, NumberStyles.HexNumber)` 解 uid/flags、`double.Parse(..., CultureInfo.InvariantCulture)` 解坐标、STR 行按 uid 归并进 `"; "` join 的 `Strings`。

- [ ] **Step 3: 跑 harness** — Expected: ✓ 143→**146**、✗ 集合不变。
- [ ] **Step 4: Commit**
```bash
git add Memory/IdentityChannel.cs Tools/PlannerHarness/IdentityChannelChecks.cs Tools/PlannerHarness/PlannerHarness.csproj
git commit -m "feat: add sidecar identity channel with offline-tested TSV parsing"
```

---

### Task 6: Settings 扩展 + 设置页 + HandleAction（9+1 键）

**Files:**
- Modify: `Settings.cs`（新键 + `Clone()` + 夹取）
- Modify: `SettingsPage.razor`（控件）
- Modify: `AutoParkingPlugin.cs`（`HandleAction` 新 case）

**Interfaces:**
- Produces（Task 8 依赖）:
```csharp
// Settings.cs 新增（默认值来自 spec §6；GameProcessName 为偏离补丁）
public bool ShadowMode { get; set; } = true;
public bool AutoParkByMarker { get; set; } = true;   // 关掉 = 旧行为（必须手动选点）
public string SidecarPath { get; set; } = "";        // 空 = 插件 DLL 同目录 MemoryProbe.exe
public string GameProcessName { get; set; } = "eurotrucks2";
public bool DynamicEnvelope { get; set; } = true;
public double ApproachEnvelopeM { get; set; } = 0.30;
public double SafetyMarginM { get; set; } = 0.10;
public double SettleTimeS { get; set; } = 3.0;
public double MinConfidence { get; set; } = 0.6;
public double MinAirBar { get; set; } = 4.0;
public double MarkerMoveToleranceM { get; set; } = 0.5;
public AutoParkingSettings Clone() => (AutoParkingSettings)MemberwiseClone();
// 夹取（与现有 clamp 同处）：
//   ApproachEnvelopeM [0.05, 0.50]、SafetyMarginM [0, 0.30]、SettleTimeS [1, 10]、
//   MinConfidence [0, 1]、MinAirBar [0, 10]、MarkerMoveToleranceM [0.1, 5]
```

- [ ] **Step 1: 加键 + Clone + 夹取**（`Settings.cs`，夹取加在现有 `ObstacleMarginM = Math.Clamp(...)` 那两行之后，:315 附近）。
- [ ] **Step 2: HandleAction cases**（`AutoParkingPlugin.cs` `HandleAction` switch，仿 `"dryRun"` case）：`"shadowMode"`、`"autoParkByMarker"`、`"dynamicEnvelope"`（ToBool）、`"sidecarPath"`（赋 string，changed=true）、`"gameProcessName"`（赋 string）、`"approachEnvelopeM"`/`"safetyMarginM"`/`"settleTimeS"`/`"minConfidence"`/`"minAirBar"`/`"markerMoveToleranceM"`（ToDouble）。
- [ ] **Step 3: 设置页控件**（`SettingsPage.razor`，仿现有 `<Switch>`/`<Slider>`；放 DryRun 开关之后一组"通用泊车"小节）：
```razor
<Switch Title="跟随游戏标记（通用泊车）"
        Description="按热键后自动解析游戏要求的停车位置（0xC17 Mover 对 → 停放车间隙 → 已存点）并执行。关掉则回到必须在地图上手动选点的旧行为。"
        Value="@Plugin.Settings.AutoParkByMarker"
        ValueChanged="@((bool value) => Plugin.HandleAction("autoParkByMarker", value))" />
<Switch Title="影子模式"
        Description="全链路解析+规划并把目标与路线画在地图上，但不发任何控制命令。实车前先用它核对规划线。"
        Value="@Plugin.Settings.ShadowMode"
        ValueChanged="@((bool value) => Plugin.HandleAction("shadowMode", value))" />
<Switch Title="倒库腿动态包络"
        Description="按 framing 车缺口的实测净宽收缩障碍包络；关闭则两条腿都用固定值。"
        Value="@Plugin.Settings.DynamicEnvelope"
        ValueChanged="@((bool value) => Plugin.HandleAction("dynamicEnvelope", value))" />
<Slider Title="接近腿包络 (m/侧)"
        Description="宽车道段的固定包络。只在车道明显宽于车身时用 0.30；窄巷按实测收紧。"
        Value="@Plugin.Settings.ApproachEnvelopeM" Min="0.05" Max="0.50" Step="0.01"
        ValueChanged="@((double value) => Plugin.HandleAction("approachEnvelopeM", value))" />
<Slider Title="动态包络安全边 (m)"
        Description="从缺口净宽反推包络时预留的安全边。实测驾校侧方缺口 3.24 m、车宽 2.60 → 0.22 m/侧刚好过 0.15 m 的历史验证线上限。"
        Value="@Plugin.Settings.SafetyMarginM" Min="0.0" Max="0.30" Step="0.01"
        ValueChanged="@((double value) => Plugin.HandleAction("safetyMarginM", value))" />
<Slider Title="停稳判定 (s)"
        Description="到达目标后保持制动观察的秒数，之后才重新解析下一个标记（多腿衔接）或宣告完成。"
        Value="@Plugin.Settings.SettleTimeS" Min="1" Max="10" Step="0.5"
        ValueChanged="@((double value) => Plugin.HandleAction("settleTimeS", value))" />
<Slider Title="起步置信度门槛"
        Description="目标解析置信度低于此值拒绝起步（0.9=标记对，0.6=车间隙，0.4=手动存点）。"
        Value="@Plugin.Settings.MinConfidence" Min="0" Max="1" Step="0.05"
        ValueChanged="@((double value) => Plugin.HandleAction("minConfidence", value))" />
<Slider Title="起步气压门槛 (bar)"
        Description="低于此气压制动踏板物理无效（气路制动），拒绝实车起步。仅门控，不会中止在跑的机动。"
        Value="@Plugin.Settings.MinAirBar" Min="0" Max="10" Step="0.5"
        ValueChanged="@((double value) => Plugin.HandleAction("minAirBar", value))" />
<Slider Title="标记移动重规划阈值 (m)"
        Description="执行中当前 0xC17 标记位移超过此值则就地重规划，不中止。"
        Value="@Plugin.Settings.MarkerMoveToleranceM" Min="0.1" Max="5" Step="0.1"
        ValueChanged="@((double value) => Plugin.HandleAction("markerMoveToleranceM", value))" />
```
（Slider 的 Min/Max/Step 属性名以 razor 现有滑杆为准，实现时对照 :19 起的现有写法。）

- [ ] **Step 4: build + harness** — `dotnet build -c Release` 0 错误；harness 集合不变（本任务不动纯源）。
- [ ] **Step 5: Commit**
```bash
git add Settings.cs SettingsPage.razor AutoParkingPlugin.cs
git commit -m "feat: add marker-following auto-park settings with shadow mode on by default"
```

---

### Task 7: AutoParkController（纯协调核心 + harness）

**Files:**
- Create: `Driving/AutoParkController.cs`
- Test: `Tools/PlannerHarness/AutoParkControllerChecks.cs`
- Modify: `Tools/PlannerHarness/PlannerHarness.csproj`

**Interfaces:**
- Consumes: `TargetResolver.Target`（Task 1）
- Produces（Task 8 依赖）:
```csharp
internal sealed class AutoParkController
{
    public TargetResolver.Target? Active { get; private set; }
    public int Legs { get; private set; }
    public DateTime ArrivedUtc { get; private set; }
    public ulong MarkerUid { get; private set; }
    public Vector2 LastMarkerPos { get; private set; }

    public void OnAcquired(TargetResolver.Target t);        // 记录腿数+1、标记 uid/位置
    public bool MarkerMoved(Vector2 markerPos, double toleranceM);   // 位移 > 阈值 → true
    public void OnArrived(DateTime utcNow);                  // 记录到达时刻
    public bool Settled(DateTime utcNow, double settleTimeS);
    public void Reset();
}
```

- [ ] **Step 1: 写失败判据**（两腿序列仿真：acquire→arrive→settle→acquire(标记已挪)→markerMoved=true）
```csharp
internal static class AutoParkControllerChecks
{
    public static void Run()
    {
        var c = new AutoParkController();
        var t1 = new TargetResolver.Target(new Pose2(2166.175, 1801.835, Math.PI / 2),
                    "mover-pair:0xA", 0.9, 0xA, null, null);
        c.OnAcquired(t1);
        bool okA = c.Legs == 1 && c.MarkerUid == 0xA;
        System.Console.WriteLine($"  {(okA ? "✓" : "✗")} Controller: OnAcquired 记腿数与标记 uid");
        c.OnArrived(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));
        bool okB = !c.Settled(c.ArrivedUtc.AddSeconds(1.0), 3.0)
                && c.Settled(c.ArrivedUtc.AddSeconds(3.1), 3.0);
        System.Console.WriteLine($"  {(okB ? "✓" : "✗")} Controller: SettleTimeS 未满不放行、满了放行");
        bool okC = !c.MarkerMoved(new Vector2(2166.30f, 1802.00f), 0.5)
                && c.MarkerMoved(new Vector2(2168.0f, 1802.0f), 0.5);
        System.Console.WriteLine($"  {(okC ? "✓" : "✗")} Controller: 标记位移阈值");
        var t2 = new TargetResolver.Target(new Pose2(2174.33, 1797.42, Math.PI / 2),
                    "framing-gap:8.78m", 0.6, 0, 3.23, new Vector2(2174.33, 1797.42));
        c.OnAcquired(t2);
        bool okD = c.Legs == 2 && c.MarkerUid == 0 && c.Active!.Value.Confidence == 0.6;
        System.Console.WriteLine($"  {(okD ? "✓" : "✗")} Controller: 第二腿无缝接管（framing 目标）");
        c.Reset();
        bool okE = c.Active == null && c.Legs == 0;
        System.Console.WriteLine($"  {(okE ? "✓" : "✗")} Controller: Reset 清空");
    }
}
```
csproj 注册两文件；跑 → 编译失败。

- [ ] **Step 2: 实现** — 纯内存状态，无线程无 IO；`MarkerMoved` 用 `Vector2.Distance(LastMarkerPos, markerPos) > toleranceM`；`Settled` 用 `(utcNow - ArrivedUtc).TotalSeconds >= settleTimeS`。
- [ ] **Step 3: 跑 harness** — Expected: ✓ 146→**151**、✗ 集合不变。
- [ ] **Step 4: Commit**
```bash
git add Driving/AutoParkController.cs Tools/PlannerHarness/AutoParkControllerChecks.cs Tools/PlannerHarness/PlannerHarness.csproj
git commit -m "feat: add pure marker-following controller core"
```

---

### Task 8: 插件接线（起步门 + 影子模式 + 多腿循环 + 标记位移重规划）

**Files:**
- Modify: `AutoParkingPlugin.cs`（多处，见下）
- Modify: `Memory/KdopReader.cs`（若 IdentityChannel 需要 exe 目录解析辅助则放插件侧）

**Interfaces:**
- Consumes: 全部前序任务产物。接线点：`StartParking()` :1642、`ValidateStart()` :1829、`HandleToggle()` :1721、`CompletePhase()` :956、`StepManeuver()` :620、HoldingBrake 分支 :607、`output.DryRun` 赋值 :636、`Replan()` :505。

- [ ] **Step 1: 字段与生命周期**
```csharp
// AutoParkingPlugin.cs 字段区：
private IdentityChannel? identity;
private readonly AutoParkController markerLoop = new();
private TargetResolver.Target? markerTarget;     // 本次机动的解析目标（null = 用户手动选点）
private Pose2? pendingSpot;                       // Selecting 阶段后台算好的下一腿
private string? pendingError;
private ulong activeMarkerUid;
private DateTime nextMarkerCheckUtc = DateTime.MinValue;
private Task? acquisitionTask;
```
`OnEnable`（:238）在 `kdopReader.Start()` 后：`identity = new IdentityChannel(Path.Combine(AppContext.BaseDirectory, string.IsNullOrEmpty(settings.SidecarPath) ? "MemoryProbe.exe" : settings.SidecarPath), settings.GameProcessName);`；`OnDisable`（:1013）里 `identity?.Dispose();`。

- [ ] **Step 2: 起步门（ValidateStart 追加，放在现有检查之后）**
```csharp
bool realControl = !settings.DryRun && !settings.ShadowMode;
if (realControl && latestTelemetry.truckFloat.engineRpm < 200.0)
    return "发动机未启动（转速过低）";
if (realControl && Math.Abs(latestTelemetry.truckFloat.speed) >= 0.5)
    return "车速过高，请先停稳";
if (realControl && !HasBrakeAir())
    return "气压过低，制动踏板此刻物理无效（§24）";
if (string.IsNullOrEmpty(latestTelemetry.configString.shifterType))
    return "档位配置未知";
if (markerTarget is TargetResolver.Target mt && mt.Confidence < settings.MinConfidence)
    return $"目标置信度 {mt.Confidence:0.00} 低于门槛 {settings.MinConfidence:0.00}";
```
（`realControl` 条件的理由写进注释：DryRun/影子只做规划，不发控制，气压/引擎门会挡住合法的离线用法。）

- [ ] **Step 3: StartParking 的 marker 获取分支**
`StartParking()` 在 `ValidateStart()` 之前插入：
```csharp
if (settings.AutoParkByMarker && target == null)
{
    markerTarget = AcquireMarkerTarget();   // 见 Step 4；可能为 null
    if (markerTarget is null)
    {
        lastActionFailed = true;
        lastActionMessage = "自动解析目标失败：无标记、无可用缺口（开地图手动选点，或开回有标记的场景）";
        Notify(NotificationLevel.Warning, lastActionMessage, 6);
        return lastActionMessage;
    }
    // 立即规划：AcquireMarkerTarget 内部已填 planResult/target（复用 Replan 的产物路径）
}
```
`ValidateStart` 里把 `if (target == null) return "还没在平面地图上选择车位";` 改为只在 `!settings.AutoParkByMarker` 时拒绝（marker 分支保证 target 非空）。

- [ ] **Step 4: AcquireMarkerTarget（新私有方法）**
```csharp
private TargetResolver.Target? AcquireMarkerTarget()
{
    KdopReader.Snapshot? snap = MemoryItemSnapshot;
    if (snap == null) return null;
    List<TargetResolver.Item> items = new(snap.Items.Count);
    foreach (KdopReader.MemItem it in snap.Items)
        items.Add(new TargetResolver.Item(it.Type, it.Uid, it.PlanePos, it.Dx, it.Dz, it.Flags));
    Pose2 saved = settings.HasSavedSpot
        ? new Pose2(settings.SavedSpotX, settings.SavedSpotZ, settings.SavedSpotHeadingRad)
        : (target ?? (Pose2?)null) ?? null;
    TargetResolver.Target? t = TargetResolver.Resolve(items, CurrentPose, saved, settings.VehicleLengthM);
    if (t is null) return null;

    // 身份通道：可选增强，失败只降级不阻塞（spec §4.3）。
    if (identity != null && identity.TryGet(CurrentPose.Position, 90.0, out var ids, out string? err) && ids != null)
    {
        foreach (var id in ids)
            if (id.Strings.Contains("state park_trigger_on_stopped"))
                Logger.Info($"AutoParking: identity confirms stop-zone trigger at ({id.Pos.X:0.0},{id.Pos.Y:0.0})");
    }
    else if (err != null)
        Logger.Warn($"AutoParking: identity channel degraded: {err}");

    lock (sync)
    {
        target = t.Value.Pose;
        markerTarget = t;
        activeMarkerUid = t.Value.Uid;
    }
    Replan(mapGeometry!);   // 走既有规划路径；mapGeometry 空则先返回 null 让 Selecting 等待
    Logger.Info($"AutoParking: acquired {t.Value.Provenance} conf={t.Value.Confidence:0.00} legs={markerLoop.Legs + 1}");
    return t.Value;
}
```

- [ ] **Step 5: 影子模式** — `StepManeuver` :636 改一行：
```csharp
output.DryRun = settings.DryRun || settings.ShadowMode;   // 影子：全链路跑，控制永不发布
```

- [ ] **Step 6: 包络注入规划（spec §4.5 落地点 = Replan）** — 新增字段 `private AutoParkingSettings? legSettings;`。`Replan(MapGeometry geometry)`（:505）改为按腿选包络后规划（替换 `Planner.Plan(..., snapshot, ...)` 一行的 settings 实参）：
```csharp
AutoParkingSettings legCfg = settings;
if (settings.AutoParkByMarker)
{
    legCfg = settings.Clone();
    if (settings.DynamicEnvelope && markerTarget?.LateralClearM is double clear)
    {
        // 倒库腿：InsertPad 是「每侧总垫」，EnvelopeWidthM = 车宽 + 2*(pad+margin)，故减掉 margin。
        double pad = TargetResolver.InsertPad(clear, settings.VehicleWidthM, settings.SafetyMarginM);
        legCfg.EnvelopePadM = Math.Clamp(pad - legCfg.ObstacleMarginM, 0.0, 1.0);
        Logger.Info($"AutoParking: insert envelope {pad:0.00} m/side (clear {clear:0.00})");
    }
    else
    {
        // 接近腿：固定松包络（宽车道）。
        legCfg.EnvelopePadM = Math.Clamp(settings.ApproachEnvelopeM - legCfg.ObstacleMarginM, 0.0, 1.0);
    }
}
lock (sync) { planResult = Planner.Plan(CurrentPose, spot.Value, legCfg, geometry.Obstacles, geometry.Trust); legSettings = legCfg; }
LogCandidateCosts(planResult);
```
（保留方法体其余结构——`spot == null` 早退、`LogCandidateCosts`；把原来的 `lock (sync) planResult = result;` 合进上面的锁。）`StartParking`/`EngageFollower` 建 `Follower` 时用 `AutoParkingSettings cfg = legSettings ?? settings;`（现有 :1664 `cfg = settings` 改此），replan 闭包里的 `Planner.Plan(from, spot, cfg, ...)` 随之用同一 legCfg，保证执行中重规划与首规划同包络。

- [ ] **Step 7: 多腿衔接（HoldingBrake 到期钩子）** — :611 附近把
`if (DateTime.UtcNow >= phaseDeadlineUtc) CompletePhase(current);` 改为：
```csharp
if (DateTime.UtcNow >= phaseDeadlineUtc)
{
    if (!settings.AutoParkByMarker || !TryNextMarkerLeg())
        CompletePhase(current);
}
```
新方法：
```csharp
private bool TryNextMarkerLeg()
{
    if (markerTarget is null) return false;
    TargetResolver.Target? next = AcquireMarkerTarget();
    if (next is null) { Logger.Info("AutoParking: no next marker, finishing"); return false; }
    if (next.Value.Uid == activeMarkerUid
        && Vector2.Distance(next.Value.Pose.Position, markerTarget.Value.Pose.Position)
           <= settings.MarkerMoveToleranceM)
    { Logger.Info("AutoParking: marker unchanged after settle, finishing"); return false; }
    // planResult 已在 AcquireMarkerTarget 内刷新；记腿数、重建 follower、回到 Following。
    markerLoop.OnAcquired(next.Value);
    EngageFollower();   // StartParking 建 follower 的那段，Step 7 内抽出
    lock (sync) { phase = ParkingPhase.Following; warnedNoAir = false; }
    Logger.Info($"AutoParking: leg {markerLoop.Legs + 1} -> {next.Value.Provenance}");
    return true;
}
```
（前置：先把 `StartParking` 中 `lock (sync)` 内从 `follower = new Follower(...)` 到 `phase = ParkingPhase.Engaging;` 的段落抽成 `private void EngageFollower()`（含 `controlTrace.Reset()`，轨迹按腿分段），`StartParking` 调它，本步与 Step 8 的重规划也调它。）

- [ ] **Step 8: 标记位移重规划（StepManeuver 内，1 Hz 节流）** — 在 `Follower.Step` 调用前：
```csharp
if (settings.AutoParkByMarker && activeMarkerUid != 0 && DateTime.UtcNow >= nextMarkerCheckUtc)
{
    nextMarkerCheckUtc = DateTime.UtcNow.AddSeconds(1.0);
    KdopReader.Snapshot? snap = MemoryItemSnapshot;   // 后台刷新的缓存，读不阻塞
    if (snap != null)
    {
        foreach (KdopReader.MemItem it in snap.Items)
        {
            if (it.Uid != activeMarkerUid) continue;
            if (markerLoop.MarkerMoved(it.PlanePos, settings.MarkerMoveToleranceM))
            {
                // 标记被游戏挪了：就地重规划，不中止（M4）。
                Pose2 from = state.Pose;
                Pose2 goal = new(it.PlanePos.X, it.PlanePos.Y,
                                 markerTarget?.Pose.HeadingRad ?? from.HeadingRad);
                AutoParkingSettings cfg = legSettings ?? settings;
                PlanResult next = Planner.Plan(from, goal, cfg, obstacles, mapGeometry?.Trust);
                if (next.Ok)
                {
                    lock (sync) { planResult = next; }
                    markerLoop.OnAcquired(new TargetResolver.Target(
                        goal, "marker-moved", markerTarget?.Confidence ?? 0.6,
                        activeMarkerUid, markerTarget?.LateralClearM, markerTarget?.GapCenter));
                    markerTarget = markerLoop.Active;
                    EngageFollower();   // Step 7 抽出的同一助手，读 planResult/legSettings
                    Logger.Warn($"AutoParking: marker 0x{activeMarkerUid:X} moved, re-planned leg {markerLoop.Legs}");
                }
                else Logger.Warn($"AutoParking: marker moved but re-plan failed: {next.Reason}");
            }
            break;
        }
    }
}
```

- [ ] **Step 9: 验证（三层）**
1. `dotnet build -c Release` → 0 错误。
2. `dotnet run --project Tools/PlannerHarness -c Release` → exit 1、✓ ≥151、三条固定 ✗ 原样。
3. 影子实车清单（车主执行，写进提交信息）：游戏内驾校 book1_2_09 → `ShadowMode=true` → 主热键 → 地图上应出现目标点（出处=mover-pair）与规划线 → **影子不发任何指令，车要你自己开**：把车开到 stopmid 停稳 → 3 s 后目标应跳到缺口（framing-gap 或新 mover-pair）→ 第二段规划线出现。全程只有你在动车，插件只负责解析、规划和画图。日志应有 `acquired ... conf=0.90 legs=1` → `leg 2 -> ...`。

- [ ] **Step 10: Commit**
```bash
git add AutoParkingPlugin.cs Memory/KdopReader.cs
git commit -m "feat: follow game parking markers end-to-end behind start gates and shadow mode"
```

---

### Task 9: 部署链 + 文档（csproj 拷 exe、README 双语、设计文档 §54）

**Files:**
- Modify: `AutoParking.csproj`（build 后拷贝 sidecar）
- Modify: `README.md` + `README.zh-CN.md`（部署与新功能各一节）
- Modify: `docs/2026-09-30-autoparking-design.md`（追加 §54 条目）

- [ ] **Step 1: csproj 拷贝 Target** — `AutoParking.csproj` 末尾加：
```xml
<Target Name="CopySidecarProbe" AfterTargets="Build"
        Condition="Exists('Tools\MemoryProbe\bin\Release\net10.0\win-x64\MemoryProbe.exe')">
  <Copy SourceFiles="Tools\MemoryProbe\bin\Release\net10.0\win-x64\MemoryProbe.exe"
        DestinationFolder="$(TargetDir)" SkipUnchangedFiles="true" />
</Target>
<Target Name="WarnMissingSidecarProbe" AfterTargets="Build"
        Condition="!Exists('Tools\MemoryProbe\bin\Release\net10.0\win-x64\MemoryProbe.exe')">
  <Warning Text="MemoryProbe.exe not built - identity channel will run degraded (geometry-only). Build Tools\MemoryProbe first for full marker identity." />
</Target>
```
先 `dotnet build Tools/MemoryProbe -c Release -r win-x64 --self-contained false` 产出 exe，再 `dotnet build -c Release`，确认 exe 出现在 `bin\Release\`。

- [ ] **Step 2: README 双语同步**（英文先写，中文镜像）：部署节补"sidecar 需先构建 Tools/MemoryProbe"；功能节补 marker-following 一段（要点：跟 0xC17 标记、影子默认开、起步门列表、M4 不变）。
- [ ] **Step 3: 设计文档 §54 条目** — `docs/2026-09-30-autoparking-design.md` 追加（编号顺延现有最大节）：症状（手动选点无法覆盖驾校多阶段任务）、线索（--strctx 挖出 0xC17 对与 enable 链）、测量（两场景 58 m 位移复现、0.6 s sidecar 延迟、动态包络 0.22）、修复（本计划九任务）、被否决假设（"Trigger 盒即车位轮廓"——尺寸反证不相交 0.12 m；"需要解析任务脚本"——MarkerFollower 不需要）。
- [ ] **Step 4: 最终验证 + 部署**
```bash
dotnet run --project Tools/PlannerHarness -c Release    # exit 1、✓ ≥151、✗ 集合原样
dotnet build -c Release                                  # 0 错误，bin\Release\ 含 DLL + MemoryProbe.exe
```
把 `bin\Release\AutoParking.dll` 与 `MemoryProbe.exe` 拷入宿主 `current\Plugins\`，提示车主重启 ETS2LA。
- [ ] **Step 5: Commit**
```bash
git add AutoParking.csproj README.md README.zh-CN.md docs/2026-09-30-autoparking-design.md
git commit -m "docs: ship the sidecar probe, document marker following and the design-log entry"
```

---

## 实车测试准备（Task 8 影子清单 + 以下实车协议，交给车主执行）

1. **影子跑通**：book1_2_09 全程两腿、目标出处正确、规划线肉眼可接受、日志有 legs=1→2。
2. **实车开关**：`ShadowMode=false`（设置页显式关）。
3. **起步门确认**：故意熄火/带速起步各一次，应被拒且原因写在横幅上。
4. **abort 键常备**：实车第一次只在空旷直线段测试跟随，确认热键能立刻交还。
5. **记录**：每次实车后把 `Goal value change`（`--strctx "Goal value change"`）与插件日志的 legs 序列对一遍，写进设计文档条目。

---

## Superseded during execution

执行中产生的裁决覆盖了下面这些行。计划正文**不回改**（它是当时的施工文本），这里逐条给出"原文 / 实际 /
依据"；两者冲突时以本节与 SDD ledger 的 Ruling 为准。

| 原文 | 原文写的 | 实际交付 | 依据 |
|---|---|---|---|
| `:95` | `MinGapOverTruckM=1.0` | **0.4**（spec §4.5：纵向净长 ≥ 车长 + 0.4，否则拒绝） | R2；Step 4 的代码与 spec 一致，常量行是陈的 |
| `:595` | `MinAirBar = 4.0` | **0** | R41：该字段单位未定，按"bar"设 4.0 在实测读数 114–123 上永不触发 |
| `:600` | 夹取 `MinAirBar [0, 10]` | **[0, 200]** | R41：宽到两种刻度都放得下，不预设单位 |
| `:637` | 滑杆 `Min="0" Max="10"`、标题"起步气压门槛" | `Min 0 / Max 200`、标题写明**（单位未定）** | R37/R41：默认 0 是"关闭可调门槛"，不是"关闭气压检查"；`HasBrakeAir()` 另算 |
| `:745` | sidecar 路径由 `AppContext.BaseDirectory` + `SidecarPath` 组成 | **候选链**：显式 `SidecarPath` → `<宿主 exe 目录>\Plugins\MemoryProbe.exe` → 插件自身目录，逐条记日志 | R7：宿主影子复制只带走 DLL，`AppContext.BaseDirectory` 是临时目录 |
| `:754` | 气压门 = `if (realControl && !HasBrakeAir())` 一处 | **两道独立拒绝**：可调门槛 `air < MinAirBar`（仅当 `MinAirBar > 0`）+ `HasBrakeAir()`（游戏自己的应急阈值）；两条消息互不冒充 | R37/R41：`HasBrakeAir()` 比的是应急气压，不是本键；`MinAirBar=0` 时前者惰性、后者照常，这个不对称是对的 |
| `:758` | 置信度门只写 `Confidence < MinConfidence` | **按出处判**：`saved-spot` 前缀豁免，其余档照旧 | R36：人自己选的位姿没有识别不确定性，0.6 默认下"退回存点"必须可用 |
| `:916`、`:952` | `✓ ≥151` | **floor 是 154**（`✗` 仍是三条固定项）；修复轮后为 **156**（同一场景里多加两条"最近缺口/存点可达"判据） | R3：判据按**增量**核（131→137→143→144→147→152→154→156），绝对值只是当时的快照 |
| `:766` | marker 获取条件是 `AutoParkByMarker && target == null` | 键在 **`markerTarget is null`**，`target` 不参与；完成/拒绝时清掉 marker 产出的 `target`（手选点保留） | R45：`target` 会活过一次动作，按它判会让每次之后的起步都跳过解析与置信度门 |
| `:842` | 每腿都克隆 `legCfg` 并写 `legSettings`（含手选车位） | 只有 marker 产出的位姿（出处不是 `saved-spot`）克隆包络；手选车位保留用户的 `EnvelopePadM`，且 `legSettings` 只在 `Replan` 提交时写、与 `planResult` 同生同灭 | R46：手选窄库位按接近腿包络放宽会被自己拒掉 |
| `:863` | "标记是否没动"用 `uid 相同 && 位置差 ≤ 容差` | 只比**位置**（`Vector2.Distance ≤ MarkerMoveToleranceM`），不比 uid | R4 的存点兜底会把已退休的标记解析到当前位姿：uid 判定把"没动"误报成新腿，"无可行路线"的 Warn 指向了错误的原因 |
| `:917` | 影子清单同时写"全程方向盘不动（影子）"和"开到 stopmid 停稳" | 影子**什么都不发**，清单改为"车要你自己开到检查点，插件只解析、规划、画图" | 自相矛盾：影子连油门刹车都不发，不可能自己开到检查点 |
| `:955` | 部署只拷 `AutoParking.dll` 与 `MemoryProbe.exe` | 拷 DLL + **四个** sidecar 文件（exe/dll/runtimeconfig/deps） | 探针是 `SelfContained=false`，只有 exe 时子进程起不来（2026-10-20 实测） |

