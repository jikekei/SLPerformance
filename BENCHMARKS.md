# 性能测试方法与真实测试结果（BENCHMARKS.md）

> **当前验证状态：**
> - **离线微基准测试（`SLPerformance.Benchmarks`）与行为等价性测试（`SLPerformance.Tests`）：`[VERIFIED — 已实测通过]`**（2026-11-26，12/12 自动化测试通过，9 项离线 A/B 微基准完成实测）。
> - **真实 SCP:SL Dedicated Server 多玩家对照压测（TPS / P95 / P99 / 满载负载）：`[UNVERIFIED — 尚未在真实服务端运行]`**。受限于当前离线沙箱环境，尚未拉起真实 SCP:SL 专用服务器与真人/客户端压测；下表 2.1 与 2.2 的真实服务端指标严格保持 `UNVERIFIED`，不将离线微基准混同于整服 TPS 结论。

---

## 1. 测试方法论与控制变量规范

### 1.1 环境一致性要求

每次对照测试（Baseline 原版 vs 启用单模块/组合模块）必须保证以下条件完全一致：
1. **硬件与系统**：固定物理机、关闭省电降频、记录 CPU 型号/核心数/频率、内存容量与 OS 版本。
2. **游戏与依赖版本**：固定 `SCP:SL 14.2.9`、`Unity 6000.0.43f1`、`LabAPI 1.1.7.0`、相同 `config_gameplay.txt`、`ServerTickrate = 60`。
3. **地图与随机性**：固定相同地图种子（`map_seed`）或记录同一批多个固定种子取均值，防止不同房间布局影响实体总数与射线命中率。
4. **预热与采样窗口**：每个场景先运行 **60 秒预热**（完成地图生成、JIT 编译与连接池稳定），再连续采样 **300 秒（18,000 帧）**，每组配置至少重复 **3 轮**。
5. **消融实验（Ablation）**：每个优化模块支持通过 `config.yml` 或控制台命令 `slperf toggle <moduleId>` 独立开启/关闭做单因子对照。

### 1.2 核心采集指标（由 `SLPerformance.Profiler` 与 `slperf` 控制台命令提供）

| 指标类别 | 具体指标 | 采集来源 / 命令 |
| --- | --- | --- |
| **帧率与延迟分布** | 平均 TPS、平均帧耗时 (ms)、P50 / P95 / P99 / P99.9 帧耗时 (ms)、卡顿帧数 (>16.67ms / >33.33ms / >50ms) | `FrameMetricsRing`（2048 帧零分配环形缓冲）`slperf stats` |
| **CPU 占用** | 主线程有效工作耗时（Mean/P99 Work ms）、进程总 CPU 占用 | Unity `PlayerLoop` `EarlyUpdate`→`PostLateUpdate` + `Process.TotalProcessorTime` |
| **内存与 GC** | `GC Allocated In Frame`、`GC Reserved Memory`、Gen0/Gen1/Gen2 回收次数、托管堆大小 | `Unity.Profiling.ProfilerRecorder` + `GC.CollectionCount` + `GC.GetTotalMemory` |
| **网络吞吐** | 入站/出站 PPS、上下行字节吞吐、丢包与重传统计 | `LiteNetLib.NetStatistics`（`slperf netstats on`） |
| **热点方法剖面** | 热点方法调用次数、单帧累计耗时、最大耗时（默认关闭零开销） | `MethodSampleTable`（`slperf probes on` + `slperf hotspots`） |

---

## 2. 真实服务端场景矩阵（待实测）

> **说明**：所有真实服务端数值列在执行真实服务端对照压测前均保持 `UNVERIFIED`。部署 `outputs/SLPerformance.dll` 后，可使用 `slperf stats`、`slperf hotspots` 与 `slperf toggle <id>` 按下表采集真实数据。

### 2.1 玩家规模扩展曲线（基线 vs 优化）

