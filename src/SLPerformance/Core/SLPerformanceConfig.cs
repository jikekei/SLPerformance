using System.ComponentModel;

namespace SLPerformance.Core;

public sealed class SLPerformanceConfig
{
    [Description("Master switch for SLPerformance.")]
    public bool Enabled { get; set; } = true;

    [Description("Enable verbose diagnostic logging.")]
    public bool Debug { get; set; } = false;

    [Description("Automatically skip a module if another plugin (e.g., EXILED or AntiCheat) has already applied conflicting Harmony Prefix/Transpiler patches to the same target method.")]
    public bool AutoSkipOnHarmonyConflict { get; set; } = true;

    [Description("Enable low-overhead frame timing, GC, and network profiler.")]
    public bool EnableProfiler { get; set; } = true;

    [Description("Enable per-hotspot method timing probes at startup (can also be toggled live with `slperf probes on|off`).")]
    public bool EnableMethodProbes { get; set; } = false;

    // --- Tier 1 Modules (High ROI, exact equivalence, low risk) ---

    [Description("OPT-01: Fix Mirror.NetworkBehaviour.SendRPCInternal packing RpcMessage P+1 times instead of once.")]
    public bool NetMirrorRpcPackOnce { get; set; } = true;

    [Description("OPT-02: Optimize FpcServerPositionDistributor (O(P) tick snapshot, single ValidateVisibility, GetVisibleRole short-circuit, lazy EventArgs, PreviouslySent leak fix).")]
    public bool NetFpcDistributorFast { get; set; } = true;

    [Description("OPT-03: Replace Mirror.NetworkServer.Broadcast O(P*E) scan with entity-first O(E + D*P) dirty broadcast and single EntityStateMessage packing.")]
    public bool NetMirrorDirtyBroadcast { get; set; } = true;

    [Description("OPT-04: Eliminate LiteNetLib4MirrorTransport 30ms NetworkPollThread sleep and per-packet closure allocations by polling directly in LateUpdate.")]
    public bool NetTransportDirectPoll { get; set; } = true;

    [Description("OPT-05: VoiceTransceiver lazy EventArgs allocation and single-pack per output VoiceChatChannel.")]
    public bool NetVoiceBatchAndLazyArgs { get; set; } = true;

    [Description("OPT-06: Cache immutable Delegate[] invocation lists in LabApi.Events.EventManager.InvokeEvent.")]
    public bool MemoryLabApiEventCache { get; set; } = true;

    [Description("OPT-07: Pre-index PlayerEffectsController interface arrays (IMovementSpeedModifier, IMovementInputOverride, IStaminaModifier, IFootstepEffect, IAmbientBoostingEffect).")]
    public bool EntitiesEffectsInterfaceIndex { get; set; } = true;

    [Description("OPT-08: Optimize Scp096TargetsTracker, Scp173ObserversTracker (dirty-only RPC), and VisionInformation darkness checks.")]
    public bool PhysicsVisionAndScpTrackers { get; set; } = true;

    [Description("If true, VisionInformation.GetVisionInformation skips CheckIsInDarkness whenever flag is already false (affects all callers; safe for vanilla but disabled by default if third-party plugins inspect IsInDarkness when !IsLooking).")]
    public bool VisionDarknessShortCircuitGlobal { get; set; } = false;

    [Description("OPT-15: Fast paths for empty AhpStat, empty Inventory, SyncedStatBase Private O(1) send, and lazy OpusDecoder creation on dedicated server.")]
    public bool MemoryInventoryStatsOpus { get; set; } = true;

    // --- Tier 2 Modules (High ROI, medium complexity) ---

    [Description("OPT-09: Put reflection-verified standard StatusEffectBase components to sleep (enabled=false) while Intensity==0.")]
    public bool EntitiesStatusEffectSleep { get; set; } = true;

    [Description("OPT-10: Zero-copy Mirror Batcher flush (skip CopyAndReturn and empty channel pool rent) and zero-copy Unbatcher fast path.")]
    public bool NetBatcherUnbatcherZeroCopy { get; set; } = true;

    [Description("OPT-11: Skip FpcMotor ground SphereCast when stationary, eliminate redundant Transform.position write-back, and skip dedicated-server Quaternion.Lerp.")]
    public bool PhysicsFpcMotorFastPath { get; set; } = true;

    [Description("OPT-12: Zero-allocation IPv4/IPv6 peer lookup in LiteNetManager.NativeReceiveLogic.")]
    public bool NetLiteNetPeerLookupNoAlloc { get; set; } = true;

    [Description("OPT-13: Stationary player room cache, bounded static multi-cell room grid cache, and compact active WaypointBase array.")]
    public bool EntitiesRoomAndWaypointAccel { get; set; } = true;

    [Description("Maximum cached secondary static room grid cells (bounded to prevent unbounded memory growth).")]
    public int MaxCachedStaticRoomCoords { get; set; } = 16384;

