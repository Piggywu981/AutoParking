# AutoParking 插件设计文档

- 日期：2026-09-30
- 目标项目：ETS2LA V3（C# / .NET 10），安装版 `2026.9.5092`
- 工作区：本仓库根目录（工作区里的 `ThirdPartyPlugins\AutoParking\`）。**文档内一律不写绝对路径**：
  相对仓库根书写，`..\..\` 即包含本仓库的那个 ETS2LA 工作区。
- 插件 Id：`local.autoparking`
- 参考代码：`SourceCode`（只读）、`ThirdPartyPlugins\{OvertakeAssistant,SequentialAutoShift,SpeedLimitUnlocker}`、`official-plugins\Plugins\InternalVisualization`、`V2-Python`（ACC 控制参数）

> **本阶段范围（用户确认）**：只泊**牵引车本体**（刚体，无挂车）。带挂车时直接拒绝执行。自动摘挂不做——V3 代码里不存在挂钩/dock API。

---

## 1. 目标

用户在平面地图上点选一个车位（位置 + 朝向），插件把车头自动泊进去：规划一条含前进/倒车段的路径，自己完成换挡（自动挡 D/R）、油门、刹车、转向，全程在 AR 叠加层显示实际位置，并有独立设置页调参与查看状态。

### 非目标（v1 明确不做）
| 项 | 原因 |
|---|---|
| 挂车 / 摘挂 / 铰接倒车 | 用户指定延后；且无挂钩 API |
| 长距离导航到泊车位附近 | 属于 ACC + Pathfinding 的职责，不做重叠实现 |
| 多点三次倒车修正、狭窄侧方位搜索 | 需要 Hybrid A* + 占据栅格，我们没有真实占据地图 |
| 车位自动识别（服务区/公司装卸位） | 默认 `DataFidelity=Medium` 会丢弃 `Service`/`Trigger`/`MapOverlay` 项（`Data\Classes.cs:21-48`） |
| 坡度/打滑补偿、载重动力学建模 | 低速 3–6 km/h 下由 PID + 自动标定半径吸收 |

---

## 2. 已核实的事实（实现必须遵守）

这些是从源码逐条确认的约束，写成文档避免实现期重复调研。

### 2.1 插件宿主契约
| 事实 | 出处 |
|---|---|
| 基类 `ETS2LA.Shared.Plugin`：`abstract PluginInformation Info`、`virtual float TickRate => 20.0f`、`Init/OnEnable/Tick/OnDisable/Shutdown` | `ETS2LA.Shared\Shared.cs:84-149` |
| `RunningThread` 已 try/catch 每次 `Tick`，异常只记 `Logger.Error` 不杀线程 ⇒ 状态机必须能自恢复 | `Shared.cs:113-126` |
| 约定：`Init` 不碰 EventBus，订阅放 `OnEnable`，退订放 `OnDisable`/`Shutdown` | `Shared.cs:51-77` |
| 加载器用 `Activator.CreateInstance` ⇒ 必须有 public 无参构造函数；建议同时设 `Instance` 静态属性给 Razor 页用 | `ETS2LA.Backend\PluginHandler\Handler.cs:184` |
| 插件目录会排除 `ETS2LA.*` 等程序集，影子复制到 `%TEMP%\ETS2LA\PluginShadow` | `Handler.cs:18-24,57,293` |
| Razor 页**没有注册 API**，纯靠约定 `@page "/plugins/adjustments/<Info.Id>"`，由 `App.razor` 的 `Router.AdditionalAssemblies` 扫描插件程序集 | `ThirdPartyPlugins\*\SettingsPage.razor:1`、`ETS2LA.UI\App.razor:8`、`Pages\Plugins\Manager.razor:126-146,208` |
| UI（Photino Blazor Hybrid）与游戏数据**同进程**，设置页里可直接读 `GameTelemetry.Current` | `ETS2LA.UI\IPC.cs:1-12`、`Pages\Visualization.razor:14-36` |
| 工程用 `Microsoft.NET.Sdk.Razor` + `<Reference HintPath>` 指向 `..\..\ETS2LA-win-release-Portable\current\*.dll` + `<Private>false</Private>` + `NuGet.Config` 清空包源 | `SpeedLimitUnlocker\*.csproj`、`OvertakeAssistant\*.csproj` |
| `build_all.ps1` 只扫 `ThirdPartyPlugins` **depth 1** 的 `.csproj`，产物 `<AssemblyName>.dll` 复制到 `current\Plugins\` | `ThirdPartyPlugins\build_all.ps1:14,47-59` |
| 本机 .NET SDK 已具备：`10.0.102`（已验证 `dotnet --list-sdks`） | — |

### 2.2 控制输出（这是最容易踩坑的地方）
| 事实 | 出处 |
|---|---|
| 唯一出口是事件总线：`Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent{...})`，`EventString = "ETS2LA.Game.Output.ControlEvent"`；没有可直接调用的方法 | `ETS2LA.Game\Output\Output.cs:13-14,107` |
| `ControlEvent`：`required ControlChannelDefinition ChannelDefinition` / `ControlProperties Properties` / `ControlVariables Variables`；`ControlChannelDefinition{required string Id; float Timeout = 0.2f}`；`ControlProperties{ControlBooleanType BooleanType; float Weight = 1.0f}` | `Output\Classes.cs:287-330` |
| **同名字段跨通道按 Weight 加权平均**，不是抢占：`(Σwᵢvᵢ)/Σwᵢ` 后 clamp(-1,1) | `Output.cs:284-291` |
| `aforward`/`abackward` 被折叠为同一 `acceleration` 通道：所有贡献做**加权平均**，结果 >0 写 `aforward`，<0 写 `abackward = -值`。**没有独立 brake 浮点通道，制动 = 负的 acceleration，且只能发一个字段**：同时发 `aforward=0, abackward=1.0` 会被平均成 +0.5 ⇒ 变成油门（实车已复现，见 §17） | `Output.cs:217-223,299-318` |
| **Windows 路径分叉**：`steering` 只写 modern（`Local\ETS2LAPluginInput`，offset 0/4/5）；`acceleration` 与其余浮点、全部 bool 只写 legacy（`Local\SCSControls`）。⇒ 两套共享内存必须同时可打开，否则表现为"只有一半能控制" | `Output.cs:26,293-323` |
| 通道超时未重发即被移除（默认 0.2 s）⇒ 控制必须以 ≥20 Hz 重发，本插件用 60 Hz | `Output.cs:273-277` |
| 释放通道：发一条 `Variables`/`Properties` 为空的 `ControlEvent`；`Channels` 空时 `GameOutput` 会把所有输出复位 | `Output.cs:118-121,131-169,256-263` |
| `ControlBooleanType.TrueToToggle` 会 `Task.Run` 写 true→sleep 50 ms→false（脉冲型按键，如 `gearup/geardrive/gearreverse`）；`Direct` 是电平型（如 `parkingbrake`） | `Output.cs:195-210,171-176` |
| 相关可用字段：`steering`、`aforward`、`abackward`、`clutch`、`parkingbrake`、`geardrive`、`gearreverse`、`gear0`、`gearup`、`geardown`、`drive`、`reverse`、`ignitionon/ignitionstrt`、`lblinker/rblinker/flasher4way`、`quickpark` | `Output\Classes.cs:79-82,126,145-149,208-217,162` |
| 没有 "safe/abort" API。等效中止 = 释放自己的通道 + `ApplicationState.EnableAssists=false` | `ETS2LA.State\Program.cs:227-249`；`SequentialAutoShift\Plugin.cs:743` |

### 2.3 遥测输入
| 事实 | 出处 |
|---|---|
| `GameTelemetry.Current.EventString = "ETS2LA.Telemetry.Data"`，`GetCurrentData()` 可轮询 | `ETS2LA.Game\Telemetry\Program.cs:16,27,97` |
| 位姿：`truckPlacement.coordinate` / `.rotation`，类型 `Vector3Double`（有 `.ToVector3()`），单位米，X/Z 为平面、Y 为高程 | `Telemetry\Classes.cs:314-318`、`ETS2LA.Shared\Shared.cs:220` |
| 朝向：`rotation.X` 是"圈数"，航向角 = `rotation.X * 360` 度（V2 同约定） | V2 `app\Plugins\Map\data.py:283-287` |
| 速度：`truckFloat.speed` 为直接读取的 SDK 浮点（`Program.cs:383`），符号约定未经实车确认 ⇒ **实现上有符号速度用 `truckInt.gear` 符号定义**：`signedSpeed = Math.Abs(speed) * Math.Sign(gear)`（gear 0=N、负=R） | `Telemetry\Classes.cs:153,185` |
| 人工/自动输入分离：`userSteer/userThrottle/userBrake` 与 `gameSteer/gameThrottle/gameBrake` ⇒ 判断"人类接管"用 `user*` | `Classes.cs:187-194` |
| 灯与开关：`truckBool.parkingBrake/motorBrake/engineEnabled/electricEnabled/blinkerLeftOn/blinkerRightOn/lightsReverse/lightsBrake` | `Classes.cs:249-272` |
| 配置：`configUI.gears`、`configString.shifterType`、`configFloat.engineRpmMax`、`configVector.truckHookPosition` | `Classes.cs:117,174,336,292` |
| 挂车：`GameTelemetry.Current.ReadTrailerData` 默认 **false**，需要显式置 true 才有 `trailers[i]`；`trailers[0].comBool.attached` 判断是否挂挂 | `Program.cs:33,637`、`Classes.cs:20,421` |
| 运行状态：`sdkActive`、`paused`、`ApplicationState.Current.IsGameRunning/RunningGame` | `Classes.cs:388-389`、`State\Program.cs:342-346` |
| **V3 遥测里没有 truck 尺寸字段** ⇒ 车身长宽来自设置项 | — |
| 动态障碍：`TrafficProvider` / `ParkedVehiclesProvider`，`BaseVehicle{Vector3 Position; Quaternion Rotation; Vector3 Size; List<Vector3> GetCornersOnGround()}` | `SDK\Traffic.cs:13-19,70,106`、`SDK\ParkedVehicles.cs:12-15,55` |

### 2.4 平面地图与 AR
| 事实 | 出处 |
|---|---|
| 地图数据入口便宜：`GameHandler.Current.Installations[i]`（找 `IsParsed` 的）→ `GetMapData()`（返回缓存字段）/`GetFileSystem()` | `ETS2LA.Game\Program.cs:31`、`Installation.cs:40,56,61` |
| 空间查询只有一个：`MapData.Nodes.Within(minX, minZ, maxX, maxZ)`（内建 RBush） | `Data\Classes.cs:61`、TruckLib `ScsMap\Collections\NodeDictionary.cs:102` |
| `MapItems` 只有 `Dictionary<ulong, MapItem>`，**没有按类型索引** ⇒ 全量 `OfType<Road>()` LINQ 很贵，只能在后台/低频做（InternalVisualization 每 Tick 做一次，1 Hz） | `official-plugins\...\InternalVisualization.cs:82-110` |
| 道路中心线：`new ParsedRoad(road)` + `InterpolateLane(t, Side.Left/Right, laneIndex).Position` + `RoadUtils.GetRoadResolution(road)`（15/5/1 m） | `Data\Classes.cs:266,317,345`、`Data\Utils.cs:174` |
| 建筑导航曲线：`PpdFileHandler.Current.GetPpdFile(prefab.Model.ToString())` → `PrefabDescriptor.NavCurves/Nodes`，`PrefabUtils.InterpolateNavCurve(curve, t)` 得**局部坐标**，需 `Vector3.Transform(p + prefabStart, rotationMatrix)` 转世界 | `Data\FileHandlers\PpdFiles.cs:90`、`Data\Utils.cs:192`、`official-plugins\...\Renderers\Prefabs.cs:76-99` |
| 世界→屏幕约定：`screenX = (wx - cx)*Scale + w/2`，`screenY = (wz - cz)*Scale + h/2`，`Scale` 像素/米（IV 用 1.25） | `official-plugins\...\Utils.cs:8-14` |
| 自车朝向绘制：`angle = 360 - (rotation.X*360) + 90`，屏幕矩形绕 Z 旋转 | `official-plugins\...\Renderers\Truck.cs:19-41` |
| Overlay 窗口：`OverlayHandler.Current.RegisterWindow(WindowDefinition, Action renderAction)`，`UnregisterWindow(def)` 按 Title 匹配，`OpenWindow/CloseWindow(title)`；`WindowDefinition{Title, Flags, Width, Height, X, Y, Alpha, Open, NoWindow, SizingFunction, LocationFunction}` | `ETS2LA.Overlay\Overlay.cs:628,656,661,670`、`Classes.cs:10-29` |
| **鼠标输入需要交互模式**：非交互时每帧给 viewport 加 `ImGuiViewportFlags.NoInputs`（Linux 另设 `GLFW_MOUSE_PASSTHROUGH`）⇒ 只有 `OverlayHandler.Current.IsOverlayFocused==true`（默认键 **RightAlt**，控制 Id `ETS2LA.Overlay.Interact`）时窗口才收鼠标事件 | `Overlay.cs:193,197-203,79,96-97,120-136` |
| AR：`OverlayHandler.Current.AR.RegisterRenderCallback(new ARRenderCallback{ Definition = new ARRendererDefinition{Name=...}, Render3D = ... })`；`UnregisterRenderCallback(name)` | `Overlay.cs:65`、`AR\AR.cs:120,129` |
| AR 图元：`Draw3DLine(ARCoordinate,ARCoordinate,uint color,float thickness=1f)`、`Draw3DCircle(coord,radius 米,uint,bool filled=false,float thickness=1)`、`Draw3DPolygon/Quad/Triangle(...,uint,bool filled=false,float thickness=1)`、`Draw3DText(ARCoordinate,string,uint, ...)`；`thickness` 是像素，`radius` 是米 | `AR\AR.cs:311,445,476,528,569,610` |
| `ARCoordinate`：`implicit operator ARCoordinate(Vector3)` 默认 `Center=World`；`ARCoordinateCenter{World,Truck,Camera}`；`InDirection(Quaternion,float,center)` | `AR\Classes.cs:21-45,55,89`、`AR\AR.cs:245-267` |
| 颜色：`ConvertColor` 会把 ARGB 转成 ImGui 的 ABGR ⇒ 用 `ImGui.GetColorU32(new Vector4(r,g,b,a))` 最省事 | `AR\AR.cs:269-277` |
| AR 有距离裁剪：`overlaySettings.MaxARDistance` 之外不画，`RenderAR`/`DisableOverlay` 会整体关闭 ⇒ 插件不能假设一定可见 | `AR\AR.cs:279-301` |
| 相机/投影数据（若需自算投影）：`CameraData{fov, position, cx, cy, rotation, truckPosition, truckRotation, projection}`，EventString `"ETS2LA.Game.SDK.Camera.Data"`；注意世界环绕修正 `512*cx/512*cy` | `SDK\Camera.cs:16-48,74`、`AR\AR.cs:135-139` |
| 事件总线：`Events.Current.Subscribe<T>(topic, handler)` / `Unsubscribe<T>` / `Publish<T>`，处理器存在**静态**字典 ⇒ 必须成对退订，否则插件重载泄漏 | `ETS2LA.Backend\EventBus\Bus.cs:12-45` |
| 处理器必须是**实例方法或保存在字段里的委托**（`Unsubscribe<T>` 按委托相等移除），lambda 里订阅等于永远退不掉 | `Bus.cs:17-38`、`SpeedLimitUnlocker\Plugin.cs:67-68,107-109` |
| 设置读写：`new SettingsHandler()` → `Load<T>(fileName)`（不存在会自动创建默认实例）/`Save(fileName, obj)`/`RegisterListener<T>(fileName, cb)`/`UnregisterListener<T>(fileName, cb)` | `ETS2LA.Settings\Program.cs:98,122,133` |
| 快捷键：`ControlsBackend.Current.RegisterControl(new ControlDefinition{Id,Name,Description,DefaultKeybind="",Type=ControlType.Boolean})` + `On(id, handler)`；Id 发布后**不可改名** | `ETS2LA.Controls\Program.cs:42,47`、`Shared.cs:123-149` |
| 通知：`NotificationHandler.Current.SendNotification(new Notification{Id,Title,Content,Level,CloseAfter})` | `SpeedLimitUnlocker\Plugin.cs:81-88` |
| 单位换算：`UnitConversions.ToScientificUnits/FromScientificUnits(UnitType.Speed, v, ApplicationState.Current.DisplayUnits)`、`GetUnitAbbreviation` | `SpeedLimitUnlocker\Plugin.cs:123,380-391` |
| 全局可调项：`ApplicationState.Current.{EnableAssists,DesiredSpeed(m/s),DrivingMode,DisplayUnits}`；`AssistanceSettings.Current.{MaximumSpeed,FollowingDistance,AccelerationResponse,CollisionAvoidance,...}` | `State\Program.cs:104-132`、`Settings\Global\Assistance.cs:46-91` |

### 2.5 现成方案（网上）对照后的取舍
- **Reeds-Shepp 闭式解**是结构化工位泊车的主流（含挡位切换标签、无需栅格搜索）→ 采纳为主规划器。
- **Hybrid A\***（ROS2 nav2 `SmacPlannerHybrid`、Apollo）适合非结构化工位，但依赖占据栅格；我们只有实体列表 → 不采纳，用「障碍 OBB + 路径走廊」替代。
- 几何两圆弧/`two-circle`（Pandas-Team Automatic-Parking 的入库段）实现成本最低 → 采纳为 **Tier-0/Tier-2 退化方案**。
- 跟踪层：Pure Pursuit（前向）+ 交叉误差 PD（倒车）+ 纵向 PID → 采纳；MPC 不采纳（低速收益小、调参成本高）。
- 游戏 mod 先例（AutoDrive `ParkTask.lua`）：所谓"泊车"其实只是"开到地图标记点"，超 30 m 先寻路到路网；失败即 `stopAutoDrive()` + 用户可读通知 → 采纳其 **UX 与中止语义**（任务化、明确错误提示、限制接管距离），但泊车机动必须自研。
- V2 ACC 已验证参数直接移植：`kp=0.30, ki=0.08, kd=0.01/0.05`，被控量为**加速度**；`clutch>0.1` 冻结积分；`v<10/3.6` 清积分；到点减速 `a = -v²/(2d)*1.2`（`d-=5` 提前量，`d<50` ×1.2，`d>20` 夹 -2 否则夹 -6，`d<5 且 v<1` → ≤-1，`d≤0` → -6）；横向 `multiplier = max(8-(kph-10)/10, 2)`、`offset_correction = lateral*5*1.5`、`|angle|>140°` 归零。（V2 `Plugins\AdaptiveCruiseControl\main.py:148-150,1151-1230,272-309`、`Modules\Steering\route\driving.py:11-12,232-335`）
- **游戏原生 autopark 不可用**：只有 `ControlVariables.quickpark` 一个开环按键和只读遥测位 `gameplayBool.jobDeliveredAutoparkUsed`，仅对运单装卸位生效，无法泊任意车位。

---

## 3. 架构与模块划分

```
AutoParkingPlugin  (生命周期 / 编排 / 60Hz Tick)
├── Settings            AutoParkingSettings + 版本迁移 + HandleAction
├── VehicleState        每 tick 的不可变位姿/速度快照 (Pose2, signedSpeed, gear)
├── Kinematics          轴距/最大转角→最小转弯半径，运行中自动标定
├── Planner             ReedsShepp(解析解) + Tier0/Tier2 退化 → ParkingPath
├── ObstacleCheck       路径走廊 vs 障碍 OBB (SAT)
├── Follower            横向(PurePursuit/PD) + 纵向(PID) + 换挡状态机
├── ControlOutput       发/续/释放 ControlEvent 通道（唯一发控制的地方）
├── SafetyMonitor       中止条件矩阵，唯一有权 Abort() 的模块
└── Rendering
    ├── MapOverlay       ImGui 平面地图（选点/拖拽朝向/路径预览）
    └── ArOverlay        ARRenderCallback（目标框/路径线/文本）