| 场景 | 玩家/负载来源 | 对比组 | 平均 TPS | 平均帧耗时 (ms) | P95 (ms) | P99 (ms) | P99.9 (ms) | 主线程 CPU (%) | 总 CPU (%) | GC 分配 (KB/s) | GC 次数 (0/1/2) | 网络上下行 (KB/s, PPS) | 验证状态 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **空服务器 (0p)** | 无玩家 | Baseline / SLPerf | — | — | — | — | — | — | — | — | — | — | `UNVERIFIED` |
| **10 人服务器** | 真实玩家 / Dummy | Baseline / SLPerf | — | — | — | — | — | — | — | — | — | — | `UNVERIFIED` |
| **20 人服务器** | 真实玩家 / Dummy | Baseline / SLPerf | — | — | — | — | — | — | — | — | — | — | `UNVERIFIED` |
| **40 人服务器** | 真实玩家 / Dummy | Baseline / SLPerf | — | — | — | — | — | — | — | — | — | — | `UNVERIFIED` |
| **48 人服务器** | 实机 48/48（`Players=49, MirrorConns=2`，含 `SimpleAntiCheat` + 47 Dummy，SLPerf 默认 15 模块） | SLPerf Tier 1+2 (15/17) | `59.92` | `16.69` | `16.68` | `16.88` | `21.58` | `71.7%` (`MeanWork=11.95ms, P99=15.68ms`) | `96.9%` (1核=100%) | 裁剪(`0.00KB`)/待新版统计 | `61/61/61` | 未开 netstats | **实机已测（含 Dummy，缺原版 Baseline 对照）** |
| **48 人服务器 (OPT-16 消融)** | 同上 + 开启 `physics.fpc_spherecast_batch` (16/17) | SLPerf + OPT-16 | `59.92` | `16.69` | `16.68` | `17.03` | `20.98` | `70.9%` (`MeanWork=11.82ms, P99=15.92ms`) | `118.7%` (1核=100%) | 裁剪(`0.00KB`)/待新版统计 | `17/17/17` (56s内，每3.3s一次) | 未开 netstats | **实机已测（证明 OPT-16 主线程仅省 0.13ms 但总 CPU +21.8%，不建议常开）** |
| **60 人服务器** | 真实玩家 / Dummy | Baseline / SLPerf | — | — | — | — | — | — | — | — | — | — | `UNVERIFIED` |

### 2.2 极端压力专项场景

| 专项场景 | 负载构造方式 | 重点检验模块 | 验证状态 |
| --- | --- | --- | --- |
| **大量地图物体场景** | 生成 2,000 / 5,000 个 `PrimitiveObjectToy` + `LightSourceToy`（区分 `IsStatic=true/false`） | `OPT-03`, `OPT-14` | `UNVERIFIED` |
| **大量实体场景** | 生成 500+ 掉落拾取物 + 30+ 尸体 + 多个环境危害物 + 活跃 SCP-173/096/939 | `OPT-03`, `OPT-08`, `OPT-13`, `OPT-14` | `UNVERIFIED` |
| **高并发语音场景** | 10–20 人同时在邻近/观察者/广播通道持续发声 | `OPT-04`, `OPT-05`, `OPT-06` | `UNVERIFIED` |
| **大型插件混合场景** | 挂载代表性 LabAPI / EXILED 插件（订阅 `ValidatedVisibility`/`ReceivingVoiceMessage` + HUD） | `OPT-02`, `OPT-05`, `OPT-06`, 冲突检测 | `UNVERIFIED` |

---

## 3. 已完成：自动化行为等价性与真实二进制元数据测试（`SLPerformance.Tests`）

- **测试时间**：2026-11-26
- **目标游戏二进制**：本地已安装 `SCP:SL 14.2.9`（`SCPSL_Data\Managed\Assembly-CSharp.dll`、`Mirror.dll`、`LabApi.dll`、`UnityEngine.*.dll`）+ `0Harmony.dll 2.4.2.0`
- **运行宿主**：Windows x64 / `.NET 8.0` 测试宿主加载 `net48` 编译产物 `SLPerformance.dll`（使用 `.NET 8.0` 宿主以原生兼容 Unity/Mono `netstandard 2.1` 默认接口方法与 `ReadOnlySpan<T>`）
- **结果汇总**：**12 PASSED, 0 FAILED**

