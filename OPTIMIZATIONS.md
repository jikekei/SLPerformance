# SCP:SL 候选优化方案与优先级矩阵（OPTIMIZATIONS.md）

> **原则声明**：本清单所有候选方案均严格遵守**不改变原版游戏玩法、不降低同步/物理精度、不降低 Tickrate、不削弱反作弊/认证、不破坏官方通信与插件生态**的底线。
> **实施顺序**：第四阶段先完成低开销采样器实测验证，再按 **Tier 1 → Tier 2 → Tier 3** 分模块灰度落地并逐项跑回归与 A/B 压测。

---

## 1. 优先级综合评分矩阵

评分维度（1–5 分制，性能收益/调用频率/插件兼容/证据可信越高越好；实现难度/行为风险/线程风险/维护成本越低越好）：

| ID | 模块名 | 对应热点 | 性能收益 | 调用频率 | 实现难度 | 行为风险 | 线程安全风险 | 插件兼容 | 版本维护成本 | 证据可信度 | 推荐分级 |
| --- | --- | --- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: | --- |
| **OPT-01** | `net.mirror_rpc_pack_once` | HS-04 | ★★★★☆ | ★★★★☆ | ★☆☆☆☆ | ★☆☆☆☆ | ★☆☆☆☆ | ★★★★★ | ★☆☆☆☆ | ★★★★★ | **Tier 1** |
| **OPT-02** | `net.fpc_distributor_fast` | HS-02 | ★★★★★ | ★★★★★ | ★★★☆☆ | ★★☆☆☆ | ★☆☆☆☆ | ★★★★☆ | ★★★☆☆ | ★★★★★ | **Tier 1** |
| **OPT-03** | `net.mirror_dirty_broadcast` | HS-01 | ★★★★★ | ★★★★★ | ★★★☆☆ | ★★☆☆☆ | ★☆☆☆☆ | ★★★★☆ | ★★☆☆☆ | ★★★★★ | **Tier 1** |
| **OPT-04** | `net.transport_direct_poll` | HS-03 | ★★★★☆ | ★★★★★ | ★★☆☆☆ | ★★☆☆☆ | ★★☆☆☆ | ★★★★☆ | ★★☆☆☆ | ★★★★★ | **Tier 1** |
| **OPT-05** | `net.voice_batch_and_lazy_args` | HS-06 | ★★★★☆ | ★★★★★ | ★★☆☆☆ | ★☆☆☆☆ | ★☆☆☆☆ | ★★★★★ | ★★☆☆☆ | ★★★★★ | **Tier 1** |
| **OPT-06** | `memory.labapi_event_cache` | HS-12 | ★★★☆☆ | ★★★★★ | ★☆☆☆☆ | ★☆☆☆☆ | ★★☆☆☆ | ★★★★★ | ★☆☆☆☆ | ★★★★★ | **Tier 1** |
| **OPT-07** | `entities.effects_interface_index` | HS-07 | ★★★★☆ | ★★★★★ | ★★☆☆☆ | ★☆☆☆☆ | ★☆☆☆☆ | ★★★★★ | ★★☆☆☆ | ★★★★★ | **Tier 1** |
| **OPT-08** | `physics.vision_and_scp_trackers` | HS-14 | ★★★★☆ | ★★★★☆ | ★★☆☆☆ | ★☆☆☆☆ | ★☆☆☆☆ | ★★★★☆ | ★★☆☆☆ | ★★★★★ | **Tier 1** |
| **OPT-09** | `entities.status_effect_sleep` | HS-05 | ★★★★☆ | ★★★★★ | ★★☆☆☆ | ★★☆☆☆ | ★☆☆☆☆ | ★★★★☆ | ★★☆☆☆ | ★★★★☆ | **Tier 2** |
| **OPT-10** | `net.batcher_unbatcher_zerocopy` | HS-13 | ★★★☆☆ | ★★★★★ | ★★☆☆☆ | ★☆☆☆☆ | ★☆☆☆☆ | ★★★★★ | ★★☆☆☆ | ★★★★★ | **Tier 2** |
| **OPT-11** | `physics.fpc_motor_fastpath` | HS-07 | ★★★☆☆ | ★★★★☆ | ★★☆☆☆ | ★★☆☆☆ | ★☆☆☆☆ | ★★★★★ | ★★★☆☆ | ★★★★★ | **Tier 2** |
| **OPT-12** | `net.litenet_peer_lookup_noalloc` | HS-11 | ★★★☆☆ | ★★★★★ | ★★★☆☆ | ★☆☆☆☆ | ★★★☆☆ | ★★★★★ | ★★★☆☆ | ★★★★☆ | **Tier 2** |
| **OPT-13** | `entities.room_and_waypoint_accel` | HS-09/10 | ★★★★☆ | ★★★★☆ | ★★★☆☆ | ★★☆☆☆ | ★☆☆☆☆ | ★★★★★ | ★★☆☆☆ | ★★★★☆ | **Tier 2** |
| **OPT-14** | `entities.admintoy_pickup_hazard` | HS-08/15/16 | ★★★★☆ | ★★★★☆ | ★★☆☆☆ | ★★☆☆☆ | ★☆☆☆☆ | ★★★★☆ | ★★☆☆☆ | ★★★★★ | **Tier 2** |
| **OPT-15** | `memory.inventory_stats_opus` | HS-17/18 | ★★★☆☆ | ★★★★☆ | ★☆☆☆☆ | ★☆☆☆☆ | ★☆☆☆☆ | ★★★★★ | ★★☆☆☆ | ★★★★★ | **Tier 1** |
| **OPT-16** | `physics.fpc_spherecast_batch` | HS-07 | ★★★☆☆ | ★★★★☆ | ★★★★☆ | ★★★☆☆ | ★★★☆☆ | ★★★★☆ | ★★★☆☆ | ★★★☆☆ | **Tier 3** |
| **OPT-17** | `threading.fpc_parallel_encode` | HS-02 | ★★☆☆☆ | ★★★★★ | ★★★★☆ | ★★☆☆☆ | ★★★★☆ | ★★★★☆ | ★★★☆☆ | ★★☆☆☆ | **Tier 3** |