    [Description("OPT-14: Static AdminToyBase LateUpdate sleep, Transform.hasChanged fast path, centralized PickupStandardPhysics updater, EnvironmentalHazard SourcePosition hoist and lazy EventArgs, Tesla lazy EventArgs.")]
    public bool EntitiesAdminToyPickupHazard { get; set; } = true;

    [Description("OPT-18: On headless dedicated servers, strip client-only visual character subcontrollers (HandPose/FeetIK/SecondaryRigs/WearableArmor/Glow/HatOffset) while preserving Lookat head hitboxes, and offload character Animator evaluation from single-threaded LateUpdate to Unity 6 parallel Job System.")]
    public bool AnimHeadlessCharacterAnimator { get; set; } = true;

    [Description("When OPT-18 is enabled, move character Animators to Unity 6 native multi-threaded DirectorUpdateAnimation (true, recommended on >=4 cores) or keep CullingSubcontroller modulo throttling with visual subcontrollers stripped (false).")]
    public bool HeadlessAnimatorParallelNative { get; set; } = true;

    [Description("OPT-19: Put idle stationary BasicDoor (BreakableDoor/ElevatorDoor/PryableDoor) and LockerChamber Animators to sleep on the server until interacted with or damaged.")]
    public bool EntitiesIdleWorldAnimatorSleep { get; set; } = true;

    [Description("OPT-20: Put idle RoomLightController, TimedGrenadePickup, and stationary Scp079Camera instances to sleep until activated, and skip client-only visual Updates/LateUpdates on headless dedicated servers (BreakableWindow, AmmoPickup digits, EnvironmentalAlarm, LiquidWobble, OneSidedKeycardFlipper, PopupInterobject).")]
    public bool EntitiesHeadlessWorldSleep { get; set; } = true;

    [Description("OPT-21: Accelerate mass role changes & simultaneous wave respawns via amortized role/model PoolManager pre-warming, a virgin starting-item reserve buffer (never recycles used items), pack-once SendNewRoleInfo + Dummy skip, and WaveManager.SyncValues direct-send fix.")]
    public bool RolesMassRoleChangeAccel { get; set; } = true;

    [Description("When OPT-21 is enabled, gradually pre-warm common PlayerRoleBase and CharacterModel pools up to server slot capacity during quiet frames.")]
    public bool PrewarmRolePools { get; set; } = true;

    [Description("When OPT-21 is enabled, maintain a virgin pre-instantiated starting-item reserve under an inactive parent (Awake deferred until handed out; used items are never recycled).")]
    public bool PrewarmItemReserve { get; set; } = true;

    [Description("Maximum per-frame main-thread time budget in milliseconds for OPT-21 amortized pool/item pre-warming (clamped to 0.5..8.0ms).")]
    public float PrewarmBudgetMsPerFrame { get; set; } = 2.0f;

    [Description("OPT-22: Eliminate real-player join TPS spikes via background ECDH P-384 keypair pool, async BouncyCastle auth/badge crypto offload, ServerRoles redundant badge verify skip, in-memory BanHandler cache, NicknameSync compiled Regex cache, and single-writer SendSpawnMessage.")]
    public bool AuthJoinSpikeAccel { get; set; } = true;

    [Description("When OPT-22 is enabled, offload pure-CPU BouncyCastle P-384 ECDSA/ECDH authentication math to bounded background workers (zero crypto behavior change).")]
    public bool AsyncAuthCryptoOffload { get; set; } = true;

    [Description("When OPT-22 is enabled, number of fresh NIST P-384 ECDH keypairs to keep pre-generated in the background pool (clamped to 2..32).")]
    public int EcdhPreGenPoolSize { get; set; } = 8;

    // --- Tier 3 Modules (Opt-in experimental parallelism / physics batching) ---

    [Description("OPT-16 (Experimental, Tier 3): Batch moving players' ground SphereCasts with Unity SpherecastCommand.ScheduleBatch when FpcStateProcessor.Mask excludes Player layer.")]
    public bool PhysicsFpcSpherecastBatch { get; set; } = false;

    [Description("Minimum active FPC players required before OPT-16 schedules a native SpherecastCommand batch.")]
    public int SpherecastBatchMinPlayers { get; set; } = 16;

    [Description("OPT-17 (Experimental, Tier 3): Parallelize pure-value-type FPC delta encoding on a bounded worker pool for very large player counts when no visibility/role hooks are active.")]
    public bool ThreadingFpcParallelEncode { get; set; } = false;

    [Description("Minimum player count to trigger OPT-17 parallel encoding.")]
    public int ParallelEncodeMinPlayers { get; set; } = 48;

    [Description("Fixed worker thread count for OPT-17 bounded pool (clamped to [1, 4]).")]
    public int ParallelWorkerThreads { get; set; } = 2;
}
