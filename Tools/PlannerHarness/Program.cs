using System.Numerics;
using AutoParking;

AutoParkingSettings settings = new();
Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine($"最小转弯半径 = {Kinematics.MinTurnRadius(settings):0.00} m " +
                  $"(轴距 {settings.WheelbaseM} m / 最大转角 {settings.MaxSteerDeg}°)");

PlannerSelfTest.Report report = PlannerSelfTest.Run(settings);
Console.WriteLine();
Console.WriteLine($"自检：{report.Summary}");

foreach (string failure in report.Failures)
{
    Console.WriteLine("  ✗ " + failure);
}

Console.WriteLine();
Console.WriteLine("几条典型路径的形状：");
Dump(settings, "直线倒车入库", new Pose2(0, 0, 0), new Pose2(0, 8, 0));
Dump(settings, "正后方 180 度", new Pose2(0, 0, 0), new Pose2(0, 12, Math.PI));
Dump(settings, "左后方斜入库", new Pose2(0, 0, 0), new Pose2(-6, 8, 45 * Math.PI / 180));
Dump(settings, "侧方平移入库", new Pose2(0, 0, 0), new Pose2(3.2, 6, 0));
Dump(settings, "车位在正前方 12m（需开过再倒回）", new Pose2(0, 0, 0), new Pose2(0, -12, 0));
Dump(settings, "车位在右前方 45 度", new Pose2(0, 0, 0), new Pose2(6, -9, -45 * Math.PI / 180));
Dump(settings, "垂直车位在左侧 8m", new Pose2(0, 0, 0), new Pose2(-8, 0, 90 * Math.PI / 180));

Console.WriteLine();
Console.WriteLine("所有候选（ReedsShepp.Solve 直接输出，前向 / 反向遍历）：");
AllCandidates(settings, "左 bay", new Pose2(0, 0, 0), new Pose2(-8, 0, 90 * Math.PI / 180));
AllCandidates(settings, "斜后", new Pose2(0, 0, 0), new Pose2(-6, 8, 45 * Math.PI / 180));

Console.WriteLine();
Console.WriteLine("闭环仿真（自行车模型驱动真实 Follower）：");
int simPassed = 0;
foreach (ClosedLoop.Outcome outcome in ClosedLoop.RunAll(settings))
{
    if (outcome.Reached) simPassed++;
    Console.WriteLine($"  {(outcome.Reached ? "✓" : "✗")} {outcome.Name}: 位置误差 {outcome.PositionError:0.00} m · " +
                      $"航向误差 {outcome.HeadingErrorDeg:0.0}° · 换挡脉冲 {outcome.GearPulses} 次 · {outcome.Status}");
}

Console.WriteLine($"闭环：{simPassed}/5 到位");

// The two-leg route is the only multi-run one, and it was un-followable end to end: the leg
// boundary sat one sample past the last arc-length the vehicle could reach while still driving
// forward, and the nearest-point projection could hop back onto the approach leg the reverse leg
// lies on top of. TerminalStraightM=2 is what makes this tier win on cost.
ClosedLoop.Outcome stagedLeg = ClosedLoop.Run("两段式路线（唯一多段路径）", new Pose2(0, 0, 0),
                                              new Pose2(6, -9, -45 * Math.PI / 180.0),
                                              new AutoParkingSettings { TerminalStraightM = 2.0 });
Console.WriteLine($"  {(stagedLeg.Reached ? "✓" : "✗")} {stagedLeg.Name}: 航向误差 {stagedLeg.HeadingErrorDeg:0.0}° · " +
                  $"换挡脉冲 {stagedLeg.GearPulses} 次 · {stagedLeg.Status}");

// An automatic that falls back to neutral at every standstill: the engaged ratio reads zero
// exactly when the follower wants proof the shift landed, so confirming on it never succeeds and
// the follower re-pulses the gearbox action for the rest of the maneuver.
ClosedLoop.Outcome autoNeutral = ClosedLoop.Run("自动箱停稳退空挡", new Pose2(0, 0, 0), new Pose2(0, 8, 0), settings,
                                                automaticNeutral: true);
Console.WriteLine($"  {(autoNeutral.Reached && autoNeutral.GearPulses <= 4 ? "✓" : "✗")} {autoNeutral.Name}: " +
                  $"换挡脉冲 {autoNeutral.GearPulses} 次（判据：到位且 ≤ 4 次）· {autoNeutral.Status}");

// The in-game gearbox may never take the pulse - that is what a real run showed. Nothing the
// follower observes is allowed to stop the maneuver, so a deaf gearbox has to degrade into
// "keep driving and keep asking", not into sitting on the brake until the hotkey.
Console.WriteLine();
Console.WriteLine("闭环·换挡指令无响应（模拟布尔动作进不去游戏）：");
ClosedLoop.Outcome deaf = ClosedLoop.Run("倒车入库", new Pose2(0, 0, 0), new Pose2(0, 8, 0), settings,
                                         gearboxDeaf: true);