---

## 2. 候选优化详细设计规范（13 项完整要素）

---

### OPT-01：`Mirror.NetworkBehaviour.SendRPCInternal` 单次打包修复 (`net.mirror_rpc_pack_once`)

- **原版程序集**：`Mirror.dll`
- **完整类名与方法签名**：`Mirror.NetworkBehaviour.SendRPCInternal(string functionFullName, int functionHashCode, NetworkWriter writer, int channelId, bool includeOwner)`
- **原版实现逻辑**：租借 `NetworkWriterPooled` 并调用 `networkWriterPooled.Write(rpcMessage)` 后弃用该缓冲区，然后在 `foreach (observer in netIdentity.observers.Values)` 循环中调用 `value.Send(rpcMessage, channelId)`，触发每次观察者独立租池、Pack、尺寸校验与 `Array.ConstrainedCopy`。
- **调用链路与调用频率**：所有 `[ClientRpc]`（枪声/开火效果、门状态音效、SCP 技能通知、提示、AdminToy 父级变化等）→ `SendRPCInternal`；60 人交火场景可达数百至数千次 RPC/秒。
- **性能瓶颈原因**：每发送 1 次全服 RPC 执行 $P+1$ 次序列化与内存拷贝（60 人即 61 倍工作量）。
- **时间与空间复杂度**：原版 $O(P \times L_{payload})$ 序列化与拷贝 → 优化后 $O(L_{payload} + P)$（仅 Pack 一次，逐连接 Batcher 拷贝 1 次）。
- **优化实现方案**：Harmony Prefix 接管 `SendRPCInternal`（返回 `false`）：使用 `NetworkMessages.Pack(rpcMessage, pooledWriter)` 打包一次，执行与 `NetworkConnection.Send<T>` 完全相同的 `MaxMessageSize(channelId)` 校验与 `NetworkDiagnostics.OnSend` 通知，将 `ArraySegment<byte>` 直接传给每个符合条件的 `value.Send(segment, channelId)`。
- **预期性能收益与依据**：消除所有 ClientRpc 的 $(P-1)/P$ 冗余序列化与 Writer 池争用（60 人减少 ~98% RPC 打包开销），依据为 `Mirror.dll` 第 275–283 行源码实证。
- **行为等价性**：发送字节流、消息 ID、长度校验错误日志、`NetworkDiagnostics` 计数及频道语义 100% 字节级等价。
- **线程安全性**：仅运行于 Unity 主线程，与原版完全一致。
- **插件兼容性**：极高；对任何定义 `[ClientRpc]` 的插件自动透明加速，不改变签名或回调。
- **版本兼容性**：高；`SendRPCInternal` 为 Mirror 长期稳定内部签名，启动时校验参数列表。
- **测试与验证方法**：单元测试比对原版与补丁后发往模拟 `NetworkConnection` 的原始字节序列完全相等；运行时用 Profiler 对比 ClientRpc 耗时。

---

### OPT-02：`FpcServerPositionDistributor` 帧快照预计算与零冗余同步 (`net.fpc_distributor_fast`)

- **原版程序集**：`Assembly-CSharp.dll`
- **完整类名与方法签名**：
  - `PlayerRoles.FirstPersonControl.NetworkMessages.FpcServerPositionDistributor.LateUpdate()`
  - `PlayerRoles.FirstPersonControl.NetworkMessages.FpcServerPositionDistributor.WriteAll(ReferenceHub receiver, NetworkWriter writer)`
  - `PlayerRoles.FirstPersonControl.NetworkMessages.FpcServerPositionDistributor.GetVisibleRole(ReferenceHub receiver, ReferenceHub target)`
- **原版实现逻辑**：每帧 60Hz 遍历所有 `receiver`，在 `WriteAll` 中遍历所有 `target`，每次重复执行：LabAPI `new PlayerValidatedVisibilityEventArgs` + 2× `Player.Get()`、2 次 `ValidateVisibility`、无视 `flag` 计算 `Vector3.Distance` + 权限/队伍/语音检查、每 receiver 重复执行 `mLook.GetSyncValues` 与原生 `IsGrounded`、无条件租还 `NetworkWriterPool`、4 次双层字典哈希查表；且断连不清理 `PreviouslySent[receiver.netId]`。
- **调用链路与调用频率**：`StaticUnityMethods.LateUpdate` → `FpcServerPositionDistributor.LateUpdate` → `conn.Send(FpcPositionMessage)` → `WriteAll`；60p/60Hz 达 **212,400 pair/秒**。
- **性能瓶颈原因**：大量仅依赖单一玩家（$O(P)$）的 Unity 原生调用与数学计算被放入 $O(P^2)$ 内层；无监听者仍大量堆分配；`GetVisibleRole` 缺少 `flag` 短路且重复验证可见性。
- **时间与空间复杂度**：
  - Unity 原生读取（`transform.position`, `CharController.isGrounded`, `GetCurrentZone`, `GetSyncValues`）：$O(P^2) \to O(P)$（60 人降为 1/60）。
  - 堆分配：从 $212,400$ 对象/秒 降至 **$0$**（当无插件订阅 `ValidatedVisibility` 时）。
  - `PreviouslySent` 空间：修复断连泄漏，通过 `ReferenceHub.OnPlayerRemoved` 精确回收，并通过 `receiver` 索引直接命中缓存表，将 4 次哈希查找降至 1 次数组/单表查表。
