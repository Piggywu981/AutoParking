# AutoParking 插件设计文档

- 日期：2026-09-30
- 目标项目：ETS2LA V3（C# / .NET 10），安装版 `2026.9.5092`
- 工作区：`E:\ETS2LA\V3-C#\ThirdPartyPlugins\AutoParking\`
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
| `aforward`/`abackward` 被折叠为同一 `acceleration` 通道：加权值 >0 写 `aforward`，<0 写 `abackward`。**没有独立 brake 浮点通道，制动 = 负的 acceleration** | `Output.cs:217-223,299-318` |
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

自动标定（`AutoCalibrateRadius=true`）：在直行/匀速行驶中用 `yawRate = (Δheading)/Δt` 与 `v` 反解瞬时半径 `R_inst = v / yawRate`，只在 `|v|>1.0 m/s`、`|steer|>0.15`、`|Δsteer|<0.02/tick` 时采样，取 20 个样本的中位数再按 `R_min_observed = R_inst / |steer_norm|` 折算；结果限幅 `4–20 m`，滑动保留最近 300 s。标定值只影响规划，设置页可"清零回退默认"。

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
| 2 | 人类接管 | `userSteer>0.15`、`userThrottle>0.1`、`userBrake>0.05`（阈值可调，`UserOverrideEnabled` 默认 true） | Abort（3 s 制动后交还） |
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
| `UserOverrideEnabled` + 3 个阈值 | true / 0.15 / 0.1 / 0.05 | — | Switch+Sliders |
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

**本地验证方式**（不需要游戏）：`E:\ETS2LA\V3-C#\scratch\PlannerHarness\` 用 `<Compile Include>` 链接纯数学子集，`dotnet run` 打印自检结果与若干典型路径的形状/长度/挡位切换。该目录在插件工作区之外，不会被 `build_all.ps1`（depth 1）或插件工程的默认 glob 收进去。

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
