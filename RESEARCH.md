# SCP:SL 原生服务端深度研究报告（RESEARCH.md）

> **研究阶段**：第一阶段（环境与程序集分析）+ 第二阶段（原版服务端全面研究）已完成（静态反编译与代码路径审计）。
> **实测状态声明**：本文档中的所有代码结构、调用链、复杂度与分配频次分析均基于目标磁盘程序集的静态反编译与元数据检查（`[STATICALLY VERIFIED]`）。尚未在真实运行中的服务端或多玩家压测环境中采集运行时耗时与 GC 实测数据（`[RUNTIME UNVERIFIED]`），任何实测收益将在第四阶段自研低开销采样器上线后补充至 `BENCHMARKS.md`。

---

## 1. 第一阶段：环境与程序集清单

### 1.1 目标服务端与运行时环境

检查目录：`C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server`

| 项目 | 实测值 | 来源 / 依据 |
| --- | --- | --- |
| 游戏版本 | **SCP:SL `14.2.9`** (`BuildType = Release`, `ActiveHoliday = None`, `BackwardCompatibility = false`) | `Assembly-CSharp.dll` → `GameCore.Version` 静态构造函数 |
| 游戏文件协议版本 | `_expectedGameFilesVersion = 4` | `Assembly-CSharp.dll` → `CustomNetworkManager` |
| Unity 引擎版本 | **Unity 6 (`6000.0.43f1 (97272b72f107)`)** | `SCPSL.exe` FileVersion / ProductVersion |
| LocalAdmin 版本 | `2.5.16` | `LocalAdmin.exe` FileVersion |
| 脚本运行时 | **MonoBleedingEdge (`mono-2.0-bdwgc.dll`, 7,827,640 bytes)** | `MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll` |
| 基础类库 Profile | `mscorlib.dll` (`4.0.0.0` / FileVer `4.6.57.0`), `netstandard.dll` (`2.1.0.0`) | `SCPSL_Data\Managed` |
| 引擎启动配置 (`boot.config`) | `gc-max-time-slice=3`（增量 GC 开启，时间片 3ms），`gfx-enable-gfx-jobs=1`，`gfx-threading-mode=6` | `SCPSL_Data\boot.config` |
| 默认服务端 Tickrate | `ServerStatic._serverTickrate = 60` (`Application.targetFrameRate = 60`) | `Assembly-CSharp.dll` → `ServerStatic` |
| 官方插件 API | **LabAPI `1.1.7.0`** (`1.1.7+84b0da472e3ce42bf86cb49bde8379d00605a8f6`) | `SCPSL_Data\Managed\LabApi.dll` |
| 公共基础库 | `NorthwoodLib.dll` (`1.4.0.0`)、`Pooling.dll` | `SCPSL_Data\Managed` |
| 传输层位置（重要修正） | **不存在独立的 `LiteNetLib.dll`**；`LiteNetLib`（45 个类型）与 `Mirror.LiteNetLib4Mirror` 均直接编译进 **`Assembly-CSharp.dll`** | 实际目录与反编译命名空间核验 |
| 原生动态库 (`Plugins\x86_64`) | `lib_burst_generated.dll` (1.14 MB), `libcaress.dll` (1.60 MB), `libopus-0.dll` (365 KB), `librnnoise-nu-1.dll` (1.05 MB), `steam_api64.dll` (319 KB) | `SCPSL_Data\Plugins\x86_64` |

### 1.2 核心托管程序集清单