- **优化实现方案**：
  1. 在 `LateUpdate()` 进入 `AllHubs` 循环前，单遍 $O(P)$ 采集所有有效玩家的不可变值类型快照数组 `PlayerTickSnapshot[]`（预复用数组，零分配）：包含 `Hub`, `NetId`, `PlayerId`, `IsLocalPlayer`, `IsFpc`, `Position`, `RelativePosition`, `MovementState`, `IsGrounded`, `RotH`, `RotV`, `RoleId`, `Team`, `IsObfuscated`, `HasCustomVisibility`, `VisController`, `IsSurfaceZone`, `HasGameplayDataPerm`, `IsSpectator`, `IsCommunicatingGlobally`, `CameraPosIf079`。
  2. 在 `WriteAll` 中：
     - 外层一次性取 `PreviouslySent[receiver.netId]` 子表（避免内层重复外层哈希）。
     - 仅调用**一次** `ValidateVisibility`（缓存原始布尔 `rawInvisible` 供 `GetVisibleRole` 复用——**注意保持原版微妙语义**：原版 `GetVisibleRole` 使用的是 LabAPI 事件修改**之前**的 `ValidateVisibility` 结果，我们必须严格复用 `rawInvisible`，绝不混用事件改写后的 `flag2`！）。
     - 仅当 `PlayerEvents.ValidatedVisibility` 非空时才构造 `PlayerValidatedVisibilityEventArgs` 并触发事件（通过反射缓存事件字段/访问器检查订阅状态）。
     - 用预计算的 `RotH/RotV/RelativePosition/MovementState/IsGrounded` 直接构造等价的 `FpcSyncData`（零 `GetSyncValues` 重复三角计算）。
     - 在 `GetVisibleRole` 逻辑中：若 `!rawInvisible`（目标对接收者可见），直接跳过 `Vector3.Distance`、`PermissionsHandler`、`IsCommunicatingGlobally` 等死代码，直接返回 `result`（100% 等价！）。
     - 仅当 `_roleSyncEvent != null` 或 `PreviouslySentRole` 发生变化时才租借 `NetworkWriterPool`；无变更时跳过池操作。
  3. 挂钩 `ReferenceHub.OnPlayerRemoved` 清理 `PreviouslySent.Remove(hub.netId)` 并清理其他玩家子表中的 `hub.netId`。
- **预期性能收益与依据**：静态分析显示将 60 人满载下每帧 212k/s 堆分配归零、424k/s `Player.Get()` 归零、212k/s 重复 `ValidateVisibility` 与 `GetSyncValues` 减半至 1/60。具体毫秒收益待第四阶段实测验证。
- **行为等价性**：逐字段核验，包括 `rawInvisible` 与 `ValidatedVisibility` 事件分离、`IObfuscatedRole.GetRoleForUser` 调用、`_roleSyncEvent` 传参、`PreviouslySentRole` 触发 `SendRole` 顺序、`Overwatch` 伪装逻辑 100% 保持原版语义。
- **线程安全性**：全程主线程执行，快照数组复用无并发竞争。
- **插件兼容性**：完整保留 `PlayerEvents.ValidatedVisibility`（有订阅者时立即执行）、`FpcServerPositionDistributor.RoleSyncEvent`、`IObfuscatedRole`、`ICustomVisibilityRole`；若其他插件对 `WriteAll` 做了 Harmony 补丁，兼容层可检测并降级或协作。
- **版本兼容性**：中等；依赖 `FpcServerPositionDistributor` 与 `FpcSyncData` 私有字段布局，启动时做字段签名校验，不匹配则自动回退原版。
- **测试与验证方法**：字节级对比测试（模拟多角色组合：人类/SCP-939/SCP-079/SCP-268隐身/观察者/带 `ValidatedVisibility` 插件/不带插件，对比优化前后 `NetworkWriter` 输出字节流 100% 相同）。

---

### OPT-03：`Mirror.NetworkServer.Broadcast` 脏实体优先与单次序列化 (`net.mirror_dirty_broadcast`)

- **原版程序集**：`Mirror.dll`
- **完整类名与方法签名**：
  - `Mirror.NetworkServer.Broadcast()`
  - `Mirror.NetworkServer.BroadcastToConnection(NetworkConnectionToClient connection)`
  - `Mirror.NetworkBehaviour.SetSyncVarDirtyBit(ulong)` / `SetSyncObjectDirtyBit(ulong)`（可选脏表追踪）
- **原版实现逻辑**：`Broadcast()` 对每个连接遍历整个 `conn.observing` 集合（含所有未变更实体），每次检查原生 `!= null` 与 `Time.frameCount`，且对每个脏实体为每个观察者重复 Pack `EntityStateMessage`。
- **调用链路与调用频率**：`NetworkLoop.NetworkLateUpdate` → `NetworkServer.NetworkLateUpdate` → `Broadcast`；60Hz × $P \times E$（3.6M–18M 次/秒）。
- **性能瓶颈原因**：连接在外、实体在内的循环对全量静止实体重复探测 $P$ 次，且未跨观察者复用已打包的 `EntityStateMessage`。
- **时间与空间复杂度**：
  - 阶段 1（无侵入实体优先过滤）：$O(E + D \times P)$（其中 $D \ll E$），且每帧仅读 1 次 `Time.frameCount`、每个脏实体仅 Pack 1 次 `EntityStateMessage`。
  - 阶段 2（脏位追踪表，基于 Hook `SetSyncVarDirtyBit` / `SetSyncObjectDirtyBit` + `syncInterval` 延迟队列）：将探测开销进一步从 $O(E)$ 降至 **$O(D)$**！
