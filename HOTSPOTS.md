# SCP:SL 原生服务端性能热点与证据清单（HOTSPOTS.md）

> **证据分级说明**：
> - `[STATICALLY VERIFIED]`：已直接反编译 `SCPSL_Data\Managed` 实际 DLL 并核验精确类名、方法签名、循环结构与调用链路。
> - `[RUNTIME UNVERIFIED]`：主线程实际毫秒占比、GC 字节数与 P99 改善幅度尚未通过真实服务端运行采样确认，待第四阶段 `SLPerformance.Profiler` 实测补全。
> - 以下量化频次均为基于 **60 玩家（$P=60$）、60 TPS（$T=60$）** 满载模型的**理论操作次数推演**，并非实测 profiler 数据。

---

## 热点总览表（按预估综合影响排序）

| ID | 程序集 | 类与方法签名 | 复杂度 | 60p/60Hz 理论频次 | 核心瓶颈类型 | 证据状态 |
| --- | --- | --- | --- | --- | --- | --- |
| **HS-01** | `Mirror.dll` | `Mirror.NetworkServer.Broadcast()` / `BroadcastToConnection(NetworkConnectionToClient)` | $O(P \times E)$ | 3.6M–18M 迭代/秒 | 全量遍历非脏实体 + 原生 `!= null`/`frameCount` + 重复序列化 | `[STATICALLY VERIFIED]` |
| **HS-02** | `Assembly-CSharp.dll` | `PlayerRoles.FirstPersonControl.NetworkMessages.FpcServerPositionDistributor.WriteAll(ReferenceHub, NetworkWriter)` | $O(P^2)$ | 212,400 pair/秒 | 212k/s EventArgs 堆分配 + 重复可见性/距离/旋转计算 + 4×字典查表 + 泄漏 | `[STATICALLY VERIFIED]` |
| **HS-03** | `Assembly-CSharp.dll` | `Mirror.LiteNetLib4Mirror.LiteNetLib4MirrorTransport.PollingLoop()` & `LiteNetLib4MirrorServer.OnNetworkReceive(...)` | $O(M_{in})$ | 5k–15k 包/秒 | 30ms 硬休眠脉冲抖动 + 每包分配闭包和 Action 委托 | `[STATICALLY VERIFIED]` |
| **HS-04** | `Mirror.dll` | `Mirror.NetworkBehaviour.SendRPCInternal(string, int, NetworkWriter, int, bool)` | $O(P)$ / RPC | 数百–数千 RPC/秒 | Mirror 编码缺陷：序列化结果弃用，每个观察者重复 Pack（$P+1$ 倍开销） | `[STATICALLY VERIFIED]` |
| **HS-05** | `Assembly-CSharp.dll` | `CustomPlayerEffects.StatusEffectBase.Update()` | $O(P \times K_{eff})$ | 180k–290k 次/秒 | 50–80+ 禁用状态效果 MonoBehaviour 每帧空转 Unity C++→Mono 回调 | `[STATICALLY VERIFIED]` |
| **HS-06** | `Assembly-CSharp.dll` | `VoiceChat.Networking.VoiceTransceiver.ServerReceiveMessage(NetworkConnection, VoiceMessage)` | $O(S \times P)$ | 60k+ 迭代/秒 (10麦) | 无条件 EventArgs 分配（含不可听玩家）+ 每个听众重复序列化 Opus 载荷 | `[STATICALLY VERIFIED]` |
| **HS-07** | `Assembly-CSharp.dll` | `PlayerRoles.FirstPersonControl.FpcMotor.UpdatePosition` / `TryGetOverride` & `FirstPersonMovementModule.VelocityForState` & `FpcStateProcessor` | $O(P \times K_{eff})$ | 1.5M–2M 接口转型/秒 + 3,600 SphereCast/秒 | 静止玩家仍执行 SphereCast；每帧每玩家 4–6 次全量遍历全部状态效果；重复写 `transform.position` | `[STATICALLY VERIFIED]` |
| **HS-08** | `Assembly-CSharp.dll` | `AdminToys.AdminToyBase.LateUpdate()` | $O(N_{toy})$ | 180k–300k 次/秒 (3k-5k Toys) | 大地图/自定义建筑场景：大量静态/未动 Toy 每帧原生读 Transform + 3× SyncVar 比较 | `[STATICALLY VERIFIED]` |
| **HS-09** | `Assembly-CSharp.dll` | `MapGeneration.CurrentRoomPlayerCache.Update()` → `RoomUtils.TryGetRoom(Vector3, out RoomIdentifier)` | $O(P)$ + Raycast | 3,600–7,200 Raycast/秒 (地表) | `RoomsByCoords` 仅存单格 `MainCoords`，多格房间/地表每帧 Miss 退化为 1–2 次射线 + `GetComponentInParent` | `[STATICALLY VERIFIED]` |
| **HS-10** | `Assembly-CSharp.dll` | `RelativePositioning.WaypointBase.ExtractWaypointData(...)` | $O(P \times 254)$ | 914k+ 槽位/秒 | 固定遍历 254 稀疏槽位 + 虚调用 `SqrDistanceTo` | `[STATICALLY VERIFIED]` |
| **HS-11** | `Assembly-CSharp.dll` | `LiteNetLib.LiteNetManager.NativeReceiveLogic()` | $O(M_{in})$ | 5k–15k 包/秒 | 每个 UDP 收包堆分配 `new IPAddress(...)` 查 Peer | `[STATICALLY VERIFIED]` |
| **HS-12** | `LabApi.dll` | `LabApi.Events.EventManager.InvokeEvent<TEventArgs>(...)` | $O(E_{fired})$ | 高频事件可达 200k+/秒 | 每次有订阅者的事件触发均调用 `GetInvocationList()` 分配新 `Delegate[]` | `[STATICALLY VERIFIED]` |
| **HS-13** | `Mirror.dll` | `Mirror.NetworkConnection.Update()` & `Batcher.GetBatch()` & `Unbatcher.AddBatch()` | $O(P \times C + M_{in})$ | 7.2k–20k 拷贝/秒 | 收包 `Unbatcher` 无条件拷入临时 Writer；发包 `Batcher` 空通道租还池 + 冗余二次拷贝 | `[STATICALLY VERIFIED]` |
| **HS-14** | `Assembly-CSharp.dll` | `PlayerRoles.PlayableScps.VisionInformation.GetVisionInformation(...)` & `CheckIsInDarkness(...)` & `Scp096TargetsTracker` & `Scp173ObserversTracker` | $O(P)$ / SCP | 3.6k–7.2k 次/秒 | `flag == false` 仍调 `CheckIsInDarkness`（含 LINQ `.Any()`）；SCP-096 对已锁定目标重复射线；SCP-173 每帧无条件全服 RPC | `[STATICALLY VERIFIED]` |
| **HS-15** | `Assembly-CSharp.dll` | `InventorySystem.Items.Pickups.PickupStandardPhysics.OnUpdate()` | $O(N_{pickup})$ | 24k–36k 次/秒 (400-600拾取物) | 所有休眠拾取物挂载到单一 `StaticUnityMethods.OnUpdate` 多播委托，每帧原生查 `IsSleeping()` + 增删重分配大委托数组 | `[STATICALLY VERIFIED]` |
| **HS-16** | `Assembly-CSharp.dll` | `Hazards.EnvironmentalHazard.UpdateTargets()` & `TeslaGateController.FixedUpdate()` | $O(H \times P)$ | 3.6k–36k 迭代/秒 | 循环内重复原生取 `SourcePosition`、线性 `List.Contains`、持续停留每帧分配 `EventArgs` | `[STATICALLY VERIFIED]` |
| **HS-17** | `Assembly-CSharp.dll` | `InventorySystem.Inventory.Update()` & `PlayerStatsSystem.AhpStat.ServerUpdateProcesses()` & `SyncedStatBase.Update()` | $O(P)$ / $O(P^2)$ | 3.6k–216k 次/秒 | 空背包/空 AHP 每帧租还 `ListPool` + 遍历；`SyncedStatBase` 私有同步模式也扫全服 `AllHubs` | `[STATICALLY VERIFIED]` |
| **HS-18** | `Assembly-CSharp.dll` | `PlayerRoles.Voice.VoiceModuleBase.SpawnObject()` & 20 个空 `Update/FixedUpdate/LateUpdate` | $O(P)$ | 每次刷兵/切角 + 每帧数千空回调 | 专用服为每个玩家分配原生 `libopus-0.dll` `OpusDecoder` 永不使用；空回调白耗跨界开销 | `[STATICALLY VERIFIED]` |

