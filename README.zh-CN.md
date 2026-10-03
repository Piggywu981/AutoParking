# AutoParking — ETS2LA V3 自动泊车第三方插件

在**平面地图**上选一个车位，插件把车位用 **AR 叠加层**画到游戏画面里，然后自动完成
**前进 / 倒车 / 换挡（自动挡）/ 刹车 / 转向**，把车头停进去。

> **当前状态：M4 实车闭环调参中。** 规划器与控制器已经离线验证（见下文「自检工具」），
> 但真车上仍有一个未结问题：**换挡指令（布尔动作）没有被游戏接合**，导致车辆停在
> 等挡位状态。本文档「故障排查」一节记录了如何一步步定位它。
>
> **只做牵引车头，不支持挂车**（带挂车时默认拒绝启动）。

---

## 1. 安装

1. 编译：
   ```
   dotnet build -c Release
   ```
   产物是单个 `AutoParking.dll`。
2. 把它放进 ETS2LA 的插件目录：
   ```
   <ETS2LA 安装目录>\current\Plugins\AutoParking.dll
   ```
   本仓库里部署到 `E:\ETS2LA\V3-C#\ETS2LA-win-release-Portable\current\Plugins\`。
3. **重启 ETS2LA**（插件是影子复制加载的，热重载不可靠）。

依赖：插件直接引用 `current\` 下的 `ETS2LA.*.dll` 与 `TruckLib*.dll`、`Hexa.NET.ImGui.dll`，
所以**必须和 ETS2LA 主程序放在同一安装目录树里**。`.csproj` 里这些引用都是 `Private=false`，
不会把宿主 DLL 拷进插件目录。

## 2. 打开设置页与地图

- 设置页路由（按约定自动被发现，无需注册）：`/plugins/adjustments/local.autoparking`
  即 设置 → 调整 → **Auto Parking**。
- 两个可视化窗口由设置页开关控制：
  - **平面地图**（ImGui 窗口，默认开）：显示道路车道线、 prefab 边/导航曲线、障碍物、当前路径。
  - **AR 叠加层**（游戏画面上，默认开）：车位框、车身投影、前进方向箭头、目标位姿。

## 3. 绑定热键（必须）

覆盖层和游戏是两个应用。**SCS SDK 在游戏失焦时不接受输入**，所以启停不能用网页按钮，
必须用游戏内热键：

| 控件 ID | 作用 |
|---|---|
| `local.autoparking.Toggle` | 空闲→开始；运行中→暂停；已暂停→继续 |
| `local.autoparking.Abort` | 立即刹停、释放控制通道、交还辅助驾驶 |

绑定位置：**设置 → 控制**（插件在 `Init()` 里注册控件，因此启动后就能在列表里看到，
不需要先启用插件）。

## 4. 使用流程

1. 把车开到车位附近（路径长度上限见 `MaxTakeoverDistanceM`）。
2. 在平面地图上点选/调整目标位姿，或用「保存的车位」记忆上一次的位置。
   AR 叠加层会同步画出车位，用来确认地图坐标和游戏里的实际位置对齐。
3. 想先看效果就保持 **Dry-run 打开**：所有控制量只计算、只显示，不发进游戏。
4. 关掉 Dry-run，回游戏画面，按 `Toggle` 热键开始。
5. 需要接管就再按一次 `Toggle` 暂停；`Abort` 直接放弃并交还车辆。

启动会被拒绝的情况（状态表和日志都会写明原因）：还没选车位 / 遥测超时 / SDK 未激活或
游戏暂停 / 挂着挂车 / 地图数据未就绪 / 路径无解或存在障碍冲突 / 路径过长。

## 5. 设置项速览

| 分组 | 关键项 | 默认 | 说明 |
|---|---|---|---|
| 总开关 | `DryRun` | true | 只算不发指令 |
| | `RefuseWithTrailer` | true | 带挂车时拒绝启动 |
| | `ControlWeight` | 20 | 宿主把写同一字段的所有通道按权重平均，所以权重低不是让位，而是被覆盖。实测对手≈14 时我们的刹车请求被换算成正油门，并把气压耗光。与 ACC/超车插件同时使用时调到高于对方 |
| 车身 | `WheelbaseM` / `MaxSteerDeg` | 4.0 / 33 | 决定最小转弯半径，是最要紧的两个几何参数 |
| | `VehicleLengthM` / `VehicleWidthM` | 6.5 / 2.6 | 障碍冲突检测用的矩形 footprint |
| 速度 | `ForwardSpeedKph` / `ReverseSpeedKph` | 6 / 3 | 爬行速度上限 |
| | `MaxAccel` / `MaxBrakeDecel` / `ComfortDecel` | 1.0 / 1.2 / 0.5 | 踏板量与参考速度剖面 |
| | `LaunchAccelMps2` | 0.6 | 静止起步补偿（滚动阻力+传动间隙） |
| 纵向 | `PidKp/Ki/Kd` | 0.35 / 0.05 / 0.02 | 在加速度域里的 PID |
| 转向 | `SteerGain` / `SteerDeadband` / `SteerRateLimitPerSecond` | 1.0 / 0.02 / 1.5 | 把几何曲率命令线性化：增益→限幅→死区→每秒速率限制 |
| | `LookaheadBaseM` / `LookaheadGainMps` | 1.5 / 0.6 | 前向 Pure Pursuit 的前视距离 |
| | `ReverseLateral` | 反向 Pure Pursuit | 倒车横向律，可切换成交叉误差 PD |
| 规划 | `PlanRadiusMargin` | 1.35 | **在比车辆自身最小转弯半径更大的圆上规划**，否则每段圆弧都要满舵，执行器饱和后控制器就没有修正能力 |
| | `GearSwitchPenaltyM` | 6.0 | 每多一次换挡的代价 |
| | `TerminalStraightM` | 1.5 | 入库前最后一段直线。圆弧的航向只有在弧的终点才正好对上，早停 0.35 m 就会带着角度出库 |
| | `PathSampleM` | 0.25 | 路径采样步长 |
| 容差 | `ToleranceLateralM` / `ToleranceHeadingDeg` | 0.20 / 4.0 | 判定"到位" |
| | `ObstacleMarginM` | 0.5 | 车身矩形外扩 |
| | `ObstacleLookaheadM` | 4.0 | 只有这个距离以内的冲突才停车。注意扫掠矩形本身约 8 m 长，所以障碍在 7.5 m 外就会登记 |
| 重规划 | `ReplanWhileStopped` | true | **只在车停着时**重新求解路径——换挡边界，或被挡停等 2 秒后。行进中不重算：新路径与旧路径只在当前点相切、弧长不重合，投影分不清两个参考 |
| | `MaxReplans` | 3 | 换路径次数上限，用完退回定死。到顶**不会中止**——只有热键能结束动作 |
| 结束 | `HandbrakeOnFinish` / `RestoreAssistsOnFinish` | true | 到位拉手刹、交还辅助驾驶 |
| 可视化 | `MapScalePxPerM` / `MapZoom` / `MapViewRadiusM` / `SnapToNavCurve` | 1.25 / 1.0 / 120 / true | |
| | `ArGroundTrimM` | 0.0 | AR 地面微调（车轮接触点估算之外的手工偏移） |

所有数值在保存时会被 `Clamp()` 夹到表内给出的安全范围。

尚未接线的预留字段（改了不会有任何效果，别按它们判断行为）：`AutoCalibrateRadius`
（转弯半径自动标定，算法在设计文档 §6.1 里已写好但没实现）、`OvershootM`、`HazardOnFinish`、
`EngineOffOnFinish`。

## 6. 自检工具

### 6.1 规划几何自检（设置页「运行规划自检」）
24 组固定起终点（直线对齐、90° 垂直入库、45° 斜入库、侧方平移、不可达等），
断言：解的存在性、段数 ≤ 4、采样连续（相邻点 ≤ 0.35 m）、终点位姿误差 < 1e-3、
无 NaN、曲率不超规划半径、路径不许多绕一整圈、障碍冲突必须被拒。
不需要游戏在跑。结果写日志 + 设置页状态行。当前 **24/24**。

### 6.2 指令通路自检（设置页「测试挂 D / 测试挂 R」）
油门/刹车走的是**模拟轴**，换挡/手刹走的是**布尔动作**——两条不同的代码路径。
在未启用车库且关掉 Dry-run 时按这两个按钮，然后看状态表「人工输入」行尾部的
`gear=实际/仪表 shifter=变速箱类型` 有没有变号。
这能在 2 秒内回答"布尔动作到底进不进得去游戏"，不用跑完一整个泊车位。

### 6.3 离线闭环仿真（仓库外，`scratch\PlannerHarness`）
自行车模型驱动真实的 `Follower`，输出横向/航向误差、换挡脉冲数，以及转向质量指标
（满舵时长、方向盘换向次数、方向盘总行程）。另有一个**挡箱失聪**用例（换挡脉冲永不接合），
用来验证"等不到挡位"会降级为"继续行驶并补发脉冲"，而不是死等。另有一个**中途冒出障碍**用例，
同一几何跑两遍：关掉重规划会停等到超时，打开则从当前位姿重算并到位。
之所以放在插件目录之外：插件 `.csproj` 用的是默认 globbing，任何 `.cs` 都会被编译进 DLL。

## 7. 架构一览

```
AutoParkingPlugin.cs   生命周期 / 遥测采样 / 控件注册 / 设置读写 / 相位机
Settings.cs            全部设置项 + Clamp() + Clone()（落盘在 tick 线程外做）
Geometry.cs            平面位姿、朝向↔前向、符号横向误差、矩形/SAT 重叠
Driving\
  Planner.cs           分层规划：直线倒车 → Reeds-Shepp 正向 → 反向遍历 → 两段中转
  ReedsShepp.cs        由转弯圆几何构造 CSC/CCC，并用"重新积分验证终点"自检
  ParkingPath.cs       弧长采样路径 + 换挡点
  Follower.cs          挡位状态机 + 纵向 PID + 横向 Pure Pursuit/倒车律 + 转向整形
  ControlOutput.cs     唯一发指令的地方：通道发布/续发/释放
  ObstacleScanner.cs   车流/停放车辆 → 多边形
  PlannerSelfTest.cs   6.1 的 24 组用例