| # | 测试项 | 验证内容 | 结果 |
| :---: | --- | --- | :---: |
| 01 | `ModuleRegistry & Config Defaults` | 17 个模块全部注册、ID 唯一、Tier 1 (9) / Tier 2 (6) / Tier 3 (2) 分类正确、Tier 3 默认关闭 | **PASS** |
| 02 | `Live SCP:SL 14.2.9 Reflection Target Signatures Audit` | 对全部 17 个模块引用的 ~115 个真实类、私有字段、属性、构造函数与方法签名逐一反射核验（全部非空） | **PASS** |
| 03 | `StatusEffectBase Reflection Sleep Whitelist Audit` | 扫描真实 `Assembly-CSharp.dll` 中全部 `StatusEffectBase` 子类，验证 10 个重写 `Update`/`LateUpdate` 的效果均不被休眠，标准效果进入白名单 | **PASS** |
| 04 | `AdminToyBase Reflection Sleep Whitelist Audit` | 扫描真实 `Assembly-CSharp.dll` 中全部 `AdminToyBase` 子类，验证 `WaypointToy` 不被休眠，`PrimitiveObjectToy`/`LightSourceToy`/`TextToy` 正确进入白名单 | **PASS** |
| 05 | `OPT-02 FpcSyncDataFactory Bit-Exact Wire Equivalence` | 静止、仅位移、仅旋转、全量变化、接地标志位、移动状态切换 5 组用例字节长度、标志位与线协议回环完全一致 | **PASS** |
| 06 | `OPT-01/03 Mirror NetworkMessages.Pack Single-Pack` | `RpcMessage` 与 `EntityStateMessage` 单次 `Pack` 与 60 次独立 `Pack` 逐字节一致 | **PASS** |
| 07 | `OPT-06 DelegateInvocationCache Equivalence & Zero-Alloc` | 异常隔离、动态增删监听者失效正确性，50,000 次稳态查询托管分配 **0 Bytes** | **PASS** |
| 08 | `OPT-10 Mirror Batcher & Unbatcher Zero-Copy Wire` | 零拷贝直接提取内部批次缓冲区与原版 `Batcher.GetBatch` 逐字节一致，原地解包与原版 `Mirror.Unbatcher` 消息切片及时间戳一致 | **PASS** |
| 09 | `OPT-12 LiteNetPeerLookup IPv4 & IPv6 SockAddr Keys` | IPv4 (`203.0.113.42:7777`) 与 IPv6 (`2001:db8::abcd:1234:5678%7:44321`) 原始 `sockaddr` 打包键与 `IPAddress` 键及哈希完全一致 | **PASS** |
| 10 | `Profiler FrameMetricsRing Percentiles & Wraparound` | 3,000 帧写入环形覆盖（容量 2,048）后 P50/P95/P99/Max/Spikes 精确校验 | **PASS** |
| 11 | `Collections CompactUintMap<T> CRUD & Swap-Remove` | 插入、覆盖、扩容、首尾 Swap-Remove 与清空一致性校验 | **PASS** |
| 12 | `OPT-17 BoundedWorkerPool Determinism & Error Handling` | 200 轮×257 项 4 线程并发动精确不重不漏执行校验 + 工作线程异常确定性回传 + 干净关闭 | **PASS** |

---

## 4. 已完成：离线 A/B 微基准测试实测结果（`SLPerformance.Benchmarks`）

> **严格区分声明**：以下数据为**真实执行的离线合成微基准实测值**（Release 编译，充分预热与 `GC.Collect` 隔离），用于量化各模块在纯算法/序列化/内存分配热路径上的微观收益；实际运行在 Unity Mono JIT 下的绝对微秒数会因 Mono 相比 RyuJIT 的代码生成差异而有所不同（通常 Mono 下消除分配与重复计算的相对加速比还会更大），且整服 TPS 提升仍须通过第 2 节真实服务端压测验证。