| 程序集 | 大小 (Bytes) | 修改时间 | SHA-256 前 16 位 | 关键职责 |
| --- | ---: | --- | --- | --- |
| `Assembly-CSharp.dll` | 4,303,360 | 2026-11-18 16:12:10 | `E098A8DDA09D6CE1` | 全部核心玩法（3,094 个类文件）、内置 `LiteNetLib`、`LiteNetLib4Mirror`、`Utf8Json`、`VoiceChat`、`RelativePositioning` |
| `Mirror.dll` | 191,488 | 2026-11-18 16:12:10 | `4D56A0B90E065939` | Northwood 定制版 Mirror（包含 `NetworkLoop`, `NetworkServer`, `NetworkIdentity`, `NetworkBehaviour`, `Batcher`, `Unbatcher`, `IpOverride` 等定制字段） |
| `Mirror.Components.dll` | 103,936 | 2026-09-22 17:04:39 | `CC3B653DCB37F232` | Mirror 附加组件（`SpatialHashingInterestManagement` 等，但原版服务端未启用它们，而是使用 `CustomInterestManagement`） |
| `Assembly-CSharp-firstpass.dll` | 187,392 | 2026-09-22 17:04:42 | `67BF73E81A8C0B69` | `MEC.Timing`（协程调度器）、Discord SDK |
| `LabApi.dll` | 822,784 | 2026-09-22 17:04:20 | `87E0ED61AC027BDF` | 官方插件加载器、事件系统 (`EventManager` / `PlayerEvents` 等)、Wrappers (`Player`, `Door` 等) |
| `NorthwoodLib.dll` | 48,224 | 2026-09-22 17:04:41 | `0672CDAE67F0CCC1` | `ListPool<T>`, `HashSetPool<T>`, `StringBuilderPool`, `ActionDispatcher` |
| `Pooling.dll` | 10,752 | 2026-09-22 17:04:39 | `018AC2B5A25CA2B0` | `GameObjectPools.PoolManager` / `Pool` / `PoolObject` |

> **关键发现**：`Assembly-CSharp.dll` 与 `Mirror.dll` 的修改时间均为 `2026-11-18 16:12:10`，而工作区 `huanmengyinghecajiang0\PluginDependencies\Assembly-CSharp-Publicized.dll` 大小为 `4,301,312` bytes（版本不一致/更旧）。构建 `SLPerformance` 时**不得引用陈旧的 Publicized DLL**，应直接引用服务端 `SCPSL_Data\Managed` 或基于当前版本按需生成。

### 1.3 本地构建与逆向分析工具链

- `.NET SDK`：`8.0.425`、`10.0.401` (`C:\Program Files\dotnet\dotnet.exe`)
- 反编译器：`ilspycmd` `11.1.0.9782`
- 运行时补丁库：本地工作区已有 `0Harmony.dll` (`2.4.2.0`)，但**服务端原版 `Managed` 目录不自带 `0Harmony.dll`**，部署 LabAPI 插件需将 `0Harmony.dll` 放入 LabAPI `dependencies` 目录或嵌入解析。
- 限制记录：当前沙箱环境对 `%APPDATA%\SCP Secret Laboratory` 返回 `Access is denied`，尚未启动真实服务端进行实测。

---

## 2. 第二阶段：六大核心方向源码级研究

### 2.1 游戏主循环与调度机制

1. **Unity PlayerLoop 注入**：
   - `Mirror.NetworkLoop.RuntimeInitializeOnLoad()` (`[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`) 将 `NetworkEarlyUpdate` 注入 `EarlyUpdate` 尾部，将 `NetworkLateUpdate` 注入 `PreLateUpdate` 尾部。
   - `Assembly-CSharp.dll` 本身 **0 处**自定义 `PlayerLoop`、**0 处** `IJob`/`JobHandle`、**0 处** `BurstCompile`（`lib_burst_generated.dll` 仅由 Unity 渲染管线/Collections 包生成，服务端游戏逻辑完全未利用 Jobs/Burst）。
2. **MonoBehaviour 生命周期密度**：
   - `Assembly-CSharp.dll` 中存在 **346 个 `Update()`**、**21 个 `FixedUpdate()`**、**76 个 `LateUpdate()`**，其中包含 **20 个完全空方法体**（例如 `PlayerEffectsController.Update()`、`NicknameSync.Update()`、`CustomNetworkManager.Update()`、`TeslaGate.Update()`、`AspectRatioSync.FixedUpdate()`、`ThirdpersonItemBase.LateUpdate()`）。在 Unity 引擎中，即使方法体为空，只要 MonoBehaviour 声明了回调且 `enabled == true`，每帧仍会触发 C++ → Mono 跨边界调用。
   - 最严重的高实例数空转来源于 **`CustomPlayerEffects.StatusEffectBase.Update()`**：每个玩家挂有数十个（50–80+）状态效果 MonoBehaviour，且 `PlayerEffectsController.Start()` 将 `effectsGameObject` 全局激活。60 人满载时，Unity 原生循环每帧要跨界调用 **3,000–4,800+ 次** `StatusEffectBase.Update()`，其中 >95% 的实例 `Intensity == 0` 直接返回。静态扫描证实 `CustomPlayerEffects` 下 **0 个类**重写 `OnEnable`/`OnDisable` 或读取 `base.enabled`，且仅有 10 个子类声明了自己的 `Update`/`LateUpdate`。
