# 项目研究进度与未解决问题追踪（PROGRESS.md）

> 最后更新：2026-11-27

---

## 1. 六阶段总体进度

| 阶段 | 目标 | 当前状态 | 完成内容 |
| --- | --- | :---: | --- |
| **第一阶段** | 环境与程序集分析 | **已完成** | 核验目标路径全部 DLL、Unity `6000.0.43f1`、SCP:SL `14.2.9`、MonoBleedingEdge、LabAPI `1.1.7.0`、`boot.config`、原生插件与元数据 API 可用性；反编译 7 个核心程序集至会话私有临时区 |
| **第二阶段** | 原版服务端全面研究 | **已完成** | 建立完整线程模型、Unity PlayerLoop 帧执行链、LiteNetLib→Mirror 收发包链、实体/物理/GC 全景审计，定位 18 个有源码实证的性能热点 (`HS-01`–`HS-18`) |
| **第三阶段** | 优化机会评估 | **已完成** | 形成 17 项候选优化方案 (`OPT-01`–`OPT-17`) 的 8 维度优先级矩阵与 13 要素设计规范，划定 Tier 1/2/3 与明确反模式清单 |
| **第四阶段** | 性能分析工具与核心框架开发 | **已完成** | 实现 `Core`（插件入口/配置/模块化注册表）、`Patching`（分模块 Harmony 管理与 `DynamicMethod` 零反射访问器）、`Compatibility`（版本核验与外部 Harmony 补丁冲突自动避让）、`Profiler`（`PlayerLoop` 帧时间环形缓冲、`ProfilerRecorder`、GC/CPU/网络统计、热点探针）及 `Diagnostics`（`slperf` 全功能控制台/RA 命令） |
| **第五阶段** | 优化模块实现 | **已完成** | 完成全部 **22 项独立可开关优化模块**（Tier 1: 12 项、Tier 2: 8 项、Tier 3 可选实验项: 2 项），涵盖稳态主循环、`OPT-21` 大规模切角色/波次复活峰值加速（`slperf roleburst`）与 `OPT-22` 真人玩家加入/认证尖峰消除（`slperf joinstats`），全部支持独立开关与运行时 `slperf toggle` 热回退；Release 编译 **0 警告 0 错误** |
| **第六阶段** | 自动化验证、离线微基准、真服三轮实测迭代与 DLL 交付 | **已完成** | `SLPerformance.Tests` **15/15 全部通过**（覆盖 22 模块、30 探针、`OPT-21` 反射/打包等价性、`OPT-22` P-384 密码学/BanCache 核验与 SCP:SL `14.2.9` 全量元数据审计）；`SLPerformance.Benchmarks` 9 项离线微基准完成；真实 48/48 专用服完成三轮稳态优化（主线程均值 `11.77ms -> 7.13ms`，`-39.4%`，P99 帧 `16.69ms`，GC 分配 `-81.6%`）；`OPT-21 v5` 与 `OPT-22` 已合包交付待真服实测验收 |

---

## 2. 交付产物清单

均位于 `projects/SLPerformance/`：

### 2.1 最终二进制与部署产物（`outputs/`）
- `outputs/SLPerformance.dll`（295,424 bytes，Release / `net48` / `x64`，SHA256: `BEDB39FD36DEE4D45EC7259ABB0A3AD07CAD7C45A5B76589C938770CEEB463E5`）
- `outputs/SLPerformance.pdb`（94,504 bytes，Release 调试符号）
- `outputs/config.example.yml`（全部 22 项模块开关、OPT-21 预热参数、OPT-22 异步认证参数及 Tier 3 参数示例配置）

### 2.2 工程源码（`src/`）
- `src/SLPerformance.sln`：完整解决方案（含插件库、自动化测试与离线微基准）
- `src/SLPerformance/`：插件完整源码（`Core`、`Patching`、`Compatibility`、`Collections`、`Serialization`、`Network`、`Memory`、`Entities`、`Physics`、`Threading`、`Profiler`、`Diagnostics`）
- `src/SLPerformance.Tests/`：15 项自动化行为等价性、探针目标分辨率、`OPT-21` 反射/打包等价性、`OPT-22` P-384 密码学/BanCache 核验与真实 `14.2.9` 二进制元数据审计测试
- `src/SLPerformance.Benchmarks/`：9 项可复现离线 A/B 微基准测试

### 2.3 研究文档
- `RESEARCH.md`：原版服务端环境、程序集与六大子系统源码级研究报告
- `ARCHITECTURE.md`：服务端线程模型、PlayerLoop 帧链路、网络收发链路与 `SLPerformance` 工程架构
- `HOTSPOTS.md`：18 个静态核验性能热点的调用链路、复杂度、理论频次与证据剖面
- `OPTIMIZATIONS.md`：17 项候选优化的优先级评分矩阵、详细技术方案与反模式清单
- `COMPATIBILITY.md`：Mono/Unity 运行时能力矩阵、构建依赖规范、版本及插件兼容策略
- `BENCHMARKS.md`：对照压测方法论、12 项测试结果、9 项离线微基准实测结果及真实服务端待测表
- `PROGRESS.md`：研究进度与未解决问题追踪（本文件）

> **合规说明**：反编译所得的临时 `.cs` 文件仅存放于会话私有 scratch 目录，未复制到 `projects/SLPerformance/`，未分发任何原版游戏 DLL 或反编译源码。