SettingsPage.razor      Blazor 参数页 + 状态表
```

**依赖方向单向**：`Rendering` 与 `SettingsPage` 只读快照，不改状态；只有 `Follower → ControlOutput` 能发控制；`SafetyMonitor` 只调用 `Plugin.Abort(reason)`；`Planner`/`Geometry`/`Kinematics` 是纯函数/纯计算，无 ETS2LA 依赖，便于自检。

**线程边界**：Tick 线程（60 Hz）、ImGui 渲染线程、Blazor UI 线程。策略：
- 共享可变状态统一用 `private readonly object sync`；
- 对外暴露一律走"快照引用"（`ParkingPath`、`Pose2` 设计为不可变 record，替换引用而非改内容），渲染线程只做拷贝读；
- 任何渲染回调里**不做规划**、**不发控制事件**，只画。

---

## 4. 数据模型

```csharp
readonly record struct Pose2(double X, double Z, double HeadingRad);

enum DriveDirection { Forward, Reverse }

sealed record PathPoint(Vector3 Position, double HeadingRad, double Curvature, DriveDirection Dir, double DistanceAlong);

sealed record PathSegment(DriveDirection Dir, IReadOnlyList<PathPoint> Points, double Length);

sealed record ParkingPath(IReadOnlyList<PathSegment> Segments, double TotalLength, double Cost,
                          int GearSwitchCount, PlanSource Source);   // Straight | ReedsShepp | TwoLeg

enum ParkingPhase { Idle, Selecting, Planned, Engaging, Following, GearHold, Aligning, HoldingBrake, Done, Aborted }

sealed record VehicleSnapshot(double TimestampUtc, Pose2 Pose, double SignedSpeed, int Gear,
                              bool TrailerAttached, bool EngineOn, bool ParkingBrakeOn,
                              double UserSteer, double UserThrottle, double UserBrake);
```

`ParkingPath` 一旦生成就不可变；重规划产生新引用并原子替换，正在执行的 `Follower` 检测到引用变化即从头重新计数。

---

## 5. 交互流程

### 5.1 用户视角
1. 打开 ETS2LA 悬浮层里的 `AutoParking Map` 窗口（插件启用时自动注册；可关）。
2. 地图以自车为中心：道路车道中心线（左右分色）、附近 prefab 导航曲线、自车三角框。按住 **RightAlt** 进入交互模式后鼠标可用（窗口内常驻提示）。
3. 左键点击 → 放置车位（车头矩形框，按设置的长宽）；按住拖动 → 定车头朝向；滚轮 → 缩放（1.0–4.0×）。
4. 每次改动即时规划：图上叠画规划路径（前进=青色，倒车=橙色，挡位切换点=白色圆），窗口底部显示 `路径长度 / 换挡次数 / 冲突数 / 结论`。
5. 点 `Start` 按钮（或按绑定的启动快捷键，**不需要**交互模式）开始执行；`Stop` 或同一快捷键随时中止。
6. 执行期间 AR 显示实际目标位姿与剩余路径；结束自动拉手刹并通知。

### 5.2 内部状态流
```
Idle ──选点/规划──▶ Planned ──Start(校验通过)──▶ Engaging ──▶ Following ◀──┐
                                                            │           │(段边界)
                                                            │        GearHold ──验证──┘
                                                            ├─末端─▶ Aligning ─▶ HoldingBrake ─▶ Done
                                                            └─任意中止条件─▶ Aborted