3. **集中式委托更新 (`StaticUnityMethods`)**：
   - `StaticUnityMethods` 提供 `OnUpdate`（32 处订阅点）、`OnLateUpdate`（5 处订阅点）、`OnFixedUpdate`（0 处订阅点）。
   - **关键隐患**：`PickupStandardPhysics` 为地图上**每一个拾取物实例**都执行 `StaticUnityMethods.OnUpdate += OnUpdate`。当地图上有 300–600 个掉落物品时，该多播委托调用链长度膨胀至数百；不仅每帧对已休眠的 `Rigidbody` 执行数百次原生 `Rb.IsSleeping()` P/Invoke，且每次掉落/拾取触发 `Delegate.Combine`/`Delegate.Remove` 都会在堆上复制整个长度数百的 `Delegate[]` 数组。
4. **场景切换强制全量 GC (`MemoryCleaner`)**：
   - `MemoryCleaner.CleanupMemory` 在每次 `SceneManager.sceneLoaded` 时启动后台线程执行 `LOH CompactOnce + GC.Collect() + WaitForPendingFinalizers() + GC.Collect()`，叠加 `Resources.UnloadUnusedAssets()`。虽然只发生在回合切换，但会与新场景初始化竞争锁。
5. **主线程分发器与 MEC 协程**：
   - `MainThreadDispatcher` 封装 `NorthwoodLib.ActionDispatcher` 在 `Update`/`LateUpdate`/`FixedUpdate` 分发跨线程回调。
   - `MEC.Timing` (`Assembly-CSharp-firstpass.dll`) 以数组槽位 + `Dictionary<CoroutineHandle, ProcessIndex>` 管理协程，定期（每 64 帧）执行压缩维护。

---

### 2.2 网络底层、传输层与同步架构

这是原版 SCP:SL 服务端**最核心的扩展性瓶颈所在**，在 40–60 人及大量实体场景下呈现明显的 $O(P \times E)$ 与 $O(P^2)$ 退化。

1. **LiteNetLib → LiteNetLib4Mirror → Mirror 收包流水线存在严重设计冗余**：
   - `LiteNetLib.LiteNetManager` 内部已经有独立 `ReceiveThread`（默认开启 `use_native_sockets = true` 走 `NativeReceiveLogic`）与 `LogicThread`（`UpdateTime = 15ms`）。
   - **瓶颈 A (`NativeReceiveLogic`)**：`Assembly-CSharp.dll` 中 `LiteNetManager.NativeReceiveLogic` 每收到一个 UDP 数据包就执行 `new IPAddress(newAddress)`（或 IPv6 `new IPAddress(ReadOnlySpan<byte>, scope)`）堆分配，仅为了调用 `TryGetPeer(tempEndPoint, out actualValue)` 查询已知 Peer。
   - **瓶颈 B (`NetworkPollThread` 30ms 休眠 + 每包闭包分配)**：`LiteNetLib4MirrorTransport.StartThreads()` 额外起了一条 `NetworkPollThread`，循环执行 `Host.PollEvents(); Thread.Sleep(30);`！它把 `LiteNetLib` 内部线程安全队列 `_pendingEventHead` 里的事件取出来，在 `LiteNetLib4MirrorServer.OnNetworkReceive` 中**为每一个 UDP 数据包分配一个闭包类对象 + `Action` 委托**，再塞进第二个队列 `ConcurrentQueue<Action> Events`，最后主线程在 `LiteNetLib4MirrorTransport.LateUpdate()` 中消费。
     - 后果 1：60 人场景下每秒数千至上万个数据包产生 **10,000–20,000+ 个闭包/委托堆对象/秒**。
     - 后果 2：`Thread.Sleep(30)` 导致收包以 ~31–33Hz 脉冲式涌入 60Hz 主线程，偶数帧空包、奇数帧双倍包冲击，直接制造 **P95/P99 帧耗时毛刺** 与 **0–30ms 人为输入延迟**。
     - 核验结果：全代码库除 `LiteNetLib4Mirror*` 自身外，无任何代码直接依赖 `Events` 队列；而耗时连接鉴权（ECDSA 验签）已经通过独立的 `_connectionRequests` + `ConnectionRequestThread` 异步处理，不阻塞 `PollEvents()`。
   - **瓶颈 C (`Mirror.Unbatcher.AddBatch` 冗余拷贝)**：`NetworkServer.OnTransportData` 收到 `ArraySegment<byte>` 后，即便 `!isLoadingScene && unbatcher.BatchesCount == 0`（99.99% 常态），仍先租借 `NetworkWriterPooled` 并 `Array.ConstrainedCopy` 完整拷贝整包数据，立刻在紧随其后的 `while` 循环中读完并归还。