- **优化实现方案**：
  - 维护每帧脏实体集合（在 `SetSyncVarDirtyBit`、`SetSyncObjectDirtyBit` 与 `NetworkBehaviour.OnSerialize` 自定义重写类型兼容列表中登记；对于重写了 `IsDirty()` 或使用 `syncInterval > 0` 的组件自动纳入轮询小集合，确保不漏同步！）。
  - 在 `Broadcast()` 中：
    1. 读取 `int frameCount = Time.frameCount;` 仅一次。
    2. 对每个就绪连接发送 `TimeSnapshotMessage`（与原版相同）。
    3. 仅遍历本帧可能脏的实体集合：调用 `GetServerSerializationAtTick(frameCount)`；若 `observersWriter.Position > 0`，使用单个 `NetworkWriterPooled` 将 `EntityStateMessage` **Pack 一次**为 `ArraySegment<byte>`，直接推送给 `identity.observers.Values` 中除 `owner` 外的所有就绪连接；若 `ownerWriter.Position > 0` 且 owner 就绪，向 owner 发送 `ownerWriter` 的 `EntityStateMessage`。
    4. 对每个连接调用 `conn.Update()` 刷出 Batcher。
  - **关键边界保障**：兼容自定义 `InterestManagement`（遍历 `identity.observers.Values` 而非假设全体连接，自动尊重 `Visibility.ForceHidden` 或任何第三方 AOI 插件！）；对 `NetworkIdentity` 未经 `NetworkServer.Destroy` 被非法销毁的情况，在实体清理时检查。
- **预期性能收益与依据**：在 60 人 + 3,000 AdminToys 大地图场景下，将每帧 180,000 次实体-连接循环降至 ~1,000 次（理论减少 >99% 广播遍历工作量）。实测值待第四阶段验证。
- **行为等价性**：发往每个客户端的 `EntityStateMessage` 内容、频道、过滤规则与 `owner` vs `observer` 差异 100% 一致；同一 tick 内不同 `netId` 的到达顺序在原版 `HashSet<NetworkIdentity>` 中本就无序，语义等价。
- **线程安全性**：纯主线程执行。
- **插件兼容性**：高；需特别识别自定义重写了 `NetworkBehaviour.IsDirty()` 或在 `OnSerialize` 中无 SyncVar 脏位却写数据的插件（通过启动时/首次 Spawn 时反射检测 `IsDirty` 覆盖并自动纳入常驻检查白名单，彻底杜绝漏同步！）。
- **版本兼容性**：高；依赖 `Mirror.NetworkServer` / `NetworkIdentity` 标准内部结构。
- **测试与验证方法**：覆盖 `SyncVar`、`SyncList`、`SyncMode.Owner`、`SyncMode.Observers`、`syncInterval > 0`、自定义 `OnSerialize`、`Visibility.ForceHidden` 全套行为等价性测试。

---

### OPT-04：消除 `NetworkPollThread` 30ms 休眠与收包闭包分配 (`net.transport_direct_poll`)

- **原版程序集**：`Assembly-CSharp.dll`
- **完整类名与方法签名**：
  - `Mirror.LiteNetLib4Mirror.LiteNetLib4MirrorTransport.StartThreads()` / `LateUpdate()`
  - `Mirror.LiteNetLib4Mirror.LiteNetLib4MirrorServer.OnPeerConnected` / `OnNetworkReceive` / `OnNetworkError` / `OnPeerDisconnected`
- **原版实现逻辑**：`NetworkPollThread` 每 30ms 调用 `Host.PollEvents()`，回调中创建闭包 + `Action` 入队至 `Events`，主线程 `LateUpdate()` 再出队执行。
- **调用链路与调用频率**：所有入站 UDP 帧（移动输入、开火、语音、交互）；5,000–15,000 包/秒。
- **性能瓶颈原因**：30ms 硬休眠造成 60Hz 帧间输入脉冲聚合（抖动 P95/P99）并增加 0–30ms 网络延迟；每包额外分配 2 个堆对象。
- **时间与空间复杂度**：单包分发从 $O(1)$ + 2 次堆分配 + 1 次跨线程无锁入队/出队 降至 $O(1)$ + **0 次堆分配**。
- **优化实现方案**：
  - 仅在服务端模式（`LiteNetLib4MirrorServer.IsActive()`）启用：
    1. 不启动冗余的 `NetworkPollThread`（或使其在服务端立即退出）。
    2. 在 `LiteNetLib4MirrorTransport.LateUpdate()` 中（保持与原版完全相同的 PlayerLoop 阶段！）先排空可能残余的 `Events`，再直接调用 `LiteNetLib4MirrorCore.Host.PollEvents()`。
    3. `OnPeerConnected` / `OnNetworkReceive` / `OnNetworkError` / `OnPeerDisconnected` 直接同步执行对应处理并 `reader.Recycle()`，彻底消除闭包、委托分配与二次入队。
    4. `OnConnectionRequest` 保持不变，仍仅执行 `_connectionRequests.Enqueue(request)` 交由 `ConnectionRequestThread` 异步做 ECDSA 验签；另可将 `ConnectionRequestLoop` 的 `Thread.Sleep(30)` 优化为可唤醒等待（如 `SemaphoreSlim`/`AutoResetEvent` + 50ms 超时兜底），降低新玩家握手延迟。
- **预期性能收益与依据**：消除每包 2 次堆分配（减少 10k–20k objects/sec GC 压力），消除 30ms 收包脉冲导致的隔帧 CPU 尖峰，平滑 P95/P99 帧耗时并降低玩家平均操作延迟 ~15ms。
- **行为等价性**：所有网络回调仍在主线程的 `LateUpdate` 阶段、按 LiteNetLib 原始 FIFO 顺序执行；`ConnectionRequest` 仍在独立后台线程执行。100% 行为等价。
- **线程安全性**：`LiteNetManager.PollEvents()` 本身设计为在主线程调用，内部通过 `lock(_eventLock)` 与 `ReceiveThread` 同步，官方线程安全。
- **插件兼容性**：高；不改变 `Transport.OnServerDataReceived` 回调线程与时机，且 `LateUpdate()` 仍兼容任何外部往 `Events` 入队的插件。
- **版本兼容性**：高；`LiteNetLib4Mirror` 长期稳定（1.2.8）。
- **测试与验证方法**：对比启用前后帧内入包数分布方差、主线程 GC 分配字节数及连接/断连/压力收包回归测试。

---

### OPT-05：`VoiceTransceiver` 延迟构造事件与按频道单次打包 (`net.voice_batch_and_lazy_args`)