```
`Engaging`：记录 `(EnableAssists, DesiredSpeed, DrivingMode)` 快照 → `EnableAssists=false` → 挂到正确挡位 → 发第一条控制。
`Done`：`parkingbrake=true` 持续 2 s 确认生效 → 释放 `acceleration/steering` 通道 → 恢复 assists 快照 → 通知 → 回 `Idle`。
`Aborted`：**先保持制动 3 s**（防溜车）再释放全部通道 → 恢复 assists 快照 → `Logger.Warn` + 通知（带中止原因）→ 回 `Idle`。

### 5.3 前置校验（`Start` 时拒绝并给出原因）
| 条件 | 原因文案 |
|---|---|
| `trailers[0].comBool.attached` | 本版本不支持带挂车 |
| `!sdkActive` 或 `paused` 或 `!IsGameRunning` | 遥测未就绪 |
| 自车到路径起点距离 > `MaxTakeoverDistance`(默认 30 m) | 请先用 ACC 开到附近 |
| 规划失败（无解） | 车位姿态不可达，试试更小的进库角或更接近 |
| 走廊冲突数 > 0 | 路径上有障碍车辆 |
| 地图数据未解析 | 等待地图解析（首次需数分钟） |

---

## 6. 规划器

### 6.1 运动学模型
自行车模型，`R_min = wheelbase / tan(maxSteerRad)`。默认 `Wheelbase=4.0 m`、`MaxSteerDeg=33°` ⇒ `R_min≈6.16 m`。

自动标定（`AutoCalibrateRadius=true`，**这一条只有设计、没有实现**：全仓库只有 `Settings.cs` 里那个开关，没有任何消费者；下面描述的是当初的打算）：在直行/匀速行驶中用 `yawRate = (Δheading)/Δt` 与 `v` 反解瞬时半径 `R_inst = v / yawRate`，只在 `|v|>1.0 m/s`、`|steer|>0.15`、`|Δsteer|<0.02/tick` 时采样，取 20 个样本的中位数再按 `R_min_observed = R_inst / |steer_norm|` 折算；结果限幅 `4–20 m`，滑动保留最近 300 s。标定值只影响规划，设置页可"清零回退默认"。

航向与前向约定（与 V2 一致）：`yawDeg = rotation.X*360`，归一到 `(-180,180]`；`forward = (-sin yaw, 0, -cos yaw)`；横向左手系用 `left = Cross(UnitY, forward)`（OvertakeAssistant 已验证的注释）。

### 6.2 Reeds-Shepp 主规划器
输入：起点位姿、目标位姿、`R_min`、`maxPathLength`。输出：符号段长序列（正=前进、负=倒车），采样成 `ParkingPath`。

实现口径：按 Reeds & Shepp (1990) 的闭式解，实现泊车够用族：
`LSL, RSR, LSR, RSL, RLR, LRL, LRSR, LRSL`（及其镜像），每族给出 `(t,p,u[,q])` 闭式；退化情形（同向直线、纯圆弧）单独处理避免 `NaN`。

代价函数：`cost = Σ|segLen| + GearSwitchPenalty(默认 6 m 当量) * 换挡次数 + CurvatureJumpPenalty(2 m) * 不连续次数`，取最小；所有解先做"起点/终点局部膨胀免检"再查走廊冲突。

采样：弧长 0.25 m 等间隔；每点带 `HeadingRad`、`Curvature=±1/R`、`Dir`、`DistanceAlong`。

### 6.3 分层退化（保证成功率的工程手段）
按顺序尝试，首个"无冲突且代价最小"者胜出：
- **Tier-0 直线倒车**：目标在车后方，横向偏差 <0.35 m、航向差 <8°、后方走廊净空 → 直接一条倒车直线。
- **Tier-1 Reeds-Shepp**：6.2 的全族搜索。
- **Tier-2 预热点两段式**：`staging = goal - forward(goal)*EntryDistance`（默认 12 m）；第一段用 RS/直线把**车头**带到 `staging`（允许 1 次换挡），第二段从 `staging` 直线倒进 `goal`。用于 RS 无解或路径过长的场景。
全部失败 → 返回带原因的拒绝，UI 显示"不可达"。

### 6.4 障碍与走廊检查
- 障碍来源：`TrafficProvider.CurrentTraffic` + `ParkedVehiclesProvider`，用 `GetCornersOnGround()` 得到实际 OBB 顶点（无需自己按 Size/Rotation 推）。
- 路径走廊：每 0.5 m 放一个车身 OBB（`Length+0.6 m` 纵向、`Width+0.5 m` 横向余量），与障碍做 2D SAT 重叠测试。
- 免检区：起点起 2 m、终点前后 1 m（自车本就占着/用户手动确认过）。
- 预算：单次检查 ≤50 ms；超预算降级为"只检终点 + 起终点 2 m"，并在 UI 标注"粗略检查"。
- 执行期每 tick 重检"前方剩余走廊"，新出现障碍 → `SafetyMonitor` 刹停等待（最多 `ObstacleWaitS` 8 s）→ 超时或持续占用 → 中止。

---

## 7. 控制器

### 7.1 纵向
参考速度：`v_ref(s) = min(v_cap(dir), sqrt(2*a_comfort*s_left), a_ramp * s_since_segment_start)`，`s_left = 段末 - 当前弧长`。
误差 `e = v_ref - signedSpeed`；PID 输出**期望加速度** `a_cmd ∈ [-a_max_brake, +a_max_accel]`（默认 `a_max_accel=1.0`、`a_max_brake=1.2 m/s²`）→ 归一化：`aforward = a_cmd/a_max_accel`（>0）、`abackward = -a_cmd/a_max_brake`（<0）。
抗 windup：换挡进行中、`|e|>3 m/s`、`userClutch>0.1` 时冻结积分；`|v|<0.5 m/s` 清积分；`dt` 上限 0.05 s。
末端：`s_left<0.3 m` 且 `|v|<0.15 m/s` → 输出 `abackward=0.6` 保持，进入 `Aligning`。
V2 到点减速公式作为兜底叠加：`a_stop = -v²/(2*max(s_left-0.5, 0.2)) * 1.2`，取 `min(a_pid, a_stop)`。

### 7.2 横向
- 前进段：Pure Pursuit，`Ld = clamp(1.5 + 0.6*|v|, 2.0, 6.0)`，`δ = atan(2*wheelbase*sin(α)/Ld)`，α 为车头向与 lookahead 点连线夹角。
- 倒车段：交叉误差 PD（更稳，默认法），`δ = -(kx*e_ct + kh*e_hdg)`，`kx=0.06 rad/m`、`kh=1.2 rad/rad`；`e_ct` 为到路径的带符号横向偏差，`e_hdg` 为航向与路径切向差（倒车时按 180° 翻转计算）。
- 备选倒车法：反向 Pure Pursuit（lookahead 取行进方向**后方**的点，输出取反），设置项 `ReverseLateralLaw` 可切换，便于实车调参。
- 映射：`steer_norm = clamp(tan(δ)/tan(maxSteerRad), -1, 1)`，直写 `steering` 通道。
- 限幅与平滑：`|Δsteer_norm|/tick ≤ 0.08`（低速下防摆头）。

### 7.3 换挡状态机（`GearHold`）
```
条件：当前段方向 != 已验证挡位方向
1) 刹停：abackward=0.5，直到 |v|<0.2 m/s（超时 5 s → 中止）
2) 保持 200 ms（等游戏结算）
3) 脉冲：TrueToToggle，前进→{geardrive=true}，倒车→{gearreverse=true}
4) 验证：轮询 truckInt.gear 符号匹配，超时 1500 ms
5) 失败重试，最多 3 次；仍失败 → Abort("挡位未响应")
6) 成功后 300 ms 内保持轻刹，再放开进入新段
```
完成时 `HoldingBrake`：`parkingbrake=true`（Direct）持续发送，2 s 后检查 `truckBool.parkingBrake` 为真；可选 `gear0` 脉冲（自动挡可能无效，失败不报错）。`EngineOffOnFinish` 默认 false。

### 7.4 通道规划（`ControlOutput`）
| 通道 Id | 字段 | 类型 | Weight | Timeout |
|---|---|---|---|---|
| `local.autoparking.drive` | `steering`,`aforward`,`abackward` | 浮点 | 5.0 | 0.3 |
| `local.autoparking.gear` | `geardrive`/`gearreverse`/`gear0` | TrueToToggle | 5.0 | 0.5 |
| `local.autoparking.hold` | `parkingbrake` | Direct | 5.0 | 0.5 |
| `local.autoparking.signal` | `lblinker`/`rblinker`/`flasher4way` | TrueToToggle | 3.0 | 0.6 |

- 60 Hz 每 tick 重发（通道 0.2 s 超时）。
- Weight 用 5.0：ACC 默认权重 1.0，加权平均后我们占绝对多数；但**更主要的隔离是靠 `EnableAssists=false`**。
- 释放：`new ControlEvent{ChannelDefinition=def, Properties=new ControlProperties(), Variables=new ControlVariables()}`（空 Variables 即删除通道）。

### 7.5 与 ACC 的接管/交还
进入：快照 `(EnableAssists, DesiredSpeed, DrivingMode)` → `EnableAssists=false`。
退出：仅当退出时 `(EnableAssists, DesiredSpeed)` 与进入快照相同（即用户期间没手动改过）才回写，否则尊重用户值。
全程不写 `DesiredSpeed`。不依赖 ACC/Pathfinding，也不声明插件依赖。

---

## 8. 安全与中止矩阵

| # | 条件 | 检测来源 | 动作 |
|---|---|---|---|
| 1 | 遥测过期 >0.5 s / `!sdkActive` / `paused` / `!IsGameRunning` | snapshot timestamp | 立即刹停 + Abort |
| 2 | ~~人类接管~~ | — | **整条已删除（2026-10-02，见 §17）**：方向盘、油门、刹车三个通道都不再触发中止 |
| 3 | 横向偏差 `|e_ct| > MaxCrossError`(2.0 m) | Follower | Abort |
| 4 | 超速 `|v| > 段限速 + 1.0 m/s`（下坡失控等） | signedSpeed | 紧急制动 `abackward=1.0`，1 s 后仍超速 → Abort |
| 5 | 速度未按预期变化（发了 >0.4 油门 2 s 而 `|v|<0.05`） | Follower | Abort("被卡住/无动力") |
| 6 | 挡位验证失败（3 次） | truckInt.gear | Abort |
| 7 | 走廊新障碍持续 >8 s | ObstacleCheck | Abort |
| 8 | 总时长 > `MaxDurationS`(180) | 计时 | Abort |
| 9 | 路径被外部改变（重规划引用变化且非用户请求） | Follower | Abort("路径已失效") |
| 10 | 插件 `OnDisable` / `Shutdown` | 宿主 | 释放通道 + 恢复 assists 快照 + 退订 |
| 11 | `DryRun` 开启 | 设置 | 只算不发控制（但状态机/显示全部照常跑） |
| 12 | 控制完全无效（发指令后 `gameSteer`/`gameThrottle` 毫无变化 >3 s） | 诊断 | Abort("SDK 输出未生效，请在设置里重装 SDK") |

**原则**：任何不确定 → 刹停 → 保持 3 s 制动 → 释放通道 → 交还 → 明确通知原因。**绝不"猜着继续"**。第 12 条是针对 §2.2 那个 Windows 双内存路径风险的运行时诊断。

---

## 9. 设置项（`AutoParkingSettings`，`SettingsVersion=1`）

| 键 | 默认 | 范围 | UI |
|---|---|---|---|
| `Enabled` | true | — | Switch |
| `DryRun` | **true** | — | Switch（首个版本默认只演不发，安全） |
| `RefuseWithTrailer` | true | — | Switch |
| `MaxTakeoverDistanceM` | 30 | 5–100 | Slider |
| `EntryDistanceM` | 12 | 6–30 | Slider |
| `OvershootM` | 0.5 | 0–2 | Slider |
| `WheelbaseM` | 4.0 | 2.5–7.5 | Slider |
| `MaxSteerDeg` | 33 | 20–45 | Slider |
| `AutoCalibrateRadius` | true | — | Switch |
| `ForwardSpeedKph` | 6 | 2–15 | Slider |
| `ReverseSpeedKph` | 3 | 1–8 | Slider |
| `ComfortDecel` | 0.5 | 0.2–1.5 | Slider |
| `PidKp/Ki/Kd` | 0.35 / 0.05 / 0.02 | 0–2 | 3×Slider |
| `ReverseKxCross` | 0.06 | 0.01–0.3 | Slider |
| `ReverseKhHeading` | 1.2 | 0.2–4.0 | Slider |
| `ReverseLateralLaw` | `Pd` | Pd/PurePursuit | Dropdown |
| `GearSwitchPenaltyM` | 6 | 0–20 | Slider |
| `ObstacleMarginM` | 0.5 | 0.2–1.5 | Slider |
| `ObstacleWaitS` | 8 | 2–30 | Slider |
| `MaxCrossErrorM` | 2.0 | 0.5–5 | Slider |
| `ToleranceLateralM` / `ToleranceHeadingDeg` | 0.20 / 4 | — | 2×Slider |
| `MaxDurationS` | 180 | 30–600 | Slider |
| `HandbrakeOnFinish` / `HazardOnFinish` / `EngineOffOnFinish` | true / false / false | — | 3×Switch |
| `ShowMapWindow` / `ShowArOverlay` | true / true | — | 2×Switch |
| `MapScalePxPerM` | 1.25 | 0.5–3 | Slider（基准比例） |
| `MapZoom` | 1.0 | 1.0–4.0 | 运行时滚轮改，写回设置；有效比例 = `MapScalePxPerM * MapZoom`，上限 6 px/m |
| `MapViewRadiusM` | 120 | 20–200 | Slider（地图数据抓取半径，取它与可视范围的较大者） |
| `SnapToNavCurve` | true | — | Switch（选点时吸附最近建筑导航曲线方向） |
| `RestoreAssistsOnFinish` | true | — | Switch |
| ~~`UserOverrideEnabled` + 3 个阈值~~ | — | — | 随"人类接管"中止条件一起删除（§17） |
| `StartKeybindId` / `AbortKeybindId` | 固定 Id，默认无键 | — | 在 ETS2LA 控制页绑定 |

`HandleAction(string id, object? value)` 统一分派（照抄 `SpeedLimitUnlocker\Plugin.cs:145-179` 的写法：改值 → `settingsHandler.Save` → 必要时重算），`RegisterListener` 处理外部改文件的热更新并 clamp。

---

## 10. 可视化规格

### 10.1 `MapOverlay`（ImGui 平面地图）
- `WindowDefinition{Title="AutoParking Map", Width=560, Height=560, X=24, Y=180, Alpha=0.85, Flags=None}`，`SizingFunction` 不用（固定尺寸便于屏幕↔世界换算）。
- 比例关系：有效比例 `pxPerM = MapScalePxPerM * MapZoom`（上限 6 px/m）；可视半径 = `560 / (2 * pxPerM)` 米（默认 1.25×1.0 ⇒ 可见 ±224 m）；数据抓取半径 = `max(MapViewRadiusM, 可视半径 + 20)`。屏幕↔世界用同一套公式正反算（照 `InternalVisualization\Utils.cs:8-14` 的约定）。
- 图层：① 道路车道中心线（左 `0.6,0.4,0.4` / 右 `0.4,0.6,0.4`）② 附近 prefab nav curves `0.5,0.5,0.4` ③ 自车矩形（绿）+ 前向射线 ④ 当前车位矩形（黄=未锁定 / 青=已锁定，按设置的长宽绘制；v1 **不做**车位自动识别）⑤ 规划路径（前进青/倒车橙）+ 换挡点白圆 ⑥ 障碍 OBB（红，来自 `GetCornersOnGround`）⑦ 网格与比例尺。
- 交互：`IsOverlayFocused` 为真时才接受鼠标；否则显示 `Hold RightAlt (ETS2LA.Overlay.Interact) to click` 的醒目提示行。左键放下/移动目标，拖动改朝向，滚轮缩放（1.0–4.0×），右键清除目标。按钮：`Plan`（重算）、`Start`、`Stop`、`Clear`。
- 地图数据获取：道路/prefab 的**全量 LINQ 过滤放在 1 Hz 级别的处理里**，渲染回调只用缓存；`Nodes.Within` 用上面的抓取半径。地图未解析时窗口显示 "Map not parsed yet"。
- 执行期：窗口跟着自车（不再居中于目标），画已走轨迹（灰）+ 剩余路径。

### 10.2 `ArOverlay`（`ARRenderCallback`，Name=`local.autoparking.ar`）
- 目标位姿：地面矩形（`Draw3DPolygon` 4 角，`filled=false`）+ 中心十字 + 1.2 m 立柱 + 车头方向短线；`Draw3DText` 显示 `距离/航向差`。
- 路径：按段分色的地面折线（`Draw3DLine` 逐点，`thickness=2`），每 5 m 一个刻度点。
- 预热点（Tier-2 时）：青色圆圈 `Draw3DCircle(center, 0.8, ...)`。
- 执行期：车身预测矩形 + 当前交叉误差连线 + `剩余 X m / 挡位 D-R / 相位`；障碍红框。
- 全部 `ARCoordinate` 用 `Center=World`（`implicit` 转换即可），颜色一律 `ImGui.GetColorU32(new Vector4(...))`。
- 因 AR 有 `MaxARDistance` 裁剪，窗口内的文字状态是权威信息源，AR 只是增强。

### 10.3 `SettingsPage.razor`
`@page "/plugins/adjustments/local.autoparking"`，`@using ETS2LA.UI.Shared.Components`，`@implements IDisposable`，`PeriodicTimer(500ms) → InvokeAsync(StateHasChanged)`（与三个样例一致）。
分区：`总览`（Enabled / **DryRun 大字号提示** / 前置校验结果）→ `几何与标定`（含当前标定 R_min 显示）→ `规划`→ `控制`→ `安全`→ `可视化`→ `快捷键`（说明在 ETS2LA 控制页绑定）→ `状态`表：
`相位 / 目标 / 剩余距离 / 横向误差 / 航向误差 / 挡位 / 有无挂车 / 障碍数 / 上次中止原因 / 输出诊断（通道是否生效）`。
只有 `Switch/Slider/Dropdown/Separator/Tooltip` 可用（`ETS2LA.UI\Shared\Components\`），按钮用 `class="button button-accent w-full!"`。

---

## 11. 文件清单

| 文件 | 职责 | 预估 |
|---|---|---|
| `AutoParking.csproj` | Razor SDK、net10.0、引用 `current\` 下 `ETS2LA.{Shared,Backend,Game,Settings,State,Overlay,Logging,Notifications,UI}`、`Hexa.NET.ImGui`、`TruckLib`、`TruckLib.Models`、`Microsoft.AspNetCore.Components[.Web]`，全部 `Private=false` | 70 |
| `NuGet.Config` | `<clear/>` 离线 | 8 |
| `.gitignore` | `bin/ obj/` | 5 |
| `AutoParkingPlugin.cs` | `Plugin` 子类：`Info`/`TickRate=60`/`OnEnable`/`Tick`/`OnDisable`/`Shutdown`、`Instance`、相位编排、`StartParking/Abort` | 340 |
| `Settings.cs` | `AutoParkingSettings` + clamp/迁移 + `HandleAction` + `StatusRows` | 260 |
| `Geometry.cs` | `Pose2`、yaw 解析、`signedSpeed`、角度归一、OBB+SAT、路径采样点查询 | 200 |
| `Kinematics.cs` | R_min 计算与自动标定 | 120 |
| `Driving\ReedsShepp.cs` | RS 闭式解各族 + 代价比较 | 260 |
| `Driving\Planner.cs` | Tier0/1/2 分层、走廊检查、`ParkingPath` 组装、拒绝原因 | 220 |
| `Driving\ObstacleCheck.cs` | 障碍快照、SAT 走廊检查、预算降级 | 130 |
| `Driving\Follower.cs` | 纵横向控制 + 换挡状态机 + 段推进 | 300 |
| `Driving\ControlOutput.cs` | 通道定义/发布/释放（唯一出口） | 120 |
| `Safety.cs` | 中止条件矩阵、诊断（输出是否生效）、制动保持计时 | 160 |
| `Rendering\MapOverlay.cs` | ImGui 平面地图 + 交互 + 地图数据缓存 | 320 |
| `Rendering\ArOverlay.cs` | AR 回调注册与绘制 | 180 |
| `SettingsPage.razor` | Blazor 参数页 | 200 |
| `README.md` | 安装、使用、限制、调参指引 | 90 |

合计约 3100 行。全部放在 `ThirdPartyPlugins\AutoParking\`，满足 `build_all.ps1` 的 depth-1 约束。

---

## 12. 实施计划（里程碑，按序执行，每个都产出可编译 DLL）

### M0 — 工作区骨架（可编译、可加载）
1. `AutoParking.csproj` + `NuGet.Config` + `.gitignore`。
2. `AutoParkingPlugin.cs`：`Info`（Id=`local.autoparking`, `SupportedETS2LA=">=2026.8.4900"`, Tags）、`TickRate=60`、`Instance`、空 `Tick`。
3. `Settings.cs` 全量字段 + 读写 + `HandleAction` 骨架。
4. `SettingsPage.razor`：先只放 Enabled/DryRun + 状态表。
5. `dotnet build -c Release` 通过；`build_all.ps1` 能产出并复制到 `current\Plugins\AutoParking.dll`。
- **验收**：编译 0 error；ETS2LA 插件管理器能看到并启用；设置页渲染；日志无 `Error in plugin`。

### M1 — 状态与可视化（不动车）
6. 遥测订阅 + `VehicleSnapshot` + yaw/`signedSpeed`/挡位解析（`Geometry.cs`）。
7. `MapOverlay`：地图数据获取与缓存、道路车道线、prefab nav curves、自车框、缩放、`IsOverlayFocused` 门控与 RightAlt 提示。
8. 选点与拖拽朝向 + 目标车位矩形绘制 + 障碍框（`ObstacleCheck` 读快照）。
9. `ArOverlay`：目标矩形/立柱/文本，路径暂用直线。
- **验收**：进入游戏，地图上地物与真实地形对齐；点选后 AR 里的框落在实际位置；不产生任何控制输出。

### M2 — 规划器（纯计算 + 自检）
10. `Kinematics`（R_min + 标定，标定需实车，先给默认值路径）。
11. `ReedsShepp.cs` 各族闭式解 + 采样器。
12. `Planner.cs` Tier0/1/2 + 代价比较 + 走廊检查 + 拒绝原因。
13. 地图窗口实时叠画规划路径 + `长度/换挡次数/冲突/结论`。
14. **规划自检**：设置页 `Run planner self-test` 按钮，跑 24 组固定起终点（含直线对齐、90° 垂直入库、45° 斜入库、平行库、不可达），断言"解存在性/段数≤4/采样连续（相邻点距离≤0.35 m）/终点位姿误差<1e-3/无 NaN"，结果写 `Logger` + 状态行。
- **验收**：自检 24/24 通过；地图上手工摆的位姿能画出合理路径且无 NaN。

### M3 — 控制输出（Dry-run 下只做闭环演算）
15. `ControlOutput` 通道发布/续发/释放。
16. `Follower` 纵横向 + 换挡状态机 + 段推进（`DryRun` 时不发控制，只显示拟发数值与"虚拟车"沿路径推进的假设位姿）。
17. `Safety` 中止矩阵全部条件 + 输出生效诊断（`gameSteer`/`gameThrottle` 反馈）。
18. ACC 接管/交还。
- **验收**：`DryRun=true`（默认）下相位能推进、状态表数值合理、无异常日志，且**确实一条控制事件都没发**（在 `ControlOutput` 里加 `Logger.Debug` 计数并在状态表显示"已屏蔽输出 N 条"来证明）；M3 到此为止不去关 DryRun，实车验证留到 M4 由你操作。

### M4 — 实车闭环（需你在场）
19. 打开真实输出，先只允许 Tier-0/Tier-2（直线），`ReverseSpeedKph=2`。
20. 逐场景验证：正后方直线倒库 → 斜后方（RS）→ 需要前进调整的（RS 两段）；每场景 3 次。
21. 按实车现象调 `kx/kh/Pid/steer 平滑` 与标定开关。
- **验收**：3 个场景各 3/3 成功、无人工介入、无碰撞、结束手刹生效。

### M5 — 安全注入与交付
22. 注入测试：执行中暂停游戏 / 踩刹车 / 在路径前放车 / 拔 SDK（关游戏）→ 每次都应"刹停→释放→交还→通知原因"。
23. `README.md`（安装、RightAlt 交互说明、DryRun 警告、调参顺序、已知限制）+ 代码走查（`caveman-review` 或自查 diff）。
- **验收**：4 类注入全部安全交还；README 覆盖"我不能验证的部分"。

---

## 13. 验证手段与诚实边界

**本机可验证**：`dotnet build`、规划器纯函数自检（M2 第 14 项）、静态代码走查、通道字段名与 API 签名（编译即校验）。
**本机不可验证**（必须实车，由你执行）：控制是否真生效、`truckFloat.speed` 符号、ImGui 鼠标手感、低速下游戏物理的打滑/挡位延迟、标定值。
**因此**：任何"M4/M5 之前"的完成汇报都不会声称"泊车可用"，只会说"编译通过 + 自检通过 + 待实车验证"。插件首版默认 `DryRun=true`，把风险留到你能观察的时候再打开。

---

## 14. 风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| Windows 下 `steering` 走 modern、油门/挡位走 legacy，两套内存缺一即"半瘫" | 车能动不能转或反之 | §8 第 12 条运行时诊断 + 提示重装 SDK |
| ACC/Pathfinding 同时开着抢方向盘 | 加权平均后互相抵消 | 接管期 `EnableAssists=false` + Weight 5.0 + 交还快照 |
| `truckFloat.speed` 符号未确认 | 倒车时控制反号 | 一律用 `gear` 符号定义 `signedSpeed`，M4 实车确认 |
| 游戏默认 `DataFidelity=Medium` 丢弃 `Service` | 车位自动检测不可行 | v1 不做自动检测；README 说明可手动切 High |
| 首次地图解析耗时长 | 地图窗口空白 | 显示"Map not parsed yet"，不阻塞其它功能 |
| RS 圆弧在 ETS2 物理里不精确（轮胎侧偏/转向不足） | 路径跟踪偏差累积 | 交叉误差 PD + 末端 `ToleranceLateralM` 判定 + 必要时重规划 |
| 自动挡换挡有延迟/游戏拒绝 | 挡位不变导致冲过头 | 换挡后 300 ms 轻刹 + 验证超时重试 + 最多 3 次 |
| 用户在不平地面泊车（坡度） | 松刹后溜车 | 完成时 `parkingbrake=true` 且验证 `truckBool.parkingBrake` |
| ETS2LA API 破坏性变更（`.github\pr-api-compat.yaml` 说明官方会做兼容检查） | 插件加载失败 | `SupportedETS2LA=">=2026.8.4900"` 显式声明；只用公开 API |

---

## 15. 后续版本（挂车）

留接口不留实现：`Planner` 的输入是"位姿 + 运动学模型"抽象（`IKinematicModel`），v2 可插入铰接模型（前向用倒车等价变换、`trailerHeading` 由 `trailers[0].comDouble.worldRotation` 得到）；`Safety` 的挡位/挂车校验已按可扩展点组织。自动摘挂依赖游戏侧 `quickpark` 或视觉对位，届时单独设计。

两条来自 ETS2LA Discord（2026-09-28，delilevente 的自动泊车帖）的备忘，等做挂车时再取用：

- **目标位姿不必让用户在地图上点**：接任务时游戏已经把车位写进存档——`player_job.selected_target`（0 easy / 1 medium / 2 hard / 3 rigid）选出四组 `target_placement_*` 字段中的哪一组持有坐标与朝向。他声称在 12 个存档上零错配，并在两处货场与地图数据对上了（sarajevo hard 167.9° 精确到小数、bordeaux medium 误差 0.0 m）。**代价先记清楚**：`SourceCode` 里没有任何读 `game.sii` 的代码（`game.sii` / `player_job` / `selected_target` / `target_placement` 四个词全库零命中），所以这条要自带存档定位与解析，而且要处理"读到的可能是上一次落盘的旧值"。
- **验收判据用游戏自己的信号，别自己造容差**：挂车到位时游戏会往 HUD 通知队列里塞 `@@unload_load@@`，是活的（非事后）状态且语言无关（拿到的是未解析模板而不是译文）。他另外追过 detach 按钮的完整代码路径：**里面根本没有泊车检查**，只有速度门 + 两个 map trigger flag + 状态机。也就是说"歪多少就不认"这件事是队列信号决定的，不是我们设的 `ToleranceLateralM` / `ToleranceHeadingDeg` 决定的——那两个数现在是我拍的，届时应当换成实测阈值。

---

## 16. 决策记录（本仓库内已定）

1. 只泊车头刚体，带挂车拒绝 — 用户指定。
2. 接管距离 ≤30 m，不做长距离导航，不依赖 ACC/Pathfinding 插件 — 设计讨论。
3. 规划器：Reeds-Shepp 为主 + 直线/Tier-2 两段式退化，不用 Hybrid A*。
4. 跟踪：Pure Pursuit（前进）+ 交叉误差 PD（倒车）+ 加速度域 PID（纵向，参数源自 V2 ACC）。
5. 选点 UI：ImGui 悬浮平面地图（仿 internal visualization）；参数 UI：Blazor 设置页。
6. 首版 `DryRun=true`。
7. 插件 Id `local.autoparking`，命名空间 `AutoParking`，产物 `AutoParking.dll`。
8. 不修改 `SourceCode` 任何文件（且官方 AGENTS.md 明确拒绝 agent 产生的 PR）。

---

## 17. 实现记录与偏差（随里程碑更新）

### M0（已完成，编译 0 警告 0 错误，DLL 已装入 `current\Plugins\`）
- 偏差：`TickRate` 用 20 而非 60 —— M0 没有 60 Hz 控制环，**M3 控制环落地时改为 60**。
- 偏差：设置页只显示 M0 用到的开关 + 1 个滑块 + Start/Stop，其余 §9 参数随各自里程碑出现（不给不存在的功能做 UI）。
- 全局副作用：`OnEnable` 把 `GameTelemetry.ReadTrailerData` 置 true（状态表要读挂车），`OnDisable` 恢复原值。

### M1（编译通过并装入；视觉效果待你在游戏里核对）
- 新增：`Geometry.cs`（Pose2/yaw/有符号速度/OBB 顶点/矩形）、`Rendering\MapGeometry.cs`（地图几何快照与构建器 + 曲线吸附）、`Rendering\MapOverlay.cs`、`Rendering\ArOverlay.cs`、`Driving\ObstacleScanner.cs`。
- 偏差：地图缩放用窗口内 `+ / -` 按钮，**没做滚轮**——`ImGui.GetIO().MouseWheel` 在 Hexa 绑定里的取法不确定，不值得为它冒风险；`MapZoom` 仍写回设置。
- 偏差：`WindowDefinition.Width/Height/X/Y` 是 `Optional<int>`（不是 float），窗口固定 560×560。
- 需要的额外程序集引用：`TruckLib.Core.dll`（`IFileSystem`/`Token`/`IBinarySerializable` 都在它里面，`TruckLib.dll`/`TruckLib.HashFs.dll` 里没有）与 `TruckLib.HashFs.dll`。
- 颜色打包两套不同：ImGui DrawList 用 **ABGR**（`r | g<<8 | b<<16 | a<<24`），AR 的 `Draw3D*` 用 **ARGB**（`AR.cs:269` 的 `ConvertColor` 会翻转）。两个 overlay 各自用了独立的打包函数，且避免在静态构造期调用 `ImGui.GetColorU32`（那时 ImGui 上下文可能还没建）。
- 地图几何只在 `ShowMapWindow` 为真时按 500 ms 节流重建；`ParsedRoad` 按 Road 对象缓存（上限 4000 后清空），`GetPpdFile` 自身已有缓存。
- 遗留到 M3：`signedSpeed = |speed| * Sign(gear)` 在空挡滑行时退化为 0。M3 需要真实速度符号，改为「速度向量与车前向点积」兜底，`gear` 符号只作为第一判据。

### 尚未验证（必须实车）
地图地物是否与真实地形对齐、AR 框落点、点击/拖拽手感、RightAlt 交互门控是否如预期。

### 实车反馈修正 1：AR 垂直方向（用户截图）
**现象**：车位框平着浮在车顶高度（约 1.2 m），不是贴地。

**根因**：`truckPlacement.coordinate.Y` 是**车辆原点**高度，不是接地面高度，且小车/卡车差别很大。我最初直接把它当地面 Y，等于整个 AR 图层抬高了一个原点高度。

**修正**：地面高度改为由车轮反推——
`groundY = truckPlacement.Y + min( truckWheelPositions[i].Y - truckWheelRadius[i] )`，
优先只取 `truckWheelOnGround[i]` 为真的轮子（避开抬起的桥），无任何有效轮时退回 0（旧行为），结果 clamp 到 `[-3, 0]`，并按遥测时间戳缓存。
另加 `ArGroundTrimM`（-1..1，默认 0）做人工微调；AR 里**始终**画一个车底绿色地面十字，配合状态表新增的「地面基准」行，一眼能判断基准面对不对。

**顺带修的**：`VehicleLengthM/VehicleWidthM` 之前在设置模型里有、但设置页没暴露，用小车测试时车位框按卡车 6.5×2.6 画，明显偏大——现已加滑块。

**仍存在的垂直局限（诚实记录）**：车位来自 2D 地图，**本身没有高程**；现在用的是"车辆所在水平面"。平地停车场正确，坡道上会偏。真要准需要地图地形高程，而 `DataFidelity=Medium` 会把 road/prefab 的 Terrain 剥掉（`Data\Classes.cs:104-119`），只有 Extreme 才有——留到后续版本。

**Razor 陷阱**：`Slider` 的 `Min/Max/Step` 是 `float`，写 `Step="0.05"` 会被解析成 double 而报 CS1503；整数形式（`Min="30"`）可以，小数必须写 `Step="@(0.05f)"`。

### M2 — 规划器（已完成，自检 24/24 真跑通过）
新增：`Driving\ReedsShepp.cs`、`Driving\Planner.cs`、`Driving\ParkingPath.cs`、`Driving\PlannerSelfTest.cs`、`Driving\ObstacleSnapshot.cs`、`Kinematics`（在 Planner.cs 内）。

**实现方式的关键决定**：不抄 Reeds-Shepp 闭式公式表（记错一个符号就会给出"看起来对"的路径，且无法自查），而是**按几何构造**：
- CSC 族：由转向圆心 `C = P + s·R·l(θ)` 出发，公切条件化为 `(C2-C1)·l(φ) = R(s2-s1)`，解出切点航向 φ。
- CCC 族：中间圆与两侧反向圆外切 ⇒ 圆心距 2R ⇒ 两个半径 2R 圆的交点。
- 每条候选都用 `EndsAt()` **独立积分一遍**验证"确实落在目标位姿且切向连续"，不通过就丢弃。⇒ 构造写错的后果是"无解"，而不是"给出一条会撞路缘的路径"。

**倒车解**来自两条途径，而不是实现带负长度的 RS 族：
1. `Invert()`：把 `goal→start` 的前向路径整体反向遍历（车身航向不变、行进方向相反）。
2. Tier-2 预热点：前进到 `goal + forward·EntryDistance` 后直线倒库。

**自检抓到的两个真缺陷**（不是测试噪声）：
1. `PlanResult.Ok` 原本只表示"有路径"，含冲突也算 Ok ⇒ 语义改名成 `Path != null && ConflictCount == 0`，预览仍可读 `Path`。
2. 起点≈终点时给出了 24 m 的无意义机动 ⇒ 加"车已经在车位里了"短路（用 `ToleranceLateralM` / `ToleranceHeadingDeg` 判）。

**自检的强度问题（重要教训）**：24/24 只证明**路径有效**，不证明**路径合理**。我一度以为构造有 bug，因为"正侧方 8 m 的垂直车位"给出 35.5 m、"斜后方"给出 42.5 m。用探针列出全部候选后确认：以 R=6.16 m 的卡车半径，这类位姿**本来就必须绕大圈**——真实卡车司机也是这么倒的，而 v1 明确不做多点修正。真正缺的是断言，所以补了一条通用规则：**路径长度不得超过"直线距离 + 一整圈 2πR"**（多绕一整圈正是"该走短弧却绕满圆"这类回归的特征），比逐 case 手写魔数稳定。

**已知局限（诚实记录）**：
- 未实现混合方向的 RS 族（LRSR/LRSL 及带负长度段），所以"正侧方车位"这类只能给大圈解或拒解并提示重新选位。
- 侧方停车（平行库）在 R≈6 m 下往往超出长度闸门，v1 不保证可泊。
- `ObstacleSnapshot` 拆成独立文件，使规划器与自检完全不依赖 ETS2LA，可在无游戏环境下编译运行。

**本地验证方式**（不需要游戏）：`Tools\PlannerHarness\` 用 `<Compile Include>` 链接纯数学子集，`dotnet run --project Tools/PlannerHarness` 打印自检结果与若干典型路径的形状/长度/挡位切换。它原来在仓库外，2026-10-03 挪了进来（见 §23）。

### M3 — 控制层（已完成，闭环仿真 5/5 到位 + 规划自检 24/24）
新增：`Driving\Follower.cs`（`VehicleState`/`ControlDemand` 纯数据 + 横纵向控制 + 换挡状态机）、`Driving\ControlOutput.cs`（唯一发控制的地方）。插件侧接了：60 Hz Tick、接管/交还辅助、快捷键 `local.autoparking.Start` / `.Abort`、`OnDisable` 强制释放通道并恢复辅助、状态表新增「控制器 / 拟发输出 / 通道计数」。

**架构决定**：`Follower` 只吃 `VehicleState`、只吐 `ControlDemand`，完全不引用 ETS2LA 类型 ⇒ 可以在无游戏环境下用运动学自行车模型闭环驱动它。这是本里程碑能真验证的原因。

**闭环仿真抓到的 5 个真 bug（都是"编译通过但车不动/乱动"那一类）**：
1. **到点减速律把车锁死**：`command = Math.Min(command, -v²/(2d))`，在 v≈0 时该式为 ≈0，于是油门永远被压成 ≤0 → 车永远不动 → v 永远≈0。修法：只在 `v>0.05 且 距终点<6 m` 时才套用该下限。
2. **起步参考速度太小**：弧长爬升 `sqrt(2a·s)` 在 s=0 给出 ≈0，参考被夹到 0.15 m/s，PID 只给 0.05 m/s²，连滚动阻力都顶不过。修法：参考速度下限抬到 0.25 m/s + 新增 `LaunchAccelMps2`（默认 0.6）在"该动却没动"时直接给起步量。
3. **从 V2 照搬的抗积分规则在泊车场景是致命的**：`speed < 0.5 m/s 清积分`——泊车本来就是蠕速，积分永远建立不起来。改成 `< 0.05` 才清。V2 那条是为高速 ACC 写的，移植时必须换量纲思考。
4. **倒车转向符号**：交叉误差 PD 在紧密弧线上被航向项主导（`kh=1.2` vs `kx·e`≈0.1，约 20:1），车只追航向不追位置 → 满舵发散。改用反向 pure pursuit 后仍需把输出取负：航向率 `θ̇ = v·tanδ/L`，倒车时 v<0 ⇒ 同样瞄准误差需要相反的方向盘指令。参数扫描（kx 跨 10 倍结果不变）是判定"结构性符号错而非调参问题"的关键证据。
5. **进度投影冻结**：为防止路径自交导致进度跳变，我加了"欧氏距离 > 3 m 就拒绝"的闸门，结果在自交弧线上把投影**冻住**，进度不再前进、横向误差单调涨到触发中止。改成按"弧长推进速率上限"约束（`|v|·dt + 0.5`），既能前进又不会瞬移。
6. （显示）`{reference * 3.6:0.1}` 是**非法自定义格式串**（应为 `0.0`），渲染出"11 km/h"的假象，一度把排查带偏。

**判据修正**：泊车的横向 0.4 m 不可接受，纵向差 0.4 m 无所谓，所以闭环仿真与到位判定都拆成 `横向 ≤0.25 m / 航向 ≤8° / 纵向 ≤0.6 m`，不再用单一标量距离。

**默认值变更**：`ReverseLateral` 默认从 `CrossTrackPd` 改为 `ReversePurePursuit`（PD 保留为可选项，设置页可切）。

**仿真不能验证的（M4 必查）**：游戏侧 `steering` 正极性是否等于我们的"左"、油门/刹车的实际响应与挡位接合延迟、轮胎打滑、以及 `steering` 走 modern 内存而油门/挡位走 legacy 内存的双通道是否都通。M4 第一步应在空旷场地用 Dry-run 对照「拟发输出」与实际车轮反应，确认极性后再放开。


### 关键使用约束（用户指出）：必须用热键，不能用按钮
overlay（ETS2LA 窗口）和游戏是**两个应用**，点 overlay 上的按钮会让游戏失焦，而 **SCS 的虚拟手柄在游戏失焦时不接收输入** ⇒ 用按钮启动泊车，车根本不会动。
`ETS2LA.Controls.ControlsBackend` 是全局键盘轮询（Windows 走 SharpDX），游戏有焦点时照样能收到，这也是 ETS2LA 自带 Cancel 键能用的原因。

因此控制层改成两个热键（默认未绑定，在 ETS2LA「控制」页绑定）：
- `local.autoparking.Toggle`：**一个键管三态** —— 空闲→开始；运行中→暂停并拉手刹；已暂停→继续。
- `local.autoparking.Abort`：立即刹停、释放通道、交还辅助驾驶。

配套实现要点：
- 新增 `ParkingPhase.Paused`。暂停时 `output.Release()` 后只持续重申手刹，不再跑 `Follower.Step`。
- `Follower` 在恢复时会遇到一个巨大的调用间隔：若 `gap > 1 s` 就重置卡死看门狗、PID 积分与微分，否则恢复瞬间就会误触发"给了油门没动"或微分冲击。
- 暂停中的任务不允许被 Start 覆盖（提示先继续或中止）；`Abort` 允许从 Paused 状态触发。
- 设置页/地图窗口的按钮保留，但明确标注"只适合调试"。

### M3 期间踩到的两个自伤型 bug（教训记录）
1. **在持有插件全局锁时做文件 IO**：`SetTarget` 于 23:50 版里在 `lock (sync)` 内、且从 ImGui 渲染线程调用 `SettingsHandler.Save`，而 Save 会回调监听器、UI 线程刷新又要抢同一把锁 ⇒ 界面卡死、点击完全无响应且不写日志。已改为：锁内只置脏标记，由插件 tick 线程在锁外用**快照副本**写文件。
2. **日志里不能用裸方括号**：`ETS2LA.Logging` 用 Spectre.Console 标记语法，`"[AutoParking] ..."` 会被当成 markup 标签**静默吞掉**，排查时一度误以为"没有日志"。改用 `AutoParking: ` 前缀。

另外删掉了设置页里与插件管理重复的 `Enabled` 总开关（两处"启用"是真实误会来源），插件管理成为唯一开关。

### 中止逻辑走查与修复（2026-10-02，`dotnet build -c Release` 通过）

**1. DryRun 与"依赖游戏回应"的判定互相冲突（真 bug）**
`ControlOutput` 在 DryRun 下屏蔽发布 ⇒ 车必然不动，而 Follower 的中止条件是按"车应该会动"写的。结果：倒车段第一步就撞挡位重试上限（约 4 s），即使有前进段也是 3 s 触发失速看门狗、8 s 触发障碍等待超时、180 s 触发总时长。
已按"该判定是否依赖车辆真实响应"逐条门控（`Driving\Follower.cs`）：
- `WaitForGear`：DryRun 下挡位视为已确认。不门控的话任何含倒车段的计划在 `Stage.WaitingForGear` 就死了，这是最早触发的一条。
- `CheckFaults`：引入 `needsResponse = !settings.DryRun`，门控失速看门狗与 `MaxDurationS` 两条（同一处还门控过人工接管，该分支后来整体删除，见第 5 条）。
- `HandleObstacles`：仍显示"路径被挡"并保压，但 DryRun 下不消耗等待预算、不中止。
横向偏差与速度失控两条未门控——车不动时它们本来就不会触发。
**同时**：`StartParking` 的 Dry-run 提示语原来写"看控制器行确认在推进"，是错的。`UpdateProgress` 用真实车位投影，DryRun 下进度停在起点，预览只显示"当前采样点该发什么"。§M3 设想的"虚拟车沿路径推进的假设位姿"**没有实现**，要真演算整条路径仍需离线 harness。
**注意**：Follower 持有的是 `StartParking` 时的 settings 实例，而 `OnSettingsChanged` 会整体替换插件的字段 ⇒ 机动途中改参数对 Follower 无效（`output.DryRun` 却是每 tick 刷新）。这是既有行为，本次未动，但它意味着中止阈值的调整只对下一次启动生效。

**2. 暂停态不监遥测**
`ParkingPhase.Paused` 分支原先只重申手刹、不看遥测，暂停中拔 SDK/关游戏会永远重申一个死通道。已补 `telemetryStale || !sdkActive || paused` → `Abort("遥测中断或游戏已暂停")`（`AutoParkingPlugin.cs` `StepControlLoop`）。`Abort` 的相位守卫本来就允许从 `Paused` 触发，所以 M5 的"暂停 + 拔 SDK"注入现在能走通。

**3. 死枚举与文件清单偏差**
`ParkingPhase.Planned / Aligning / Done` 从未被赋值（只出现在 switch 分支里），已删除，同步清掉 `StepControlLoop`、`HandleToggle`、`Abort` 守卫里的三处分支。
§11 计划独立的 `Safety.cs` 没有落盘：中止条件实际分散在 `Driving\Follower.cs`（`CheckFaults`、挡位重试、障碍等待）和 `AutoParkingPlugin.cs`（`StepManeuver` 遥测守卫、`CheckOutputIsEffective` 输出生效诊断、`Abort`/`CompletePhase` 时序）。当前规模下两处内聚度够，不再为它拆文件——§11 的预估行数本就与实际不符。

**4. 本地验证方式失效**
§M2 记的 harness 目录（那里当时写的是绝对路径，这也是本文档现在一律改写相对路径的原因）在 `D:` 工作树里不存在（顶层只有 `ETS2LA-win-release-Portable`、`SourceCode`、`ThirdPartyPlugin`）。重跑闭环仿真需要重建该 harness，并且**必须显式 `DryRun=false`**，否则上面那批门控会把中止条件全部跳过，等于没测。

**5. "人类接管"中止条件整体删除（2026-10-02，用户要求：先删方向盘，再删油门/刹车）**
`Follower.CheckFaults` 里整个 `UserOverrideEnabled` 分支移除，三个通道（`userSteer` / `userThrottle` / `userBrake`）都不再触发中止。连带清理：
- `VehicleState` 从 7 个字段缩到 5 个（`Utc, Position, HeadingRad, SignedSpeed, Gear`），`UserSteer/UserThrottle/UserBrake` 及其在 `ReadVehicleState` 里的"减掉我们发出去的量"残差计算一起删除。
- `AutoParkingSettings` 删掉 `UserOverrideEnabled`、`UserSteerThreshold`、`UserThrottleThreshold`、`UserBrakeThreshold` 四个属性及其 clamp（它们本来就没有设置页控件，也没有 `HandleAction` 分支）。
- 保留：状态表的「原始输入」行仍然打印 `user=(...)  sent=(...)`。这是唯一能在实车上看出"是谁在动车"的窗口，删掉判定不等于删掉观测。
- §10 条件矩阵第 2 行、§9 参数行已标为删除。

**删除后的安全边界（重要）**：人类介入不再自动中止机动。现在交还控制的途径只剩三条 —— `local.autoparking.Abort` 热键、`Toggle` 热键（暂停并拉手刹）、以及遥测/输出生效两类守卫（`StepManeuver` 的 stale/`!sdkActive`/`paused`、`CheckOutputIsEffective` 的 4 s 无反馈）。也就是说：**踩刹车不再是退出手段**，实车测试时必须先绑定 Abort 热键再动车；宿主侧的加权通道抢占（§16）能不能在人类打方向时压过我们，仍未在实车上验证过。
残留的 `UserSteerThreshold` 等键会在下一次保存 `AutoParking.json` 时自然消失（`System.Text.Json` 默认忽略未知属性），无需手工清理。

**6. 实车现象"速度失控"（2026-10-02）**
先核对单位：`truckFloat.speed` 是 **m/s**，证据是同目录 `OvertakeAssistant\OvertakeAssistantPlugin.cs:25` 的 `MinimumSpeed = 55f / 3.6f` 直接与它比较。所以失控线 `max(ForwardSpeedKph, ReverseSpeedKph)/3.6 + 1.0` 在默认 6 km/h 下等于 2.67 m/s = **9.6 km/h**，离目标速度只有 3.6 km/h 余量——"轻微超速"就是这么来的。

**根因 A：控制律的 dt 取错。** `ReadVehicleState` 拿 `DateTime.UtcNow` 当时间戳，而 Tick 是 60 Hz、遥测到达慢得多 ⇒ 同一帧数据被反复喂进 PID，积分与微分都按 `60Hz/采样率` 的比例放大。车速一掉，微分项就顶出大油门，随后冲过失控线。
修正：`VehicleState.Utc` 改用遥测到达时刻（`lastTelemetryUtc`）；`Longitudinal` 的 dt 上限从 0.05 放宽到 0.25——dt 现在是真实采样间隔，10 Hz 进料不该被折半。重复采样时 dt=0，由 `Math.Clamp` 的 0.005 下限兜住。

**根因 B：判定与 §10 设计不符。** 条件矩阵第 4 行原本要求"超速 → `abackward=1.0` 紧急制动，1 s 后仍超速才 Abort"，M3 落地时写成了**单帧立即中止**，没有防抖。现在补回：`CheckFaults` 在超速时先返回 brake=1.0（不拉手刹）并跳过本帧路径跟踪，持续超过 `OverspeedGraceS`(1 s) 才中止，文案改为"速度 X km/h，刹了 1 s 仍不降到 Y km/h 以下"；回落到 `cap - 0.3` 以下才清零计时，避免尖峰反复重置。宽限期内状态表显示"超速 X km/h，紧急制动"。

**仍需实车确认**：修掉 dt 后过冲到底还剩多少；`MaxBrakeDecel=1.2 m/s²` 映射成的 `abackward=1.0` 能否在 1 s 内把 9.6 km/h 拉回线内。若还在触发，先试 `PidKd=0` 和降 `ForwardSpeedKph`，**不要**继续放宽失控线——那条线是最后的兜底。

**第二轮实车日志（2026-10-02）**：`sent_brake` 从 0.52 一路涨到 1.00 期间，`v` 反而从 8.9 涨到 10.3 km/h（gear=-1），`game_brake` 恒为 0.00。
先纠正探针自己的错误：反射 `ETS2LA.Game.dll` 得到 `ETS2LA.Game.Output.ControlVariables` 就是虚拟控制动作表（`steering / aforward / abackward / clutch / parkingbrake / motorbrake / engbrake* / …`），所以 **`abackward` 字段名没取错**，它就是刹车踏板轴。而 `TruckFloat` 同时有 `user*` 和 `game*` 两套回显——按本文件 M3 的记录，我们注入的量回显在 `user*`，因此 `game_brake=0.00` **不能**证明通道失效，是第一版探针读错了字段。
仍未分辨的是两种可能：(a) 踏板指令根本没到轮端；(b) 到了但authority不足（坡道/空挡滑行/游戏刹车曲线）。为此改了两处：
- 探针改读 `user_brake / user_throttle / airPressure / brakeTemperature / parkingBrake`。气压是"轮端确实施加了制动"的物理证据，比任何回显字段都硬。
- 紧急制动从"只踩踏板"改成 `ControlDemand.Hold(1.0f)`，即**踏板 + 手刹一起**。理由：手刹在泊车完成时已证明能真正停住车，而踏板这一路还没有任何一次实车证据表明它会减速。
一次性反射探针当时写在仓库外的 `scratch\ChannelProbe\`；它是用完即删的，现在那个目录已经不存在了。要再查宿主字段，就照 `Tools\PlannerHarness` 的做法在 `Tools\` 下另起一个临时工程（见 §23：放外面会被 glob 编进 DLL 的说法已经不适用，`Tools\**` 现在被排除）。

**7. 第三轮实车：指令根本没进游戏（2026-10-02）**
日志：`sent_brake=0.50 user_brake=0.00 user_throttle=0.00 air=114.18→116.01 hand=False gear=0` 连续 8 条 → `aborted: 换挡 3 次未成功（目标 Forward）`。
判读：挡位脉冲 3 次无效、手刹没拉起、**气压还在往上涨**（真正施加制动时气压会掉）、`user*` 全 0 ⇒ 我们发出的每一条指令都没到达车辆。§M3 留的未验证项"油门/挡位走 legacy 内存、steering 走 modern 内存，双通道是否都通"有了部分答案：**踏板与挡位这一路没通**。最可能原因就是本文件早就记过的那条——游戏失焦时 SCS 虚拟手柄不接收输入（用 overlay 上的 `Start` 按钮启动必然踩坑，所以才有"必须用热键"的约定）。
**顺带修掉一个自伤 bug**：`CheckOutputIsEffective` 原本用 `game*` 判断输出是否生效，但我们的注入回显在 `user*` ⇒ 这个看门狗**永远不可能触发**，"输出全废"于是被伪装成不相干的"换挡 3 次未成功"。现在改读 `userThrottle/userBrake/userSteer/truckBool.parkingBrake`，窗口从 4 s 缩到 2 s（抢在 4.5 s 的挡位重试之前给出正确结论），中止文案直接点名"失焦或 SDK 未生效"。
**推翻第 6 项的结论**：那次"超速 8.9→10.3 km/h 且刹车无效"不能作为刹车通道失效的证据——同一批通道数据显示输出根本没进游戏，当时车速变化更可能是自行蠕行/倒溜。超速与制动 authority 的判断，等输出真的通了、`user*` 能跟上 `sent` 之后重测再说。


### 自动中止逻辑整体删除（2026-10-02，用户要求："删除所有自动暂停逻辑，除非按下热键"）

**触发这件事的直接原因**：实车正常倒车过程中被自动中止，文案是"指令没有进入游戏（user* 全为 0）"。

**同时推翻了一个前提**：此前（§17 第 5、7 项）我们认定"我们注入的输入会回显进 `truckFloat.user*`"。这次车上正在正常倒车（输出显然是通的），而看门狗读到 `user*` 全 0 ⇒ **`user*` 是纯玩家设备输入，我们注入的量只体现在 `game*`**。那条把看门狗从 `game*` 改到 `user*` 的修正方向是错的，它在一切正常时每 2 s 误报一次。

**删除内容**（`Driving\Follower.cs`、`AutoParkingPlugin.cs`）：
| 原自动中止 | 现在的行为 |
|---|---|
| 挡位 3 次未确认 | 无限次继续请求挡位，状态行显示第几次 |
| 障碍等待超时（`ObstacleWaitS`） | 一直保压等待，走廊一空就走，不设时限 |
| 总时长超 `MaxDurationS` | 删除该设置 |
| 横向偏差超 `MaxCrossErrorM` | 删除该设置，横向误差仍显示在状态表 |
| 遥测中断 / `!sdkActive` / 游戏暂停 | 保压 + 手刹等待恢复，不再中止 |
| 输出 2 s 无回显（`CheckOutputIsEffective`） | 整个方法删除（误报源，且读的是错字段） |
| 失速看门狗（给油门 3 s 不动） | 删除 |
| `ControlDemand.AbortReason` 通道 | 从 record 里删除，`Aborted()` 工厂删除 |

**唯一保留的自动干预**：超速时 `HandleOverspeed` 踩下踏板 + 手刹，**降到上限以下自动恢复跟踪**。它是速度控制而不是暂停——永远不会结束机动。若这条也要删，说一声。

**删除后的安全边界（必须知道）**：交还控制的途径只剩 `local.autoparking.Abort` 热键，以及 `Toggle` 热键（暂停并拉手刹）。踩刹车、打方向、遥测丢失、挡位卡死、被车挡住——**都不会**再自动结束机动，车会一直等在原地。所以：**绑好 Abort 热键之前不要动车**。

**验证**：`dotnet build -c Release` 通过；离线 harness 自检 24/24、闭环 5/5 到位（harness 已去掉对 `AbortReason` 的断言，并显式 `DryRun=false`）。


### 转向线性化（2026-10-02）

**先记录一次被数据推翻的假设**。我以为满舵振荡来自 `Smooth()` 里硬编码的 `0.08/tick`（60 Hz 下 4.8/s，0.21 s 打满舵，等于 bang-bang），于是加了比例增益 / 死区 / **按秒**计算的速率限制，并在 harness 里加了转向质量指标（满舵时长、方向盘换向次数、方向盘总行程）做 A/B。

结果：旧参数 vs 新参数，满舵时长 **95.6 s → 96.8 s**，几乎没变。假设错了。

**真正的原因在规划器**：`R_min = 轴距/tan(最大转角) = 6.16 m`，Reeds-Shepp 用**恰好等于极限**的半径出弧，于是每段弧的曲率都是 `1/R_min`，映射到方向盘正好 = 1.0。**执行器长期饱和 ⇒ 回路不再线性 ⇒ 控制器没有任何修正余量**（这也正是之前倒车段发散、误差单调增长的根因）。

**修法**：规划时用一个放大后的半径 `PlanningRadius = MinTurnRadius × PlanRadiusMargin`（新增设置，默认 **1.35**，范围 1.0–2.5），`Follower` 的曲率上限仍按车辆真实极限，把方向盘余量留给修正。

**效果（同一批 5 条闭环用例）**：
| 指标 | 余量 1.0（旧） | 余量 1.35（新） |
|---|---|---|
| 满舵时长（左侧垂直库） | 95.6 s | **0.0 s** |
| 满舵时长（左后斜入库） | 66.6 s | **0.0 s** |
| 横向到位误差 | 0.00–0.07 m | **0.00–0.02 m** |
| 自检 / 闭环 | 24/24 · 5/5 | 24/24 · 5/5 |

**代价**：路径变长（紧密位姿从 39–45 m 涨到 51–60 m）。这是有意的取舍——长但可控，优于短但饱和。要更短的路线就调小 `PlanRadiusMargin`，代价是方向盘余量。

**顺带修了一条断言的尺子**：§M2 加的"路径不许多绕一整圈（2πR）"用的是 `MinTurnRadius`，加了余量之后必然误判（自检一度掉到 17/24）。改成按 `PlanningRadius` 衡量——比较基准必须跟实际规划用的半径一致。

**新增设置项**：`PlanRadiusMargin`(1.35)、`SteerGain`(1.0)、`SteerDeadband`(0.02)、`SteerRateLimitPerSecond`(1.5)、`LookaheadBaseM`(1.5)、`LookaheadGainMps`(0.6)，全部在设置页「转向与规划余量」一节。


### 严重 bug：刹车指令被宿主换算成油门（2026-10-02，实车日志确认）

**现象**：`engaged: RS RSL · 36.5 m`，随后车速从 0 一路涨到 **50.3 km/h**，而状态一直是 `sent_brake=1.00`（我们在全程踩刹车），最后靠人工按暂停热键才停下。

**对应关系是决定性的**：`sent_brake=0.50 → user_throttle=0.24`、`0.72 → 0.32`、`1.00 → 0.50`。永远是"一半"。

**根因**：`GameOutput` 把 `aforward` 与 `abackward` 折进同一个 `acceleration` 桶做**加权平均**，再按符号分派：正→`aforward=值`，负→`abackward=-值`（`Output.cs:284-318`）。我们两个字段都发（`aforward=0`、`abackward=1.0`）⇒ 平均 = +0.5 ⇒ 宿主写的是 **`aforward=0.5`**，即半油门。越"踩刹车"越加速。

**修法**（`Driving\ControlOutput.cs`）：算出带符号的 `acceleration = throttle - brake`，**只发一个字段**——非负发 `aforward`，负值发 `abackward`（宿主自己取反）。这样与 ACC 等其它通道混算也正确：ACC `aforward=0.3`(w=1) 与我们 `abackward=-1`(w=5) 平均 = -0.78 ⇒ 刹车 0.78。

**顺带纠正本文档先前两条错误结论**：
- §17 第 6 项"超速且刹车无效、刹车 authority 不足"——不是 authority 问题，是符号问题，刹车根本没生效过。
- §17 第 7 项"指令根本没进游戏（`user_brake=0`、气压还在涨）"——指令进了，只是变成了油门；`user_brake=0` 是必然的，因为宿主写的是 `aforward`。当时据此删掉的那批自动中止，其前提部分失效。
- 探针也补了 `sent_accel`（带符号实际发布值），下次一眼能验证映射方向。

**遗留安全事实**：这轮失控能冲到 50 km/h 而不停，是因为上一轮按要求删除了全部自动中止（含超速中止），最后靠人工热键救场。目前唯一的自动干预是 `HandleOverspeed` 的"踏板+手刹"，但它当时发的是被换算成油门的方向——修好符号后它才真正具备减速能力，是否再给它一个兜底中止由用户决定。


### 符号修好之后"不动了"：卡在等挡位（2026-10-03，实车日志确认）

**日志**（`current\ets2la.log`，00:01–00:05 两轮）：

```
制动探针 v=0.0 km/h sent_accel=-0.50 (brake=0.50 throttle=0.00) user_brake=0.50 user_throttle=0.00 air=114→123 hand=False gear=0
```

**好消息**：上一节的符号修正是对的。`sent_accel=-0.50 → user_brake=0.50、user_throttle=0.00`——刹车终于作为刹车进了游戏，不再变成半油门。

**坏消息**：这条探针连续出现 60+ 次、`gear` 始终 0，中间只有 `paused/resumed by hotkey`。也就是说追踪器一直停在换挡状态机里踩着刹车等 `truckInt.gear` 变负数，而挡位从来没变过。"不动"不是纵向控制的问题，是**挡位确认永远不成立**。

**先排除掉一个我怀疑过的原因**：会不会是布尔动作在共享内存里偏移错位（`legacyShmOffsets` 按 `ControlVariables` 声明顺序累加，bool 只算 1 字节）。核对办法是把 `scs_sdk_controller.dll` 里的动作名表抽出来与 `SourceCode\ETS2LA.Game\Output\Classes.cs` 的字段顺序逐个比对：**276 个字段，顺序完全一致，0 处不符**。偏移不是问题。（顺带：`steering/aforward/abackward` 是第 71–73 个字段，`parkingbrake` 第 118，`gear0/geardrive/gearreverse` 第 202–204； pedal 能生效本身就说明前面 70 个字段的累加是对的。）

**剩下的三个候选，按代价排序**：
1. **换挡脉冲写法与可用的参考实现不一致**。官方示例 `SequentialAutoShift\PulseShift()` 每个脉冲只写 `gearup=up, geardown=!up`（两个字段，一真一假）。我们写 `geardrive=false, gearreverse=true, gear0=false`——三个字段里两个 false 写在同一次事件里。false 落在别的偏移上，理论上无害，但这是与"已知能用"的实现唯一的差别，所以先改成同样只带一个动作字段的形式（`ControlOutput.PublishGear`）。
2. **等挡位期间手刹是拉着的**。`ControlDemand.Hold()` 第四个参数是 `HoldBrake=true`，而换挡等待用的正是 `Hold(0.5)`——即"行车制动+驻车制动"。若 ETS2 在驻车制动生效时拒绝接合 D/R，就永远确认不了。（注意 `hand=False`：驻车制动的布尔写入也没在遥测里体现，这本身是另一条待查线索。）新增 `ControlDemand.BrakeOnly()`，换挡等待只踩行车踏板。
3. **这辆车可能根本不认 `gear_drive/gear_reverse`**。该动作只对 H 挡自动箱存在；顺序箱/手动箱要靠 `gearup/geardown`。现在 `shifter_type` 进了状态表和探针日志，一眼可见。

**把"等不到"从死路改成降级**（`Driving\Follower.cs`）：脉冲每秒重发一次，最多 4 次；之后**按请求方向继续行驶**，并在行驶段每 1 s 补发一次，直到遥测真的出现对应符号。理由：上一轮按要求删掉了全部自动中止，"永远在等"等于把插件变成一个只能靠热键解除的刹车——这不可接受。继续补发是无害的：游戏什么时候接合，车就什么时候动。

**新增证据通道**：
- `挡位探针`（每次发脉冲记一行）：`请求 → gear/dash/slot/shifter_type/rpm/hand/v`。
- 状态表「人工输入」行尾部改成 `gear=实际/仪表 shifter=变速箱类型`。
- 设置页新增「指令通路自检」两个按钮：**测试挂 D / 测试挂 R**（未启用车库且非 Dry-run 时可按）。油门刹车走模拟轴、换挡手刹走布尔动作，是两条不同代码路径；这两个按钮能在 2 秒内回答"布尔动作到底进不进得去游戏"，不必跑完整个泊车位。

**离线回归**：`scratch\PlannerHarness`（2026-10-03 起改在仓库内 `Tools\PlannerHarness`）加了 `gearboxDeaf` 仿真（脉冲永远不接合）。结果：自检 24/24、闭环 5/5 不变；失聪挡箱用例换挡脉冲 296 次、正常进入行驶段（不再是踩着刹车等到超时）。构建 0 错误，DLL 已更新到 `current\Plugins\AutoParking.dll`（00:26），**需要重启 ETS2LA 才生效**。


### 末端停车角度：一个症状，四个独立缺陷（2026-10-03，离线闭环定位）

**症状**（用户截图）：入库后车头角度偏。离线仿真早就复现了同一件事——硬用例终端航向误差 3.6–4.3°，而横向误差只有 0.01–0.02 m。**位置对、角度不对**，这个组合本身就是线索。

**判据先立**：把 `ClosedLoop.reached` 的航向门限从 8° 收到 1°，基线立刻 2/5。随后 5 个用例分成两类：全直线路线（`直线倒车入库`、`正前方同向`）= 0.0°；尾段是圆弧的 = 3.6–4.3°。分界干净，说明不是转向律增益问题。

**逐 tick 证据**（末段，剩余 0.5 m）：`航向差=0.46° 横=-0.018 steer=0.78 thr 在 0.6/0.0 之间抖`。也就是说**车已经正对路径切向、也压在路径上，方向盘却还打在 78%**，然后在 剩=0.25 m 处被判完成、停在圆弧上。

四个缺陷按发现顺序：

1. **完成判据只看弧长**（`RemainingDistance <= max(0.25, ToleranceLateralM)`）：把"横向容差"当"纵向停止容差"用。停在圆弧上少走的每一分弧长，都原封不动变成航向误差（R=8.3 m 时 0.35 m ≈ 2.4°）。
   **修法**：给 Reeds-Shepp 两层加**直线入位尾段**——规划目标改成"位姿同向、退让 `TerminalStraightM` 的预备点"，再接一段直线进库（`Planner.Standoff` / `AppendStraightTail`）。这段里任意一点停下都是正的角度。尾段 0 → 1.5 m：最大航向误差 **3.64° → 1.41°**。
   扫参（3 个硬用例，最大/平均航向误差）：`0.0 → 3.64/3.39`、`1.0 → 1.98/0.82`、`1.5 → 1.41/0.81`、`≥2.0` 撞见缺陷 4 的爬行所以数字失真。默认取 **1.5 m**。
2. **前视点被夹到路径终点**：`alpha` 量到的是被 clamp 后很近的点，除的却是没 clamp 的 `lookahead`——末端命令被放大，而且 pure pursuit 追一个"就在终点线上"的点会斜着切进去。
   **修法**：`Follower.BeyondEnd`——越过末点时沿末点切向**外推**前视点，前视距离恒定。航向误差 **1.41° → 0.4–0.6°**（左侧垂直库 1.4→0.4，左后斜入库 1.9→0.6）。
3. **两段式路线（`TryTwoLegStaging`）根本没法跟**：`BuildRuns` 把段边界定在**下一段第一个采样点**（23.51），而车在当前方向上最多只能投影到**上一段最后一个采样点**（23.26）——边界永远差一个采样步，`run.Travel != confirmedGear` 永不成立。表现是车朝已经走过的边界点打满舵**原地画圈**，航向误差一路飘到 −107°，永不超时完成。
   **修法**：边界取 `points[i-1].DistanceAlong`；`RunAt` 的并列归属改成"边界属于后一段"（`<=End+1e-6` → `<End-1e-6`）。
4. **跨段投影抖动**：边界可达之后，`UpdateProgress` 的最近点搜索会跨段——这条路线的进库线与接近线**共线**（开过库位再倒回来），倒车时投影跳回上一段，于是 `剩` 越倒越大、挡位请求在 D/R 之间反复（实测 560 次换挡脉冲）。
   **修法**：`CurrentRun()` 段索引**只前进**（锁存）+ 投影搜索窗口限制在当前段 ±1 m 内（`LegWindowM`）。脉冲数 560 → 2。

**回归**：新增具名用例 `两段式路线（唯一多段路径）`（`TerminalStraightM=2` 时该层会在代价上胜出），并入退出码。

**当前数字**：自检 24/24；闭环 4/5，终端航向误差 `0.0 / 1.4 / 0.4 / 0.0 / 0.6°`（基线 `0.0 / 4.3 / 3.9 / 0.0 / 3.6°`）；两段式路线从"永远画圈"变成"能倒进库、但没走完就超时"。

**下一个缺陷（已定位，未修）**：纵向权限不足。`Kp=0.35` 单独对抗仿真里的滚动阻力，稳态误差 ≈ 阻力/Kp ≈ 0.5 m/s，而积分被 `±1.0 × Ki=0.05` 限死在 0.05 m/s²，起步补偿只在 `speed<0.15` 生效——于是长倒库段以 0.15 m/s（0.5 km/h）爬行，12 m 的段跑不完 300 s 预算。这是"最后半米磨蹭"的同一根因，与末端角度无关，动它需要重新整定 `PidKp/PidKi/LaunchAccelMps2`，属于另一次权衡。

---

## 18. 重规划：路径在换挡边界重新求解（2026-10-03 设计）

### 18.1 现状（改之前）
- `Planner.Plan` 在地图刷新时反复计算，但结果只用于预览：`Follower` 在 `AutoParkingPlugin.cs:1000` 取走 engage 那一刻的路径快照，之后**全程定死**。
- `HandleObstacles` 检查的是**整条剩余走廊**，任何远处冲突都会让车在起步前无限期停等。也就是说：车永远走不到换挡边界，"到边界再重新想"这个触发点根本不会发生。
- 结论：既不能中途反应，也不能分段反应。这是"动态规划"没接上的部分。

### 18.2 决策
**只在车停着的时候重新求解，触发点有两个：**

| 触发 | 条件 | 采纳条件 |
|---|---|---|
| A 边界 | 走到换挡边界（`run.Travel != confirmedGear`，车已停） | 新路线代价比"剩余旧路线"至少好 0.5 m |
| B 被挡 | 前方冲突且已停等 > 2 s | 新路线无冲突 |

配套改动：**障碍检查只看前方 4 m**（`ObstacleLookaheadM`）。远处障碍不改道、不停车，走到边界再重算；近处障碍照旧停等。这是"分段反应"能成立的前提。

**次数封顶** `MaxReplans=3`：用完后退回定死行为。**封顶绝不中止**——按既定规则，只有热键能结束动作；到顶后要么继续走完，要么停在那儿等热键。

### 18.3 为什么不在行进中重规划
1. 行进中重算出来的新路径与旧路径在**当前点相切但不重合**，跟踪器要突然改横向目标；我们这次刚修完末端角度，`progress` 最近点投影在自交路径上本来就脆（缺陷 4），中途换参考等于再制造一次跨段跳变。
2. 车停着时位姿是唯一确定的、且换挡本来就要停——边界是免费的决策点。
3. 只做车头，任意位姿都能重新解出 RS 路径，"停下来重新想"永远是可行退路；带挂车时不成立（挂车姿态不可由车头单步恢复），所以这条**是"暂不做挂车"换来的自由度**，将来支持挂车时本节要重写。

### 18.4 换路径时必须重置的状态
`path`、`runs`、`index=0`、`progress=0`、`runIndex=0`、`integral/lastError=0`、`blockedSince`、`confirmedGear=null`（让挡位状态机重新确认一次）、`stage=SelectGear`。
`lastSteer` **不重置**——方向盘没被命令动过，重置会让下一 tick 从 0 重新爬升，等于凭空一次转向冲击。
新路径的起点就是当前实测位姿，所以 `progress=0` 天然正确。

### 18.5 验收
- 判据先行：仿真里放一个"落在旧路线第二段、起步时距离 > 4 m"的障碍。关掉重规划 → 车开进第二段后停等到超时（`超时未完成`）；打开重规划 → 边界/停等触发重算，换到无冲突路线并到位。
- 现有回归不得退化：规划自检 24/24、闭环 5 用例 + 两段式用例、挡箱失聪用例。

### 18.6 实现后的实测修正（2026-10-03）

写完跑起来，18.2 的两处假设与事实不符，按事实改了实现与命名：

1. **"换挡边界"是很稀疏的事件，不能当主触发。** 探针打印实际分段：左侧垂直库选中的是
   `倒车入库（RS 反向遍历）+ 直线入位 · 49.1 m · 换挡 1`，分段是 `Reverse@0.0->49.1` ——
   **整条只有一个倒车段**，"换挡 1"数的是初始挂挡，不是中途换向。多数路线都是这样，
   所以只挂边界触发的话，功能实车上基本永远不会生效。现在两个触发点都在，
   **被挡停等是主路径**，边界是顺带的免费决策点；总开关因此改名 `ReplanWhileStopped`
   （原名 `ReplanAtBoundaries` 会误导）。
2. **障碍探测距离不是 `ObstacleLookaheadM`。** 扫掠矩形是 `VehicleLengthM + 0.6 + 2×margin ≈ 8.1 m`
   且以路径点为中心，所以冲突在障碍还在车前约 7.5 m 时就登记了。实测车停在障碍前
   7.4 m（`progress=4.9`、障碍在 12.3 m）。这个参数因此读作"车头前再往前看多少"，
   测试场景必须把障碍放到 30 m 处，车停在 22.7 m 处才有替代机动可用——
   停得太靠里，扫描显示 20/24/26 m 处重算全部"路径上有 19~20 处冲突"，已经没有退路。
3. **停等计时器必须带滞回。** 第一版在冲突数为 0 的那一 tick 立刻清零计时器，而投影会按一个
   采样步呼吸，于是状态永远显示"已等 0.3 s"、2 秒阈值永远够不到。改成"连续清空 2 s 才忘记"，
   并把重算尝试按 2 s 节流（否则 60 Hz 会每 tick 调一次规划器）。

### 18.7 验收结果
仿真具名用例「中途冒出障碍」（起点 (0,0,0)、目标 (-8,0,90°)、障碍在弧长 30 m 处侧偏 0.8 m）：

| | 结果 |
|---|---|
| 关重规划 | ✗ 超时未完成，停等累计 253.6 s，重算 0 次 |
| 开重规划 | ✓ 到位（重算成 `RS LSL + 直线入位 · 31.9 m · 换挡 0`） |

回归无退化：规划自检 24/24、闭环 4/5（`0.0/1.4/0.4/0.0/0.6°` 终端航向误差）、挡箱失聪用例仍不卡死。
「两段式路线」用例仍因**纵向权限不足**超时，那是 §17 记过的另一个缺陷，本次没动。

---

## 19. 倒车段末端"原地打转"：确认信号读错了量（2026-10-03，实车日志）

**现象**（用户）：最后一段是两段式路线，第二阶段倒车接近终点时原地打转。

**日志（02:0x 那一轮，`engaged: 前进到预热点 12 m 后直线倒库 · 40.7 m`）**：

```
请求=Drive → gear=0 dash=4 ... 下一行 gear=4                 ← 挡位动作确实进了游戏
sent_accel=-0.51 (brake=0.51) user_brake=0.00 user_throttle=0.60
air 104→112（回升）  brake_temp 32.5→30.6（降温）  v 1.2~1.7 km/h 不停
请求=Reverse → gear=-1 ... 请求=Reverse → gear=0 dash=-1 v=0.0 ...（每秒一次）
```

先排除"缺陷3/4 复发"：那一轮跑的是 01:03 版（engage 行带 `+ 直线入位` 后缀、且全轮无 `replanned` 行），四个修复都在；缺陷3 的签名是 `剩` 冻在边界、`steer` 饱和、挡位停在 **+1**、航向单调飘到 −107°、发生在**前进段**——与本轮（`gear=-1` 的倒车段、车在动）不符。

**根因两条，都是判据与真实执行器不匹配：**

1. **确认信号用错了量。** `WaitForGear` 看 `truckInt.gear`（实际啮合的挡）。自动箱在**每次停稳时把挡退回空挡**（日志反复出现 `gear=0 dash=-1`），也就是说：我们要求"先刹停、再挂挡、再用 `gear` 确认"，可一旦真的刹停，`gear` 就归零，确认窗口正好在我们成功的那一刻消失。于是 `gearConfirmed` 永假 → `Drive()` 每秒补发一次挡位动作（实车 1 Hz 脉冲风暴），而车在别人的油门下以 1.5 km/h 移动、转向仍由我们给出 → 视觉上就是原地转。
   **修法**：确认改为 `gear` **或** `gearDashboard` 任一符合请求方向即可（`dash` 保留挡杆位置，正是我们要的"动作已落地"证据）。
2. **"换挡前必须刹停"没有上限。** `StopAndRequestGear` 里 `|速度|>0.15 → 继续刹` 是无条件的，刹车没有权限时就永远出不去。
   **修法**：加 4 s 耐心窗口（`GearStopPatienceSeconds`），到点**照发挡并继续**，状态写明"刹停无望，边发挡边等"。这与 §17 的原则一致：观测只能降级，不能中止动作；游戏在车速下本就忽略换挡动作，提前发的代价为零。

**回归**：`ClosedLoop` 新增 `automaticNeutral` 模式（`gear` 在停稳时归零、`dash` 保留、行进方向仍由 `dash` 决定），具名用例「自动箱停稳退空挡」判据"到位且换挡脉冲 ≤ 4 次"。修前 **110 次**，修后 **2 次**；其余不变（自检 24/24、闭环 4/5、挡箱失聪与被挡重规划两例仍通过）。

**仍未定（诚实记录）**：那 0.60 的油门是谁给的。SDK 输入表里没有 cruise 动作可注入（只有遥测 `cruiseControlSpeed` 可读），所以只能检测不能关闭。探针已补 `steer / game_brake / game_throttle / cruise / 剩 / 状态文本`，下一轮即可区分三种可能：游戏自己的定速巡航（车速贴着 5.8 km/h 很像巡航目标）、别的插件通道（权重：我们 5.0、OvertakeAssistant 5.0、SequentialAutoShift 1.0，加权平均下都可能压过我们）、或玩家设备。定出来源之前不动权重，避免把军备竞赛往上推一层。

---

## 20. 一挂 R 就方向打死：pure pursuit 的后方视点奇点（2026-10-03，实车日志 + 仿真复现）

**现象**（用户）：进入第二阶段倒车的瞬间方向直接打死，方向盘不再动。

**新探针一次就抓到**（`steer / 剩 / cruise / game_*` 是上一轮刚加的）：

```
脉冲挡位 Reverse（第 1/2 次，遥测 0/4） 剩=12.0 m      ← progress 钉在分段边界
挂上 R 后 0.5 s：steer 0.00 → 0.84 → 1.00 并停住
cruise=0.0  game_throttle=0.00  user_throttle=0.60  user_brake=0.00（我们发的是 brake=0.58）
```

**顺带把 §19 遗留的"0.60 油门是谁给的"定死了**：不是游戏巡航（`cruise=0`）、也不是玩家（`game_throttle=0`）。宿主把 `aforward/abackward` 折进同一个桶按权重平均再按符号分派，所以 `(W·1.0 + 5·(−0.55))/(W+5) = 0.60` ⇒ **另一个通道以权重 ≈14 在发满油门**，我们 5.0 输了平均。身份未定（二进制里扫浮点常量噪音太大，不据此指认）；能确定的是机制。停稳时我们的刹车能落地（`user_brake=0.50 game_brake=0.50`），一旦对方发油门就被平均成正值。

**根因（仿真复现，不靠推理）**：把 `MaxBrakeDecel` 压到 0.05 模拟"刹不住"，立刻重现：车冲过预热点约 4.7 m，`剩` 冻在 12.00，`steer=-1.00` 钉住，横 -4.0 m。因为 leg-2 的采样点从预热点往库里走，车冲过头之后最近点被钉在边界，**前视点落到车身后方**，α 逼近 ±180°——`sin α` 在奇点两侧翻号，命令饱和且不再收敛。

**修法**：`AimPointAhead` 判据——视点不在行进方向前方时不追它，改走切向律（横偏 + 航向差，无奇点）。效果：

| 指标 | 修前 | 修后 |
|---|---|---|
| 刹不住冲过预热点：满舵时长 | 218~270 s | **0.0 s** |
| 两段式路线：终端航向误差 | 37.6° | **0.6°**（横 -0.21 m） |
| 自检 / 闭环 / 自动箱 / 被挡重规划 | 24/24 · 4/5 · 2 次 · 通过 | 不变 |

**没弄懂的地方（诚实记录）**：`ReverseLateralLaw.CrossTrackPd` 这个非默认选项，**两个符号都发散**——原符号跑出 横 39.5 m/航向 89.7°/`剩` 冻在 49.1 m，我按 pure pursuit 的取反规则翻转后作为整段主律仍然发散（25.3 m/77.5°）。按 `SignedLateral`（正=左）与 `SmallestAngleDifference(a,b)=norm(a-b)` 手推，反向段两种符号各推得出一次，与实测都不完全吻合，说明我对锚点切向/倒车航向的某个约定理解仍有偏差。当前状态：回退律用的是实测不发散的那个符号，且只在过渡帧生效（视点一回到前方就交还 pure pursuit），所以 §20 的修复是有效的；但这个选项作为主律不可用，建议直接删掉（或另开一轮把约定逐条对齐）。

**仍未解决**：① 那个权重 ≈14 的油门通道是谁；② 纵向权限不足（§17 记过的爬行）——两段式用例现在跟得住但仍在 300 s 预算内跑不完。

---

## 21. 气压归零与权重被压：§20 的结论要收窄（2026-10-03）

用户接着报："二阶段倒车不会往左打方向，且倒过头也不停车"。日志（12:0x 那轮，`前进到预热点 12 m 后直线倒库 · 35.7 m`）：

```
air=0.05 → 0.00（连续 60 行）   brake_temp=68（此前一直满刹车）
steer=0.00 全程                 ← 第二轮选的就是"直线倒库"，第二段是直线，0 是正确值
user_brake=0.00 user_throttle=0.60 恒定；我们发 brake=0.50~0.82
```

**对 §20 的收窄**：视点奇点在仿真里是真的（满舵 218 s → 0 s、两段式航向误差 37.6° → 0.6° 都成立），但它**不是**用户这次看到的现象的解释——这一轮第二段本来就是直线，`steer=0` 是正确输出。把"方向盘不动"归给奇点是我过度延伸。

**真正的解释是物理的**：ETS2 的行车制动走气路，`air≈0` 时踏板值再大也不产生制动力。而气压是被我们自己的控制律耗掉的：为了对抗那个恒定 0.60 的油门，PID 连续几分钟压 0.5–0.8 刹车，压缩机补不回来。于是"倒过头也不停车"——不是判据不触发，是**没有制动物理可用**。

同一份日志也把 §19 遗留的问题定量了：`(W·1.0 + 5·(−0.55))/(W+5) = 0.60` ⇒ 对手权重 ≈ 14，我们固定 5.0。**权重低不是礼貌让位，是被覆盖**——宿主按权重平均后按符号分派，我们连刹车都发不出去。

**本轮改动：**

1. `ControlOutput` 的权重从硬编码 5.0 改为设置项 `ControlWeight`，默认 **20**（高于实测对手≈14），可在设置页调，1–60。
2. **没气时改用驻车制动停车**：`demand.Brake > 0.1` 且气压低于应急值且 `剩 < 2 m` 时置 `HoldBrake=true`（驻车制动不走气路）。限制在终点 2 m 内，避免高速下拉死手刹锁后轮。
3. 状态表新增「制动能力」行显示气压与应急值，低于阈值追加"行车制动无效"；首次进入该状态写一条 WARN。
4. 制动探针补 `剩 / 横 / 航向差`，用来区分"直线段本该 steer=0"与"投影被钉住导致误差被隐藏"——这正是这次误判的根源。

**未验证与未解决（诚实记录）**：
- 仿真没有气路模型，第 2 条只经过代码审查，**未经测试**，需要实车确认。
- 权重≈14 的那个通道仍不知是谁。10 秒 A/B：把 tumppi066 的 ACC / OvertakeAssistant 逐个关掉再跑，看 `user_throttle=0.60` 是否消失。
- `ReverseLateralLaw.CrossTrackPd` 的符号问题（§20）仍未定论，且我现在怀疑原符号才是对的——我按 pure pursuit 推的翻转使仿真指标变好，但作为整段主律两个符号都发散，说明我对倒车锚点约定的理解还有缺口。
- 纵向权限不足（爬行）未动。

---

## 22. `剩` 冻结在 12.90 倒穿终点：段窗口下界的余量是我自己造的死角（2026-10-03）

用户两条观察：**"倒到最后不往右打轮修正"** 与 **"距终点从来没进过 2 m"**，并要求"距离变大就停下重排二段修正"。

**轨迹证据**（两段式，`TerminalStraightM=2`）：车的位置 (14.41,−17.41)→(13.32,−16.33)→…→(1.24,−4.60)，与"预热点→库位"的轴线**完全吻合**（方向 (−0.707,0.707)），`横` 只有 −0.02→−0.09、`航向差` 0.24°、`steer=0.00`，而 **`剩` 从 t=40 起冻在 12.90 直到超时**，车一路倒穿终点又开出 5 m。

**根因**：§18/§20 里我加的投影段窗口下界是 `leg.Start − LegWindowM`（留了 1 m 余量）。这条两段式路线的进库线与接近线共线，于是投影可以合法地停在**上一段**的采样点上（22.36）；而 `maxAdvance = max(0.75, …)` 的每 tick 推进上限**小于**它到第二段第一个采样点（23.51）的 1.15 m 间距 → 前进扫描一进来就 `break`，progress 永久冻结。

于是用户的两条观察是**同一个故障的两面**：锚点被钉在一个已经离开的旧点上，横向误差被算成 0.03 m ⇒ 控制器认为无需修正（"不往右打轮"）；`剩` 被钉在 12.9 ⇒ 永远进不了 2 m、完成判据永不触发（"倒穿终点不停车"）。

**修法两条：**

1. **窗口下界不留余量**：`windowLow = leg.Start`（上界仍给 `+LegWindowM` 以容纳终点冲过头）。段起点那个采样点本身仍然合格（判据是 `< windowLow` 才跳过）。
2. **距离变大监视器（用户提的规则）**：行驶中 `剩` 只准变小或持平；一旦 `剩 > 历史最小 + 0.6 m`，先刹停（`BrakeOnly(0.6)`，等 `|v| ≤ 0.1`），再走 `TryReplan` 重新规划一段修正。这是 §18"只在停着时重算"的第三个触发点，也是唯一能兜住"参考系与车脱钩"这一整类故障的通用保险。换路径时 `bestRemaining` 复位。

**验证**（`dotnet build` 0 错误；仿真）：

| 用例 | 修前 | 修后 |
|---|---|---|
| 两段式路线 | ✗ 超时，`剩` 冻在 12.9，航向 37.6° | ✓ 到位，航向 **0.0°**，换挡脉冲 3 次 |
| 刹不住冲过预热点 | ✗ 超时 | ✓ 到位 |
| 自检 / 闭环 / 自动箱 / 被挡重规划 | 24/24 · 4/5 · ✓ · ✓ | 不变 |
| 监视器（合成退行） | 无此路径 | ✓ 制动 + 重规划 1 次 + 新路径 |

监视器在闭环里**没有自然触发场景**（窗口修好后 `剩` 不再变大），所以用合成状态序列单独测：沿直线路线前进 9 m 再退回，使 `剩` 超过历史最小值——断言"发制动"且"重规划 ≥1 次"，两条都通过。它不是只写了没测的代码。

**遗留**：`右前45度` 终端航向 1.4° 未达我设的 1.0° 判据（harness 退出码因此仍为 1）；纵向权限/爬行未动；权重≈14 的对手身份未定；`CrossTrackPd` 符号仍未定论。

---

## 23. harness 挪进仓库：`Tools\PlannerHarness`（2026-10-03，推翻 §M2 的决定）

用户要求：把离线仿真工程放进项目仓库，然后同步 AGENTS.md 与 README。

**当年为什么放外面**（§M2 记的理由，至今仍然成立）：插件 `.csproj` 用默认 globbing，仓库里任何 `.cs` 都会被编译进发布的 DLL——一个带 `Program.cs` 的目录会让插件工程直接炸掉（top-level statements 与自动生成的 `Main` 冲突）。

**为什么现在可以放进来**：`DefaultItemExcludes` 正是为这种目录准备的开关，代价比"测试代码不在仓库里"低得多。仓库外的 harness 对任何只 clone 本仓库的人来说等于不存在，自检 24/24 与闭环用例因此不可复现。

**三处改动：**

1. `AutoParking.csproj`：`<DefaultItemExcludes>$(DefaultItemExcludes);Tools\**</DefaultItemExcludes>`。
2. `Tools\PlannerHarness\PlannerHarness.csproj`：链接路径从 `..\..\ThirdPartyPlugins\AutoParking\…` 改为 `$(PluginRoot)`（= `..\..\`），并把"这是开发者工程、永不被游戏加载"写进文件注释。
3. 文档：AGENTS.md 的命令表与边界条款、两份 README 的 6.3 与架构一览、本文件里指向旧路径的三处。

**为什么放在 `Tools\` 而不是仓库根**：`ThirdPartyPlugins\build_all.ps1` 用 `Get-ChildItem -Filter *.csproj -Depth 1` 收集插件，也就是 `ThirdPartyPlugins\*\*.csproj`。它把 `AutoParking.csproj` 当插件（正确），但摆在 `ThirdPartyPlugins\` 直属层的控制台工程会被它 build 完再复制进 `current\Plugins\`（错误）。放进 `Tools\` 后它落在深度 2，脚本看不到——这条是读脚本核实过的，不是猜的。

**验证**（两条命令都按文档里的写法从仓库根执行）：

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release` | 0 错误，警告数与挪动前一致（13 个，均为既有） |
| harness 有没有混进发布的 DLL | `bin\Release\AutoParking.dll` 里搜 `ClosedLoop` / `PlannerHarness`：**0 / 0** |
| `dotnet run --project Tools/PlannerHarness -c Release` | 自检 24/24、闭环 4/5、具名用例逐条与挪动前同值，退出码仍 1（那条 1.4°） |
| `git check-ignore` | `Tools\**\bin`、`Tools\**\obj` 已被既有的 `bin/`、`obj/` 规则覆盖，`.gitignore` 不用改 |

