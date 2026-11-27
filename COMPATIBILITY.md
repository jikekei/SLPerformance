# 运行时、游戏版本与插件生态兼容性分析（COMPATIBILITY.md）

---

## 1. 目标运行时能力矩阵（实测元数据确认）

目标运行时：**Unity `6000.0.43f1` + MonoBleedingEdge (`mono-2.0-bdwgc.dll`) + `mscorlib 4.6.57.0` / `netstandard 2.1.0.0`**

| 技术特性 | 实际可用性 | 元数据核验结果 | 工程使用建议 |
| --- | :---: | --- | --- |
| `System.Span<T>` / `ReadOnlySpan<T>` | **可用** | `mscorlib.dll` 原生存在 `public readonly ref struct Span<T>` | 推荐在序列化/协议处理中使用；编译期若目标为 `net48` 需加 `System.Memory` 仅编译期引用（`ExcludeAssets="runtime"`），避免向服务端分发重复 DLL |
| `System.Buffers.ArrayPool<T>` | **可用** | `mscorlib.dll` 原生存在 `public abstract class ArrayPool<T>` | 推荐用于临时缓冲复用，但须注意归还并限制最大租借长度防大对象滞留 |
| `ConditionalWeakTable<TKey, TValue>` | **可用** | `mscorlib.dll` 原生存在 | 推荐用于不可变 `Delegate` 调用链缓存及为原版组件附加零泄漏扩展缓存 |
| `System.Numerics.Vector<T>` (SIMD) | **受限** | 存在，但 MonoBleedingEdge JIT 对 SIMD 向量内联优化远弱于 CoreCLR RyuJIT | **不可假设自动硬件加速**；须实测对照，避免引入负优化复杂度 |
| `Unity.Burst` | **不可用于插件** | 存在 `Unity.Burst.dll` 与 `lib_burst_generated.dll` | Burst 仅在 Unity Editor 构建游戏时 AOT 编译游戏内置程序集，运行时动态加载的 LabAPI 插件无法被 Burst JIT/AOT 编译 |
| `Unity.Jobs` (`IJobParallelFor`) & `NativeArray<T>` | **可用** | `UnityEngine.CoreModule.dll` 原生存在 | 仅适用于配合 Unity 原生批处理 API（如 `Physics.*Command.ScheduleBatch`），纯托管计算受限于无 Burst |
| `RaycastCommand` / `SpherecastCommand` / `OverlapSphereCommand` | **可用** | `UnityEngine.PhysicsModule.dll` 原生存在 | 可用于 Tier-3 物理批处理实验，须用 `Allocator.Persistent` 复用 `NativeArray` 避免每帧非托管分配 |
| `UnityEngine.LowLevel.PlayerLoop` | **可用** | `UnityEngine.CoreModule.dll` 原生存在 | 可用于第四阶段低开销帧阶段计时器挂载（零 MonoBehaviour 额外开销） |
| `Unity.Profiling.ProfilerRecorder` | **可用** | `UnityEngine.CoreModule.dll` 原生存在 | 可直接读取 Unity 引擎底层原生计数器（GC Allocated In Frame、Physics.Simulate、Main Thread 等），极低开销！ |
| 增量 GC (`gc-max-time-slice=3`) | **已默认开启** | `SCPSL_Data\boot.config` 确认 | 减少 Gen0 分配速率可直接减少增量 GC 切片频率与帧尾抖动 |

---

## 2. 编译目标与依赖管理规范

1. **推荐 TargetFramework**：`net48` / `net481`（与本工作区现有 LabAPI 插件 `AdminAudit`、`RoundLeaderboardPanel` 一致），`<PlatformTarget>x64</PlatformTarget>`，`<LangVersion>latest</LangVersion>`。
2. **禁止引用过期的 Publicized DLL**：
   - 经检查，`SCPSL_Data\Managed\Assembly-CSharp.dll` 为 `4,303,360` bytes (`2026-11-18`，SHA-256 `E098A8DDA09D6CE1...`)，而 `huanmengyinghecajiang0\PluginDependencies\Assembly-CSharp-Publicized.dll` 为 `4,301,312` bytes（旧构建）。
   - `SLPerformance` 必须直接针对服务端当前 `SCPSL_Data\Managed\*.dll` 编译，私有成员访问通过 Harmony `AccessTools` / 预编译表达式树委托（`DynamicMethod` / `FieldRefAccess`）实现零反射热路径访问，避免对 Publicizer 工具产生硬依赖。
3. **Harmony 依赖版本**：
   - 工作区已有 `0Harmony.dll` (`2.4.2.0`)；原版服务端 `SCPSL_Data\Managed` 不含 `0Harmony.dll`。
   - 部署时 `0Harmony.dll` 放置在 LabAPI `dependencies` 目录，由 `LabApi.Loader.PluginLoader.LoadAllDependencies()` 优先加载。

---

## 3. 游戏版本兼容性策略（SCP:SL `14.2.9` 及后续版本）

| 补丁风险等级 | 涉及目标 | 随版本失效概率 | 防护策略 |
| --- | --- | --- | --- |
| **低风险** | `Mirror.dll` (`SendRPCInternal`, `Broadcast`, `Batcher`, `Unbatcher`)、`LabApi.dll` (`EventManager`) | 低（Mirror 升级频次远低于游戏玩法更新） | 启动时通过 `AccessTools` 验证精确方法签名与关键私有字段类型，缺失则安全跳过模块 |
| **中风险** | `LiteNetLib4MirrorTransport`、`VoiceTransceiver`、`StatusEffectBase`、`VisionInformation`、`AdminToyBase` | 中（大版本可能微调字段或新增事件） | 每次加载记录 `GameCore.Version.VersionString`；签名不匹配自动禁用受影响模块并告警，绝不崩溃服 |
| **高风险** | `FpcServerPositionDistributor`、`FpcMotor`、`LiteNetManager.NativeReceiveLogic` | 较高（Northwood 频繁调整 FPC 移动/反作弊/网络代码） | 严禁使用脆弱的硬编码 IL 偏移 Transpiler；优先采用可验证签名与结构指纹的 Prefix/Postfix 或高鲁棒性匹配，并在不匹配时优雅退回原版 |