Rendering\
  MapGeometry.cs       map/ppd 数据 → 可绘制几何（含 prefab 描述符解析）
  MapOverlay.cs        平面地图窗口 + 选位/缩放/按钮
  ArOverlay.cs         游戏内 AR 绘制
SettingsPage.razor     @page "/plugins/adjustments/local.autoparking"
docs\                  设计文档 + 逐条实现记录（含被实测推翻的假设）
```

控制通道：`local.autoparking.drive`（0.3 s 超时，每 tick 续发）、`.gear`（TrueToToggle 脉冲）、
`.hold`（驻车制动，撤除前显式写 false 释放）。

## 8. 故障排查

日志位置：`<安装目录>\current\ets2la.log`，搜 `AutoParking`。

**关键：本插件没有自动中止。** 按需求，除热键外任何观测都不终止动作；唯一的自动干预是
超速时的"踏板+手刹"。失控只能靠 `Abort` 热键，调试时手要放在键上。

几条例志的含义：

| 日志 | 说明 |
|---|---|
| `engaged: … · 换挡 N · 冲突 0, dry-run=…` | 已接受任务并开始跟随 |
| `start rejected: …` | 被 §4 的哪一条拒绝 |
| `制动探针 sent_accel=-0.50 … user_brake=0.50` | 带符号的实际发布值 + 游戏回执。`sent_accel` 为负时应当看到 `user_brake` 上升、`user_throttle` 为 0 |
| `挡位探针 请求=Reverse → gear=0 dash=-1 … shifter_type=…` | 每次发换挡脉冲记一行，带发出请求的阶段。`shifter_type` 决定这辆车认不认 `gear_drive/gear_reverse`。**`gear=0` 而 `dash=-1` 是正常现象**：自动箱每次停稳都会退回空挡，所以确认两个读数任一符合即算成功；这里出现脉冲风暴说明该锁存失效 |
| `控件已注册（…Toggle / .Abort）` | 启动时一行；没有它就说明 `Init()` 没跑到 |

两个已经踩过的坑，记录在 `docs\2026-09-30-autoparking-design.md` §17，遇到同类现象先看它们：

1. **刹车变成油门**：宿主把 `aforward`/`abackward` 折进同一个 `acceleration` 桶做加权平均，
   再按符号分派。两个字段同时发 ⇒ 平均后变成半油门。正确做法是只发**一个带符号的字段**。
2. **`user_*` 是虚拟手柄的回声**：SDK 注入的量会以"玩家输入"的形式出现在 `user_*` 里。
   不要用它来判断"人类是否接管"，会误判。

设置文件：`%APPDATA%\ETS2LA\AutoParking.json`（含上次选的车位，重载插件不会丢）。
日志用的是 Spectre.Console markup，**消息里裸的 `[Tag]` 会被静默吞掉**。

## 9. 参考

- ETS2LA V3 源码：`E:\ETS2LA\V3-C#\SourceCode`（只读参考，不得修改）
- 平面地图渲染参考：`official-plugins\Plugins\InternalVisualization`
- 算法参考：V2 Python 版（V3 核心的 ACC 闭源）
- 设计文档与逐条实现记录：`docs\2026-09-30-autoparking-design.md`
