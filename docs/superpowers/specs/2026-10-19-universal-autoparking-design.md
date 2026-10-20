# 通用自动泊车（MarkerFollower）设计

日期：2026-10-19　状态：已获车主批准（范围=跟随 trigger/mover 协议；通路=先 sidecar；形态=先影子模式再实车）

## 1. 目标与非目标

**目标**：把驾校场景验证过的能力做进插件——按下热键后，插件自己找到游戏要求的停车位置，规划并执行泊车；影子模式先行，实车按门控放行。

**通用性来源**：不解析任务脚本，只跟随游戏自己的标记协议。游戏把 0xC17 Mover 对挪到哪，插件就往哪停。book1_2_09（纵列式）、其他驾校场景、任何复用该协议的场景都不改代码。

**非目标**：世界地图停车场（无 Mover 标记，纯锥桶推断）——后续版本；挂车——保持 `RefuseWithTrailer` 默认开；任何自动中止逻辑——M4 边界不动，中止永远只有热键。

## 2. 会话内已确立的事实（设计依据）

| # | 事实 | 证据 |
|---|---|---|
| F1 | 要求的停车位置 = `Mover`(0x09) 条目 `flags==0xC17` 成对出现；**只在当前激活阶段存在** | 两场景复现：同一 uid 对换场景位移 58 m；stopmid 激活时 (2166.0,1801.8) 有对，backward 未激活时缺口内无对 |
| F2 | 0xC17 对无朝向（两点相距 0.3 m），kdop 盒各向同性（8.75/7.94 m 立方）不能当车位尺寸 | 两场景一致 |
| F3 | 车位几何真值 = framing 车（Hookup `academy.parked.truck`）kdop 之间的缺口：本场景 8.78×3.24 m，中心 (2174.33, 1797.42)，航向 90°（由 dx≫dz 推断） | `--deep` 全量扫描 + kdop 数学 |
| F4 | 任务阶段链写在任务单元动作表里，条件即顺序：start(engine on) → check1(speed>0) → stopmid(检查点1) → checkmirror → backward(speed<0) → stop → end(驻车+熄火) | 任务单元 0x2CBD1D97000 dump + 本地化 goal 链 |
| F5 | 触发顺序的动态证据：`[trigger] Handled action` / `[academy] Goal value change ... actor_pos` 日志，按时间追加 | `--strctx` 扫描 |
| F6 | MemoryProbe sidecar 一轮 0.3–0.6 s（`--viz 60`=0.62 s，`--deep 90`=0.28 s） | 实测计时 |
| F7 | 曲线跟随路线在精确多边形判据下 0 冲突（外扩 ≤0.15 m/侧）；解析规划器沿不了 §45 弯道 | §45/§53、r4 系列 run |
| F8 | 闭环仿真泊入实测：横向 0.012 m、航向 0.18°、90.3 s | r4 闭环 |
| F9 | `KdopReader` 已在插件内产品化（639 行，后台线程，几何+Uid+Flags，无身份串） | 仓库现状 |
| F10 | 可行中心窗很窄：x∈[2173.19, 2175.47]（0 间隙 ±1.14，0.15 包络 ±0.99）、z ±0.34；Trigger 盒（7.36³/10.16×6.68）是松容积，判不了落位 | 偏差反证（两窗差 0.12 m 不相交） |

## 3. 架构：MarkerFollower 协议环

```
                    ┌────────────────────────────────────┐
  热键 ──────────►  │ Idle → Acquiring → Executing(leg)  │──热键──► Abort（唯一中止路径）
                    │      ◄_______重新解析______◄       │
                    └────────────────────────────────────┘
```

每圈四步：

1. **Acquiring**：`TargetResolver` 用最新 KdopReader 快照（几何）+ IdentityChannel（身份，仅此步调用 sidecar）解析目标 `Pose2 + 出处 + 置信度`。
2. **规划**：接近腿用 §53 全前进平滑族（松包络）；倒库腿用现有 RS 规划器 + 动态包络。0 冲突才放行。
3. **Executing**：`Follower.Step` 现有闭环执行；每步检查目标 uid 是否位移 >0.5 m——是则该腿作废回到 1。
4. **Done**：目标位姿到达（位置 ≤0.3 m、航向 ≤3°）+ 停稳 `SettleTimeS`；或标记消失且已泊稳。

## 4. 组件

### 4.1 `Driving/Autoparking/AutoParkController.cs`（新，状态机）

- 纯状态机 + 协调，不碰内存、不碰规划数学；输入是快照/遥测的不可变 struct，输出是 `ControlOutput` 或 `Idle`。
- 日志契约：每次状态迁移一行（含出处字符串与置信度）；任务阶段名（stopmid/backward 等）只进日志不进逻辑。
- 失败即停步降级：解析失败→Idle+原因；规划失败→Idle+原因。**永不自动重试、永不自动中止**（AGENTS.md 政策）。