Console.WriteLine($"  换挡脉冲 {deaf.GearPulses} 次 · {deaf.Status}");
bool keptGoing = deaf.GearPulses > 10;
Console.WriteLine($"  {(keptGoing ? "✓" : "✗")} 未卡在挡位等待：{(keptGoing ? "已进入行驶段并持续补发" : "仍在等挡位")}");

Console.WriteLine();
Console.WriteLine("转向整形 A/B（旧=4.8/s 无死区，新=1.5/s + 0.02 死区）：");
CompareSteering();

Console.WriteLine();
Console.WriteLine("直线入位段长度扫参（终端航向误差 / 横向误差 / 路径长度）：");
SweepTail();

ClosedLoop.Outcome pdLaw = ClosedLoop.Run("倒车用交叉误差PD", new Pose2(0, 0, 0), new Pose2(-8, 0, 90 * Math.PI / 180.0),
                                         new AutoParkingSettings { ReverseLateral = ReverseLateralLaw.CrossTrackPd });
Console.WriteLine($"  PD 律对照: 航向 {pdLaw.HeadingErrorDeg:0.0}° 横 {pdLaw.PositionError:0.00} m 满舵 {pdLaw.FullLockSeconds:0.0} s · {(pdLaw.Reached ? "到位" : pdLaw.Status)}");

// The recession watchdog needs states that make the distance grow. Nothing in the closed loop does
// that any more - the window fix removed the pinning that used to - so it is fed synthetic ones.
bool watchdogWorks = WatchdogFires();
Console.WriteLine($"  {(watchdogWorks ? "✓" : "✗")} 距离变大触发二段修正并重新规划：{watchdogWorks}");

bool autoNeutralOk = autoNeutral.Reached && autoNeutral.GearPulses <= 4;

Console.WriteLine();
Console.WriteLine("中途冒出障碍：路径定死 vs 停住时重算：");
(ClosedLoop.Outcome frozen, ClosedLoop.Outcome replanned) = ReplanAgainstBlocker(settings);
Console.WriteLine($"  关：{(frozen.Reached ? "✓ 到位" : "✗ " + frozen.Status)}");
Console.WriteLine($"  开：{(replanned.Reached ? "✓ 到位" : "✗ " + replanned.Status)}");
bool replanWorks = !frozen.Reached && replanned.Reached;
Console.WriteLine($"  {(replanWorks ? "✓" : "✗")} 重规划把不可完成的机动变成了可完成：{replanWorks}");

ClosedLoop.Outcome overshot = ClosedLoop.Run("刹不住冲过预热点", new Pose2(0, 0, 0),
                                             new Pose2(6, -9, -45 * Math.PI / 180.0),
                                             new AutoParkingSettings { TerminalStraightM = 2.0, MaxBrakeDecel = 0.05, ComfortDecel = 0.05 });
bool unwinds = overshot.FullLockSeconds < 60.0;
Console.WriteLine($"  {(unwinds ? "✓" : "✗")} {overshot.Name}: 满舵 {overshot.FullLockSeconds:0.0} s（判据 < 60 s，不能钉死）· {overshot.Status}");

// 选位拖动：按下那一刻车位就落在光标底下，所以"划方向"的起点与终点重合，
// 第一个像素的抖动就决定了车头朝向。判据：短于死区的拖动不得改朝向，
// 超过死区的拖动必须等于朝那个方向。
bool jitterIgnored = !Geometry.TryHeadingFromDrag(new Vector2(100, 100), new Vector2(101, 100), 12.0, out _);
bool deliberate = Geometry.TryHeadingFromDrag(new Vector2(100, 100), new Vector2(140, 100), 12.0, out double dragged);
double dragErrorDeg = deliberate
    ? Math.Abs(Geometry.SmallestAngleDifference(dragged, Geometry.HeadingFromForward(new Vector2(1, 0)))) * 180.0 / Math.PI
    : double.PositiveInfinity;
bool pickDragOk = jitterIgnored && deliberate && dragErrorDeg < 1e-6;
Console.WriteLine($"  {(pickDragOk ? "✓" : "✗")} 选位拖动死区：1 px 抖动被忽略={jitterIgnored} · 40 px 生效={deliberate} · 与正东夹角 {dragErrorDeg:0.000}°");

return report.Failures.Count == 0 && simPassed == 5 && keptGoing && stagedLeg.Reached
    && replanWorks && autoNeutralOk && unwinds && watchdogWorks && pickDragOk ? 0 : 1;