**遗留**：旧目录 `..\..\scratch\PlannerHarness\` 只剩 `bin\`、`obj\` 两份生成物，源码已搬空；自动模式下对工作区外的删除被拦，需要手动删。

---

## 24. 踏板走的是哪条通路：宿主实验开关的身份与影响（2026-10-03）

**背景**：§21/§22 之后一直挂着"纵向权限不足"。其中**踏板这一半的原因不在我们的 PID 里**（长倒库爬行是否同一因，见文末待测）。ETS2LA 在 2026-09-29 18:06 加了提交 `5299cc9 "Add option to enable memory output for pedals"`，用户于 2026-10-02 22:47 打开后踏板问题消失（**这条是用户实车反馈，不是我测的**）。

**开关**：设置 → Experiments → **Enable Memory Output for Pedals**，落盘在 `%APPDATA%\ETS2LA\GameSettings.json` 的 `EnableModernOutputForPedals`（默认 false），UI 自己提示"改完可能要重启游戏"。Tumppi066 在 Discord（2026-09-29 17:20）解释过为什么默认关闭：*"Our memory implementation (the same one we use for steering) doesn't work for everyone, so it's not enabled."*；同一天 23:00 他又说明失焦时*"the game doesn't allow any inputs if you're tabbed out"*，而 memory 写入绕开这一层。

**分发代码事实**（`ETS2LA.Game\Output\Output.cs`，宿主最新版）：

- `:217-218` 把 `aforward`/`abackward` 折叠成同一个 `acceleration` 桶，**不取反**；
- `:289-291` 跨通道**加权平均**发生在传输分支**之前**；
- `:301-306` 开关打开时，带符号的 `weightedValue` 原样写进 modern 面 offset 13（另 17=bool、18=时间戳）；
- `:309-318` 关闭时才按符号拆回 legacy 的 `aforward`/`abackward` 两个字段；
- 布尔动作（挡位、手刹）**始终** legacy（`:207`、`:209`），不受这个开关影响。

**对我们三件事：**

1. **代码一行都不用改**："只发一个有符号字段"的约定在两条通路上同时成立（§17 那次符号修复没有白做，反而在 memory 通路上更直）。上面第 2 条也说明 §21 的权重结论与传输层无关，**不收窄、不推翻**。
2. **但观测面变了**：`truckFloat.userBrake/userThrottle` 是 legacy 虚拟手柄面的回声。踏板改走 modern 面之后，"`user_brake=0.50` 就证明指令进了游戏"这条推理**不再自动成立**，需要重测才知道哪个回声字段对应哪条通路。气压 `air=` 是物理量、与通路无关，仍然是最硬的那条证据。
3. **多了一个看不见的前提**：memory 通路失焦也生效。热键仍必须（那是**输入**侧，SCS SDK 失焦不吃输入，与本开关无关），但以前"人一切走踏板就失效"其实是游戏替我们兜了一层底——现在这层底没了。§18 的"只在停着时重算"、"永不自动中止"这类约束因此更需要 `Abort` 手不离键。

**已做**：`制动探针` 每行前面加 `transport=memory|legacy`，状态表加"踏板通路"一行。取值来自 `ETS2LA.Game.GameSettings.Current.EnableModernOutputForPedals`（public static，我们本来就引用 `ETS2LA.Game.dll`，**没有修改宿主**）。这样任何一条历史 trace 从此自带通路信息，不必再靠日期去猜当时是哪条路。构建 0 错误、警告数不变（13）。

**待测**：长倒库段 0.5 km/h 爬行是否随开关消失（若已消失，README 状态栏那句"纵向权限不足"就该删）；memory 通路下 `user_*` 与 `game_*` 谁在回声我们的指令；≈14 那个对手通道的身份仍未定。

---

## 25. 地图选位：朝向由第一像素抖动决定，而拖动期间参考系在动（2026-10-03，用户报"B：在地图里拖动，整个窗口跟着走"）

**用户观察**：覆盖层窗口一动，就只能选到大致位置，**车头朝向是随机的**。

**代码能直接证明的缺陷（不需要复现）**：`MapOverlay.HandleMouse` 按下时把车位放在光标底下，随后用
`delta = mouse − ToCanvas(target)` 算朝向。按下那一帧两者**重合**，`delta` 恰为 0，唯一的守卫是
`delta.X != 0 || delta.Y != 0`——于是真正决定朝向的是**按下后第一像素的抖动方向**，`atan2` 对一个 1 px
矢量的角度毫无意义。harness 里加了一条判据先让它失败（`选位拖动死区`：1 px 抖动被忽略=False），
再实现 `Geometry.TryHeadingFromDrag` 的最小死区（12 px）使其通过；`Geometry.cs` 属于 harness 链接的纯数学子集，
所以这条能离线钉住。

**为什么拖动期间参考系不可信（两条机制，同一修法）**：

1. **窗口边框是 ImGui 的缩放手柄。** 我们没有设 `Flags`，宿主就按 `ImGuiWindowFlags.None` 开窗口
   （`Overlay.cs:385`），于是窗口可移动可缩放；而地图画布只离窗边 `Padding = 8 px`，落在缩放抓取区内。
   按在画布边缘 = 同时在按窗边：拖左边/上边框会**连窗口原点一起移动**，看起来就是"窗口跟着鼠标走"。
   宿主在 `Overlay.cs:367` 明写了 *"Use ImGuiWindowFlags to disallow movement"*，我们没采纳。
2. **投影每帧重算，而图是跟着车滚的。** 地图中心是 `truck.Position`，`canvasCenter` 又来自
   `ImGui.GetWindowPos()`；拖动过程中车在动（M4 现成的爬行）或窗口在动，车位就会从光标底下滑走，
   `delta` 跟着跳——朝向追的是车/窗口，不是手。

**修法（一次改完，都指向"这次手势的参考系必须钉住"）**：

- 窗口 `Flags = ImGuiWindowFlags.NoResize`；
- 按下时**锁存** `canvasCenter / truckPlane / scale / canvasMin`，整次拖动都用这套投影，中途窗口或车位姿变化不再影响手势；
- `Geometry.TryHeadingFromDrag(pivot, cursor, 12 px)` 死区，短于死区保持点击时的朝向（吸附导航线或车头方向）；
- 拖动时画出**死区圈 + 车位到光标的指示线**，让"我正在划哪个方向"看得见，而不是松手才发现歪了。

**一次回退（记下来免得后人以为 NoMove 是被否决的方案）**：第一版是 `NoResize | NoMove`，用户实测确认"窗口的确移动不了了"，随后要求**保留可拖动、同时选位正常**。最终只留 `NoResize`：ImGui 只在标题栏移动窗口，而标题栏在画布矩形之外，永远不会被当成选位按下——两个手势在空间上不相交；吃掉按下的是**边框缩放手柄**，那才是必须禁的那个。窗口被挪动对选位也不再有影响，因为投影在按下那一刻锁存。窗口的初始位置仍是 `X=24, Y=180`，宿主按 `ImGuiCond.Once` 只在首次打开时施加（`Overlay.cs:389-397`），所以本次会话里拖到哪就停在哪；跨启动能不能记住取决于宿主的 ImGui ini，我没有验证过。

**证据**：选位开始/结束各一行日志——世界坐标、是否吸附、比例、画布左上角、拖动半径、以及
`期间地图滑动=? px`、`画布位移=? px`。后两个数就是判别器：如果下次还乱，`画布位移>0` 说明窗口仍在被拖
（flags 没生效或被宿主覆盖），`地图滑动>0` 说明是车在动导致的投影漂移，两个都是 0 而朝向仍不对才是手势逻辑本身的问题。

**仍未修的已知缺口**：`HandleMouse` 读的是**全局**鼠标状态（`IsMouseClicked` 没带窗口作用域的 flags，
ImGui 1.92 支持），只靠"鼠标落在画布矩形内"过滤。所以按住 RightAlt 时点在别的窗口上、只要落点在我们的
矩形内，仍会重放车位。这条我没顺手改，因为它与"窗口跟着走"不是同一个根因，要单独验证。

## 26. 静态地图内容：路灯和建筑物本来就在数据里，是我们只捡了 Road 和 Prefab（2026-10-03，读 official-plugins 与宿主数据层）

**动机**：M4 之后能停了，但障碍物只有实时车辆（`ObstacleScanner` 只读 traffic + parked）。
停车真正撞的东西是墙、路灯、绿化带。用户要求参考 `InternalVisualization` 和 `VisualizationSockets`
看怎么识别并显示 buildings / POI。

**读到的事实（都在本工作区里，不需要猜）**：

1. 空间索引只有节点：`Nodes.Within()` 是 RBush R-tree（`TruckLib/.../Collections/NodeDictionary.cs:102`），
   `Map.MapItems` 是无索引 `Dictionary<ulong, MapItem>`（`Map.cs:35`）。所以"附近有什么"只能走节点。
2. **所有 item 都挂在自己的节点上**：`SingleNodeItem.Add` 只写 `node.ForwardItem`（`SingleNodeItem.cs:34-44`），
   折线 item 两端各挂一次。也就是说 `MapGeometry.cs` 现有的节点遍历**早就把 buildings / model / sign / POI
   区域送到手上了**，是 `Collect()` 的 switch 只认 `Road`/`Prefab`，其余全部丢弃。这不是缺接口，是缺两行。
3. 类型判别用 `ItemType`（`ScsMap/Enums.cs:12`）。`Buildings` 是 `PolylineItem`：`Node`/`ForwardNode`/`Length`/
   `Name`（building scheme token，来自 `/def/world/building_scheme.sii`）/`Collision`。`Model` 是 `SingleNodeItem`：
   `Name`（`/def/world/model.sii` token）/`Scale`/`Collision`。
4. 真实尺寸要从 **PMD** 来：`PmdFileHandler.Current.GetPmdModel(token)` → `TruckLib.Models.Model`，
   带 `BoundingBox`/`BoundingBoxCenter`/`Parts→Pieces`。`VisualizationSockets/Classes.cs:201-216` 就是这么用的。
   安装版 `TruckLib.Models.dll` 里这些成员确实存在；宿主在地图解析完后自己绑好了 Sii/Ppd/Pmd 三个
   FileSystem（`ETS2LA.State/Program.cs:365-367`，安装版 `ETS2LA.State.dll` 里有 `PmdFileHandler`）。
   我们自己的 builder 再绑一次 Ppd 已经做了，Pmd 还没做。
5. **保真度门控**（`ETS2LA.Game/Data/Classes.cs:63-123`）按 `IgnoredItemTypes` 的名字丢条目：
   Medium 保 Buildings/Sign/BusStop/FuelPump/Gate/Trigger，**Model（路灯、杆、集装箱）要 High**，
   **Company/CityArea/MapArea/Garage 这类 POI 要 Extreme**。另外 `ShowInUiMap=false` 的 prefab/road 在
   <High 时整段被丢——"地图上不存在的隐蔽场区"就是这么消失的，对停车业务这条比路灯更要命。
   用户机器 `%APPDATA%\ETS2LA\DataSettings.json` 实测 `"DataFidelity": 3` = Extreme，**这条不用改**。
6. 上游踩过的坑，直接写在他代码里：`VisualizationSockets.cs:226-230` 把 Model 分支整段注释掉，理由
   `// TODO: Optimize model loading, this lags ETS2LA for ~20 seconds at first start`。PMD 是逐 token 惰性解析，
   市区附近实例上万、token 上百，同步扫一次就是几十秒冻结。**这条决定了第 2/3 步的实现形状**：
   按 token 缓存、后台线程、每 tick 限额，没算出来之前先用"类别半径圆"兜底。