---

## 关键热点详细证据剖面

### HS-01：`Mirror.NetworkServer.Broadcast()` 与 `BroadcastToConnection()`

- **位置**：`Mirror.dll` → `Mirror.NetworkServer` (约第 1063–1131 行) + `Assembly-CSharp.dll` → `CustomInterestManagement`
- **原版实现链**：
  1. `CustomInterestManagement.OnRebuildObservers` 将全部就绪连接加入每个 spawned `NetworkIdentity` 的 `observers`。
  2. `NetworkServer.NetworkLateUpdate()` 按 `sendInterval`（60Hz）调用 `Broadcast()`。
  3. `Broadcast()` 复制连接表到 `connectionsCopy`，逐连接发送 `TimeSnapshotMessage`，再调用 `BroadcastToConnection(conn)` 遍历 `conn.observing`。
  4. `BroadcastToConnection` 对每个 `item` 执行 `item != null`（Unity 原生检查）→ `SerializeForConnection(item, conn)` → `item.GetServerSerializationAtTick(Time.frameCount)`（Unity 原生 `Time.frameCount`）。
  5. 第一个连接触发 `item.SerializeServer(false, ownerWriter, observersWriter)` 检查所有 `NetworkBehaviour.IsDirty()`；其余 $P-1$ 个连接仍逐一进入 `GetServerSerializationAtTick` 检查 `tick` 与 `observersWriter.Position > 0`。
  6. 若 `networkWriter != null`，构造 `EntityStateMessage` 并调用 `connection.Send<EntityStateMessage>(message)`——导致每个观察者独立租借 `NetworkWriterPooled`、写入消息 ID/netId/VarUInt 长度、并用 `Array.ConstrainedCopy` 拷贝整个 payload。