- **原版程序集**：`Assembly-CSharp.dll`
- **完整类名与方法签名**：`VoiceChat.Networking.VoiceTransceiver.ServerReceiveMessage(NetworkConnection conn, VoiceMessage msg)`
- **原版实现逻辑**：每条入站语音包无条件分配 `PlayerSendingVoiceMessageEventArgs`；随后遍历全服玩家，即使听众 `ValidateReceive` 返回 `None` 也无条件分配 `PlayerReceivingVoiceMessageEventArgs`（触发 2× `LabApi.Player.Get()`），并对每个可听见的玩家独立调用 `allHub.connectionToClient.Send(msg)` 重复序列化 Opus 字节流。
- **调用链路与调用频率**：客户端语音包（最高 100 包/秒/麦）→ `NetworkServer.OnTransportData` → `VoiceTransceiver.ServerReceiveMessage`；10 麦 × 60p = 60,000 内层迭代/秒。
- **性能瓶颈原因**：无监听者时每秒数万次 `EventArgs` 堆分配与 `Player.Get()`；相同 `(Speaker, Channel, Data)` 语音包被重复 Pack $P$ 次。
- **时间与空间复杂度**：序列化从 $O(P \times L_{opus})$ 降至 $O(K_{ch} \times L_{opus})$（通常 $K_{ch} \in \{1, 2\}$，减少 90%–98% 序列化与拷贝）；无事件监听者时堆分配归零。
- **优化实现方案**：
  1. 分别检测 `PlayerEvents.SendingVoiceMessage != null` 与 `PlayerEvents.ReceivingVoiceMessage != null`。无订阅者时跳过对应 `EventArgs` 构造与 `Player.Get()`。
  2. 若无 `ReceivingVoiceMessage` 订阅者（或订阅者未改写 `Speaker/Data/DataLength`），对每个实际出现的目标 `VoiceChatChannel`（枚举值极少，可用栈上/复用小数组按 `(byte)channel` 槽位缓存已 Pack 的 `NetworkWriterPooled`）**仅序列化一次**，随后直接向该频道所有听众发送 `ArraySegment<byte>`，最后统一归还池化 Writer。
  3. 完整保留 `OnVoiceMessageReceiving?.Invoke(msg, allHub)` 回调。
- **预期性能收益与依据**：多人开麦（大厅、阵营刷兵、观察者聊天、广播放歌）场景下显著降低主线程 CPU 与 Gen0 分配。
- **行为等价性**：发送字节、速率限制、静音检查、频道校验、`OnVoiceMessageReceiving` 与 LabAPI 事件语义 100% 等价。
- **线程安全性**：纯主线程。
- **插件兼容性**：高；完美兼容自定义语音插件、广播/音频插件（`AudioPlayerApi`/`SCPSLAudioApi`）与订阅 LabAPI 语音事件的插件。
- **版本兼容性**：高；`VoiceTransceiver.ServerReceiveMessage` 结构简洁稳定。
- **测试与验证方法**：单元测试覆盖邻近/无线电/SCP/观察者/广播/SCP-1576 频道及订阅事件修改 `msg.Channel`/`IsAllowed=false` 场景。

---

### OPT-06：`LabApi.Events.EventManager.InvokeEvent` 零分配调用链缓存 (`memory.labapi_event_cache`)

- **原版程序集**：`LabApi.dll`
- **完整类名与方法签名**：
  - `LabApi.Events.EventManager.InvokeEvent(this LabEventHandler? eventHandler)`
  - `LabApi.Events.EventManager.InvokeEvent<TEventArgs>(this LabEventHandler<TEventArgs>? eventHandler, TEventArgs args)`
- **原版实现逻辑**：每次有订阅者的事件触发均调用 `eventHandler.GetInvocationList()`，在 Mono 中每次都会在堆上分配一个全新的 `Delegate[]` 数组。
- **调用链路与调用频率**：全服所有 LabAPI 事件统一入口；若有插件订阅高频事件（如 `ValidatedVisibility`、`ReceivingVoiceMessage`），可达 60k–212k 次/秒。
- **性能瓶颈原因**：C# 委托本身**完全不可变**，但 `GetInvocationList()` 每次返回防御性拷贝数组。
- **时间与空间复杂度**：单次事件派发堆分配从 $O(k)$ 降至 **$0$ bytes**。
- **优化实现方案**：使用 `ConditionalWeakTable<Delegate, Delegate[]>` 缓存不可变委托实例与其 `GetInvocationList()` 结果（注意避免引用泄漏：当事件 `+=`/`-=` 产生新委托实例后，旧委托会被 GC 自动从 `ConditionalWeakTable` 回收！）；对单播委托（绝大多数情况，如缓存数组长度为 1）直接调用并保留相同的 `try/catch` 异常隔离与日志格式。
- **预期性能收益与依据**：在大型插件混合运行场景（多插件监听高频事件）中彻底消除事件派发本身的数组分配。
- **行为等价性**：100% 等价（含异常捕获与后续监听者继续执行的隔离语义）。
- **线程安全性**：`ConditionalWeakTable` 在 Mono/BCL 中线程安全，支持在后台线程触发的 `PlayerPreAuthenticating` 事件。
- **插件兼容性**：极高；透明利好所有 LabAPI 插件（需注意泛型方法 Harmony 补丁在 Mono 下的适配性，若泛型 Patch 受限可针对非泛型与已知高频事件特化或改用调用点 Transpiler）。
- **版本兼容性**：高；`LabApi.dll` 稳定公开 API。
- **测试与验证方法**：多播订阅、动态增删监听者、单监听者抛异常时验证后续监听者仍正常执行，并用 `GC.GetAllocatedBytesForCurrentThread()` 断言 0 分配。

---

### OPT-07：`PlayerEffectsController` 状态效果接口索引与活跃位图 (`entities.effects_interface_index`)