**诚实的边界**：`InternalVisualization` 里**没有** buildings renderer（6 个是 Nodes/Roads/Prefabs/Traffic/Truck/
Statistics），`ItemType.Buildings` 只在 node tooltip 的 type switch 里露过一次（`Renderers/NodesRenderer.cs:36`）；
socket 那边有 `SocketModel`（带包围盒）但也没有 buildings。能借的是写法，不是成品。`ETS2LA.ML/Vision`
同样只画道路 + 车辆。

**第 1 步做了什么（本次，只读测量，零控车影响）**：`Rendering/MapItemProbe.cs`——按 `ItemType` 分桶，
每桶记 `Count / Collidable / NearestM / DistinctTokens / TopTokens`，UID 去重（折线 item 从两端到达会重复上报，
这是实测出来的：harness 里 6 次上报 = 5 个唯一 UID）。`MapGeometryBuilder.Build` 在同一次节点遍历里
`tally` 所有 item；显示三处：地图窗口**一行**摘要、状态页「地图内容」行、`设置 -> 地图清单` 按钮把
8 类明细 + 中心/半径/节点数/构建毫秒/宿主保真度写进日志。标签写成 `[[地图清单]]`——双括号是因为 `ETS2LA.Logging` 会把裸写的 `[Tag]` 当 Spectre markup 吞掉。