- **瓶颈机理**：
  - 时间复杂度 $O(P \times E)$，与脏实体数 $D$ 无关。在大地图/高实体服（$E=3000, P=60$）下，即使本帧仅 15 个实体变化（$D=15$），仍要执行 **180,000 次/帧** 循环与 **360,000 次/帧** Unity C++ 跨界调用！
  - 空间/带宽/CPU：每个脏实体重复 Pack $P$ 次。

### HS-02：`FpcServerPositionDistributor.WriteAll()` 与 `GetVisibleRole()`

- **位置**：`Assembly-CSharp.dll` → `PlayerRoles.FirstPersonControl.NetworkMessages.FpcServerPositionDistributor` (第 59–231 行) + `FpcSyncData` (第 42–50 行) + `FpcMouseLook.GetSyncValues` (第 170–174 行) + `FpcVisibilityController.GetActiveFlags` (第 31–55 行)
- **实证代码路径**：
  - 外层 `LateUpdate()` 按 60Hz 遍历 `ReferenceHub.AllHubs`（$P$ 个 `receiver`），发送 `FpcPositionMessage(receiver)`。
  - 序列化触发 `WriteAll(receiver, writer)`，再次遍历 `ReferenceHub.AllHubs`（$P-1$ 个 `target`）：
    - Line 178：`visibilityController.ValidateVisibility(allHub)`（内含 `observer.GetCurrentZone()` → `Time.frameCount`；若为 SCP-939 还扫全效果 + 254 路标）。
    - Line 179–180：无条件 `new PlayerValidatedVisibilityEventArgs(receiver, allHub, !flag2)`（构造时两次 `LabApi.Player.Get()`）+ `PlayerEvents.OnValidatedVisibility(e)`。
    - Line 182：`GetNewSyncData` → `GetPrevSyncData` 查双层字典，再 `PreviouslySent[receiver.netId][target.netId] = ...` 再查一次双层字典（共 4 次哈希查找）；构造 `FpcSyncData` 调用 `fpmm.IsGrounded`（原生）与 `mLook.GetSyncValues`（四元数旋转 + 路标查表 + 三角反解）。
    - Line 189：`GetVisibleRole(receiver, allHub)` **第二次**调用 `ValidateVisibility(allHub)`；无视 `flag == false` 提前计算 `Vector3.Distance(receiver.transform.position, target.transform.position)`（2× 原生 `transform.position` + `Sqrt`）、权限、队伍、语音状态。
    - Line 190/200：无条件 `NetworkWriterPool.Get()` 与 `Return()`。