### 4.2 `Driving/Autoparking/TargetResolver.cs`（新，目标解析链）

优先级（每级返回 `Target{ Pose2 pose; string provenance; double confidence; ulong uid }`）：

1. **0xC17 对**（confidence 0.9）：快照内 `Mover && Flags==0xC17` 的成对条目；pose = 对中点；航向 = 最近 framing 车排轴线（F2：对本身无朝向）。
2. **framing 缺口**（0.6）：Hookup `parked` 排相邻两车间 kdop 间隙 ≥ 车长+1 m；pose = 缺口中心；航向 = 排轴线。**依赖修好的 `ParkingBays.Detect`**（见 §7）。
3. **SavedSpot**（0.4）：现有配置点。
4. 全部失败 → 返回 None，controller 拒绝起步并给日志。

置信度门槛 `MinConfidence`（默认 0.6）：低于门槛拒绝起步，不猜。

### 4.3 `Memory/IdentityChannel.cs`（新，sidecar 身份通道）

- 调 `MemoryProbe.exe --deep <x> <z> <r>`（r≈90），解析 ITEM 行得 Trigger/Hookup/Mover 的 uid/flags/坐标/身份串；进程级单例，**只被 TargetResolver 调用**，绝不在控制环（tick/渲染线程禁入——KdopReader 同规则）。
- 按 uid 缓存身份串（身份不变）；解析触发节流：每次 Acquiring 最多一次调用，结果 5 s 内复用。
- 失败（exe 缺失/超时/解析空）→ 返回"纯几何模式"标志，TargetResolver 降级到只用 KdopReader 几何（1/2 级仍可用，只是没有身份串确认 `parked`/`academy` 字样）；日志标注降级。
- exe 路径：`SidecarPath` 配置，默认找插件 DLL 同目录 `MemoryProbe.exe`（部署时一起拷贝）。

### 4.4 `KdopReader` 扩展（现有文件，两个新 MemKind）

- `MemKind.Trigger`（item_type 0x22）、`MemKind.ParkedVehicle`（0x2F Hookup，仅当 flags 含 parked 位族 0x100/0x10C/0x200/0x3100——判据进注释）。几何+Uid+Flags 走现有快照通道，**不加字符串提取**（那是 sidecar 的活）。

### 4.5 两段腿与动态包络（配置项，无新规划器）

- **接近腿**：`Planner` §53 平滑前进族，包络固定 `ApproachEnvelopeM = 0.30`（10 m 宽车道，松）。
- **倒库腿**：现有 RS + 闭环。包络 `DynamicEnvelope(true)` 时**只用横向净宽**（framing 排的 kdop 深度方向净间隙，本场景 3.24）算：`pad = clamp((横向净宽 − VehicleWidthM)/2 − SafetyMarginM(0.10), 0.05, 0.30)` → 本场景 (3.24−2.57)/2−0.10 = 0.235。纵向（8.78）只做另一条检查：**纵向净长 ≥ 车长 + 0.4 m，否则拒绝**（物理不可行）。pad 写进 `ParkingPath` 构造参数，判据 `CountCorridorConflicts` 不改。

### 4.6 影子模式与起步门（M4 兼容）

- `ShadowMode`（默认 **true**）：1–4 全跑，`ControlOutput` 不发布；AR/地图叠加画目标 pose+出处+规划线。影子下到达判定照常走（用遥测位姿），方便离线验证判定逻辑。
- **起步门**（gate，不是 abort——只在 Idle→Acquiring 检查一次；判据一律写拒绝式）：拒绝起步当——引擎未启动（转速过低）、|speed| 不小于 0.5 m/s、档位未知、气压 < `MinAirBar`（默认 4.0，制动证据；严格小于，所以 0 = 关掉这一项）、`RefuseWithTrailer` 且带挂车、置信度 < MinConfidence（严格小于，0 = 关掉；出处前缀为 `saved-spot` 的用户自述档豁免——门是拦插件猜的，人自己选的点没有识别不确定性，见 Task 6 的 R36）、规划冲突数不为 0。任一命中 → 拒绝起步 + 日志逐条列出不过项。
- 运行中：**只有 `OnAbortKeyPressed` 能结束**。标记消失、sidecar 死亡、遥测缺口——降级/停步/保持报告，绝不自动 abort（AGENTS.md 硬政策）。
- 实车开关：`ShadowMode=false` 需在 SettingsPage 显式关（默认安全）。

## 5. 数据流（一次完整纵列式泊车）