2. **Mirror 实体广播 (`NetworkServer.Broadcast`) 与 `CustomInterestManagement` 的 $O(P \times E)$ 灾难**：
   - `CustomInterestManagement.OnCheckObserver` 恒返回 `true`，所有就绪玩家均观察场景中**全部** `NetworkIdentity`（常态 500–1,200 个；使用 AdminToys / 自定义地图插件时可达 2,000–8,000+ 个）。
   - `NetworkServer.Broadcast()` 每帧（60Hz）采用**连接在外、实体在内**的双重循环 `for (conn in connectionsCopy) for (identity in conn.observing)`：
     - 60 人 × 1,000 实体 × 60Hz = **3,600,000 次/秒**循环（5,000 AdminToys 时达 **18,000,000 次/秒**）。
     - 每次迭代均触发 1 次 Unity 原生 `identity != null` 检查 + 1 次 `Time.frameCount` 原生调用，即使 98%+ 的实体本帧**完全没有脏数据**！
     - 对确实有脏数据的实体，`BroadcastToConnection` 为每个观察者单独调用 `connection.Send(new EntityStateMessage { ... })`，导致同一份 `observersWriter` payload 被重复 `NetworkMessages.Pack` + `Array.ConstrainedCopy` **$P$ 次**。
   - **Mirror `NetworkBehaviour.SendRPCInternal` 严重缺陷**：代码租借 `networkWriterPooled` 并执行 `networkWriterPooled.Write(rpcMessage)` 后**竟然完全丢弃不用**，在循环中对每个 observer 再次调用 `value.Send(rpcMessage, channelId)`，导致每次 `[ClientRpc]` 发生 **$P + 1$ 次完整序列化与拷贝**（60 人即 61 次）！
   - **`NetworkConnection.Update()` + `Batcher.GetBatch()` 冗余拷贝与空池操作**：每帧对每个连接每个已创建频道无条件租借/归还 `NetworkWriterPooled`（即使队列为空），且发送时 `CopyAndReturn` 将已经打包好的 `NetworkWriterPooled` 再次拷贝进第二个 `NetworkWriter` 后才交给传输层。