- **内存泄漏实证**：`Init()` 仅监听 `Inventory.OnServerStarted += PreviouslySent.Clear` 与 `PlayerRoleManager.OnRoleChanged += ResetPlayer`，未监听 `ReferenceHub.OnPlayerRemoved`，导致断连玩家的 `PreviouslySent[receiver.netId]` 子字典持续滞留至回合结束。

### HS-03：`LiteNetLib4MirrorTransport` 双重队列与 30ms 轮询休眠

- **位置**：`Assembly-CSharp.dll` → `Mirror.LiteNetLib4Mirror.LiteNetLib4MirrorTransport` (第 116–178 行) + `LiteNetLib4MirrorServer` (第 78–122 行)
- **实证代码路径**：
  - `PollingLoop()` 在独立线程上执行 `Host.PollEvents(); Thread.Sleep(30);`。
  - `OnNetworkReceive` 回调中编写捕获 `peer` 与 `reader` 的 Lambda `() => { OnServerDataReceived(peer.Id + 1, reader.GetRemainingBytesSegment(), -1); reader.Recycle(); }` 并推入 `ConcurrentQueue<Action> Events`。
  - `LateUpdate()` 在主线程排空 `Events`。
- **瓶颈机理**：
  - 60Hz 服务端帧周期为 16.67ms，而 30ms 休眠（Windows 默认时钟粒度下实际约 31.25ms）使每约 2 帧才一次性涌入双倍客户端输入包（位置包、语音包、开火包），既人为增加 P95/P99 帧峰值，又制造 10k–20k/s 闭包短命对象。

### HS-04：`Mirror.NetworkBehaviour.SendRPCInternal()` 序列化结果弃用 Bug

- **位置**：`Mirror.dll` → `Mirror.NetworkBehaviour.SendRPCInternal` (第 252–284 行)
- **实证代码**：
  - 第 275–276 行：`using NetworkWriterPooled networkWriterPooled = NetworkWriterPool.Get(); networkWriterPooled.Write(rpcMessage);`
  - 第 277–283 行：循环所有观察者，调用 `value.Send(rpcMessage, channelId)`（传入 struct 而非 `networkWriterPooled`！）。
  - 不仅 `networkWriterPooled.Write(rpcMessage)` 完全浪费（且没写 `NetworkMessageId<RpcMessage>.Id`），每次观察者发送都重新租池、Pack、检查长度、拷贝 `payload`。

### HS-05：`StatusEffectBase.Update()` 大规模空闲 MonoBehaviour 回调

- **位置**：`Assembly-CSharp.dll` → `CustomPlayerEffects.StatusEffectBase` (第 212–219 行) + `PlayerEffectsController` (第 201–235 行)
- **实证数据**：`CustomPlayerEffects` 命名空间共 110 个文件，每个玩家在 `effectsGameObject` 下挂载数十个启用的效果组件；仅 10 个子类声明了自己的 `Update`/`LateUpdate`，其余所有子类均直接继承 `StatusEffectBase.Update()`（`if (IsEnabled) { RefreshTime(); OnEffectUpdate(); }`），且没有任何子类重写 `OnEnable`/`OnDisable` 或读取 `base.enabled`。

### HS-06：`VoiceTransceiver.ServerReceiveMessage()` 重复分配与重复序列化