// The recession watchdog needs states that make the distance grow. Nothing in the closed loop does
// that any more - the window fix removed the pinning that used to - so it is fed synthetic ones:
// drive 9 m along a straight forward route, then walk back. The projection may only regress a
// couple of metres, which is already more than the watchdog allows, and that is the point.
static bool WatchdogFires()
{
    AutoParkingSettings cfg = new();
    Pose2 start = new(0, 0, 0);
    Pose2 goal = new(0, -12, 0);
    PlanResult plan = Planner.Plan(start, goal, cfg, new ObstacleSnapshot());
    ParkingPath route = plan.Path!;

    Follower follower = new(route, cfg, cfg.WheelbaseM,
        (from, blocked) =>
        {
            PlanResult next = Planner.Plan(from, goal, cfg, blocked);
            return next.Ok ? next.Path : null;
        });

    DateTime clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    Vector2 ahead = Geometry.ForwardFromHeading(0.0);
    bool braked = false;

    for (int i = 0; i < 30; i++)
    {
        clock = clock.AddSeconds(0.1);
        double along = i < 15 ? 0.6 * i : 0.6 * (30 - i);
        VehicleState state = new(clock, start.Position + ahead * (float)along, 0.0, 0.5, 1, 1);
        ControlDemand demand = follower.Step(state, new ObstacleSnapshot());

        if (demand.Brake > 0.3f && demand.Throttle == 0f)
            braked = true;
    }

    for (int i = 0; i < 20; i++)
    {
        clock = clock.AddSeconds(0.1);
        follower.Step(new VehicleState(clock, start.Position + ahead * -0.6f, 0.0, 0.0, 1, 1), new ObstacleSnapshot());
    }

    Console.WriteLine($"  合成退行：制动={braked} 重规划 {follower.Replans} 次 · {follower.Status}");

    return braked && follower.Replans >= 1;
}

static void SweepTail()
{
    const double Deg = Math.PI / 180.0;
    (string, Pose2, Pose2)[] cases =
    {
        ("右前 45 度", new Pose2(0, 0, 0), new Pose2(6, -9, -45 * Deg)),
        ("左侧垂直库", new Pose2(0, 0, 0), new Pose2(-8, 0, 90 * Deg)),
        ("左后斜入库", new Pose2(0, 0, 0), new Pose2(-6, 8, 45 * Deg))
    };

    Console.WriteLine($"  {"尾段 m",-8} {"最大航向°",-12} {"平均航向°",-12} {"最大横向 m",-12} {"自检",-6} 规划失败");
    foreach (double tail in new[] { 0.0, 1.0, 1.5, 2.0, 2.5, 3.0 })
    {
        AutoParkingSettings cfg = new() { TerminalStraightM = tail };

        double worstHeading = 0.0, sumHeading = 0.0, worstLateral = 0.0;
        int failures = 0;
        PlannerSelfTest.Report sub = PlannerSelfTest.Run(cfg);
        if (sub.Failures.Count > 0)
            failures += sub.Failures.Count;

        foreach ((string _, Pose2 start, Pose2 goal) in cases)
        {
            ClosedLoop.Outcome outcome = ClosedLoop.Run("sweep", start, goal, cfg);
            if (!outcome.Reached && !outcome.Status.StartsWith("超时"))
                failures++;

            worstHeading = Math.Max(worstHeading, outcome.HeadingErrorDeg);
            sumHeading += outcome.HeadingErrorDeg;
            worstLateral = Math.Max(worstLateral, outcome.PositionError);
        }

        Console.WriteLine($"  {tail,5:0.0}     {worstHeading,8:0.00}     {sumHeading / cases.Length,8:0.00}     " +
                          $"{worstLateral,8:0.000}   {(failures == 0 ? "OK" : failures.ToString())}");

        if (tail == 1.5 || tail == 2.0)
        {
            foreach ((string name, Pose2 start, Pose2 goal) in cases)
            {
                PlanResult plan = Planner.Plan(start, goal, cfg, new ObstacleSnapshot());
                Console.WriteLine($"        {name,-12} tail={tail:0.0} → {(plan.Ok ? plan.Summary : "无解 " + plan.Reason)}");
            }
        }
    }
}

static void AllCandidates(AutoParkingSettings settings, string label, Pose2 start, Pose2 goal)
{
    double radius = Kinematics.MinTurnRadius(settings);

    Console.WriteLine($"  [{label}] start->goal 前向候选：");
    foreach (ParkingPath p in ReedsShepp.Solve(start, goal, radius, settings.PathSampleM, settings.GearSwitchPenaltyM))
    {
        Console.WriteLine($"      {p.Description} 长度 {p.Length:0.0} m 代价 {p.Cost:0.0}");
    }

    Console.WriteLine($"  [{label}] goal->start 前向候选（反向遍历即倒车入库）：");
    foreach (ParkingPath p in ReedsShepp.Solve(goal, start, radius, settings.PathSampleM, settings.GearSwitchPenaltyM))
    {
        ParkingPath? inverted = ReedsShepp.Invert(p, settings.GearSwitchPenaltyM);
        Console.WriteLine($"      {p.Description} 长度 {p.Length:0.0} m → 倒库代价 {(inverted?.Cost ?? double.NaN):0.0}");
    }
}