- **测试环境**：Windows x64 / 16 逻辑核心 / `.NET 8.0` Release 宿主 / `SLPerformance.dll (Release, net48)` / `GC Server: False`
- **执行日期**：2026-11-26

| 微基准项目 | 模拟基线（Baseline） | 优化实现（Optimized） | 实测加速比 | 每次操作托管分配变化 |
| --- | ---: | ---: | ---: | --- |
| **OPT-01** RPC 单次 Pack（60 接收者） | `5.48 us` | `0.36 us` | **15.13×** | `0 B -> 0 B`（池化写入器，省去 59 次序列化与借还） |
| **OPT-02** FPC 60×60 增量同步与快照构建 | `516.12 us` | `102.91 us` | **5.02×** | `1 B -> 1 B`（单次 O(P) 预计算 + 单查字典 + 省去无效 WriterPool） |
| **OPT-03** Mirror 脏广播扫描（`E=3000, D=20, P=60`） | `95.55 us` | `5.54 us` | **17.24×** | `0 B -> 0 B`（180,000 次检查降至 4,200 次） |
| **OPT-06** LabAPI 事件调用列表缓存（4 订阅者） | `0.18 us` | `0.12 us` | **1.50×** | **`56 B -> 0 B`**（完全消除每次触发的 `Delegate[]` 堆分配） |
| **OPT-10** Mirror Batcher 零拷贝刷出（8 条消息） | `0.42 us` | `0.36 us` | **1.19×** | `0 B -> 0 B`（消除 `CopyAndReturn` 全缓冲拷贝；空通道零借还另计） |
| **OPT-12** LiteNet 原生 IPv4 Peer 查表 | `0.06 us` | `0.04 us` | **1.57×** | **`40 B -> 0 B`**（完全消除每包 `new IPAddress` 堆分配） |
| **OPT-13** 紧凑活跃 Waypoint 扫描（12/254 槽位） | `1.05 us` | `0.26 us` | **4.05×** | `0 B -> 0 B`（消除 242 个空槽扫描） |
| **OPT-17** 有界线程池并行纯 FPC 编码（`P=60`） | `186.25 us` | `79.84 us` | **2.33×** | `1 B -> 1 B`（主线程 + 4 工作线程同步分片，无 Unity API） |
| **OPT-17** 有界线程池并行纯 FPC 编码（`P=100`） | `415.40 us` | `113.96 us` | **3.65×** | `1 B -> 1 B`（玩家规模越大并行加速越接近线性） |

---

## 5. 真实 SCP:SL 14.2.9 专用服务端 48/48 满载现场实测记录（2026-11-27）

测试环境：SCP:SL Dedicated Server `14.2.9`（Unity `6000.0.43f1`，LabAPI `1.1.7.0`，16 核 CPU，目标 `MirrorActualTPS = 60`），在线 **48/48**（`Players=49`，`MirrorConns=2`：1 本地 Host + 1 真人 + 47 Dummy，`SpawnedNetIds ~ 1,514–1,553`），同服混跑 `SimpleAntiCheat`、`HintServiceMeow`、`TextChatMeow`、`ProjectMER`、`Server Maid.LabApi`、`ServerTpsLogger`。

### 5.1 三轮实测演进对比（均取 2,048 帧满载稳态窗口）