- **位置**：`Assembly-CSharp.dll` → `VoiceChat.Networking.VoiceTransceiver` (第 41–80 行)
- **实证路径**：
  - 单个发言者每次发送 480 samples（48kHz 采样下为 10ms/包 = 100 包/秒）。
  - 对每个收到语音包的服务器线程调用：1 次 `new PlayerSendingVoiceMessageEventArgs` + 遍历全部 $P$ 个玩家执行 `ValidateReceive` → 无条件 `new PlayerReceivingVoiceMessageEventArgs`（即使听不到！）→ 对所有听众逐一调用 `connectionToClient.Send(msg)` 重复序列化并拷贝整份 Opus 帧。

### HS-07：`FpcMotor` / `FirstPersonMovementModule` / `FpcStateProcessor` 玩家移动热路径

- **位置**：`Assembly-CSharp.dll` → `FpcMotor.cs` (第 147–199, 294–355, 433–459 行), `FirstPersonMovementModule.cs` (第 164–200, 275–309 行), `FpcStateProcessor.cs` (第 74–123, 189–225 行)
- **实证冗余**：
  1. `UpdatePosition()` 在求 `DesiredMove` 之前就执行 `Physics.SphereCast`，静止玩家（`DesiredMove == Vector3.zero`）完全丢弃 `hitInfo`。
  2. `Move()` 中 `Position = CachedTransform.position;` 反向触发 `_transform.position = value;` 原生冗余写回。
  3. `DesiredMove` 与 `GetFrameMove()` 同帧两次调用 `ReceivedPosition.Position`。
  4. 单帧单玩家对 `AllEffects` 执行 4–6 次全量扫描（`IMovementSpeedModifier`, `IMovementInputOverride`, `IStaminaModifier` ×3, `IFootstepEffect`）。

### HS-14：`VisionInformation` 与 SCP-173 / SCP-096 视野检测

- **位置**：`Assembly-CSharp.dll` → `VisionInformation.cs` (第 52–112, 215–225 行), `Scp173ObserversTracker.cs` (第 133–182 行), `Scp096TargetsTracker.cs` (第 211–256 行)
- **实证冗余**：
  1. `VisionInformation.GetVisionInformation` 第 106 行在 `flag == false` 时仍执行 `CheckIsInDarkness`（包含 LINQ `AllEffects.Any`）。
  2. `Scp096TargetsTracker.UpdateTarget` 对已在 `Targets` 中的玩家仍每帧执行 `IsObservedBy` + 物理射线检测；`Update()` 每帧为服务端空 `_markers` 分配闭包。
  3. `Scp173ObserversTracker.UpdateObservers` 每帧无条件 `ServerSendRpc(toAll: true)`，不检查 `CurrentObservers` 是否发生变化（客户端 `ClientProcessRpc` 仅设置 `CurrentObservers = reader.ReadByte()`，而其 setter 自身在值相等时什么也不做）。

---

## 待第四阶段运行时验证的核心假设

在编写任何优化补丁前，第四阶段 `SLPerformance.Profiler` 需实测确认以下关键问题：
1. 在空服 / 20p / 48p / 60p 以及 3,000+ AdminToys 场景下，`NetworkServer.Broadcast`、`FpcServerPositionDistributor.LateUpdate`、`FirstPersonMovementModule._activeUpdates`、`StatusEffectBase.Update`、`VoiceTransceiver.ServerReceiveMessage`、`CurrentRoomPlayerCache.Update`、`LiteNetLib4MirrorTransport.LateUpdate` 各自占单帧 CPU 时间的真实百分比。
2. Gen0 分配速率（KB/s）与增量 GC 时间片（`gc-max-time-slice=3`）对 P95/P99/P99.9 帧耗时的真实贡献。
3. 地表（Surface Zone）与重收容（HCZ）中 `RoomUtils.TryGetRoom` 的实际 `RoomsByCoords` 命中率与 `TryRaycastRoom` 触发率。
4. `FpcStateProcessor.Mask` 的实际位掩码值（是否包含 `Player` 层），以决定 `SpherecastCommand.ScheduleBatch` 的可行性。