---

## 3. 部署要求与尚未在真实服务端现场验证的事项

### 3.1 运行时部署前提
1. 将 `outputs/SLPerformance.dll` 放入 LabAPI 插件目录（例如 `LabAPI/plugins/global/` 或对应端口目录）。
2. 确保 LabAPI 依赖目录（`LabAPI/dependencies/global/` 或对应端口目录）存在 **`0Harmony.dll`（2.4.2.0 或兼容 2.x 版本）**——原版服务端 `SCPSL_Data\Managed` 不自带 `0Harmony.dll`。
3. 首次启动会自动生成配置，或参考 `outputs/config.example.yml` 调整开关；Tier 3（`OPT-16`、`OPT-17`）默认关闭，按需在压测确认后启用。
4. 插件以 `LoadPriority.Lowest` 最后加载，若检测到 EXILED / AntiCheat 等插件已对同一方法施加冲突 Prefix/Transpiler，默认自动跳过对应模块（`auto_skip_on_harmony_conflict: true`），可通过控制台 `slperf status` 查看每个模块的实际状态。

### 3.2 真实服务端验证状态
以下事项已在真实 SCP:SL `14.2.9` + LabAPI `1.1.7.0` + `SimpleAntiCheat` 多插件 48/48 服务端完成现场实测验证（详情见 `BENCHMARKS.md` 第 5 节）：
1. **Mono 运行时加载、Harmony 共存与不变量核验（已验证）**：18/18 默认模块 `Active`，与 `SimpleAntiCheat`（`WriteAll [Prefix]`、`FpcMotor.Move [Transpiler]`）零冲突共存；`slperf verify` 确认 `FpcStateProcessor.Mask = 0xECC158F9`（`MaskIncludesPlayer = False`），`RoomsByCoords = 99 / 99`。
2. **三轮 48/48 满载剖面与单模块 A/B 对照（已验证）**：主线程均值 `11.77ms -> 7.13ms`（`-39.4%`），P99 帧 `16.69ms` / Max `16.92ms`（60.00 TPS），`C# LateUpdate` `-80.4%`，GC 分配 `-81.6%`（间隔 `3.35s -> 16.64s`）；单变量实测确认 `OPT-18` 净省 `-1.22ms`、`OPT-16` 维持默认关闭。
3. **`OPT-21` 大规模切角色实服实测（v1 -> v4 已实测，v5 待实测）**：
   - 48/48 冷开局 Round 1 最大帧：`365~427ms`（无 OPT-21）-> `315.35ms`（v2）-> `224.60ms`（v3，`CreateItem = 5.45ms [Hit 86/86]`，`PoolMiss = 0`）；
   - 同模板 48× 切换（v4 `Reuse = 48`）：`RoleSetup` 从 `61.65ms` 降至 **`11.42ms`（`-81.5%`）**（`Disable 21.3ms -> 2.52ms`，`SetModel 29.6ms -> 1.17ms`），48× 重装 `forceclass` 最大帧从 `179~195ms` 降至 **`122.49ms`**；
   - **`OPT-21 v5` 实服验收（已验证）**：热激活驻留池（`WarmParkedNow = 172`）在 48 人跨兵种切换中将 `SetActive(true)` 从 `24.51~33.87ms` 降至 **`0.00ms`**，`SetModel` 降至 **`5.78ms`**，`Disable` 从 `21.3ms` 降至 **`8.29ms`**，48× 重装跨模板切换总耗时降至 **`80.48ms`**；冷开局 Round 1 49 人 `RoleChange` 总耗时降至 **`100.91ms`**（`PoolMiss = 0`，`CreateItem Hit 86/86 = 6.01ms`）。
4. **`OPT-22` 真人玩家加入与认证尖峰消除（v2 真服已验证闭环）**：
   - **v2 真服连续 3 次真人进服实测（甚至在 `120 TPS` / `8.33ms` 帧预算下）**：
     - 首人冷进服（`pid=2`）：`ReqAuth = 0.59ms (ECDH=HIT)`，`OffloadedCrypto = 15.96ms` 离主线程；`AuthFinishMain = 42.48ms` 经 X-Ray 确认为 `CustomizableUIMeowLabAPI`（`14.12ms`）与 `TextChatMeow`（`9.44ms`）等插件开服后一次性首次 JIT。
     - **第 2、3 次真人进服（`pid=3`、`pid=4`，正常线上态）**：`ReqAuth = 0.04~0.05ms (ECDH=HIT)`、`AuthEnqueue = 0.02~0.03ms`、`OffloadedCrypto = 6.48~24.27ms`（100% 移出主线程）、**`AuthFinishMain = 1.68~1.92ms`**（`Bans 0.37~0.41ms`、`Finalize 0.62~0.66ms`、`OnJoined 0.69~0.84ms`），**进服瞬间稳态 `119.8~119.9 / 120 TPS`（最大帧仅 `10.28~10.55ms`，零掉帧）**。
   - **48–60+ 全真人玩家高峰期网络吞吐**：当前 48/48 稳态压测含 47 个 Dummy，真人高峰期高并发语音与高频交火下的 `slperf netstats on` 与 Tier 3 `OPT-17` 收益仍建议现场观察。