---

## 4. 第三方插件生态兼容性分析

### 4.1 与 LabAPI 插件的兼容

- **事件完整性**：`OPT-02`（`FpcServerPositionDistributor`）、`OPT-05`（`VoiceTransceiver`）、`OPT-14`（`EnvironmentalHazard` / `TeslaGate`）仅在对应 `LabApi.Events.Handlers.*` 事件**无任何订阅者**时跳过 `EventArgs` 构造；一旦有任何插件订阅（包括动态热加载后订阅），实时检测立即生效，完整构造参数并按原版顺序触发。
- **动态订阅安全**：不得在插件启动时“一次性静态缓存事件是否为 null”，必须在每帧/每次调用时做一次轻量静态字段判空（单条指针读指令，~0.5ns），确保任意时刻动态订阅/退订的插件 100% 正常工作。
- **VSR 与透明模组标记 (`IsTransparent`)**：`LabApi.Loader.PluginLoader.ResolveTransparentlyModdedFlag()` 会检查所有已加载插件的 `Plugin.IsTransparent`。`SLPerformance` 作为不修改原版玩法与客户端协议的纯底层性能优化插件，须核对 Northwood 官方 Verified Server Rules (VSR) 关于 Harmony 补丁与透明标志的合规要求，默认保持透明合规、不隐藏插件信息、不修改任何认证/封禁/列表上报行为。

### 4.2 与常见重型插件的交互排查

| 第三方插件类别 | 代表插件 | 潜在交互点 | 兼容设计 |
| --- | --- | --- | --- |
| **EXILED 框架** | `Exiled.Events.dll` | EXILED 大量使用 Harmony Transpiler 注入 `Assembly-CSharp`（如 `FpcServerPositionDistributor`、`VoiceTransceiver`、`Scp173ObserversTracker`、`DoorVariant`） | **关键防冲突机制**：在 `SLPerformance.Compatibility` 中通过 `Harmony.GetPatchInfo(method)` 检测目标方法是否已被其他插件打了 Transpiler 或 Prefix；对存在冲突的模块自动输出告警并支持按配置降级（例如当 EXILED 已 Transpile `WriteAll` 时，自动避让或切换为仅优化其内部无冲突子方法 `GetVisibleRole` / `FpcSyncData`！） |
| **自定义地图 / 建筑插件** | MapEditorReborn / AdminToy 建筑类插件 | 生成数千 `AdminToyBase`（可能动态修改 Transform 而未设 `IsStatic`） | `OPT-03`（Mirror 脏广播）将极大提升此类服性能；`OPT-14` 仅休眠显式 `IsStatic == true` 的 Toy，不误伤动态移动建筑 |
| **音频播放插件** | `AudioPlayerApi` / `SCPSLAudioApi` | 通过 `SpeakerToy` / `AudioMessage` 或 `VoiceMessage` 发送音频 | `OPT-05` 保持 `VoiceMessage` 完整协议与 `OnVoiceMessageReceiving` 回调；`AudioMessage` 不受影响 |
| **反作弊插件** | `SimpleAntiCheat` / `PDAC` | 监听移动包、射线检测、视野或 Hook `FpcMotor` / `FpcServerPositionDistributor` | 严禁绕过或修改任何位置覆盖、速度校验、速率限制与预鉴权逻辑；遇到同方法 Harmony 冲突时通过 `Compatibility` 模块安全协调 |
| **HUD / Hint 插件** | `HintServiceMeow` | 高频向玩家发送 `HintMessage`（走 Mirror `NetworkConnection.Send`） | `OPT-10`（Batcher 零拷贝）与 `OPT-04`（直连 Poll）透明加速此类高频网络消息 |

### 实机实测共存记录（2026-11-27）
- **`SimpleAntiCheat` (`SimpleAntiCheat-*`)**：已在实机启动日志确认其对 `FpcServerPositionDistributor.WriteAll` 与 `FpcMotor.Move` 施加了 Harmony 补丁。
  - `SLPerformance` 自动以 `LoadPriority.Lowest` 后置检测，**绝不覆盖、绕过或削弱反作弊补丁**；
  - `net.fpc_distributor_fast` (`OPT-02`) 自动切换至**安全共存降级模式**：完整保留 `WriteAll` 与 `LateUpdate` 给 `SimpleAntiCheat`，同时启用无冲突的 `GetVisibleRole` 快速短路与 `PreviouslySent` 断线内存泄漏修复；
  - `physics.fpc_motor_fastpath` (`OPT-11`) 自动以 **2/3 共存模式** 运行：保留 `FpcMotor.Move` 给 `SimpleAntiCheat`，同时启用 `FpcMotor.UpdatePosition` 静止跳过地面射线与 `FpcMouseLook.UpdateRotation` 专服快速路径；
  - 其余 13 项默认开启模块与 `SimpleAntiCheat` 无冲突，全部正常激活。
  - **注意**：切勿为了强行启用 `WriteAll` / `Move` 补丁而将 `auto_skip_on_harmony_conflict` 设为 `false`，否则可能干扰反作弊检测逻辑。