static void Dump(AutoParkingSettings settings, string label, Pose2 start, Pose2 goal)
{
    PlanResult result = Planner.Plan(start, goal, settings, new ObstacleSnapshot());

    if (!result.Ok)
    {
        Console.WriteLine($"  {label}: 无解 - {result.Reason}");
        return;
    }

    ParkingPath path = result.Path!;
    int forward = 0;
    int reverse = 0;
    foreach (PathPoint point in path.Points)
    {
        if (point.Travel == DriveDirection.Forward) forward++; else reverse++;
    }

    Console.WriteLine($"  {label}: {path.Description} | 长度 {path.Length:0.0} m | 点数 {path.Points.Count} " +
                      $"| 前进/倒车点 {forward}/{reverse} | 换挡 {path.GearSwitches} | 代价 {path.Cost:0.0}");
    Console.WriteLine($"      起点 {Format(path.Points[0])} → 终点 {Format(path.Points[^1])} (目标 {goal.X:0.0}, {goal.Z:0.0}, {goal.YawDegrees:0}°)");
}

static (ClosedLoop.Outcome frozen, ClosedLoop.Outcome replanned) ReplanAgainstBlocker(AutoParkingSettings cfg)
{
    const double Deg = Math.PI / 180.0;
    Pose2 start = new(0, 0, 0);
    Pose2 goal = new(-8, 0, 90 * Deg);

    ParkingPath route = Planner.Plan(start, goal, cfg, new ObstacleSnapshot()).Path!;

    // A parked car across the route 22 m in. It has to be that far out because the swept footprint
    // is 8 m long, so the truck is held about that far before the blocker - and from a pose closer
    // in than this, no alternative maneuver exists any more.
    PathPoint at = route.Points.First(point => point.DistanceAlong >= 30.0);
    Vector2 tangent = Geometry.ForwardFromHeading(at.HeadingRad);
    Vector2 centre = at.Position + new Vector2(-tangent.Y, tangent.X) * 0.8f;

    ObstacleSnapshot blocker = new();
    blocker.Polygons.Add(Geometry.RectangleCorners(new Pose2(centre.X, centre.Y, at.HeadingRad), 2.2, 0.45));

    return (ClosedLoop.Run("被挡", start, goal, cfg, obstacles: blocker),
            ClosedLoop.Run("被挡", start, goal, cfg, obstacles: blocker, replan: true));
}

static string Format(PathPoint point)
    => $"({point.Position.X:0.00}, {point.Position.Y:0.00}, {point.HeadingRad * 180 / Math.PI:0}°)";

void CompareSteering()
{
    const double Deg = Math.PI / 180.0;
    (string, Pose2, Pose2)[] cases =
    {
        ("直线倒车入库", new Pose2(0, 0, 0), new Pose2(0, 8, 0)),
        ("右前 45 度", new Pose2(0, 0, 0), new Pose2(6, -9, -45 * Deg)),
        ("左侧垂直库", new Pose2(0, 0, 0), new Pose2(-8, 0, 90 * Deg)),
        ("左后斜入库", new Pose2(0, 0, 0), new Pose2(-6, 8, 45 * Deg)),
        ("正前方同向", new Pose2(0, 0, 0), new Pose2(0, -20, 0))
    };

    AutoParkingSettings oldWay = new() { SteerRateLimitPerSecond = 4.8, SteerDeadband = 0.0, SteerGain = 1.0 };
    AutoParkingSettings newWay = new() { SteerRateLimitPerSecond = 1.5, SteerDeadband = 0.02, SteerGain = 1.0 };

    Console.WriteLine($"  {"用例",-14} {"满舵s 旧->新",-22} {"换向次 旧->新",-20} {"方向盘行程 旧->新",-22} 到位");
    foreach ((string name, Pose2 start, Pose2 goal) in cases)
    {
        ClosedLoop.Outcome a = ClosedLoop.Run(name, start, goal, oldWay);
        ClosedLoop.Outcome b = ClosedLoop.Run(name, start, goal, newWay);
        Console.WriteLine($"  {name,-14} {a.FullLockSeconds,5:0.0} -> {b.FullLockSeconds,5:0.0}      " +
                          $"{a.SteerReversals,4} -> {b.SteerReversals,4}      " +
                          $"{a.SteerTravel,6:0.0} -> {b.SteerTravel,6:0.0}        " +
                          $"{(b.Reached ? "✓" : "✗")} (横 {b.PositionError:0.00} m)");
    }
}