- **原版程序集**：`Assembly-CSharp.dll`
- **完整类名与方法签名**：
  - `PlayerEffectsController.Awake()`
  - `PlayerRoles.FirstPersonControl.FirstPersonMovementModule.VelocityForState(PlayerMovementState, bool)`
  - `PlayerRoles.FirstPersonControl.FpcMotor.TryGetOverride(out Vector3)`
  - `PlayerRoles.FirstPersonControl.FpcStateProcessor` (`ServerUseRate`, `ServerRegenRate`, `SprintingDisabled`)
  - `PlayerRoles.FirstPersonControl.Thirdperson.AnimatedCharacterModel.PlayFootstep()`
  - `PlayerRoles.PlayableScps.VisionInformation.CheckIsInDarkness(ReferenceHub, Vector3)`
- **原版实现逻辑**：每帧每个存活玩家重复 4–6 次全量遍历 `AllEffects`（50–80+ 元素）并执行 `is I...` 接口转型检查；`CheckIsInDarkness` 更使用了 LINQ `.Any()`。
- **调用链路与调用频率**：每帧每存活玩家 4–6 次 × 60p × 60Hz ≈ **1.5M–2M 次接口类型检查/秒**。
- **性能瓶颈原因**：`AllEffects` 在 `Awake()` 后不可变，且各接口实际实现类仅占总数 3%–15%，却每次扫描 100% 元素。
- **时间与空间复杂度**：单次查询从 $O(K_{all})$ 降至 $O(K_{iface})$（通常 1–8 个预转型接口引用，0 次 `is` 转型）乃至 $O(1)$（当活跃效果计数为 0 时直接返回）。
- **优化实现方案**：在 `PlayerEffectsController.Awake()` Postfix 为每个实例附加轻量缓存结构（通过 `ConditionalWeakTable` 或组件附加），预分类存储已转型数组：`IMovementSpeedModifier[]`、`IMovementInputOverride[]`、`IStaminaModifier[]`、`IFootstepEffect[]`、`IAmbientBoostingEffect[]`；可选叠加活跃计数快路径（在 `StatusEffectBase.ForceIntensity` 维护，但注意 `MovementModifierActive` 等属性可能不完全等同于 `IsEnabled`，因此**必须仅跳过未实现该接口的效果，绝不跳过实现类自身对 `MovementModifierActive` 的判断**，除非逐类核实！仅预分类接口数组即保证 **100% 严格等价**且已削减 85%–95% 遍历量！）。
- **预期性能收益与依据**：将热路径接口扫描从 60–80 次动态转型降至 1–8 次直接数组访问，同时消除 `VisionInformation.CheckIsInDarkness` 的 LINQ 分配。
- **行为等价性**：100% 严格数学等价（所有实现对应接口的效果都会按与原版完全相同的数组顺序被检查）。
- **线程安全性**：纯主线程。
- **插件兼容性**：高；如果某插件在 `Awake()` 后动态修改 `AllEffects` 数组引用（极罕见），可通过比对 `AllEffects` 引用自动刷新缓存。
- **版本兼容性**：高；接口与方法签名稳定。
- **测试与验证方法**：对各状态效果（可乐、207、减速、肾上腺素、SCP-1344、滞留等）验证移动速度、体力消耗/恢复、脚步与黑暗视野判定 100% 一致。

---

### OPT-08：`VisionInformation` 短路与 SCP-173 / SCP-096 追踪器优化 (`physics.vision_and_scp_trackers`)

- **原版程序集**：`Assembly-CSharp.dll`
- **完整类名与方法签名**：
  - `PlayerRoles.PlayableScps.VisionInformation.GetVisionInformation(...)` & `CheckIsInDarkness(...)`
  - `PlayerRoles.PlayableScps.Scp096.Scp096TargetsTracker.Update()` & `UpdateTarget(ReferenceHub)`
  - `PlayerRoles.PlayableScps.Scp173.Scp173ObserversTracker.UpdateObservers()` & `IsObservedBy(ReferenceHub, float)`
- **原版实现逻辑**：
  1. `GetVisionInformation` 在 `flag == false` 时仍无条件执行 `CheckIsInDarkness`。
  2. `Scp096TargetsTracker.UpdateTarget` 对已在 `Targets` 中的敌人仍执行 `IsObservedBy` + 射线检测；`Update()` 在服务端对空 `_markers` 分配闭包。
  3. `Scp173ObserversTracker.UpdateObservers` 每帧无条件向全服广播 `ServerSendRpc(toAll: true)`（即使观察者数未变），且 `IsObservedBy` 内重复查询 `Owner.GetCurrentZone()`。
- **优化实现方案**：
  1. `Scp096TargetsTracker.UpdateTarget`：在 `IsObservedBy` 前增加 `if (Targets.Contains(target)) return;`；`Update()` 中当 `_markers.Count == 0` 跳过 `ForEachValue` 闭包。
  2. `Scp173ObserversTracker.UpdateObservers`：记录 `int prev = CurrentObservers;` 仅在 `CurrentObservers != prev` 时发送 `ServerSendRpc(toAll: true)`（新角色同步时 `FpcServerPositionDistributor.SendRole` 已自动补发确认包；可提供心跳兜底开关以防极端丢包，因通道 0 为 ReliableOrdered 理论不丢包）。
  3. `VisionInformation.GetVisionInformation`：当确认仅需 `IsLooking` 时（或在 `Scp173`/`Scp096` 调用点短路，或配置开关短路 `if (flag && checkInDarkness)`），避免 `flag == false` 时执行 `CheckIsInDarkness`；同时将 `CheckIsInDarkness` 改为 OPT-07 零分配数组循环。
- **预期性能收益与依据**：消除 SCP-173 在场时每秒 3,600 次冗余全服 RPC 与无效黑暗/LINQ 检测，消除 SCP-096 狂暴期对已锁定目标的持续冗余射线。
- **行为等价性**：对原版所有 7 处调用点 100% 行为等价（已静态审计全部调用点）。对于第三方插件可能读取 `!IsLooking` 时的 `IsInDarkness` 字段，采用“仅在内部调用点短路或提供显式配置开关”保证零破坏。
- **线程安全性**：纯主线程。
- **插件兼容性**：极高（不影响 `Scp173Events` / `Scp096Events` 任何触发时机）。
- **版本兼容性**：高。
- **测试与验证方法**：验证 SCP-173 凝视冻结/解冻、客户端 UI 观察者计数同步、SCP-096 触发/冷却/狂暴行为与原版完全一致。