**为什么排序和截断放在探针里而不是渲染里**：同一份数要进 560 px 的 ImGui 窗口和日志两个地方，
一个依赖字典插入顺序的 top-N 会让同一个位置两次读数不一致，所以钉死为"数量降序、同数按名字序"，
行宽 ≤108 字符、超出类别折成一行。`MapItemProbe.cs` 已加进 harness 的链接子集，14 条判据离线跑绿。

**刻意没做的**：没有加设置开关、没有改窗口布局塞明细行、没有碰 `ObstacleSnapshot`/规划器。
第 2 步（显示 buildings/models）和第 3 步（静态障碍分级进规划）各自单独一轮——
识别得越全，规划越容易判"无解"，一排路灯就能把车位围死，所以第 3 步必须分级：
默认只让"确定的硬障碍"（Buildings、`Collision=true` 的 model）参与，软的只显示不拦。

**第 3 步必须先解决的已知障碍**：`Tick` 里地图几何只在 `settings.ShowMapWindow` 为真时重建
（`AutoParkingPlugin.cs:211`，注释写着"构建要 10–50 ms"）。也就是说**关掉平面地图窗口 = 静态数据停止更新**，
真跑动作时如果窗口是关的，第 3 步的障碍集会边跑边消失。直接把守卫去掉也不可行——50 ms 落在 60 Hz
控制线程上就是方向盘抖动。所以要动的是构建本身：分片/后台构建 + 双缓冲快照，或者动作开始时冻结一份
静态几何。这条现在没做，因为它是第 3 步的前置，不是第 1 步的缺陷。