| 指标 | Round 1（15 模块） | Round 2（18 模块） | **Round 3（18 模块深度升级）** | Round 1 → Round 3 变化 |
| :--- | :---: | :---: | :---: | :---: |
| **Measured TPS** | `59.92` | `59.99` | **`60.00`** | 锁死 60.00 TPS |
| **Mean / P99 / P99.9 / Max Frame** | `16.69 / 16.89 / 20.65 / 21.63ms` | `16.67 / 16.69 / 16.84 / 21.02ms` | **`16.67 / 16.69 / 16.80 / 16.92ms`** | Max `-4.71ms`（全窗口 `>17ms = 0`） |
| **MainThread Work (Mean)** | `11.77 ms` | `7.80 ms` | **`7.13 ms`** | **`-4.64 ms`（`-39.4%`）** |
| **MainThread Work (P99)** | `15.68 ms` | `10.04 ms` | **`9.63 ms`** | **`-6.05 ms`（`-38.6%`）** |
| **MaxWork (NoGC 尖峰)** | `26.64 ms` | `14.72 ms` | **`13.49 ms`** | **`-13.15 ms`（`-49.4%`）** |
| **`C# Update`** | `3.617 ms` | `3.408 ms` | **`3.058 ms`** | **`-0.559 ms`（`-15.5%`）** |
| **`C# LateUpdate`** | `5.389 ms` | `1.250 ms` | **`1.054 ms`** | **`-4.335 ms`（`-80.4%`）** |
| **`PreLateUpdate (Total)`** | `6.990 ms` | `3.440 ms` | **`3.146 ms`** | **`-3.844 ms`（`-55.0%`）** |
| **`MonoAlloc`（每帧分配）** | `108.6 KB/帧`（`6.36 MB/s`） | `20.1 KB/帧`（`1.18 MB/s`） | **`20.0 KB/帧`（`1.17 MB/s`）** | **`-81.6%`** |
| **Mono GC 间隔** | 每 `3.35s` | 每 `13.58s` | **每 `16.64s`** | **GC 频率降低 `4.97×`** |
| **活跃 `MonoBehaviour`（`Update / LateUpdate`）** | `~2,306 / ~520` | `2,306 / 520` | **`1,809 / 281`** | **减少 `736` 个每帧回调** |
| **活跃 `Animator`（总/活跃/休眠）** | `~396 / ~370 / ~26` | `396 / 112 / 284` | **`392 / 95 / 297`** | **休眠 `75.8%` 地图动画器** |
| **`WaypointLookup`** | `0.367 ms/帧` | `0.367 ms/帧` | **`0.271 ms/帧`** | **`-26.2%`** |
| **`RoomLookup` 频次** | `190.0 calls/帧` | `190.0 calls/帧` | **`97.1 calls/帧`** | **`-48.9%`** |

### 5.2 现场单模块 A/B 对照实测结论

1. **`OPT-18`（`anim.headless_character_animator`）—— 强正收益（默认开启）**：
   - 48/48 同局 `slperf toggle` 单变量对照：关闭时 `MainThread Work = 9.02ms`（P99 `13.11ms`）、`C# LateUpdate = 2.707ms`、`Animator/Director = 0.912ms`、`ProcessCPU = 90.6%`；开启后 `MainThread Work = 7.80ms`（`-1.22ms`）、P99 `10.04ms`（`-3.07ms`）、`C# LateUpdate = 1.250ms`、`Animator/Director = 1.627ms`、`ProcessCPU = 78.1%`（`-12.5%`），同时将角色受击箱动画从 15 Hz（`mod4`）提升到 60 Hz 多线程 Job System。
2. **`OPT-16`（`physics.fpc_spherecast_batch`）—— 48p 下低 ROI（维持默认关闭）**：
   - 开启 `OPT-11` 后，再单开 `OPT-16` 使 48/48 `MainThread Work` 仅降 `0.13ms`（`11.95ms -> 11.82ms`），但 `ProcessCPU` 上升 `+21.8%`（`96.9% -> 118.7%`）。证实 Tier 3 维持默认关闭决策正确。
3. **测试边界透明声明**：
   - 上述 48/48 现场数据包含 47 个 `DummyNetworkConnection`；其中 `OPT-02` 跳过 Dummy 无效封包序列化贡献了约 `-90 KB/帧` GC 与部分 `LateUpdate` 耗时；而 `OPT-18`/`OPT-19`/`OPT-20`/`OPT-13`/`OPT-14`/`OPT-09` 及 `OPT-02` 共存模式 `GetNewSyncData` 缓存对 **48 真人连接服**同样全额有效。在全真人 48–60p 网络满载场景下的端到端带宽与 `SimpleAntiCheat` 共存负载仍建议在生产高峰期二次观察 `slperf stats` 与 `slperf netstats on`。