---

### OPT-09：`StatusEffectBase` 禁用态组件休眠 (`entities.status_effect_sleep`) — Tier 2

- **原版程序集**：`Assembly-CSharp.dll`
- **完整类名与方法签名**：`CustomPlayerEffects.StatusEffectBase` (`Awake`, `ForceIntensity`, `Update`)
- **原版实现逻辑**：每个玩家 50–80+ 状态效果组件即使 `Intensity == 0` 仍保持 `Behaviour.enabled == true`，Unity 原生层每帧回调数千次。
- **优化实现方案**：启动时反射白名单筛选“未重写 `Update()` 且未声明 `LateUpdate()`/`FixedUpdate()`/`OnEnable()`/`OnDisable()`”的纯标准子类；在 `Awake()` 后若 `Intensity == 0` 则设 `enabled = false`；在 `ForceIntensity()` 检测到 `value > 0` 时设 `enabled = true`，在变为 `0` 时设 `enabled = false`。
- **风险与验证重点**：需审计第三方插件（含 Exiled / LabAPI Wrappers）是否曾误用 `effect.enabled` 判断效果激活（正确属性是 `effect.IsEnabled`）。因此归入 Tier 2，独立开关且启动时扫描或测试验证。

---

### OPT-10：`Mirror.Batcher` / `Unbatcher` 零拷贝与空池跳过 (`net.batcher_unbatcher_zerocopy`) — Tier 2

- **原版程序集**：`Mirror.dll`
- **完整类名与方法签名**：`Mirror.NetworkConnection.Update()`, `Mirror.Batcher.GetBatch(NetworkWriter)`, `Mirror.NetworkServer.OnTransportData(int, ArraySegment<byte>, int)`
- **优化实现方案**：
  1. `NetworkConnection.Update()`：检查 `Batcher` 是否有待发数据，空通道不租借 `NetworkWriterPooled`；非空时直接取出内部 `NetworkWriterPooled`（零 `CopyAndReturn` 二次拷贝）传给 `SendToTransport`，发完直接 `NetworkWriterPool.Return`。
  2. `NetworkServer.OnTransportData`：当 `!isLoadingScene && unbatcher.BatchesCount == 0 && data.Count >= 8` 时，直接用可复用 `NetworkReader` 原地解帧 `data`（零 `Unbatcher.AddBatch` 整包拷贝）；若处理中途 `isLoadingScene` 变为 `true`，才将剩余切片溢出写入 `unbatcher`。
- **行为等价性**：100% 字节级与异常断连语义等价。

---

### OPT-11：`FpcMotor` 静止跳过 SphereCast 与冗余写入消除 (`physics.fpc_motor_fastpath`) — Tier 2

- **原版程序集**：`Assembly-CSharp.dll`
- **完整类名与方法签名**：`PlayerRoles.FirstPersonControl.FpcMotor.UpdatePosition(out bool)` & `Move()`
- **优化实现方案**：
  1. 先计算 `Vector3 desiredMove = DesiredMove;`（注意保留 `DesiredMove` 内的 `stepOffset` 设置与反作弊位移校验副作用！）；仅当 `desiredMove.sqrMagnitude > 0f` 时才执行 `Physics.SphereCast`，否则直接设 `MoveDirection = new Vector3(0f, MoveDirection.y, 0f)`。
  2. `Move()` 中读取 `CachedTransform.position` 后直接更新 `_cachedPosition` 而非通过 `Position` setter 回写 `_transform.position`（保留 `CharacterModelInstance.OnPlayerMove()` 语义）。
  3. 缓存同帧 `ReceivedPosition.Position` 避免 `DesiredMove` 与 `GetFrameMove()` 二次计算。
  4. `FpcMouseLook.UpdateRotation()` 在专用服 `t2 == 1f` 时跳过读 `transform.rotation` 与 `Quaternion.Lerp`。
- **行为等价性**：数学严格等价（`Vector3.ProjectOnPlane(Vector3.zero, anyNormal).normalized == Vector3.zero`），不减少任何碰撞检测或反作弊校验。

---

### OPT-12：`LiteNetManager.NativeReceiveLogic` 零分配 Peer 查找 (`net.litenet_peer_lookup_noalloc`) — Tier 2

- **原版程序集**：`Assembly-CSharp.dll`
- **完整类名与方法签名**：`LiteNetLib.LiteNetManager.NativeReceiveLogic()`
- **优化实现方案**：维护一个与 `AddPeer`/`RemovePeer` 同步的轻量只读映射（IPv4 将 4 字节 IP + 2 字节端口打包为 `ulong` 键；IPv6 打包为 `struct Ipv6Key` 值类型），在 `NativeReceiveFrom` 中先按原始字节查已知 Peer，命中时零分配直传 `actualValue`；仅在未知新连接（握手/A2S 查询）时才分配 `IPAddress` 构造 `IPEndPoint`。
- **线程安全性**：与原版一样在 `_peersLock` 读写锁保护下同步更新与查询。

---

### OPT-13：房间多格缓存与路标紧凑索引 (`entities.room_and_waypoint_accel`) — Tier 2

- **原版程序集**：`Assembly-CSharp.dll`
- **完整类名与方法签名**：
  - `MapGeneration.CurrentRoomPlayerCache.ValidateCache()` / `RoomUtils.TryGetRoom(Vector3, out RoomIdentifier)`
  - `RelativePositioning.WaypointBase.ExtractWaypointData(...)`