**待测（下一步的输入，不是结论）**：实车在真车位附近点一次`地图清单`，看①Model 的 distinct token 数（决定 PMD 路线可不可行、要不要限流）；②Buildings 的 `可碰` 是不是几乎全 1
（若是，`Collision` 标志就没法区分墙和花坛，得另找依据）；③`构建 ms` 有没有因为多走一次类型判断而涨；
④Extreme 下 POI 类（Company/CityArea）到底叫什么 token，能不能直接拿来标车位语义。

## 27. 第 2 步：把建筑线段和模型点画到平面上（2026-10-03，用户"map 里没看到渲染出来的障碍物"）

**先分清是哪一种"障碍物"**：地图上红色多边形一直是实时车（车流+停放车，来自
`Local\ETS2LATraffic` / `Local\ETS2LAParkedVehicles` 两块共享内存，无需任何 opt-in 标志）。
用户要的是**静态**内容——建筑、路灯。第 1 步只打清单没画东西，所以他"看不到"是准确的现状，不是 bug。
这一轮就是把它画出来（M6b）。

**动手前用代码钉死的前置事实**（避免"推理出一个不存在的渲染"）：

- 折线 item 从**两端节点都可达**：`PolylineItem.Add` 写 `backwardNode.ForwardItem = newItem` 且
  `forwardNode.BackwardItem = newItem`（`TruckLib/.../PolylineItem.cs:103-108`）；`Append` 同样
  （`:152-156`）。所以现有 `Nodes.Within()` 遍历已经能拿到 buildings，不需要新查询。