3. **玩家位置同步 (`FpcServerPositionDistributor`) 的 $O(P^2)$ 超级热点**：
   - 注册于 `StaticUnityMethods.OnLateUpdate`，以 `Mathf.Clamp(ServerTickrate, 10, 60)` 频率（默认 60Hz）对每个 `receiver` 发送 `FpcPositionMessage`，触发 `WriteAll(receiver, writer)` 遍历全部 `ReferenceHub.AllHubs`。
   - 60 人满载时内层循环达 **$60 \times 59 \times 60 = 212,400$ 次/秒**，每次迭代暴露 **7 重叠加浪费**：
     1. 无条件 `new PlayerValidatedVisibilityEventArgs(receiver, allHub, !flag2)` → 即使无插件订阅 `PlayerEvents.ValidatedVisibility`，也每秒分配 **212,400 个堆对象** + 执行 **424,800 次** `LabApi.Player.Get()`（内部触发 `ReferenceHub.GetHashCode()` → `gameObject.GetHashCode()` 原生调用！）。
     2. `GetVisibleRole(receiver, allHub)` **重复调用第二次** `VisibilityController.ValidateVisibility(allHub)`；且在未判断 `flag`（是否不可见）前，就无条件调用 `Vector3.Distance(receiver.transform.position, target.transform.position)`（两次原生 `transform.position` + `Sqrt`）、权限位检查、队伍/角色查询与 `IsCommunicatingGlobally()`——而当 `flag == false`（90%+ 场景）时，这些计算结果**完全不被使用**！
     3. `FpcSyncData` 构造函数在 $O(P^2)$ 内层调用 `mLook.GetSyncValues(pos.WaypointId, out _rotH, out _rotV)`（含 `Quaternion.Euler` + `WaypointBase.GetRelativeRotation` + `eulerAngles` + `InverseLerp`），以及读取 `fpmm.IsGrounded`（原生 `CharController.isGrounded`）——这些值仅取决于 `target`，对所有 60 个 `receiver` 完全一致，被重复计算了 **59 倍**！
     4. `FpcVisibilityController.GetActiveFlags` 在 $O(P^2)$ 内层调用 `observer.GetCurrentZone()` → `CurrentRoomPlayerCache.ValidateCache()` → **212,400 次/秒原生 `Time.frameCount`**。
     5. 对每个 pair 无条件 `NetworkWriterPool.Get()` / `Return()`（212,400 次/秒），即使 `_roleSyncEvent == null` 且角色未变化。
     6. `PreviouslySent` 嵌套 `Dictionary<uint, Dictionary<uint, FpcSyncData>>` 每个 pair 执行 **4 次哈希查找**（**849,600 次/秒**），且外层 `receiver.netId` 在整个 `WriteAll` 中恒定；另外玩家断连时外层 `receiver.netId` 条目从不清理（回合内字典泄漏）。
     7. 若 `receiver` 为 SCP-939，`Scp939VisibilityController.ValidateVisibility` 每次被调用（且每帧每目标被调两次！）还执行 `fpcModule.MaxMovementSpeed`（扫描全部状态效果）与 `new RelativePosition(fpcModule.Position)`（全量扫描 254 路标槽位，明明 `fpcModule.RelativePosition` 已缓存！）。
4. **语音转发 (`VoiceTransceiver.ServerReceiveMessage`) 的 $O(S \times P)$ 热点**：
   - 客户端发送频率按 `48000Hz / 480 samples` 可达 **100 包/秒/说话者**。10 人同时开麦（大厅/观察者/广播/刷兵）× 60 人 = **60,000 次内层迭代/秒**。
   - 对每个听众（哪怕 `ValidateReceive` 返回 `VoiceChatChannel.None` 听不到！）都无条件分配 `new PlayerReceivingVoiceMessageEventArgs(allHub, ref msg)` + 2 次 `Player.Get()`；且每个实际收听者都通过 `allHub.connectionToClient.Send(msg)` **重复序列化并拷贝整段 Opus 音频载荷**，而非按目标 `VoiceChatChannel` 打包一次后批量复用 `ArraySegment<byte>`。
   - 此外，`VoiceModuleBase.SpawnObject()` 在专用服务器上也为每个生成角色的玩家调用 `new OpusDecoder()`（P/Invoke 创建原生 C `libopus-0.dll` 解码器），但服务端仅转发原始 Opus 字节流，从不解码。

---

### 2.3 实体、空间索引与玩家状态管理

1. **状态效果接口重复全量扫描 (`PlayerEffectsController.AllEffects`)**：
   - `AllEffects` 在 `Awake()` 后长度与内容固定（通常 50–80+ 元素），但每帧每个存活玩家至少执行 **4–6 次全量遍历 + `is` 接口类型检查**：
     - `FirstPersonMovementModule.VelocityForState` (`IMovementSpeedModifier`)
     - `FpcMotor.TryGetOverride` (`IMovementInputOverride`)
     - `FpcStateProcessor.ServerUseRate` / `ServerRegenRate` / `SprintingDisabled` (`IStaminaModifier`)
     - `AnimatedCharacterModel.PlayFootstep` (`IFootstepEffect`)
     - `VisionInformation.CheckIsInDarkness`（更是使用 **LINQ `AllEffects.Any(...)`** 分配枚举器检查 `IAmbientBoostingEffect`！）