```
热键 → 门检查 → Acquiring:
  KdopReader 快照 → 快照内0xC17 对@(2166.0,1801.8) → Target(conf 0.9, prov "mover-pair")
  （IdentityChannel 同时确认 2166 处 Trigger 带 state park_trigger_on_stopped → 日志）
→ 规划接近腿（松包络 0.30）→ Executing 跟随 → 到达停稳 SettleTimeS
→ Acquiring: 0xC17 对已挪进缺口（游戏 enable backward 后出现）→ Target=缺口内对(conf 0.9)
  若对尚未出现 → fallback framing 缺口(conf 0.6) → 照样能停对地方
→ 规划倒库腿（动态包络 0.235）→ RS+闭环倒库 → 到达停稳 → Done
```

## 6. 配置（`Settings.cs` 追加，全部有默认）

| 键 | 默认 | 说明 |
|---|---|---|
| `ShadowMode` | true | 影子模式 |
| `SidecarPath` | ""=DLL同目录 | MemoryProbe.exe 路径 |
| `DynamicEnvelope` | true | 倒库腿动态包络 |
| `ApproachEnvelopeM` | 0.30 | 接近腿包络 |
| `SafetyMarginM` | 0.10 | 动态包络安全边 |
| `SettleTimeS` | 3.0 | 停稳判定时长 |
| `MinConfidence` | 0.6 | 起步置信度门槛 |
| `MinAirBar` | 4.0 | 起步气压门槛 |
| `MarkerMoveToleranceM` | 0.5 | 标记位移重规划阈值 |

SettingsPage 加对应控件（镜像现有滑杆/开关风格）。

## 7. 附带修复（设计范围内，有证据的两个已知缺陷）

1. **`ParkingBays.Detect` 行分组**（§54 链）：现在按垂坐标在全采样半径分组，驾校远排并入导致跨度 >`MaxBayLengthM` 全拒。修为**局部带分组**（以每条锥排的连续 x 段切分，段间距 >`MaxBayLengthM` 断开）。这是 TargetResolver 第 2 级的前提。harness 已有 `--rows` 诊断可作回归。
2. **包络默认值**：出厂 0.10 m/侧对实测车过紧（§51 已收编公式），本设计用动态包络后出厂默认不再是关键路径，但 `ApproachEnvelopeM=0.30` 的文案要写清适用条件（宽车道）。

## 8. 错误处理矩阵

| 故障 | 行为 |
|---|---|
| sidecar exe 缺失/超时 | 降级纯几何模式，日志标注；继续 |
| 0xC17 对不存在 | fallback 缺口解析；conf 0.6 |
| 缺口 < 车长+0.4 | 拒绝起步，原因入日志 |
| 规划 0 冲突无解 | 拒绝起步（倒库腿）/拒绝起步（接近腿），不硬闯 |
| 执行中标记位移 >0.5 m | 该腿作废，回 Acquiring 重规划 |
| 执行中标记消失 | 保持当前腿走完（不 mid-abort）；走完后 Acquiring 失败 → Idle |
| 遥测缺口 >1 s | 沿用最后位姿继续 Follower（现有行为），日志告警 |
| 挂车在档 | 拒绝起步（RefuseWithTrailer） |

## 9. 测试

1. **TargetResolver 单测**（harness 新 `*Checks.cs`）：fixture = 本会话 `deep_parallel.txt` 转的内存条目表（含 0xC17 对、framing Hookup 排、Trigger 族）；断言第 1 级命中对中点 (2166.2, 1801.8)、删对后第 2 级命中缺口 (2174.33, 1797.42)、置信度单调。
2. **harness 基线纪律**：新判据追加，基线按 ✗ 集合比对（当前 exit 1 / 三条固定 ✗ / ✓131+），**永不为变绿降门槛**。
3. **闭环倒库腿仿真**：Follower + 实测缺口几何（两台 framing 车 kdop 当障碍）跑一遍，判据沿用 r4 口径（横向 ≤0.1 m、航向 ≤1°）。
4. **影子实车协议**（车主执行）：驾校 book1_2_09 内 ShadowMode=true 跑全程 → 肉眼核对 AR 规划线与标记 → 确认 Done 判定在正确位置触发。
5. **实车协议**：ShadowMode=false，热键启动，abort 键常备；记录每次门检查结果与状态迁移日志。

## 10. 部署

- sidecar 部署：`AutoParking.csproj` 加一个 build 后 Target——若 `Tools\MemoryProbe\bin\Release\net10.0\win-x64\MemoryProbe.exe` 存在就拷进插件输出目录（找不到只警告不报错，裸 clone 不带预编译 exe 时插件降级纯几何模式照样能编能跑）。`build_all.ps1` 不用改。
- 按"改完即部署"惯例：插件源码改动 → `dotnet build -c Release` → DLL 拷 `current\Plugins\`（重启 ETS2LA 生效）。

## 11. 明确不做

- 世界地图停车场目标推断（无标记场景）
- 从任务单元脚本解析阶段语义（MarkerFollower 不需要）
- 任何"观察到 X 就自动停"的逻辑（M4）
- 内化进程内读内存（v2 再议，接口已按可替换设计：IdentityChannel 是接口）