- 单节点 item 只写 `node.ForwardItem`（`SingleNodeItem.cs:36-41`），`Model` 和 `Sign` 都继承
  `SingleNodeItem`（`Model.cs:16`、`Sign.cs:14`）→ 点位同样已经在手上。
- 于是 `CollectStatic()` 与第 1 步的 `Tally()` 共用同一次遍历，UID 集合去重（同一个 item 会被
  上报两次，这是 §26 里实测到的同一条性质）。

**画了什么**：建筑=品红线段（取 item 自己的 `Node.Position`/`ForwardNode.Position`，不是"我从哪个
节点到达"的那个节点，否则同一面墙会画成半截）；Model=黄绿点；Sign=蓝紫点；实时车红多边形仍然画在
最上层。**实心=地图的 `Collision` 标志为真，空心=假**——这个编码是刻意的测量手段：如果整个场区
画出来全是实心，就说明该标志分不出墙和花坛，第 3 步的硬/软分级必须另找依据（这正是 §26 待测②）。

**上限与诚实标注**：`MaxBuildingSegments = 2000`、`MaxStaticPoints = 3000`，撞到就把
`MapGeometry.StaticTruncated` 置真，状态行末尾显示"（静态内容已截断）"——**不许静默少画**。
这两个数是显示上限不是规划上限：采样每 0.5 s 在 tick 线程重建一次，市区 120 m 内散落模型轻松上千。

**刻意没做**：没有 PMD 真实包围盒（等 §26 待测①的 token 种类数再决定要不要按 token 缓存 + 后台构建）、
没进 `ObstacleSnapshot`、没进 AR 叠加层、没加显示开关（关窗口就是开关）。窗口里加了一行图例，
把"只画不拦"写在脸上，免得用户以为它已经在拦了。

**验证状态**：构建 0 error / 13 既有 warning；harness 判定与 §26 相同（`自检 24/24`、`闭环 4/5`、
探针 14 项全绿，退出码仍由那条 1.4° 航向 case 决定）。`MapGeometry.cs` 不属于 harness 链接的纯数学子集，
所以这一轮**没有离线断言能覆盖它**——画得对不对只能在游戏里看，尚未实测。

## 28. 场区里的箱子/墩子/配电箱不在清单上：Compound 把自己的孩子藏在外面（2026-10-03，用户截图）

**观察**：截图里 `11 类 185 个：Sign 65 · Buildings 31 · Trajectory 25 等 11 类`，而画面是一个堆满
集装箱、木托盘堆、配电箱的场区。120 m 半径只数出 185 个条目，说明**这些道具压根没进清单**——
不是"没画"，是"没数到"。这一条区分很重要：如果是没画，改渲染；如果是没数到，改遍历。

**代码证明的机制**：`Compound` 是 `SingleNodeItem`（`Compound.cs:26`），所以节点遍历能看见它**本身**；
但它的孩子只存在它自己的字典里——`CompoundSerializer.Deserialize` 把子 item 写进 `comp.MapItems`、
把子节点写进 `comp.Nodes`（`:17-39`），而 `Map.CompoundItems` 那条路径明确显示孩子会**从地图的
`MapItems`/`Nodes` 里被移除**（`Map.cs:415-422`）。于是一堆木托盘在清单里就是**一个** item。
`Nodes.Within()` 也搜不到它们：那棵 R-tree 只装地图自己的节点。

**改了什么**：`MapGeometryBuilder.Visit()` 在数完 compound 之后**下钻一层**遍历 `compound.MapItems.Values`
（一层就够，格式里没有 compound 套 compound；`MapItems` 为 null 时跳过）。孩子的坐标用孩子自己的节点——
compound 内部节点的序列化格式和地图节点相同，是**绝对世界坐标**，所以能直接进同一套画布投影。
`MapItemProbe` 加了 `TypeTally.Nested`，明细行显示 `内含 N`：如果只看到 `Model 1400` 而不知道其中
1380 个来自 compound，下一步就会去查一个根本不存在的"漏画"问题。harness 里加了两条判据
（`compound 内的道具单独计数`、`明细行显示内含数`），探针判据 14 → 16 全绿。
顺带把窗口那行摘要从 top-3 提到 top-5——这次正是 top-3 把证据藏住了。

**同时排掉的一条路**：`PrefabDescriptor` 在这个构建里只暴露 `Nodes / NavCurves / Semaphores / TerrainPoints`
（安装版 `TruckLib.Models.dll` 的元数据里就这些 getter），**没有摆放模型列表**。所以"prefab 内部塞的道具"
这条路走不通，能拿到的只有 compound 的孩子和地图上的散件。

**尚未证实（等下一次读数，别当结论）**：下钻之后 `Model` 到底会不会出现在清单里、`内含` 有多少。
如果 `Model` 仍然接近 0，那说明这些箱子是 **prefab 自带几何**，只能靠 PMD（按 token 缓存 + 后台构建，
见 §26 待测①）或者退一步用"prefab 边界多边形"近似——那时近似反而更诚实，因为规划真正需要的是
"这块地不能压"，不是"这个箱子在 (x,z)"。

## 29. "能不能再多显示一些"：把静态图层从三种类改成"凡是有地面几何的条目都画"（2026-10-03）

**先撤一条弱证据**。§28 里我写 `PrefabDescriptor` "只有 Nodes/NavCurves/Semaphores/TerrainPoints"，
那是拿一份**我猜的候选名**去 grep `strings` 的结果——命中不了不等于不存在。这次改成反射实测：
新工具 `Tools\MapSurfaceDump`（和 PlannerHarness 一样放在 `Tools\` 下，不会被编进 DLL，也不会被
`build_all.ps1` 收走），直接加载**安装版**的 `TruckLib.dll` / `TruckLib.Models.dll`，打印每个
`IMapObject` 实现类和 PPD/PMD 类型的公开成员。结论：

- `PrefabDescriptor` 的公开成员确实是 `Intersections / MapPoints / NavCurves / NavNodes / Nodes /
  Semaphores / Signs / SpawnPoints / TriggerPoints`——**没有摆放模型列表**。所以"prefab 内部塞的道具"
  用随宿主的库拿不到，要拿必须自己解析 `.pdll`/`.ppd` 的模型段。这条现在是证出来的，不是猜出来的。
- 但**地图条目那一层我们还漏了一大片**。反射列出的类里，凡是公开暴露节点的都是可画几何：
  `SingleNodeItem.Node`（点）、`PolylineItem.Node/ForwardNode`（线段）、`PathItem.Nodes`（链，
  `PathNodeList : IList<INode>`）、`PolygonItem.Nodes`（环，`PolygonNodeList : IList<INode>`）。
  也就是说 Vegetation、Hinge、Mover、AnimatedModel、Gate、BezierPatch、Company、Hookup、Garage、
  Service、FuelPump、BusStop、CityArea、MapArea、TrafficArea、Trigger 全都能画，之前只挑了三种。

**改法**：与其再手挑六个类，不如把静态图层做成**通用**的。`MapGeometry.StaticShapes` 一条一个条目，
`StaticShape(Points, Kind, Token, Collision, Closed)`，`TryShape` 按基类给形状；渲染端未命名的类
一律画成灰色，**先让你看见，再决定它叫什么**。只有三类被显式跳过并写明理由：`Road`/`Terrain`
（车道线已经画得更好）、`Compound`（它自己的节点就在刚画出来的那堆孩子上面，纯重复点）。
上限合并成一个 `MaxStaticShapes = 4000`，撞到就在状态行写"（静态内容已截断）"。
`Kind` 就是地图的 `ItemType` 名，所以"那一坨灰点是什么"由地图清单那行直接回答，不需要第二套映射。

**仍未解决、而且这条改完更清楚的一件事**：如果那些箱子/托盘既不在 `Model` 也不在 `Compound` 孩子里，
它们就只能是 prefab 自带几何——那不在"多显示几个类"的能力范围内，只能走 PMD（按 token 缓存 + 后台
构建，§26 待测①）或者退到"prefab 边界近似"。判据是现成的：重启后地图清单/`[[全图清点]]` 里
`Model` 与 `Vegetation`/`Hinge` 的计数，加上现在这一层画出来之后**屏幕上灰点跟实物对不对得上**。

**验证**：构建 0 error / 13 既有 warning；harness 判定不变（`自检 24/24`、`闭环 4/5`、探针 16 项全绿，
退出码仍由那条 1.4° 航向 case 决定）。`MapGeometry.cs`/`MapOverlay.cs` 不在 harness 的纯数学子集里，
所以这一层依旧是**没有离线断言覆盖、只能实车看**的代码。