2. **房间网格查询盲区 (`RoomIdentifier.RoomsByCoords` vs 多格大房间)**：
   - 静态检索证实 `RoomsByCoords` 仅在 `RoomIdentifier.RegisterCoords()` 中登记每个房间单个 `MainCoords`（15m × 100m × 15m 网格）。
   - 对于地表（Surface Zone）及任何跨越多格的大型房间（Gate A/B, HCZ 大厅等），`worldPos.TryGetRoom(out room)` **每帧都会 Hash Miss**，从而退化为最多 2 次 `Physics.Raycast`（向上/向下 15m）+ 最多 3 次 `TryGetComponentInParent<...>`！60 人在地表交火时，仅 `CurrentRoomPlayerCache.Update()` 每秒就会产生 **3,600–7,200 次额外射线检测**。
3. **相对坐标路标遍历 (`WaypointBase.ExtractWaypointData`)**：
   - `new RelativePosition(Vector3)` 每次固定遍历 `byte b = 1..254` 共 254 个槽位并虚调用 `SqrDistanceTo(worldPoint)`。每个存活玩家每帧调用 1 次（3,600 次/秒 × 254 槽位 ≈ **914,400 次槽位检查/秒**），另加拾取物、投掷物、SCP-939 等调用。
4. **大型地图/自定义物体瓶颈 (`AdminToyBase.LateUpdate`)**：
   - 当插件生成数千个 `PrimitiveObjectToy`/`LightSourceToy`/`TextToy` 时，即使物体静止，只要未显式设 `IsStatic = true`，每帧均执行 `GetLocalPositionAndRotation` + `localScale`（2 次原生调用）+ 3 次 `GeneratedSyncVarSetter`（虚调用 `EqualityComparer<T>.Default.Equals`）；且即便 `IsStatic = true`，MonoBehaviour 仍保持 `enabled = true` 持续空转 `LateUpdate()`。
5. **其他高频实体循环**：
   - `EnvironmentalHazard.UpdateTargets()`：在遍历 `AllHubs` 的内层循环中重复调用 `SourcePosition`（原生 `transform.position` P/Invoke，60× 冗余）、线性 `AffectedPlayers.Contains`、以及有人停留时每帧无条件分配 `PlayersStayingInHazardEventArgs` + `ListPool<Player>`。
   - `TeslaGateController.FixedUpdate()`：玩家处于待机范围时每 50Hz 分配 `PlayerIdlingTeslaEventArgs`（即使无监听者）。
   - `Inventory.Update()`：即使空背包（观察者/SCP），每帧仍在 `UpdateObserverItems` 租还 `ListPool<ushort>` 并执行 `RefreshModifiers`（4 次 SyncVar setter）。
   - `AhpStat.ServerUpdateProcesses()`：即使 `_activeProcesses.Count == 0`（95%+ 玩家常态），每帧仍租还 `ListPool<int>`。
   - `SyncedStatBase.Update()`：`SyncMode.Private` 或 `PrivateAndSpectators` 状态变化（如冲刺时 `StaminaStat` 每帧变化）走 `SendToHubsConditionally` 遍历全部 60 人。
   - `Scp173ObserversTracker.UpdateObservers()`：**每帧无条件**向全服所有就绪玩家广播 `ServerSendRpc(toAll: true)`，即便 `CurrentObservers` 数值完全未变（3,600 次冗余 RPC/秒）。

---

### 2.4 Unity 物理系统与碰撞查询

全程序集共 **77 个文件**调用 `Physics.*`（其中 32 个文件位于含 `Update/FixedUpdate/LateUpdate` 的类型中），**0 处**手动调用 `Physics.SyncTransforms()`。主要热点：