- **优化实现方案**：
  1. `CurrentRoomPlayerCache`：死人/观察者先判 `IsAlive()` 再读位置；存活玩家若本帧 `worldPos` 与上次完全相同（或在同一静态房间且未移动超过阈值且非电梯内，精确做法是先做坐标精确相等快路径），直接复用结果。
  2. `RoomUtils.TryGetRoom`：地图生成后，将各静态房间的 `WorldspaceBounds` 覆盖的网格坐标（确认无重叠冲突的网格单元）预填充进辅助网格缓存，或对 `TryRaycastRoom` 命中的静态坐标网格按需缓存（排除动态电梯区域），大幅削减地表/大房间的每帧射线。
  3. `WaypointBase`：维护紧凑的活跃路标数组 `ActiveWaypoints[0.._activeCount-1]`（替代遍历全部 254 稀疏槽位），并将静态 `NetIdWaypoint` 位置提取为连续 `Vector3[]` 结构体数组，减少虚函数分派与指针跳转；对移动中的 `ElevatorWaypoint` 仍优先检查（因其命中返回 `-1f` 最高优先级）。

---

### OPT-14：`AdminToyBase` / `PickupStandardPhysics` / `EnvironmentalHazard` / `TeslaGate` 优化 (`entities.admintoy_pickup_hazard`) — Tier 2

- **`AdminToyBase`**：对于 `IsStatic == true` 且子类未重写 `LateUpdate`/`Update` 的实例，在服务端设置 `enabled = false`（通过 `NetworkIsStatic` setter 联动恢复），彻底消除数千静态建筑物体的每帧 `LateUpdate()`；非静态 Toy 在无变换改变时（需确认安全）减少冗余 SyncVar 虚比较。
- **`PickupStandardPhysics`**：用单一集中管理器替代几百个拾取物分别订阅 `StaticUnityMethods.OnUpdate` 多播委托（消除数百长度 `Delegate[]` 增删重分配）；对已休眠拾取物可维持极简连续数组检查（甚至通过低开销轮询或保持每帧检查以确保外力唤醒即时响应，不丢物理同步！）。
- **`EnvironmentalHazard.UpdateTargets()`**：将 `SourcePosition` 提至 `AllHubs` 循环外；`AffectedPlayers` 配合快速存在检查；无订阅者时跳过 `PlayersStayingInHazardEventArgs` 与 `ListPool<Player>` 分配。
- **`TeslaGateController.FixedUpdate()`**：无 `PlayerEvents.IdlingTesla` 订阅者时跳过每 50Hz 的 `PlayerIdlingTeslaEventArgs` 分配。

---

### OPT-15：低风险轻量微优化包 (`memory.inventory_stats_opus`) — Tier 1

- **`AhpStat.ServerUpdateProcesses()`**：`_activeProcesses.Count == 0` 时直接返回，跳过每帧 3,600 次 `ListPool<int>` 租还。
- **`Inventory.Update()`**：`UserInventory.Items.Count == 0` 时跳过 `ListPool<ushort>` 租还与字典遍历，且仅在值变化时写 4 个 SyncVar。
- **`SyncedStatBase.Update()`**：`Mode == SyncMode.Private` 时直接单播给 `Hub.connectionToClient`，从 $O(P)$ 降至 $O(1)$。
- **`VoiceModuleBase.SpawnObject()`**：在专用服上延迟初始化 `_defaultDecoder`（仅在首次访问 `Decoder` 属性时创建），彻底消除每次玩家生成/切角调用原生 `opus_decoder_create`/`destroy` 的非托管内存抖动。
- **空生命周期回调**：清理/验证确认无副作用的高实例数空 `Update` 组件（如 `PlayerEffectsController` 等不依赖 `enabled` 状态的空回调类）。

---

### OPT-16 & OPT-17：Tier 3 高阶并行实验（仅在实测证明瓶颈仍显著时启用）

- **OPT-16 (`physics.fpc_spherecast_batch`)**：在实测确认 `FpcStateProcessor.Mask` 不含 `Player` 层且 60 人移动 `SphereCast` 仍占 >0.8ms/帧时，在 `FirstPersonMovementModule._activeUpdates` 前批量填充 `NativeArray<SpherecastCommand>` 并调用 `SpherecastCommand.ScheduleBatch`，将移动玩家的坡面检测分散至 Unity PhysX 多核 Worker。
- **OPT-17 (`threading.fpc_parallel_encode`)**：在 OPT-02 完成后，仅当超大服（>60–100 人）实测 `FpcServerPositionDistributor` 纯值类型 Delta 编码与网络写入仍 >1.5ms/帧时，才在纯值类型快照上引入固定工作线程池分片写入各自独立缓冲，主线程统一递交 Batcher。

---

## 3. 明确否决的伪优化与反模式（严格避坑清单）

| 拟议做法 | 否决原因 |
| --- | --- |
| 禁用服务端 `AnimatedCharacterModel` 的 `Animator` | **破坏核心玩法**：命中盒 `HitboxIdentity` 挂载在骨骼节点上，SCP-096 头部判定与 SCP-939 脚步监听均依赖 `Animator`。 |
| 替换为 `SpatialHashingInterestManagement` 强制距离裁剪全部实体 | **破坏原版机制**：SCP-079 摄像头跨图切视角、观察者自由切换目标、全图门锁/广播/音效/生成器状态同步均依赖全图观察者可见性。 |
| 降低 `ServerTickrate` 或降低 `FpcServerPositionDistributor` 同步频次 | **违反严格约束**：直接降低同步精度与手感，属于牺牲质量换帧率。 |
| 直接对 `WriteAll` 或 `Broadcast` 套 `Parallel.ForEach` / `Task.Run` | **崩溃与负优化**：内层直接访问 Unity 非线程安全 API（`transform`、`CharacterController`、`Time.frameCount`）且破坏 `NetworkWriterPool` 非线程安全假设，同时小任务调度开销反超计算本身。 |
| 为减少 GC 把所有 `List`/`byte[]` 放进无上限静态缓存 | **内存泄漏风险**：所有对象池必须有明确容量上限与回合清理机制。 |