1. **`FpcMotor.UpdatePosition()`**：每帧每个存活玩家执行 1 次 `Physics.SphereCast` 探测脚下坡面法线，即使 `DesiredMove == Vector3.zero`（静止玩家，`ProjectOnPlane(zero, normal)` 恒为零向量，该次 SphereCast 结果被完全丢弃）。此外 `FpcMotor.Move()` 中 `Position = CachedTransform.position;` 导致刚读出原生 `transform.position` 又立刻写回 `_transform.position`，造成无意义的原生写入与 PhysX Transform 脏标记。
2. **`VisionInformation.GetVisionInformation()`**：
   - `Scp173ObserversTracker.UpdateObservers`（60Hz × 60 人）与 `Scp096TargetsTracker.ServerCheckTargets`（60Hz × 60 人）调用该方法。
   - **致命冗余 1**：第 106 行 `if (checkInDarkness)` 未判断 `flag` 是否已为 `false`！静态遍历全代码库 7 处调用点证实：**没有任何原版调用者读取 `IsInDarkness` 或 `GetFailReason()`**，但即使玩家在另一楼层/背对 SCP-173，每帧仍对全部 60 人执行 `CheckIsInDarkness`（`RoomLightController.IsInDarkenedRoom` + LINQ `AllEffects.Any`）。
   - **致命冗余 2**：`Scp096TargetsTracker.UpdateTarget` 未先检查 `Targets.Contains(target)`，导致**已入队目标每帧仍重复执行 `VisionInformation` + `Physics.RaycastNonAlloc`**，随后在 `AddTarget` 第一行被丢弃。
   - **致命冗余 3**：`Scp173ObserversTracker.IsObservedBy` 对每个视线内的敌人最多执行 `2 × _visibilityReferencePoints.Length` 次 `Physics.Linecast`，且在内层重复查询 `Owner.GetCurrentZone()`。
3. **`RoomUtils.TryRaycastRoom()`**：多格大房间（尤其是地表）触发的每帧每玩家 1–2 次 `Physics.Raycast`。
4. **骨骼动画与命中盒 (`AnimatedCharacterModel` / `CullingSubcontroller` / `FpcBacktracker`)**：
   - 核验确认服务端**依赖 `Animator` 驱动骨骼上挂载的 `HitboxIdentity` 碰撞体**及 SCP-096 头部位置 (`Scp096CharacterModel.Head`)，以及 `OnFootstepPlayed`（供 SCP-939 感知）。因此**绝对禁止通过禁用 `Animator` 换取性能**。
5. **Unity `SpherecastCommand` / `RaycastCommand` 批量并行可行性**：
   - 元数据核验确认 `UnityEngine.PhysicsModule.dll` 提供 `RaycastCommand`、`SpherecastCommand`、`OverlapSphereCommand`，且 `UnityEngine.CoreModule.dll` 提供 `NativeArray<T>`、`IJobParallelFor`。
   - `FpcStateProcessor.Mask` 基于 `Player` 层碰撞矩阵生成，需在运行时验证 `Player` 层是否与自身碰撞；若不与玩家碰撞（或仅检测静态/门/地形），所有移动玩家的地面 `SphereCast` 具备跨 PhysX 工作线程 `ScheduleBatch` 的理论条件（归类为 Tier-3 高阶研究项，须先验证 Mask 与批处理调度开销阈值）。

---

### 2.5 内存与 GC 分析

1. **运行时内存与池化特性**：
   - `boot.config` 启用 `gc-max-time-slice=3`（Mono Boehm 增量 GC，单帧增量标记上限 3ms）。Boehm GC 为非移动/保守式分代 GC，**高频率小对象分配会快速推高堆水位并触发高频增量切片甚至 STW 回收**。
   - `NorthwoodLib.Pools.ListPool<T>` 内部是无界的 `ConcurrentQueue<List<T>>`，默认初始容量高达 **512**（`new List<T>(512)`）。
   - `mscorlib.dll` 原生包含 `Span<T>`、`ReadOnlySpan<T>`、`ArrayPool<T>`、`ConditionalWeakTable<TKey, TValue>`，可用于零分配缓冲区和不可变委托缓存。
2. **高频 GC 分配源总览（静态确认）**：
   - `FpcServerPositionDistributor.WriteAll`：无条件 `new PlayerValidatedVisibilityEventArgs`（60p/60Hz = **212,400 次/秒**）。
   - `VoiceTransceiver.ServerReceiveMessage`：无条件 `new PlayerSendingVoiceMessageEventArgs` + 内层无条件 `new PlayerReceivingVoiceMessageEventArgs`（10 麦/60p = **60,000+ 次/秒**）。
   - `LabApi.Events.EventManager.InvokeEvent<T>`：有订阅者时每次触发调用 `eventHandler.GetInvocationList()` 分配新 `Delegate[]` 数组。
   - `LiteNetLib4MirrorServer.OnNetworkReceive`：每个 UDP 包分配闭包 + `Action`（**10,000–20,000+ 次/秒**）。
   - `LiteNetManager.NativeReceiveLogic`：每个 UDP 包分配 `new IPAddress(...)`（**5,000–15,000 次/秒**）。
   - `VisionInformation.CheckIsInDarkness`：`AllEffects.Any(...)` LINQ 枚举器分配（**3,600+ 次/秒**）。
   - `Scp096TargetsTracker.Update`：服务端每帧空 `_markers` 仍分配闭包 `ForEachValue(x => x.SetActive(visible))`。
   - `EnvironmentalHazard.UpdateTargets` / `TeslaGateController.FixedUpdate`：持续停留/待机时每帧分配 `EventArgs`。
   - `PickupStandardPhysics`：订阅/退订 `StaticUnityMethods.OnUpdate` 导致数百长度多播委托数组频繁重分配。
   - 潜在泄漏：`FpcServerPositionDistributor.PreviouslySent` 在玩家断连时未移除其外层 `receiver.netId` 键（直至下回合 `Inventory.OnServerStarted` 才清空）。

---

### 2.6 多线程与并行计算可行性评估

| 候选任务 | 能否安全并行/卸载后台 | 核心分析与约束 |
| --- | --- | --- |
| **UDP 收包与协议拆包** | **已在后台线程** (`ReceiveThread` + `LogicThread`) | 应消除多余的 `NetworkPollThread` + `Thread.Sleep(30)`，将已解帧的 `NetEvent` 在主线程零闭包直接消费，而非再加线程。 |
| **FPC 同步数据计算 (`FpcSyncData` 打包 / Delta 编码)** | **先做 $O(P^2) \to O(P)$ 单线程预计算；大服 (>60p) 可评估纯值类型快照并行写缓冲** | 主线程先一次性采集全部玩家的纯值类型 `PlayerFpcSnapshot`（含 `Position`, `RelativePosition`, `RotH`, `RotV`, `Zone`, `Team`, `Role`, `Flags`），彻底隔绝 Unity API。当 $P \le 60$ 时，预计算后单线程纯 struct 循环耗时极低（预期 <0.2ms），引入线程池同步屏障反而可能得不偿失；仅在实测证明序列化/压缩仍占 >1.5ms 时才启用工作线程分片写缓冲区。 |
| **物理批量射线 (`SpherecastCommand` / `RaycastCommand`)** | **有条件可行（Unity 原生 Job Worker）** | 仅当 `Physics` 层掩码确认各查询间无互相写依赖，且单帧查询数足够大（如 >20 次）能摊销 `NativeArray` 填充与 `JobHandle.Complete()` 成本时才收益为正。 |
| **连接预鉴权 / ECDSA 签名验证** | **已在后台线程** (`ConnectionRequestThread`) | 原版已正确异步化；但 `ConnectionRequestLoop` 内 `Thread.Sleep(30)` 可改为事件唤醒 (`AutoResetEvent` / `ManualResetEventSlim`) 降低建连延迟，无竞争风险。 |
| **禁止后台化的任务** | **严格禁止** | 任何访问 `Transform`、`CharacterController`、`GameObject`、`Rigidbody`、`Animator`、`Time.frameCount`、`UnityEngine.Object == null`、`LabApi` 事件回调的逻辑均必须在 Unity 主线程执行。 |
