using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using CentralAuth;
using GameObjectPools;
using HarmonyLib;
using InventorySystem;
using InventorySystem.Items;
using InventorySystem.Items.Firearms;
using InventorySystem.Items.Pickups;
using InventorySystem.Items.Thirdperson;
using Mirror;
using NetworkManagerUtils.Dummies;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using PlayerRoles.FirstPersonControl.Spawnpoints;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables;
using PlayerStatsSystem;
using Respawning;
using Respawning.Waves;
using RoundRestarting;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using UnityEngine;
using VoiceChat;

namespace SLPerformance.Entities;

/// <summary>
/// OPT-21: Mass Role-Change &amp; Simultaneous Respawn Spike Accelerator + Deep Burst Profiler (`slperf roleburst`).
///
/// Eliminates single-frame freeze spikes at 48-player Round Start, MTF/Chaos respawn waves, mass Spectator
/// transitions, and mass `forceclass`:
/// 1. Amortized Adaptive PoolManager Pre-Warmer: Gradually expands cold PlayerRoleBase and shared CharacterModel
///    pools during lobby &amp; quiet frames within a strict ms/frame budget, with Adaptive High-Water-Mark scaling.
/// 2. Safe Virgin ItemBase Reserve Buffer (with Firearm Animator pre-disable &amp; Adaptive High-Water-Mark):
///    Pre-instantiates brand-new untouched starting items under an inactive DontDestroyOnLoad parent (Awake/OnEnable
///    deferred until popped onto active ItemWorkspace; Firearm ServerSideAnimator pre-disabled while inactive so Unity
///    never runs useless Mecanim bind only for Firearm.InitializeSubcomponents() to disable it). Used items are NEVER recycled.
/// 3. Headless Zero-Ping-Pong Model Activation &amp; Parallel Setup (when OPT-18 ParallelNative is active):
///    - Disables pooled CharacterModel's Animator on ResetObject so `SetActive(true)` never triggers premature Animator bind.
///    - Reparents CharacterModel under Hub.transform WHILE STILL INACTIVE in `FirstPersonMovementModule.SetModel`,
///      eliminating `SetParent(null)` + `SceneManager.MoveGameObjectToScene` + second active `SetParent`.
///    - Replaces 48x sync main-thread `Animator.Rebind() + Animator.Update(10f)` with parameter init + single
///      `Animator.enabled = true` evaluated in parallel across worker threads in the very same frame's `PreLateUpdate`.
/// 4. Headless UI/Network Burst Cleanup:
///    - Strips client-only `GlobalChatIndicatorManager` (Subscribe/Unsubscribe/LateUpdate TMP Canvas UI rebuilds).
///    - Strips client-only `EmotionSubcontroller` facial blendshapes.
///    - Pack-once `PlayerRoleManager.SendNewRoleInfo` + Dummy skip.
///    - Direct per-player `WaveManager.SyncValues` fix (prevents O(P^2) broadcast storm) + `SyncedStatMessages` Dummy skip.
/// 5. Zero-Conflict Deep Role-Change Burst Profiler (`slperf roleburst`): Full breakdown of RoleSetup
///    (DisableRole, SetModel, AnimSetup, SpawnPos, PoolMiss), Loadout (Clear, CreateItem, OnAdded/Attach),
///    SendRoleInfo, and Events/Other.
/// </summary>
public sealed class MassRoleChangeAccelModule : OptimizationModuleBase
{
    public override string Id => "roles.mass_role_change_accel";
    public override string Name => "Mass Role-Change & Wave Respawn Spike Accelerator";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Amortized adaptive role/model pool pre-warmer, virgin item reserve (with Firearm Animator pre-disable), inactive SetModel reparent & parallel AnimSetup, headless GlobalChatIndicator strip, pack-once SendNewRoleInfo, WaveManager/SyncedStat fixes, and `slperf roleburst`.";

    private delegate bool TryGetPoolableDelegate(object pool, out PoolObject poolableObject);

    private sealed class PoolPrewarmTarget
    {
        public GameObject Prefab;
        public string Label;
        public int TargetCount;
        public object PoolInstance;
        public Stack<PoolObject> PoolStack;
        public bool IsRoundStartPriority;
        public bool IsParkableHumanModel;
        public bool AllWarmParked;
    }

    private sealed class ItemReserveEntry
    {
        public ItemType ItemType;
        public ItemBase Template;
        public int TargetCount;
        public bool IsRoundStartPriority;
        public readonly Stack<ItemBase> Stack = new Stack<ItemBase>(48);
    }

    private struct BurstAccumulator
    {
        public bool Active;
        public DateTime StartUtc;
        public int StartFrame;
        public int LastFrame;
        public long LastTimestamp;
        public int RoleChanges;
        public RoleChangeReason PrimaryReason;
        public long ServerSetRoleTicks;
        public long InitNewRoleTicks;
        public long DisableRoleTicks;
        public long DisableModelRetTicks;
        public long SetModelTicks;
        public long SetModelParentTicks;
        public long SetModelActiveTicks;
        public int SetModelReuseCount;
        public int SetModelParkedCount;
        public long AnimSetupTicks;
        public long SpawnPosTicks;
        public long GrantLoadoutTicks;
        public long ClearItemsTicks;
        public long CreateItemTicks;
        public long SendNewRoleInfoTicks;
        public long PoolMissTicks;
        public int PoolMissCount;
        public int ItemReserveHits;
        public int ItemReserveMisses;
        public int ClearItemsCount;
    }

    private struct RoleBurstSnapshot
    {
        public DateTime TimestampUtc;
        public int RoleChanges;
        public int FrameSpan;
        public RoleChangeReason PrimaryReason;
        public double ServerSetRoleMs;
        public double InitNewRoleMs;
        public double DisableRoleMs;
        public double DisableModelRetMs;
        public double SetModelMs;
        public double SetModelParentMs;
        public double SetModelActiveMs;
        public int SetModelReuseCount;
        public int SetModelParkedCount;
        public double AnimSetupMs;
        public double SpawnPosMs;
        public double GrantLoadoutMs;
        public double ClearItemsMs;
        public double CreateItemMs;
        public double SendNewRoleInfoMs;
        public double PoolMissMs;
        public int PoolMissCount;
        public int ItemReserveHits;
        public int ItemReserveMisses;
        public int ClearItemsCount;
    }

    private const int BurstHistoryCapacity = 8;
    private const int MassBurstMinCalls = 4;
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private static int _hashForward;
    private static int _hashGrounded;
    private static int _hashStrafe;
    private static int _hashSpeed;
    private static bool _animHashesInitialized;

    // Compiled reflection accessors
    private static Func<PoolManager, IDictionary> _getPoolLookup;
    private static Func<PoolManager, Dictionary<GameObject, PoolObject>> _getPoolObjectLookup;
    private static Func<object, Stack<PoolObject>> _getPoolStack;
    private static Func<object, Transform> _getPoolParentTransform;
    private static Func<object, PoolObject> _getPoolPrefab;
    private static Action<PoolObject, object> _initPoolObject;
    private static Action<object> _addNewPrefabToPool;
    private static TryGetPoolableDelegate _tryGetPoolable;
    private static Func<PoolObject, object> _getPoolObjectOwner;
    private static Func<bool> _getRolesLoaded;
    private static Func<bool> _getItemsLoaded;
    private static Func<Inventory, ReferenceHub> _getInventoryHub;
    private static Action<ItemBase, ReferenceHub> _setItemOwner;
    private static Action<ItemBase, ushort> _setItemSerial;
    private static Func<Func<ReferenceHub, ReferenceHub, RoleTypeId, NetworkWriter, RoleTypeId>> _getRoleSyncEvent;
    private static Action<RoleSyncInfo, int, int, int> _diagOnSendRoleSync;
    private static Func<SpawnableWaveBase, UpdateMessageFlags, WaveUpdateMessage> _createWaveUpdateMessage;

    // FirstPersonMovementModule.SetModel & AnimatedCharacterModel.Setup headless parallel accessors
    private static Func<FirstPersonMovementModule, ReferenceHub> _getFpmmHub;
    private static Func<FirstPersonMovementModule, PlayerRoleBase> _getFpmmRole;
    private static Action<FirstPersonMovementModule, CharacterModel> _setFpmmModelInstance;
    private static Action<FirstPersonMovementModule, bool> _setFpmmCharControllerSet;
    private static Action<CharacterModel, ReferenceHub, IFpcRole, Vector3, Quaternion> _callBaseCharModelSetup;
    private static Action<CharacterModel, Material[]> _setCharModelFadeableMaterials;
    private static Action<AnimatedCharacterModel, PlayerRoleBase> _setAnimModelRole;
    private static Action<AnimatedCharacterModel, FirstPersonMovementModule> _setAnimModelFpcModule;
    private static Func<AnimatedCharacterModel, Animator> _getAnimModelAnimator;
    private static Func<AnimatedCharacterModel, AnimatorOverrideController> _getAnimModelOverride;
    private static Func<AnimatedCharacterModel, IAnimatedModelSubcontroller[]> _getAnimModelSubcontrollers;
    private static MethodInfo _animModelOnGroundedMethod;

    // Headless Wearable accessor
    private static Action<DisplayableWearableBase, WearableSubcontroller> _setWearableSubcontroller;

    // Warm-active parked human model position (5 km below map, far from Pocket Dimension at -1995 and Surface at +1000)
    private static readonly Vector3 ParkedPoolWorldPosition = new Vector3(0f, -5000f, 0f);

    // Runtime module state
    private static bool _active;
    private static bool _prewarmRolePoolsEnabled;
    private static bool _prewarmItemReserveEnabled;
    private static float _prewarmBudgetMs = 2.0f;
    private static bool _poolPlanBuilt;
    private static bool _itemPlanBuilt;
    private static int _poolPlanSlots = -1;
    private static bool _isPrewarming;
    private static long _lastBurstEndTimestamp;

    // Same-template CharacterModel fast-reassign stash (consumed inside the same InitializeNewRole call; Finalizer guarantees flush)
    private static ReferenceHub _retainedModelHub;
    private static GameObject _retainedModelTemplate;
    private static CharacterModel _retainedModel;
    private static bool _isRetainingModelReset;

    // Headless model visual stripping & warm-active parking state
    private static readonly HashSet<CharacterModel> StrippedHeadlessModels = new HashSet<CharacterModel>();
    private static readonly HashSet<AnimatedCharacterModel> ParkedActiveModels = new HashSet<AnimatedCharacterModel>();
    private static readonly HashSet<Transform> ParkedPoolParents = new HashSet<Transform>();
    private static readonly List<Renderer> DisabledRenderers = new List<Renderer>(256);
    private static readonly List<MonoBehaviour> DisabledVisualBehaviours = new List<MonoBehaviour>(256);
    private static readonly List<Renderer> RendererScratch = new List<Renderer>(32);
    private static readonly List<KeyValuePair<AnimationClip, AnimationClip>> OverrideCheckBuffer = new List<KeyValuePair<AnimationClip, AnimationClip>>(64);

    // Pool pre-warmer state
    private static readonly List<PoolPrewarmTarget> PoolTargets = new List<PoolPrewarmTarget>(48);
    private static readonly Dictionary<object, PoolPrewarmTarget> PoolTargetByPool = new Dictionary<object, PoolPrewarmTarget>(48);
    private static readonly Dictionary<object, int> PoolTotalCounts = new Dictionary<object, int>(64);
    private static int _poolCursor;
    private static long _totalPoolPrewarmed;
    private static long _totalPoolMisses;

    // Virgin item reserve state
    private static GameObject _reserveRoot;
    private static readonly List<ItemReserveEntry> ItemReserveList = new List<ItemReserveEntry>(36);
    private static readonly Dictionary<ItemType, ItemReserveEntry> ItemReserveMap = new Dictionary<ItemType, ItemReserveEntry>(40);
    private static readonly Dictionary<ItemType, int> BurstItemDemand = new Dictionary<ItemType, int>(16);
    private static readonly Dictionary<object, int> BurstPoolMissByPool = new Dictionary<object, int>(8);
    private static int _itemCursor;
    private static long _totalItemsPrewarmed;
    private static long _totalItemReserveHits;
    private static long _totalItemReserveMisses;

    // Network & headless setup counters
    private static long _sendNewRoleInfoCalls;
    private static long _sendNewRoleInfoDummySkips;
    private static long _sendNewRoleInfoPackOnceSends;
    private static long _waveSyncDirectCalls;
    private static long _waveSyncDummySkips;
    private static long _waveSyncBroadcastsPrevented;
    private static long _animSetupFastPathCalls;
    private static long _setModelFastPathCalls;
    private static long _setModelReuseHits;
    private static long _setModelParkedHits;
    private static long _animOverrideSkips;
    private static long _headlessModelsStripped;
    private static long _syncedStatDummySkips;
    private static long _globalChatUiSkips;

    // Role burst profiler state (main-thread only)
    private static int _inRoleChangeDepth;
    private static int _inRoleChangeStartFrame;
    private static int _inInitRoleDepth;
    private static int _inDisableRoleDepth;
    private static int _inSetModelDepth;
    private static int _inAnimSetupDepth;
    private static int _inSpawnPosDepth;
    private static int _inGrantLoadoutDepth;
    private static int _inCreateItemDepth;
    private static long _totalRoleChangesObserved;
    private static BurstAccumulator _activeBurst;
    private static readonly RoleBurstSnapshot[] BurstHistory = new RoleBurstSnapshot[BurstHistoryCapacity];
    private static int _burstHistoryWrite;
    private static int _burstHistoryCount;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.RolesMassRoleChangeAccel;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        if (!CompileAccessors(out string err))
        {
            return MarkIncompatible(err);
        }

        _prewarmRolePoolsEnabled = config.PrewarmRolePools;
        _prewarmItemReserveEnabled = config.PrewarmItemReserve;
        _prewarmBudgetMs = Mathf.Clamp(config.PrewarmBudgetMsPerFrame, 0.5f, 8.0f);

        int appliedFeatures = 0;
        bool okSendRole = false;
        bool okCreateItem = false;
        bool okWaveSync = false;
        bool okAnimSetup = false;
        bool okSetModel = false;
        bool okModelReuse = false;
        bool okWarmPark = false;
        bool okAnimOverrideSkip = false;
        bool okWearableStrip = false;
        bool okChatUi = false;
        bool okStatDummy = false;
        bool okBurstHooks = false;

        // 1. Pack-once SendNewRoleInfo + Dummy skip
        MethodInfo sendNewRoleInfo = AccessTools.Method(typeof(PlayerRoleManager), "SendNewRoleInfo", Type.EmptyTypes);
        if (sendNewRoleInfo != null)
        {
            okSendRole = patchManager.PatchPrefix(
                Id,
                sendNewRoleInfo,
                AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(SendNewRoleInfoPrefix)),
                guard,
                config);
            if (okSendRole) appliedFeatures++;
        }

        // 2. Virgin ItemBase reserve interception in Inventory.CreateItemInstance (+ full timing Prefix/Postfix)
        MethodInfo createItem = AccessTools.Method(
            typeof(Inventory),
            nameof(Inventory.CreateItemInstance),
            new[] { typeof(ItemIdentifier), typeof(bool) });

        if (_prewarmItemReserveEnabled && createItem != null)
        {
            okCreateItem = patchManager.PatchPrefix(
                Id,
                createItem,
                AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(CreateItemInstancePrefix)),
                guard,
                config);
            if (okCreateItem) appliedFeatures++;
        }

        if (createItem != null)
        {
            patchManager.PatchPrefix(Id, createItem, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(CreateItemTimingPrefix)), guard, config, Priority.First, checkConflict: false);
            patchManager.PatchPostfix(Id, createItem, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(CreateItemTimingPostfix)), guard, config, Priority.Last, checkConflict: false);
        }

        // 3. WaveManager.SyncValues direct per-player send + Dummy skip
        MethodInfo waveSync = AccessTools.Method(typeof(WaveManager), "SyncValues", new[] { typeof(ReferenceHub) });
        if (waveSync != null && _createWaveUpdateMessage != null)
        {
            okWaveSync = patchManager.PatchPrefix(
                Id,
                waveSync,
                AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(WaveSyncValuesPrefix)),
                guard,
                config);
            if (okWaveSync) appliedFeatures++;
        }

        // 4. Headless zero-ping-pong SetModel (inactive reparent + same-template fast-reassign) & parallel AnimatedCharacterModel.Setup
        MethodInfo setModel = AccessTools.Method(typeof(FirstPersonMovementModule), "SetModel", new[] { typeof(GameObject) });
        MethodInfo onRoleDisabled = AccessTools.Method(typeof(FirstPersonMovementModule), "OnRoleDisabled", new[] { typeof(RoleTypeId) });
        if (setModel != null && _tryGetPoolable != null && _getFpmmHub != null && _getFpmmRole != null && _setFpmmModelInstance != null)
        {
            okSetModel = patchManager.PatchPrefix(
                Id,
                setModel,
                AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(SetModelFastPrefix)),
                guard,
                config);
            if (okSetModel) appliedFeatures++;

            if (onRoleDisabled != null && _setFpmmCharControllerSet != null)
            {
                okModelReuse = patchManager.PatchPrefix(
                    Id,
                    onRoleDisabled,
                    AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(OnRoleDisabledRetainModelPrefix)),
                    guard,
                    config);
                if (okModelReuse) appliedFeatures++;
            }
        }

        if (setModel != null)
        {
            patchManager.PatchPrefix(Id, setModel, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(SetModelTimingPrefix)), guard, config, Priority.First, checkConflict: false);
            patchManager.PatchPostfix(Id, setModel, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(SetModelTimingPostfix)), guard, config, Priority.Last, checkConflict: false);
        }

        if (onRoleDisabled != null)
        {
            patchManager.PatchPrefix(Id, onRoleDisabled, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(OnRoleDisabledTimingPrefix)), guard, config, Priority.First, checkConflict: false);
            patchManager.PatchPostfix(Id, onRoleDisabled, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(OnRoleDisabledTimingPostfix)), guard, config, Priority.Last, checkConflict: false);
        }

        // Disable pooled Animator while inactive on return-to-pool so next SetActive(true) doesn't trigger premature Mecanim bind
        MethodInfo animModelReset = AccessTools.Method(typeof(AnimatedCharacterModel), nameof(AnimatedCharacterModel.ResetObject), Type.EmptyTypes);
        if (animModelReset != null && _getAnimModelAnimator != null)
        {
            patchManager.PatchPostfix(
                Id,
                animModelReset,
                AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(AnimModelResetPostfix)),
                guard,
                config);
        }

        MethodInfo animModelSetup = AccessTools.Method(
            typeof(AnimatedCharacterModel),
            nameof(AnimatedCharacterModel.Setup),
            new[] { typeof(ReferenceHub), typeof(IFpcRole), typeof(Vector3), typeof(Quaternion) });

        if (animModelSetup != null && _callBaseCharModelSetup != null && _setAnimModelRole != null &&
            _setAnimModelFpcModule != null && _getAnimModelAnimator != null &&
            _getAnimModelSubcontrollers != null && _animModelOnGroundedMethod != null)
        {
            okAnimSetup = patchManager.PatchPrefix(
                Id,
                animModelSetup,
                AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(AnimatedModelSetupPrefix)),
                guard,
                config);
            if (okAnimSetup) appliedFeatures++;
        }

        if (animModelSetup != null)
        {
            patchManager.PatchPrefix(Id, animModelSetup, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(AnimSetupTimingPrefix)), guard, config, Priority.First, checkConflict: false);
            patchManager.PatchPostfix(Id, animModelSetup, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(AnimSetupTimingPostfix)), guard, config, Priority.Last, checkConflict: false);
        }

        // Skip redundant AnimatorOverrideController.ApplyOverrides() rebuilds in ResetOverrides when no clips changed
        MethodInfo resetOverrides = AccessTools.Method(
            typeof(ThirdpersonItemAnimationManager),
            nameof(ThirdpersonItemAnimationManager.ResetOverrides),
            new[] { typeof(AnimatedCharacterModel), typeof(Dictionary<AnimationClip, AnimationClip>) });
        if (resetOverrides != null && _getAnimModelOverride != null)
        {
            okAnimOverrideSkip = patchManager.PatchPrefix(
                Id,
                resetOverrides,
                AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(ResetOverridesFastPrefix)),
                guard,
                config);
            if (okAnimOverrideSkip) appliedFeatures++;
        }

        // Warm-active parking for Human AnimatedCharacterModels in GameObjectPools.Pool (eliminates 40ms SetActive(false/true) churn)
        Type poolType = typeof(PoolManager).Assembly.GetType("GameObjectPools.Pool");
        MethodInfo poolSpawnObj = poolType != null ? AccessTools.Method(poolType, "SpawnObject", Type.EmptyTypes) : null;
        MethodInfo poolResetObj = poolType != null ? AccessTools.Method(poolType, "ResetObject", new[] { typeof(PoolObject) }) : null;
        if (poolSpawnObj != null && poolResetObj != null &&
            _getPoolParentTransform != null && _getPoolPrefab != null && _initPoolObject != null)
        {
            bool wp1 = patchManager.PatchPrefix(Id, poolSpawnObj, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(PoolSpawnObjectParkPrefix)), guard, config);
            bool wp2 = patchManager.PatchPrefix(Id, poolResetObj, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(PoolResetObjectParkPrefix)), guard, config);
            okWarmPark = wp1 && wp2;
            if (okWarmPark) appliedFeatures++;
        }

        // Skip headless client-only visuals: EmotionSubcontroller, WearableArmor unpacking/material cloning, WearableSubcontroller visual toggles, CharacterModel fade materials, GlobalChatIndicatorManager TMP UI
        MethodInfo skipHeadless = AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(SkipOnHeadlessPrefix));

        MethodInfo emotionReset = AccessTools.Method(typeof(EmotionSubcontroller), nameof(EmotionSubcontroller.ResetObject), Type.EmptyTypes);
        if (emotionReset != null)
        {
            patchManager.PatchPrefix(Id, emotionReset, skipHeadless, guard, config);
        }

        MethodInfo charPrepFade = AccessTools.Method(typeof(CharacterModel), "PrepareFadeableMaterials", Type.EmptyTypes);
        if (charPrepFade != null && _setCharModelFadeableMaterials != null)
        {
            patchManager.PatchPrefix(Id, charPrepFade, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(CharModelPrepareFadeablePrefix)), guard, config);
        }

        MethodInfo wearableArmorInit = AccessTools.Method(typeof(WearableArmor), nameof(WearableArmor.Initialize), new[] { typeof(WearableSubcontroller) });
        if (wearableArmorInit != null && _setWearableSubcontroller != null)
        {
            bool w1 = patchManager.PatchPrefix(Id, wearableArmorInit, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(WearableArmorInitializePrefix)), guard, config);
            MethodInfo[] wearableSkipMethods =
            {
                AccessTools.Method(typeof(WearableArmor), "SetArmor", new[] { typeof(ItemType) }),
                AccessTools.Method(typeof(WearableArmor), nameof(WearableArmor.UpdateVisibility), Type.EmptyTypes),
                AccessTools.Method(typeof(WearableArmor), nameof(WearableArmor.SetFade), new[] { typeof(float) }),
                AccessTools.Method(typeof(WearableArmor), nameof(WearableArmor.ApplySyncvars), new[] { typeof(NetworkReader) }),
                AccessTools.Method(typeof(WearableArmor), nameof(WearableArmor.OnWornStatusChanged), Type.EmptyTypes),
                AccessTools.Method(typeof(WearableSubcontroller), nameof(WearableSubcontroller.OnReassigned), Type.EmptyTypes),
                AccessTools.Method(typeof(WearableSubcontroller), nameof(WearableSubcontroller.ResetObject), Type.EmptyTypes),
                AccessTools.Method(typeof(WearableSubcontroller), "SetWearables", new[] { typeof(WearableElements) }),
                AccessTools.Method(typeof(WearableSubcontroller), nameof(WearableSubcontroller.ClientReceiveWearables), new[] { typeof(WearableElements), typeof(NetworkReader) }),
                AccessTools.Method(typeof(SimpleWearable), nameof(SimpleWearable.UpdateVisibility), Type.EmptyTypes),
                AccessTools.Method(typeof(SimpleWearable), nameof(SimpleWearable.SetFade), new[] { typeof(float) })
            };
            int wCount = w1 ? 1 : 0;
            for (int i = 0; i < wearableSkipMethods.Length; i++)
            {
                if (wearableSkipMethods[i] != null && patchManager.PatchPrefix(Id, wearableSkipMethods[i], skipHeadless, guard, config))
                {
                    wCount++;
                }
            }

            okWearableStrip = wCount >= 6;
            if (okWearableStrip) appliedFeatures++;
        }

        MethodInfo chatSub = AccessTools.Method(typeof(GlobalChatIndicatorManager), nameof(GlobalChatIndicatorManager.Subscribe));
        MethodInfo chatUnsub = AccessTools.Method(typeof(GlobalChatIndicatorManager), nameof(GlobalChatIndicatorManager.Unsubscribe));
        MethodInfo chatLate = AccessTools.Method(typeof(GlobalChatIndicatorManager), "LateUpdate", Type.EmptyTypes);
        MethodInfo skipChatPrefix = AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(SkipGlobalChatUiOnHeadlessPrefix));
        bool c1 = chatSub != null && patchManager.PatchPrefix(Id, chatSub, skipChatPrefix, guard, config);
        bool c2 = chatUnsub != null && patchManager.PatchPrefix(Id, chatUnsub, skipChatPrefix, guard, config);
        bool c3 = chatLate != null && patchManager.PatchPrefix(Id, chatLate, skipChatPrefix, guard, config);
        okChatUi = c1 && c2 && c3;
        if (okChatUi) appliedFeatures++;

        // 5. Skip SyncedStatMessages.OnRoleChanged for DummyNetworkConnection
        MethodInfo syncedStatOnRole = AccessTools.Method(
            typeof(SyncedStatMessages),
            "OnRoleChanged",
            new[] { typeof(ReferenceHub), typeof(PlayerRoleBase), typeof(PlayerRoleBase) });
        if (syncedStatOnRole != null)
        {
            okStatDummy = patchManager.PatchPrefix(
                Id,
                syncedStatOnRole,
                AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(SyncedStatOnRoleChangedPrefix)),
                guard,
                config);
            if (okStatDummy) appliedFeatures++;
        }

        // 6. Non-conflicting read-only burst profiler & pool-miss counters (checkConflict: false)
        MethodInfo serverSetRole = AccessTools.Method(
            typeof(PlayerRoleManager),
            nameof(PlayerRoleManager.ServerSetRole),
            new[] { typeof(RoleTypeId), typeof(RoleChangeReason), typeof(RoleSpawnFlags), typeof(Action<PlayerRoleBase>) });
        MethodInfo initNewRole = AccessTools.Method(
            typeof(PlayerRoleManager),
            nameof(PlayerRoleManager.InitializeNewRole),
            new[] { typeof(RoleTypeId), typeof(RoleChangeReason), typeof(RoleSpawnFlags), typeof(NetworkReader) });
        MethodInfo disableRole = AccessTools.Method(
            typeof(PlayerRoleBase),
            nameof(PlayerRoleBase.DisableRole),
            new[] { typeof(RoleTypeId) });
        MethodInfo setPosition = AccessTools.Method(
            typeof(RoleSpawnpointManager),
            "SetPosition",
            new[] { typeof(ReferenceHub), typeof(PlayerRoleBase) });
        MethodInfo grantLoadout = AccessTools.Method(
            typeof(InventoryItemProvider),
            nameof(InventoryItemProvider.ServerGrantLoadout),
            new[] { typeof(ReferenceHub), typeof(PlayerRoleBase), typeof(bool) });
        MethodInfo destroyItem = AccessTools.Method(
            typeof(Inventory),
            nameof(Inventory.DestroyItemInstance),
            new[] { typeof(ushort), typeof(ItemPickupBase), typeof(ItemBase).MakeByRefType() });

        bool b1 = serverSetRole != null &&
                  patchManager.PatchPrefix(Id, serverSetRole, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(ServerSetRoleTimingPrefix)), guard, config, Priority.First, checkConflict: false) &&
                  patchManager.PatchPostfix(Id, serverSetRole, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(ServerSetRoleTimingPostfix)), guard, config, Priority.Last, checkConflict: false);
        bool b2 = initNewRole != null &&
                  patchManager.PatchPrefix(Id, initNewRole, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(InitNewRoleTimingPrefix)), guard, config, Priority.First, checkConflict: false) &&
                  patchManager.PatchFinalizer(Id, initNewRole, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(InitNewRoleTimingFinalizer)), guard, config, Priority.Last, checkConflict: false);
        bool b3 = grantLoadout != null &&
                  patchManager.PatchPrefix(Id, grantLoadout, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(GrantLoadoutTimingPrefix)), guard, config, Priority.First, checkConflict: false) &&
                  patchManager.PatchPostfix(Id, grantLoadout, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(GrantLoadoutTimingPostfix)), guard, config, Priority.Last, checkConflict: false);
        bool b4 = poolSpawnObj != null &&
                  patchManager.PatchPrefix(Id, poolSpawnObj, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(PoolSpawnObjectPrefix)), guard, config, Priority.First, checkConflict: false) &&
                  patchManager.PatchPostfix(Id, poolSpawnObj, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(PoolSpawnObjectPostfix)), guard, config, Priority.Last, checkConflict: false);
        if (disableRole != null)
        {
            patchManager.PatchPrefix(Id, disableRole, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(DisableRoleTimingPrefix)), guard, config, Priority.First, checkConflict: false);
            patchManager.PatchPostfix(Id, disableRole, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(DisableRoleTimingPostfix)), guard, config, Priority.Last, checkConflict: false);
        }
        if (setPosition != null)
        {
            patchManager.PatchPrefix(Id, setPosition, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(SpawnPosTimingPrefix)), guard, config, Priority.First, checkConflict: false);
            patchManager.PatchPostfix(Id, setPosition, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(SpawnPosTimingPostfix)), guard, config, Priority.Last, checkConflict: false);
        }
        if (destroyItem != null)
        {
            patchManager.PatchPrefix(Id, destroyItem, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(DestroyItemTimingPrefix)), guard, config, Priority.First, checkConflict: false);
            patchManager.PatchPostfix(Id, destroyItem, AccessTools.Method(typeof(MassRoleChangeAccelModule), nameof(DestroyItemTimingPostfix)), guard, config, Priority.Last, checkConflict: false);
        }

        okBurstHooks = b1 && b2 && b3 && b4;

        if (!_prewarmRolePoolsEnabled && appliedFeatures == 0)
        {
            patchManager.UnpatchModule(Id);
            return MarkConflict("External Harmony conflicts prevented OPT-21 from activating any acceleration hooks.");
        }

        if (_prewarmRolePoolsEnabled)
        {
            appliedFeatures++;
        }

        ResetRuntimeState();
        _active = true;
        StaticUnityMethods.OnUpdate += OnUpdateTick;

        StatusDetail =
            $"Active (PoolPrewarm={_prewarmRolePoolsEnabled}, WarmPark={okWarmPark}, ItemReserve={okCreateItem}, SetModelFast={okSetModel}, ModelReuse={okModelReuse}, AnimSetupFast={okAnimSetup}, OverrideSkip={okAnimOverrideSkip}, WearableStrip={okWearableStrip}, ChatUiStrip={okChatUi}, SendRolePackOnce={okSendRole}, WaveSyncFix={okWaveSync}, StatDummySkip={okStatDummy}, BurstProfiler={okBurstHooks}, Budget={_prewarmBudgetMs:F1}ms/fr)";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        _active = false;
        StaticUnityMethods.OnUpdate -= OnUpdateTick;
        FlushRetainedModel();
        RestoreParkedActiveModels();
        RestoreStrippedHeadlessVisuals();
        DestroyItemReserveRoot();
        ResetRuntimeState();
    }

    private static void RestoreParkedActiveModels()
    {
        foreach (AnimatedCharacterModel model in ParkedActiveModels)
        {
            if (model == null) continue;
            SetGameplaySubcontrollersEnabled(model, true);
            model.enabled = true;
            if (model.Pooled && model.gameObject != null && model.gameObject.activeSelf)
            {
                model.gameObject.SetActive(false);
            }
        }
        ParkedActiveModels.Clear();

        foreach (Transform parent in ParkedPoolParents)
        {
            if (parent != null)
            {
                parent.position = Vector3.zero;
            }
        }
        ParkedPoolParents.Clear();
    }

    private static void RestoreStrippedHeadlessVisuals()
    {
        for (int i = 0; i < DisabledRenderers.Count; i++)
        {
            Renderer r = DisabledRenderers[i];
            if (r != null) r.enabled = true;
        }
        DisabledRenderers.Clear();

        for (int i = 0; i < DisabledVisualBehaviours.Count; i++)
        {
            MonoBehaviour mb = DisabledVisualBehaviours[i];
            if (mb != null) mb.enabled = true;
        }
        DisabledVisualBehaviours.Clear();
        StrippedHeadlessModels.Clear();
    }

    private static void ResetRuntimeState()
    {
        _poolPlanBuilt = false;
        _itemPlanBuilt = false;
        _poolPlanSlots = -1;
        _isPrewarming = false;
        _lastBurstEndTimestamp = 0;
        _retainedModelHub = null;
        _retainedModelTemplate = null;
        _retainedModel = null;
        _isRetainingModelReset = false;
        PoolTargets.Clear();
        PoolTargetByPool.Clear();
        PoolTotalCounts.Clear();
        _poolCursor = 0;
        ItemReserveList.Clear();
        ItemReserveMap.Clear();
        BurstItemDemand.Clear();
        BurstPoolMissByPool.Clear();
        _itemCursor = 0;
        _inRoleChangeDepth = 0;
        _inRoleChangeStartFrame = -1;
        _inInitRoleDepth = 0;
        _inDisableRoleDepth = 0;
        _inSetModelDepth = 0;
        _inAnimSetupDepth = 0;
        _inSpawnPosDepth = 0;
        _inGrantLoadoutDepth = 0;
        _inCreateItemDepth = 0;
        _activeBurst = default;
    }

    internal static bool CompileAccessors(out string error)
    {
        error = null;

        Type poolType = typeof(PoolManager).Assembly.GetType("GameObjectPools.Pool");
        if (poolType == null)
        {
            error = "GameObjectPools.Pool internal type was not found in Pooling.dll.";
            return false;
        }

        _getPoolLookup = ReflectionHelpers.CreateInstanceFieldGetter<PoolManager, IDictionary>("_poolLookup");
        _getPoolObjectLookup = ReflectionHelpers.CreateInstanceFieldGetter<PoolManager, Dictionary<GameObject, PoolObject>>("PoolObjectLookup");
        _getPoolObjectOwner = ReflectionHelpers.CreateInstanceFieldGetter<PoolObject, object>("<_myPool>k__BackingField");
        _getRolesLoaded = ReflectionHelpers.CreateStaticFieldGetter<bool>(typeof(PlayerRoleLoader), "_loaded");
        _getItemsLoaded = ReflectionHelpers.CreateStaticFieldGetter<bool>(typeof(InventoryItemLoader), "_loaded");
        _getInventoryHub = ReflectionHelpers.CreateInstanceFieldGetter<Inventory, ReferenceHub>("_hub");
        _getRoleSyncEvent = ReflectionHelpers.CreateStaticFieldGetter<Func<ReferenceHub, ReferenceHub, RoleTypeId, NetworkWriter, RoleTypeId>>(
            typeof(FpcServerPositionDistributor), "_roleSyncEvent");

        MethodInfo setOwner = AccessTools.PropertySetter(typeof(ItemBase), nameof(ItemBase.Owner));
        MethodInfo setSerial = AccessTools.PropertySetter(typeof(ItemBase), nameof(ItemBase.ItemSerial));
        _setItemOwner = ReflectionHelpers.CreateOpenDelegate<Action<ItemBase, ReferenceHub>>(setOwner);
        _setItemSerial = ReflectionHelpers.CreateOpenDelegate<Action<ItemBase, ushort>>(setSerial);

        if (_getPoolLookup == null || _getPoolObjectLookup == null || _getPoolObjectOwner == null ||
            _getRolesLoaded == null || _getItemsLoaded == null || _getInventoryHub == null ||
            _getRoleSyncEvent == null || _setItemOwner == null || _setItemSerial == null)
        {
            error = "One or more required PoolManager / Inventory / PlayerRoleManager fields were not found.";
            return false;
        }

        FieldInfo poolStackField = AccessTools.Field(poolType, "_pool");
        FieldInfo poolParentField = AccessTools.Field(poolType, "parentTransform");
        FieldInfo poolPrefabField = AccessTools.Field(poolType, "Prefab");
        MethodInfo addNewPrefabMethod = AccessTools.Method(poolType, "AddNewPrefabToPool", Type.EmptyTypes);
        MethodInfo tryGetPoolableMethod = AccessTools.Method(poolType, "TryGetPoolableObject", new[] { typeof(PoolObject).MakeByRefType() });
        MethodInfo initPoolObjectMethod = AccessTools.Method(typeof(PoolObject), "InitializePoolObject", new[] { poolType });
        if (poolStackField == null || poolParentField == null || poolPrefabField == null ||
            addNewPrefabMethod == null || tryGetPoolableMethod == null || initPoolObjectMethod == null)
        {
            error = "GameObjectPools.Pool required fields or methods were not found.";
            return false;
        }

        try
        {
            DynamicMethod dmStack = new DynamicMethod(
                "SLPerf_Pool_GetStack",
                typeof(Stack<PoolObject>),
                new[] { typeof(object) },
                poolType.Module,
                skipVisibility: true);
            ILGenerator ilStack = dmStack.GetILGenerator();
            ilStack.Emit(OpCodes.Ldarg_0);
            ilStack.Emit(OpCodes.Castclass, poolType);
            ilStack.Emit(OpCodes.Ldfld, poolStackField);
            ilStack.Emit(OpCodes.Ret);
            _getPoolStack = (Func<object, Stack<PoolObject>>)dmStack.CreateDelegate(typeof(Func<object, Stack<PoolObject>>));

            DynamicMethod dmParent = new DynamicMethod(
                "SLPerf_Pool_GetParent",
                typeof(Transform),
                new[] { typeof(object) },
                poolType.Module,
                skipVisibility: true);
            ILGenerator ilParent = dmParent.GetILGenerator();
            ilParent.Emit(OpCodes.Ldarg_0);
            ilParent.Emit(OpCodes.Castclass, poolType);
            ilParent.Emit(OpCodes.Ldfld, poolParentField);
            ilParent.Emit(OpCodes.Ret);
            _getPoolParentTransform = (Func<object, Transform>)dmParent.CreateDelegate(typeof(Func<object, Transform>));

            DynamicMethod dmPrefab = new DynamicMethod(
                "SLPerf_Pool_GetPrefab",
                typeof(PoolObject),
                new[] { typeof(object) },
                poolType.Module,
                skipVisibility: true);
            ILGenerator ilPrefab = dmPrefab.GetILGenerator();
            ilPrefab.Emit(OpCodes.Ldarg_0);
            ilPrefab.Emit(OpCodes.Castclass, poolType);
            ilPrefab.Emit(OpCodes.Ldfld, poolPrefabField);
            ilPrefab.Emit(OpCodes.Ret);
            _getPoolPrefab = (Func<object, PoolObject>)dmPrefab.CreateDelegate(typeof(Func<object, PoolObject>));

            DynamicMethod dmInitPo = new DynamicMethod(
                "SLPerf_PoolObject_Init",
                typeof(void),
                new[] { typeof(PoolObject), typeof(object) },
                typeof(PoolObject).Module,
                skipVisibility: true);
            ILGenerator ilInitPo = dmInitPo.GetILGenerator();
            ilInitPo.Emit(OpCodes.Ldarg_0);
            ilInitPo.Emit(OpCodes.Ldarg_1);
            ilInitPo.Emit(OpCodes.Castclass, poolType);
            ilInitPo.Emit(OpCodes.Callvirt, initPoolObjectMethod);
            ilInitPo.Emit(OpCodes.Ret);
            _initPoolObject = (Action<PoolObject, object>)dmInitPo.CreateDelegate(typeof(Action<PoolObject, object>));

            DynamicMethod dmAdd = new DynamicMethod(
                "SLPerf_Pool_AddNewPrefab",
                typeof(void),
                new[] { typeof(object) },
                poolType.Module,
                skipVisibility: true);
            ILGenerator ilAdd = dmAdd.GetILGenerator();
            ilAdd.Emit(OpCodes.Ldarg_0);
            ilAdd.Emit(OpCodes.Castclass, poolType);
            ilAdd.Emit(OpCodes.Callvirt, addNewPrefabMethod);
            ilAdd.Emit(OpCodes.Ret);
            _addNewPrefabToPool = (Action<object>)dmAdd.CreateDelegate(typeof(Action<object>));

            DynamicMethod dmTryGet = new DynamicMethod(
                "SLPerf_Pool_TryGetPoolable",
                typeof(bool),
                new[] { typeof(object), typeof(PoolObject).MakeByRefType() },
                poolType.Module,
                skipVisibility: true);
            ILGenerator ilTryGet = dmTryGet.GetILGenerator();
            ilTryGet.Emit(OpCodes.Ldarg_0);
            ilTryGet.Emit(OpCodes.Castclass, poolType);
            ilTryGet.Emit(OpCodes.Ldarg_1);
            ilTryGet.Emit(OpCodes.Callvirt, tryGetPoolableMethod);
            ilTryGet.Emit(OpCodes.Ret);
            _tryGetPoolable = (TryGetPoolableDelegate)dmTryGet.CreateDelegate(typeof(TryGetPoolableDelegate));
        }
        catch (Exception ex)
        {
            error = $"Failed to compile GameObjectPools.Pool accessors: {ex.Message}";
            return false;
        }

        // FirstPersonMovementModule protected properties & CharacterModel/Wearable/Voice accessors
        _getFpmmHub = ReflectionHelpers.CreateOpenDelegate<Func<FirstPersonMovementModule, ReferenceHub>>(
            AccessTools.PropertyGetter(typeof(FirstPersonMovementModule), "Hub"));
        _getFpmmRole = ReflectionHelpers.CreateOpenDelegate<Func<FirstPersonMovementModule, PlayerRoleBase>>(
            AccessTools.PropertyGetter(typeof(FirstPersonMovementModule), "Role"));
        _setFpmmModelInstance = ReflectionHelpers.CreateOpenDelegate<Action<FirstPersonMovementModule, CharacterModel>>(
            AccessTools.PropertySetter(typeof(FirstPersonMovementModule), nameof(FirstPersonMovementModule.CharacterModelInstance)));
        _setFpmmCharControllerSet = ReflectionHelpers.CreateOpenDelegate<Action<FirstPersonMovementModule, bool>>(
            AccessTools.PropertySetter(typeof(FirstPersonMovementModule), nameof(FirstPersonMovementModule.CharControllerSet)));
        _setCharModelFadeableMaterials = ReflectionHelpers.CreateInstanceFieldSetter<CharacterModel, Material[]>("_fadeableMaterials");
        _setWearableSubcontroller = ReflectionHelpers.CreateOpenDelegate<Action<DisplayableWearableBase, WearableSubcontroller>>(
            AccessTools.PropertySetter(typeof(DisplayableWearableBase), nameof(DisplayableWearableBase.Subcontroller)));

        // Non-virtual base CharacterModel.Setup caller & AnimatedCharacterModel private accessors
        MethodInfo baseSetupMethod = AccessTools.Method(
            typeof(CharacterModel),
            nameof(CharacterModel.Setup),
            new[] { typeof(ReferenceHub), typeof(IFpcRole), typeof(Vector3), typeof(Quaternion) });
        _setAnimModelRole = ReflectionHelpers.CreateOpenDelegate<Action<AnimatedCharacterModel, PlayerRoleBase>>(
            AccessTools.PropertySetter(typeof(AnimatedCharacterModel), "Role"));
        _setAnimModelFpcModule = ReflectionHelpers.CreateOpenDelegate<Action<AnimatedCharacterModel, FirstPersonMovementModule>>(
            AccessTools.PropertySetter(typeof(AnimatedCharacterModel), "FpcModule"));
        _getAnimModelAnimator = ReflectionHelpers.CreateInstanceFieldGetter<AnimatedCharacterModel, Animator>("<Animator>k__BackingField");
        _getAnimModelOverride = ReflectionHelpers.CreateInstanceFieldGetter<AnimatedCharacterModel, AnimatorOverrideController>("<AnimatorOverride>k__BackingField");
        _getAnimModelSubcontrollers = ReflectionHelpers.CreateInstanceFieldGetter<AnimatedCharacterModel, IAnimatedModelSubcontroller[]>("_subcontrollers");
        _animModelOnGroundedMethod = AccessTools.Method(typeof(AnimatedCharacterModel), "OnGrounded", Type.EmptyTypes);

        if (baseSetupMethod != null)
        {
            try
            {
                DynamicMethod dmBaseSetup = new DynamicMethod(
                    "SLPerf_CharModel_BaseSetup",
                    typeof(void),
                    new[] { typeof(CharacterModel), typeof(ReferenceHub), typeof(IFpcRole), typeof(Vector3), typeof(Quaternion) },
                    typeof(CharacterModel).Module,
                    skipVisibility: true);
                ILGenerator ilBase = dmBaseSetup.GetILGenerator();
                ilBase.Emit(OpCodes.Ldarg_0);
                ilBase.Emit(OpCodes.Ldarg_1);
                ilBase.Emit(OpCodes.Ldarg_2);
                ilBase.Emit(OpCodes.Ldarg_3);
                ilBase.Emit(OpCodes.Ldarg_S, (byte)4);
                ilBase.Emit(OpCodes.Call, baseSetupMethod);
                ilBase.Emit(OpCodes.Ret);
                _callBaseCharModelSetup = (Action<CharacterModel, ReferenceHub, IFpcRole, Vector3, Quaternion>)dmBaseSetup.CreateDelegate(
                    typeof(Action<CharacterModel, ReferenceHub, IFpcRole, Vector3, Quaternion>));
            }
            catch (Exception ex)
            {
                SLPerformanceLog.Warn($"OPT-21 base CharacterModel.Setup compile note: {ex.Message}");
                _callBaseCharModelSetup = null;
            }
        }

        MethodInfo onSendGen = AccessTools.Method(typeof(NetworkDiagnostics), "OnSend");
        if (onSendGen != null && onSendGen.IsGenericMethodDefinition)
        {
            _diagOnSendRoleSync = ReflectionHelpers.CreateOpenDelegate<Action<RoleSyncInfo, int, int, int>>(
                onSendGen.MakeGenericMethod(typeof(RoleSyncInfo)));
        }

        ConstructorInfo waveCtor = AccessTools.Constructor(
            typeof(WaveUpdateMessage),
            new[] { typeof(SpawnableWaveBase), typeof(UpdateMessageFlags) });
        if (waveCtor != null)
        {
            try
            {
                DynamicMethod dmWave = new DynamicMethod(
                    "SLPerf_New_WaveUpdateMessage",
                    typeof(WaveUpdateMessage),
                    new[] { typeof(SpawnableWaveBase), typeof(UpdateMessageFlags) },
                    typeof(WaveUpdateMessage).Module,
                    skipVisibility: true);
                ILGenerator ilWave = dmWave.GetILGenerator();
                ilWave.Emit(OpCodes.Ldarg_0);
                ilWave.Emit(OpCodes.Ldarg_1);
                ilWave.Emit(OpCodes.Newobj, waveCtor);
                ilWave.Emit(OpCodes.Ret);
                _createWaveUpdateMessage = (Func<SpawnableWaveBase, UpdateMessageFlags, WaveUpdateMessage>)dmWave.CreateDelegate(
                    typeof(Func<SpawnableWaveBase, UpdateMessageFlags, WaveUpdateMessage>));
            }
            catch (Exception ex)
            {
                SLPerformanceLog.Warn($"OPT-21 WaveUpdateMessage factory compile note: {ex.Message}");
                _createWaveUpdateMessage = null;
            }
        }

        return true;
    }

    internal static bool TestVerifyCompiledDelegates(object samplePool, out Stack<PoolObject> stack)
    {
        if (_getPoolStack == null)
        {
            CompileAccessors(out _);
        }

        stack = samplePool != null && _getPoolStack != null ? _getPoolStack(samplePool) : null;
        return _getPoolLookup != null &&
               _getPoolObjectLookup != null &&
               _getPoolStack != null &&
               _getPoolParentTransform != null &&
               _getPoolPrefab != null &&
               _initPoolObject != null &&
               _addNewPrefabToPool != null &&
               _tryGetPoolable != null &&
               _getPoolObjectOwner != null &&
               _getRolesLoaded != null &&
               _getItemsLoaded != null &&
               _getInventoryHub != null &&
               _setItemOwner != null &&
               _setItemSerial != null &&
               _getRoleSyncEvent != null &&
               _createWaveUpdateMessage != null &&
               _getFpmmHub != null &&
               _getFpmmRole != null &&
               _setFpmmModelInstance != null &&
               _setFpmmCharControllerSet != null &&
               _setCharModelFadeableMaterials != null &&
               _setWearableSubcontroller != null &&
               _callBaseCharModelSetup != null &&
               _setAnimModelRole != null &&
               _setAnimModelFpcModule != null &&
               _getAnimModelAnimator != null &&
               _getAnimModelOverride != null &&
               _getAnimModelSubcontrollers != null &&
               _animModelOnGroundedMethod != null;
    }

    // =========================================================================
    // Part 1 & 2: Amortized Budgeted Adaptive Pre-Warmer (PoolManager + ItemBase)
    // =========================================================================

    private static void OnUpdateTick()
    {
        if (!_active)
        {
            return;
        }

        if (_inRoleChangeDepth > 0 && Time.frameCount > _inRoleChangeStartFrame)
        {
            _inRoleChangeDepth = 0;
            _inInitRoleDepth = 0;
            _inDisableRoleDepth = 0;
            _inSetModelDepth = 0;
            _inAnimSetupDepth = 0;
            _inSpawnPosDepth = 0;
            _inGrantLoadoutDepth = 0;
            _inCreateItemDepth = 0;
        }

        if (_activeBurst.Active && _inRoleChangeDepth == 0)
        {
            int frameDelta = Time.frameCount - _activeBurst.LastFrame;
            double quietMs = (Stopwatch.GetTimestamp() - _activeBurst.LastTimestamp) * TicksToMs;
            if (frameDelta >= 2 || quietMs >= 150.0)
            {
                CommitActiveBurst();
            }
        }

        if (!NetworkServer.active || RoundRestart.IsRoundRestarting || _inRoleChangeDepth > 0)
        {
            return;
        }

        if (_lastBurstEndTimestamp != 0 && (Stopwatch.GetTimestamp() - _lastBurstEndTimestamp) * TicksToMs < 500.0)
        {
            return;
        }

        if (_prewarmRolePoolsEnabled && !_poolPlanBuilt && PoolManager.Singleton != null && _getRolesLoaded())
        {
            BuildPoolPrewarmPlan();
        }

        if (_prewarmItemReserveEnabled && !_itemPlanBuilt && _getItemsLoaded() && SharedHandsController.Singleton != null)
        {
            BuildItemReservePlan();
        }

        long frameStart = Stopwatch.GetTimestamp();
        double budgetMs = _prewarmBudgetMs;

        bool didWork = false;
        if (_prewarmRolePoolsEnabled && _poolPlanBuilt)
        {
            didWork = StepPoolPrewarm(frameStart, budgetMs, priorityOnly: true);
        }

        if (!didWork && _poolPlanBuilt && HeadlessCharacterAnimatorModule.IsHeadlessDedicated)
        {
            didWork = StepWarmParkExistingModels(frameStart, budgetMs, priorityOnly: true);
        }

        if (!didWork && _prewarmItemReserveEnabled && _itemPlanBuilt)
        {
            didWork = StepItemReservePrewarm(frameStart, budgetMs, priorityOnly: true);
        }

        if (!didWork && _prewarmRolePoolsEnabled && _poolPlanBuilt)
        {
            didWork = StepPoolPrewarm(frameStart, budgetMs, priorityOnly: false);
        }

        if (!didWork && _poolPlanBuilt && HeadlessCharacterAnimatorModule.IsHeadlessDedicated)
        {
            didWork = StepWarmParkExistingModels(frameStart, budgetMs, priorityOnly: false);
        }

        if (!didWork && _prewarmItemReserveEnabled && _itemPlanBuilt)
        {
            StepItemReservePrewarm(frameStart, budgetMs, priorityOnly: false);
        }
    }

    private static int GetEffectiveSlotTarget()
    {
        int slots = CustomNetworkManager.slots;
        if (slots <= 0)
        {
            slots = 48;
        }

        return Mathf.Clamp(slots, 16, 64);
    }

    private static void BuildPoolPrewarmPlan()
    {
        PoolTargets.Clear();
        PoolTargetByPool.Clear();
        PoolTotalCounts.Clear();
        _poolCursor = 0;

        PoolManager pm = PoolManager.Singleton;
        IDictionary lookup = pm != null ? _getPoolLookup(pm) : null;
        if (lookup == null)
        {
            return;
        }

        Dictionary<GameObject, PoolObject> objectLookup = _getPoolObjectLookup(pm);
        if (objectLookup != null)
        {
            foreach (KeyValuePair<GameObject, PoolObject> kv in objectLookup)
            {
                PoolObject po = kv.Value;
                if (po == null) continue;
                object ownerPool = _getPoolObjectOwner(po);
                if (ownerPool == null) continue;
                PoolTotalCounts.TryGetValue(ownerPool, out int c);
                PoolTotalCounts[ownerPool] = c + 1;
            }
        }

        int slots = GetEffectiveSlotTarget();
        _poolPlanSlots = slots;

        (RoleTypeId Role, int Target, bool Priority)[] roleSpecs =
        {
            // Round Start & Lobby heavy roles
            (RoleTypeId.ClassD,        Math.Min(slots, 36), true),
            (RoleTypeId.Spectator,     slots,               true),
            (RoleTypeId.FacilityGuard, Math.Min(slots, 18), true),
            (RoleTypeId.Scientist,     Math.Min(slots, 16), true),

            // MTF & Chaos Wave roles
            (RoleTypeId.NtfPrivate,      Math.Min(slots, 32), false),
            (RoleTypeId.NtfSergeant,     Math.Min(slots, 16), false),
            (RoleTypeId.NtfSpecialist,   Math.Min(slots, 8),  false),
            (RoleTypeId.NtfCaptain,      Math.Min(slots, 6),  false),
            (RoleTypeId.ChaosRifleman,   Math.Min(slots, 32), false),
            (RoleTypeId.ChaosConscript,  Math.Min(slots, 16), false),
            (RoleTypeId.ChaosMarauder,   Math.Min(slots, 12), false),
            (RoleTypeId.ChaosRepressor,  Math.Min(slots, 12), false),

            // Zombies, Tutorial & SCPs
            (RoleTypeId.Scp0492,  Math.Min(slots, 18), false),
            (RoleTypeId.Tutorial, Math.Min(slots, 8),  false),
            (RoleTypeId.Scp173,   2, false),
            (RoleTypeId.Scp049,   2, false),
            (RoleTypeId.Scp096,   2, false),
            (RoleTypeId.Scp106,   2, false),
            (RoleTypeId.Scp939,   2, false),
            (RoleTypeId.Scp079,   2, false),
            (RoleTypeId.Scp3114,  2, false)
        };

        Dictionary<GameObject, PoolPrewarmTarget> modelTargets = new Dictionary<GameObject, PoolPrewarmTarget>(16);
        Dictionary<RoleTypeId, PlayerRoleBase> allRoles = PlayerRoleLoader.AllRoles;

        for (int i = 0; i < roleSpecs.Length; i++)
        {
            var spec = roleSpecs[i];
            if (!allRoles.TryGetValue(spec.Role, out PlayerRoleBase rolePrefab) || rolePrefab == null)
            {
                continue;
            }

            GameObject roleGo = rolePrefab.gameObject;
            if (lookup.Contains(roleGo))
            {
                object poolObj = lookup[roleGo];
                Stack<PoolObject> stack = poolObj != null ? _getPoolStack(poolObj) : null;
                if (poolObj != null && stack != null && !PoolTargetByPool.ContainsKey(poolObj))
                {
                    PoolPrewarmTarget rt = new PoolPrewarmTarget
                    {
                        Prefab = roleGo,
                        Label = $"Role:{spec.Role}",
                        TargetCount = spec.Target,
                        PoolInstance = poolObj,
                        PoolStack = stack,
                        IsRoundStartPriority = spec.Priority
                    };
                    PoolTargets.Add(rt);
                    PoolTargetByPool.Add(poolObj, rt);
                }
            }

            if (rolePrefab is IFpcRole fpcRole && fpcRole.FpcModule != null)
            {
                GameObject modelGo = fpcRole.FpcModule.CharacterModelTemplate;
                if (modelGo != null && lookup.Contains(modelGo))
                {
                    if (!modelTargets.TryGetValue(modelGo, out PoolPrewarmTarget mt))
                    {
                        object mPool = lookup[modelGo];
                        Stack<PoolObject> mStack = mPool != null ? _getPoolStack(mPool) : null;
                        if (mPool != null && mStack != null && !PoolTargetByPool.ContainsKey(mPool))
                        {
                            mt = new PoolPrewarmTarget
                            {
                                Prefab = modelGo,
                                Label = $"Model:{modelGo.name}",
                                TargetCount = spec.Target,
                                PoolInstance = mPool,
                                PoolStack = mStack,
                                IsRoundStartPriority = spec.Priority,
                                IsParkableHumanModel = IsParkableHumanModel(modelGo.GetComponent<PoolObject>())
                            };
                            modelTargets.Add(modelGo, mt);
                            PoolTargets.Add(mt);
                            PoolTargetByPool.Add(mPool, mt);
                        }
                    }
                    else
                    {
                        mt.TargetCount = Math.Min(slots, mt.TargetCount + spec.Target);
                        mt.IsRoundStartPriority |= spec.Priority;
                    }
                }
            }
        }

        _poolPlanBuilt = true;
        SLPerformanceLog.Debug($"OPT-21 Pool pre-warm plan ready: {PoolTargets.Count} role/model pools scaled to {slots} slots.");
    }

    private static bool StepPoolPrewarm(long frameStart, double budgetMs, bool priorityOnly)
    {
        int count = PoolTargets.Count;
        if (count == 0)
        {
            return false;
        }

        int checkedCount = 0;
        int createdThisFrame = 0;
        bool foundPending = false;

        while (checkedCount < count && createdThisFrame < 3)
        {
            if (_poolCursor >= count)
            {
                _poolCursor = 0;
            }

            PoolPrewarmTarget target = PoolTargets[_poolCursor];
            _poolCursor++;
            checkedCount++;

            if (priorityOnly && !target.IsRoundStartPriority)
            {
                continue;
            }

            if (target.PoolInstance == null || target.PoolStack == null)
            {
                continue;
            }

            PoolTotalCounts.TryGetValue(target.PoolInstance, out int totalOwned);
            int currentCount = Math.Max(totalOwned, target.PoolStack.Count);
            if (currentCount >= target.TargetCount)
            {
                continue;
            }

            foundPending = true;
            _isPrewarming = true;
            try
            {
                _addNewPrefabToPool(target.PoolInstance);
                _totalPoolPrewarmed++;
                createdThisFrame++;
            }
            catch (Exception ex)
            {
                SLPerformanceLog.Warn($"OPT-21 pool pre-warm failed for {target.Label}: {ex.Message}");
                target.TargetCount = currentCount;
            }
            finally
            {
                _isPrewarming = false;
            }

            if ((Stopwatch.GetTimestamp() - frameStart) * TicksToMs >= budgetMs)
            {
                break;
            }
        }

        return foundPending;
    }

    private static bool StepWarmParkExistingModels(long frameStart, double budgetMs, bool priorityOnly)
    {
        if (_getPoolParentTransform == null)
        {
            return false;
        }

        bool foundPending = false;
        int parkedThisFrame = 0;

        for (int i = 0; i < PoolTargets.Count; i++)
        {
            PoolPrewarmTarget target = PoolTargets[i];
            if (!target.IsParkableHumanModel || target.AllWarmParked || target.PoolStack == null || target.PoolInstance == null)
            {
                continue;
            }

            if (priorityOnly && !target.IsRoundStartPriority)
            {
                continue;
            }

            Transform poolParent = _getPoolParentTransform(target.PoolInstance);
            if (poolParent == null)
            {
                target.AllWarmParked = true;
                continue;
            }

            bool anyInactiveRemaining = false;
            foreach (PoolObject po in target.PoolStack)
            {
                if (po is not AnimatedCharacterModel animModel || !IsParkableHumanModel(po))
                {
                    continue;
                }

                if (animModel.gameObject.activeSelf)
                {
                    continue;
                }

                foundPending = true;
                anyInactiveRemaining = true;

                // Wake this pre-existing inactive model ONCE during quiet lobby frames and park it active at Y = -5000
                // so Round 1 & Wave 1 spawns have ZERO SetActive(true) cost!
                EnsurePoolParentParked(poolParent);
                PrepareHeadlessModelWhileInactive(animModel);
                SetGameplaySubcontrollersEnabled(animModel, false);
                animModel.enabled = false;
                animModel.OwnerHub = null;
                animModel.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                animModel.gameObject.SetActive(true);
                ParkedActiveModels.Add(animModel);
                parkedThisFrame++;

                if (parkedThisFrame >= 2 || (Stopwatch.GetTimestamp() - frameStart) * TicksToMs >= budgetMs)
                {
                    return true;
                }
            }

            if (!anyInactiveRemaining)
            {
                target.AllWarmParked = true;
            }
        }

        return foundPending;
    }

    private static void BuildItemReservePlan()
    {
        ItemReserveList.Clear();
        ItemReserveMap.Clear();
        _itemCursor = 0;

        EnsureReserveRoot();

        int slots = GetEffectiveSlotTarget();
        float scale = Mathf.Clamp(slots / 48f, 0.35f, 1.5f);
        int Scaled(int base48, int minCount) => Math.Max(minCount, Mathf.RoundToInt(base48 * scale));

        (ItemType Type, int Target, bool Priority)[] itemSpecs =
        {
            // Round Start priority items (FacilityGuard + Scientist)
            (ItemType.Medkit,           Scaled(36, 8), true),
            (ItemType.KeycardGuard,     Scaled(16, 4), true),
            (ItemType.GunFSP9,          Scaled(16, 4), true),
            (ItemType.GrenadeFlash,     Scaled(16, 4), true),
            (ItemType.Radio,            Scaled(32, 6), true),
            (ItemType.ArmorLight,       Scaled(16, 4), true),
            (ItemType.KeycardScientist, Scaled(14, 4), true),

            // Wave Respawn items (NTF + Chaos Insurgency)
            (ItemType.ArmorCombat,            Scaled(32, 6), false),
            (ItemType.KeycardMTFOperative,    Scaled(28, 6), false),
            (ItemType.KeycardChaosInsurgency, Scaled(28, 6), false),
            (ItemType.GunCrossvec,            Scaled(24, 4), false),
            (ItemType.GunE11SR,               Scaled(20, 4), false),
            (ItemType.GunAK,                  Scaled(24, 4), false),
            (ItemType.Painkillers,            Scaled(24, 4), false),
            (ItemType.GrenadeHE,              Scaled(22, 4), false),
            (ItemType.GunShotgun,             Scaled(12, 2), false),
            (ItemType.GunRevolver,            Scaled(12, 2), false),
            (ItemType.GunLogicer,             Scaled(12, 2), false),
            (ItemType.Adrenaline,             Scaled(12, 2), false),
            (ItemType.ArmorHeavy,             Scaled(8,  2), false),
            (ItemType.KeycardMTFCaptain,      Scaled(6,  1), false),
            (ItemType.GunFRMG0,               Scaled(6,  1), false)
        };

        Dictionary<ItemType, ItemBase> available = InventoryItemLoader.AvailableItems;
        for (int i = 0; i < itemSpecs.Length; i++)
        {
            var spec = itemSpecs[i];
            if (!available.TryGetValue(spec.Type, out ItemBase template) || template == null)
            {
                continue;
            }

            if (ItemReserveMap.ContainsKey(spec.Type))
            {
                continue;
            }

            ItemReserveEntry entry = new ItemReserveEntry
            {
                ItemType = spec.Type,
                Template = template,
                TargetCount = spec.Target,
                IsRoundStartPriority = spec.Priority
            };
            ItemReserveList.Add(entry);
            ItemReserveMap.Add(spec.Type, entry);
        }

        _itemPlanBuilt = true;
        SLPerformanceLog.Debug($"OPT-21 Virgin ItemBase reserve plan ready: {ItemReserveList.Count} starting item types scaled to {slots} slots.");
    }

    private static void EnsureReserveRoot()
    {
        if (_reserveRoot != null)
        {
            return;
        }

        _reserveRoot = new GameObject("_SLPerf_VirginItemReserve");
        _reserveRoot.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_reserveRoot);
    }

    private static void DestroyItemReserveRoot()
    {
        if (_reserveRoot != null)
        {
            try
            {
                UnityEngine.Object.Destroy(_reserveRoot);
            }
            catch (Exception ex)
            {
                SLPerformanceLog.Debug($"OPT-21 reserve cleanup note: {ex.Message}");
            }

            _reserveRoot = null;
        }
    }

    private static bool StepItemReservePrewarm(long frameStart, double budgetMs, bool priorityOnly)
    {
        int count = ItemReserveList.Count;
        if (count == 0)
        {
            return false;
        }

        EnsureReserveRoot();
        Transform reserveTransform = _reserveRoot.transform;
        Dictionary<ItemType, ItemBase> available = InventoryItemLoader.AvailableItems;

        int checkedCount = 0;
        int createdThisFrame = 0;
        bool foundPending = false;

        // Hard time-budgeted refill (<= budgetMs/frame), allowing up to 12 lightweight items or 2-3 heavy firearms per frame
        while (checkedCount < count && createdThisFrame < 12)
        {
            if (_itemCursor >= count)
            {
                _itemCursor = 0;
            }

            ItemReserveEntry entry = ItemReserveList[_itemCursor];
            _itemCursor++;
            checkedCount++;

            if (priorityOnly && !entry.IsRoundStartPriority)
            {
                continue;
            }

            if (entry.Stack.Count >= entry.TargetCount)
            {
                continue;
            }

            if (!available.TryGetValue(entry.ItemType, out ItemBase currentTemplate) || currentTemplate == null)
            {
                continue;
            }

            if (!ReferenceEquals(entry.Template, currentTemplate))
            {
                DiscardEntryStack(entry);
                entry.Template = currentTemplate;
            }

            foundPending = true;
            try
            {
                ItemBase virginClone = UnityEngine.Object.Instantiate(currentTemplate, reserveTransform);
                if (virginClone != null)
                {
                    // Pre-disable Firearm ServerSideAnimator while still inactive: Firearm.InitializeSubcomponents()
                    // immediately sets ServerSideAnimator.enabled = false anyway, so disabling it here prevents Unity
                    // from running an expensive Mecanim Animator bind inside CreateItemInstance.SetParent()!
                    if (virginClone is Firearm)
                    {
                        Animator gunAnim = virginClone.GetComponent<Animator>();
                        if (gunAnim != null)
                        {
                            gunAnim.enabled = false;
                        }
                    }

                    entry.Stack.Push(virginClone);
                    _totalItemsPrewarmed++;
                    createdThisFrame++;
                }
            }
            catch (Exception ex)
            {
                SLPerformanceLog.Warn($"OPT-21 virgin item pre-warm failed for {entry.ItemType}: {ex.Message}");
                entry.TargetCount = entry.Stack.Count;
            }

            if ((Stopwatch.GetTimestamp() - frameStart) * TicksToMs >= budgetMs)
            {
                break;
            }
        }

        return foundPending;
    }

    private static void DiscardEntryStack(ItemReserveEntry entry)
    {
        while (entry.Stack.Count > 0)
        {
            ItemBase stale = entry.Stack.Pop();
            if (stale != null && stale.gameObject != null)
            {
                UnityEngine.Object.Destroy(stale.gameObject);
            }
        }
    }

    private static bool CreateItemInstancePrefix(
        Inventory __instance,
        ItemIdentifier identifier,
        bool updateViewmodel,
        ref ItemBase __result)
    {
        if (!_active || updateViewmodel || !NetworkServer.active)
        {
            return true;
        }

        if (_activeBurst.Active)
        {
            BurstItemDemand.TryGetValue(identifier.TypeId, out int d);
            BurstItemDemand[identifier.TypeId] = d + 1;
        }

        SharedHandsController shc = SharedHandsController.Singleton;
        if (shc == null)
        {
            RecordItemReserveMiss();
            return true;
        }

        if (!InventoryItemLoader.AvailableItems.TryGetValue(identifier.TypeId, out ItemBase currentTemplate) ||
            currentTemplate == null)
        {
            __result = null;
            return false;
        }

        if (ItemReserveMap.TryGetValue(identifier.TypeId, out ItemReserveEntry entry))
        {
            if (!ReferenceEquals(entry.Template, currentTemplate))
            {
                DiscardEntryStack(entry);
                entry.Template = currentTemplate;
            }
            else
            {
                while (entry.Stack.Count > 0)
                {
                    ItemBase virgin = entry.Stack.Pop();
                    if (virgin == null || virgin.gameObject == null)
                    {
                        continue;
                    }

                    virgin.transform.SetParent(shc.transform, worldPositionStays: false);
                    virgin.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                    _setItemOwner(virgin, _getInventoryHub(__instance));
                    _setItemSerial(virgin, identifier.SerialNumber);
                    __result = virgin;

                    _totalItemReserveHits++;
                    if (_activeBurst.Active)
                    {
                        _activeBurst.ItemReserveHits++;
                    }

                    return false;
                }
            }
        }

        RecordItemReserveMiss();

        // Even on a cold reserve miss for a Firearm, instantiate under inactive _reserveRoot and disable
        // ServerSideAnimator BEFORE parenting to active ItemWorkspace, skipping the wasted Mecanim Animator bind!
        if (HeadlessCharacterAnimatorModule.IsHeadlessDedicated && currentTemplate is Firearm)
        {
            EnsureReserveRoot();
            ItemBase coldGun = UnityEngine.Object.Instantiate(currentTemplate, _reserveRoot.transform);
            if (coldGun != null)
            {
                Animator gunAnim = coldGun.GetComponent<Animator>();
                if (gunAnim != null)
                {
                    gunAnim.enabled = false;
                }

                coldGun.transform.SetParent(shc.transform, worldPositionStays: false);
                coldGun.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                _setItemOwner(coldGun, _getInventoryHub(__instance));
                _setItemSerial(coldGun, identifier.SerialNumber);
                __result = coldGun;
                return false;
            }
        }

        return true;
    }

    private static void RecordItemReserveMiss()
    {
        _totalItemReserveMisses++;
        if (_activeBurst.Active)
        {
            _activeBurst.ItemReserveMisses++;
        }
    }

    // =========================================================================
    // Part 3: Headless Model Strip, Warm-Active Human Model Parking, Same-Template Fast-Reassign,
    //         Inactive Reparent SetModel, Parallel AnimSetup, Zero-Redundant ResetOverrides
    // =========================================================================

    private static bool IsParkableHumanModel(PoolObject po)
    {
        return po != null && po.GetType() == typeof(AnimatedCharacterModel);
    }

    private static void EnsurePoolParentParked(Transform poolParent)
    {
        if (poolParent != null && ParkedPoolParents.Add(poolParent))
        {
            poolParent.position = ParkedPoolWorldPosition;
        }
    }

    private static void SetGameplaySubcontrollersEnabled(AnimatedCharacterModel model, bool enabled)
    {
        if (model == null || _getAnimModelSubcontrollers == null)
        {
            return;
        }

        IAnimatedModelSubcontroller[] subs = _getAnimModelSubcontrollers(model);
        if (subs == null)
        {
            return;
        }

        for (int i = 0; i < subs.Length; i++)
        {
            IAnimatedModelSubcontroller sub = subs[i];
            if (sub is InventorySubcontroller inv)
            {
                if (inv.enabled != enabled) inv.enabled = enabled;
            }
            else if (sub is IdlePoseRetainerSubcontroller idle)
            {
                if (idle.enabled != enabled) idle.enabled = enabled;
            }
        }
    }

    private static void WakeParkedModel(CharacterModel model)
    {
        if (model is not AnimatedCharacterModel animModel)
        {
            return;
        }

        ParkedActiveModels.Remove(animModel);
        if (!animModel.enabled)
        {
            animModel.enabled = true;
        }

        SetGameplaySubcontrollersEnabled(animModel, true);
    }

    private static void ParkHumanModelActive(AnimatedCharacterModel model, Transform poolParent, bool runResetPoolObject)
    {
        if (model == null || poolParent == null)
        {
            return;
        }

        EnsurePoolParentParked(poolParent);

        if (runResetPoolObject)
        {
            model.ResetPoolObject();
        }

        PrepareHeadlessModelWhileInactive(model);
        SetGameplaySubcontrollersEnabled(model, false);
        if (model.enabled)
        {
            model.enabled = false;
        }

        model.OwnerHub = null;
        Transform tr = model.transform;
        if (tr.parent != poolParent)
        {
            tr.SetParent(poolParent, worldPositionStays: false);
        }

        tr.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        ParkedActiveModels.Add(model);
    }

    private static bool PoolSpawnObjectParkPrefix(object __instance, ref PoolObject __result)
    {
        if (!_active || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || __instance == null)
        {
            return true;
        }

        PoolObject prefab = _getPoolPrefab(__instance);
        if (!IsParkableHumanModel(prefab))
        {
            return true;
        }

        Transform poolParent = _getPoolParentTransform(__instance);
        PoolManager pm = PoolManager.Singleton;
        if (poolParent == null || pm == null)
        {
            return true;
        }

        EnsurePoolParentParked(poolParent);

        // Instantiate active directly under parked poolParent (Y = -5000): Awake() runs once,
        // and we NEVER call SetActive(false), eliminating SetActive(true) cost on all future spawns!
        GameObject go = UnityEngine.Object.Instantiate(prefab.gameObject, poolParent);
        if (go == null || !go.TryGetComponent(out AnimatedCharacterModel animModel))
        {
            return true;
        }

        Dictionary<GameObject, PoolObject> objLookup = _getPoolObjectLookup(pm);
        objLookup[go] = animModel;
        _initPoolObject(animModel, __instance);

        ParkHumanModelActive(animModel, poolParent, runResetPoolObject: false);
        __result = animModel;
        return false;
    }

    private static bool PoolResetObjectParkPrefix(object __instance, PoolObject poolableObject)
    {
        if (!_active || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || !IsParkableHumanModel(poolableObject))
        {
            return true;
        }

        Transform poolParent = _getPoolParentTransform(__instance);
        if (poolParent == null)
        {
            return true;
        }

        AnimatedCharacterModel animModel = (AnimatedCharacterModel)poolableObject;
        if (!animModel.gameObject.activeSelf)
        {
            return true;
        }

        // Park ACTIVE at Y = -5000 with Animator + Update scripts slept instead of SetActive(false),
        // eliminating ~17ms SetActive(false) + ~25-34ms SetActive(true) churn across 48 players!
        ParkHumanModelActive(animModel, poolParent, runResetPoolObject: true);
        return false;
    }

    private static void PrepareHeadlessModelWhileInactive(CharacterModel model)
    {
        if (model == null || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated)
        {
            return;
        }

        // Ensure Animator is disabled while pooled/inactive (covers BOTH newly prewarmed models & returned models)
        // so SetActive(true) NEVER fires a premature Animator::OnEnable() before Setup().
        if (model is AnimatedCharacterModel animModel && _getAnimModelAnimator != null)
        {
            Animator anim = _getAnimModelAnimator(animModel);
            if (anim != null && anim.enabled)
            {
                anim.enabled = false;
            }
        }

        if (!StrippedHeadlessModels.Add(model))
        {
            return;
        }

        _headlessModelsStripped++;

        // One-time headless visual strip: disable all Renderers (SkinnedMeshRenderer & MeshRenderer) so Unity 6
        // skips RenderScene BVH and SkinnedMeshRenderer bone-bounds add/remove on every SetActive(true/false).
        // HitboxIdentity Colliders and skeleton bone Transforms are 100% untouched.
        RendererScratch.Clear();
        model.GetComponentsInChildren(includeInactive: true, RendererScratch);
        for (int i = 0; i < RendererScratch.Count; i++)
        {
            Renderer r = RendererScratch[i];
            if (r != null && r.enabled)
            {
                r.enabled = false;
                DisabledRenderers.Add(r);
            }
        }
        RendererScratch.Clear();

        // Disable headless-unused visual subcontrollers while inactive so SetActive(true) skips Unity BehaviourManager registration.
        if (model is AnimatedCharacterModel anim2 && _getAnimModelSubcontrollers != null)
        {
            IAnimatedModelSubcontroller[] subs = _getAnimModelSubcontrollers(anim2);
            if (subs != null)
            {
                for (int i = 0; i < subs.Length; i++)
                {
                    IAnimatedModelSubcontroller sub = subs[i];
                    if (sub is MonoBehaviour mb && mb != null && mb.enabled &&
                        (sub is CullingSubcontroller ||
                         sub is HandPoseSubcontroller ||
                         sub is FeetStabilizerSubcontroller ||
                         sub is SecondaryRigsSubcontroller ||
                         sub is EmotionSubcontroller ||
                         sub is GlowInTheDarkSubcontroller))
                    {
                        mb.enabled = false;
                        DisabledVisualBehaviours.Add(mb);
                    }
                }
            }
        }
    }

    private static void FlushRetainedModel()
    {
        CharacterModel leftover = _retainedModel;
        _retainedModel = null;
        _retainedModelHub = null;
        _retainedModelTemplate = null;
        if (leftover != null)
        {
            leftover.Pooled = false;
            leftover.ReturnToPool();
        }
    }

    private static void AnimModelResetPostfix(AnimatedCharacterModel __instance)
    {
        if (!_active || _isRetainingModelReset || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || __instance == null)
        {
            return;
        }

        // Pool.ResetObject() already set gameObject.SetActive(false) right before calling ResetObject().
        // Disabling Animator while inactive prevents Unity from firing a wasted Animator::OnEnable() during SetActive(true)
        // on the next spawn; AnimatedModelSetupPrefix / OPT-18 enables it cleanly ONCE in its final parent & scene.
        Animator anim = _getAnimModelAnimator(__instance);
        if (anim != null && anim.enabled)
        {
            anim.enabled = false;
        }
    }

    private static bool OnRoleDisabledRetainModelPrefix(FirstPersonMovementModule __instance, RoleTypeId rid)
    {
        if (!_active || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || _inInitRoleDepth <= 0 || __instance == null)
        {
            return true;
        }

        CharacterModel oldModel = __instance.CharacterModelInstance;
        GameObject oldTemplate = __instance.CharacterModelTemplate;
        if (oldModel == null || oldTemplate == null || oldModel.Pooled || oldModel.DetachedFromPool)
        {
            return true;
        }

        ReferenceHub hub = _getFpmmHub(__instance);
        if (hub == null || oldModel.transform.parent != hub.transform || !oldModel.gameObject.activeInHierarchy)
        {
            return true;
        }

        if (!PlayerRoleLoader.AllRoles.TryGetValue(rid, out PlayerRoleBase newRolePrefab) ||
            newRolePrefab is not IFpcRole newFpc ||
            newFpc.FpcModule == null ||
            !ReferenceEquals(newFpc.FpcModule.CharacterModelTemplate, oldTemplate))
        {
            return true;
        }

        // Same CharacterModelTemplate! Keep active & parented on Hub.transform, completely bypassing
        // SetActive(false) + cross-scene SetParent(DontDestroyOnLoad) + SetParent(Hub) + SetActive(true)!
        FlushRetainedModel();

        _setFpmmCharControllerSet(__instance, false);
        oldModel.Pooled = true;
        _isRetainingModelReset = true;
        try
        {
            oldModel.ResetPoolObject();
        }
        finally
        {
            _isRetainingModelReset = false;
        }

        __instance.Noclip.ShutdownModule();

        _retainedModelHub = hub;
        _retainedModelTemplate = oldTemplate;
        _retainedModel = oldModel;
        return false;
    }

    private static bool SetModelFastPrefix(FirstPersonMovementModule __instance, GameObject template)
    {
        if (!_active || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || __instance == null || template == null)
        {
            return true;
        }

        ReferenceHub hub = _getFpmmHub(__instance);
        PlayerRoleBase role = _getFpmmRole(__instance);
        if (hub == null || role == null)
        {
            return true;
        }

        // Fast-reassign hit: reuse the already-active, already-parented CharacterModel from the previous same-template role!
        if (_retainedModel != null &&
            ReferenceEquals(_retainedModelHub, hub) &&
            ReferenceEquals(_retainedModelTemplate, template))
        {
            CharacterModel reused = _retainedModel;
            _retainedModel = null;
            _retainedModelHub = null;
            _retainedModelTemplate = null;

            if (reused != null && reused.gameObject != null && reused.gameObject.activeInHierarchy)
            {
                _setModelFastPathCalls++;
                _setModelReuseHits++;
                if (_activeBurst.Active)
                {
                    _activeBurst.SetModelReuseCount++;
                }

                reused.SetupPoolObject();
                _setFpmmModelInstance(__instance, reused);
                Transform tplReuseTr = template.transform;
                reused.Setup(hub, role as IFpcRole, tplReuseTr.localPosition, tplReuseTr.localRotation);
                reused.OnPlayerMove();
                return false;
            }
        }

        PoolManager pm = PoolManager.Singleton;
        IDictionary lookup = pm != null ? _getPoolLookup(pm) : null;
        if (lookup == null || !lookup.Contains(template))
        {
            return true;
        }

        object poolObj = lookup[template];
        if (poolObj == null)
        {
            return true;
        }

        if (!_tryGetPoolable(poolObj, out PoolObject po) || po is not CharacterModel characterModel)
        {
            UnityEngine.Debug.LogError("Can't spawn '" + template.name + "' - FPC models must derive from CharacterModel.");
            return false;
        }

        _setModelFastPathCalls++;

        if (characterModel.gameObject.activeSelf)
        {
            // Warm-active parked hit! Zero SetActive(true) call — just reparent to Hub, wake 3 Update scripts, and Setup!
            long tParentParked = Stopwatch.GetTimestamp();
            characterModel.transform.SetParent(hub.transform, worldPositionStays: false);
            long tAfterParentParked = Stopwatch.GetTimestamp();

            if (_activeBurst.Active)
            {
                _activeBurst.SetModelParentTicks += tAfterParentParked - tParentParked;
                _activeBurst.SetModelParkedCount++;
            }

            _setModelParkedHits++;
            WakeParkedModel(characterModel);
            characterModel.SetupPoolObject();
            _setFpmmModelInstance(__instance, characterModel);
            Transform tplParkedTr = template.transform;
            characterModel.Setup(hub, role as IFpcRole, tplParkedTr.localPosition, tplParkedTr.localRotation);
            characterModel.OnPlayerMove();
            return false;
        }

        // Fallback for non-human SCP models or not-yet-warm-parked models: inactive reparent + single SetActive(true)
        PrepareHeadlessModelWhileInactive(characterModel);

        long tParent = Stopwatch.GetTimestamp();
        characterModel.transform.SetParent(hub.transform, worldPositionStays: false);
        long tActive = Stopwatch.GetTimestamp();

        characterModel.gameObject.SetActive(true);
        long tAfterActive = Stopwatch.GetTimestamp();

        if (_activeBurst.Active)
        {
            _activeBurst.SetModelParentTicks += tActive - tParent;
            _activeBurst.SetModelActiveTicks += tAfterActive - tActive;
        }

        characterModel.SetupPoolObject();

        _setFpmmModelInstance(__instance, characterModel);
        Transform tplTr = template.transform;
        characterModel.Setup(hub, role as IFpcRole, tplTr.localPosition, tplTr.localRotation);
        return false;
    }

    private static bool ResetOverridesFastPrefix(AnimatedCharacterModel target, Dictionary<AnimationClip, AnimationClip> defaultOverrides)
    {
        if (!_active || target == null || _getAnimModelOverride == null)
        {
            return true;
        }

        AnimatorOverrideController animOverride = _getAnimModelOverride(target);
        if (animOverride == null)
        {
            return true;
        }

        OverrideCheckBuffer.Clear();
        animOverride.GetOverrides(OverrideCheckBuffer);
        bool changed = false;
        int count = OverrideCheckBuffer.Count;
        for (int i = 0; i < count; i++)
        {
            AnimationClip key = OverrideCheckBuffer[i].Key;
            AnimationClip desired = null;
            if (defaultOverrides != null)
            {
                defaultOverrides.TryGetValue(key, out desired);
            }

            if (!ReferenceEquals(OverrideCheckBuffer[i].Value, desired))
            {
                changed = true;
                OverrideCheckBuffer[i] = new KeyValuePair<AnimationClip, AnimationClip>(key, desired);
            }
        }

        if (changed)
        {
            animOverride.ApplyOverrides(OverrideCheckBuffer);
        }
        else
        {
            _animOverrideSkips++;
        }

        OverrideCheckBuffer.Clear();
        return false;
    }

    private static bool CharModelPrepareFadeablePrefix(CharacterModel __instance)
    {
        if (!_active || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || __instance == null)
        {
            return true;
        }

        _setCharModelFadeableMaterials(__instance, Array.Empty<Material>());
        return false;
    }

    private static bool WearableArmorInitializePrefix(WearableArmor __instance, WearableSubcontroller subcontroller)
    {
        if (!_active || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || __instance == null)
        {
            return true;
        }

        // Preserve Subcontroller reference so WriteSyncvars() -> ServerCurArmor works 100% identically,
        // while skipping InitCuller(), InitElements() (30+ armor part unpacks), InitFadeables() (material clones), and SetArmor().
        _setWearableSubcontroller(__instance, subcontroller);
        if (__instance.enabled)
        {
            __instance.enabled = false;
            DisabledVisualBehaviours.Add(__instance);
        }

        return false;
    }

    private static bool AnimatedModelSetupPrefix(
        AnimatedCharacterModel __instance,
        ReferenceHub owner,
        IFpcRole role,
        Vector3 localPos,
        Quaternion localRot)
    {
        if (!_active || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || !HeadlessCharacterAnimatorModule.IsParallelNativeActive)
        {
            return true;
        }

        if (__instance == null || owner == null || role == null)
        {
            return true;
        }

        _animSetupFastPathCalls++;
        WakeParkedModel(__instance);

        // 1. Run exact CharacterModel.Setup (registers Hitboxes & SetColliders ONCE instead of twice)
        _callBaseCharModelSetup(__instance, owner, role, localPos, localRot);

        // 2. Bind Role, FpcModule, and OnGrounded identically to vanilla AnimatedCharacterModel.Setup
        _setAnimModelRole(__instance, role as PlayerRoleBase);
        FirstPersonMovementModule fpcModule = role.FpcModule;
        _setAnimModelFpcModule(__instance, fpcModule);
        Action onGroundedDel = (Action)Delegate.CreateDelegate(typeof(Action), __instance, _animModelOnGroundedMethod);
        fpcModule.OnGrounded = (Action)Delegate.Combine(fpcModule.OnGrounded, onGroundedDel);

        // 3. Reassign subcontrollers (skipping EmotionSubcontroller facial blendshapes and CullingSubcontroller
        //    Animator disable/re-enable ping-pong — we enable Animator ONCE right below).
        IAnimatedModelSubcontroller[] subcontrollers = _getAnimModelSubcontrollers(__instance);
        if (subcontrollers != null)
        {
            for (int i = 0; i < subcontrollers.Length; i++)
            {
                IAnimatedModelSubcontroller sub = subcontrollers[i];
                if (sub == null || sub is EmotionSubcontroller)
                {
                    continue;
                }

                if (sub is CullingSubcontroller culler)
                {
                    if (culler.enabled)
                    {
                        culler.enabled = false;
                    }
                    continue;
                }

                sub.OnReassigned();
            }
        }

        // 4. Enable Animator ONCE in its final parent & scene, initialize grounded idle params, and let Unity 6's
        //    multi-threaded PreLateUpdate.DirectorUpdateAnimation evaluate all 48 skeletons in parallel THIS SAME FRAME!
        Animator anim = _getAnimModelAnimator(__instance);
        if (anim != null && anim.runtimeAnimatorController != null)
        {
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (!anim.enabled)
            {
                anim.enabled = true;
            }

            if (anim.isActiveAndEnabled)
            {
                if (!_animHashesInitialized)
                {
                    _hashForward = Animator.StringToHash("Forward");
                    _hashGrounded = Animator.StringToHash("Grounded");
                    _hashStrafe = Animator.StringToHash("Strafe");
                    _hashSpeed = Animator.StringToHash("Speed");
                    _animHashesInitialized = true;
                }

                if (__instance.HasParameter(_hashGrounded)) anim.SetBool(_hashGrounded, true);
                if (__instance.HasParameter(_hashForward)) anim.SetFloat(_hashForward, 0f);
                if (__instance.HasParameter(_hashStrafe)) anim.SetFloat(_hashStrafe, 0f);
                if (__instance.HasParameter(_hashSpeed)) anim.SetFloat(_hashSpeed, 0f);
            }
        }

        return false;
    }

    private static bool SkipOnHeadlessPrefix()
    {
        return !(_active && HeadlessCharacterAnimatorModule.IsHeadlessDedicated);
    }

    private static bool SkipGlobalChatUiOnHeadlessPrefix()
    {
        if (_active && HeadlessCharacterAnimatorModule.IsHeadlessDedicated)
        {
            _globalChatUiSkips++;
            return false;
        }

        return true;
    }

    // =========================================================================
    // Part 4: Pack-Once SendNewRoleInfo, WaveManager.SyncValues, SyncedStatMessages
    // =========================================================================

    private static bool SendNewRoleInfoPrefix(PlayerRoleManager __instance)
    {
        if (!_active)
        {
            return true;
        }

        long t0 = Stopwatch.GetTimestamp();
        _sendNewRoleInfoCalls++;

        NetworkWriterPooled writer1 = null;
        NetworkWriterPooled writer2 = null;
        NetworkWriterPooled spoofWriter = null;
        RoleTypeId cachedRole1 = RoleTypeId.None;
        RoleTypeId cachedRole2 = RoleTypeId.None;
        bool hasCache1 = false;
        bool hasCache2 = false;
        int sent1 = 0;
        int sent2 = 0;
        RoleSyncInfo diagMsg1 = default;
        RoleSyncInfo diagMsg2 = default;
        int maxSize = NetworkMessages.MaxMessageSize(Channels.Reliable);

        try
        {
            ReferenceHub targetHub = __instance.Hub;
            Dictionary<uint, RoleTypeId> prevSent = __instance.PreviouslySentRole;
            Func<ReferenceHub, ReferenceHub, RoleTypeId, NetworkWriter, RoleTypeId> roleSyncEvent = _getRoleSyncEvent();

            if (roleSyncEvent != null)
            {
                spoofWriter = NetworkWriterPool.Get();
                foreach (ReferenceHub observer in ReferenceHub.AllHubs)
                {
                    if (observer.isLocalPlayer)
                    {
                        continue;
                    }

                    NetworkConnectionToClient conn = observer.connectionToClient;
                    if (observer.Mode == ClientInstanceMode.Dummy || conn == null || conn is DummyNetworkConnection)
                    {
                        _sendNewRoleInfoDummySkips++;
                        continue;
                    }

                    RoleTypeId visibleRole = FpcServerPositionDistributor.GetVisibleRole(observer, targetHub);
                    spoofWriter.Reset();
                    RoleTypeId? overridden = roleSyncEvent(targetHub, observer, visibleRole, spoofWriter);
                    if (overridden.HasValue)
                    {
                        visibleRole = overridden.Value;
                    }

                    conn.Send(new RoleSyncInfo(targetHub, visibleRole, observer, spoofWriter));
                    prevSent[observer.netId] = visibleRole;
                }

                return false;
            }

            foreach (ReferenceHub observer in ReferenceHub.AllHubs)
            {
                if (observer.isLocalPlayer)
                {
                    continue;
                }

                NetworkConnectionToClient conn = observer.connectionToClient;
                if (observer.Mode == ClientInstanceMode.Dummy || conn == null || conn is DummyNetworkConnection)
                {
                    _sendNewRoleInfoDummySkips++;
                    continue;
                }

                RoleTypeId visibleRole = FpcServerPositionDistributor.GetVisibleRole(observer, targetHub);

                if (observer == targetHub)
                {
                    conn.Send(new RoleSyncInfo(targetHub, visibleRole, observer, null));
                    prevSent[observer.netId] = visibleRole;
                    continue;
                }

                if (hasCache1 && visibleRole == cachedRole1)
                {
                    if (writer1.Position <= maxSize)
                    {
                        conn.Send(writer1.ToArraySegment(), Channels.Reliable);
                        sent1++;
                        _sendNewRoleInfoPackOnceSends++;
                    }
                    prevSent[observer.netId] = visibleRole;
                    continue;
                }

                if (hasCache2 && visibleRole == cachedRole2)
                {
                    if (writer2.Position <= maxSize)
                    {
                        conn.Send(writer2.ToArraySegment(), Channels.Reliable);
                        sent2++;
                        _sendNewRoleInfoPackOnceSends++;
                    }
                    prevSent[observer.netId] = visibleRole;
                    continue;
                }

                if (!hasCache1)
                {
                    writer1 = NetworkWriterPool.Get();
                    diagMsg1 = new RoleSyncInfo(targetHub, visibleRole, observer, null);
                    NetworkMessages.Pack(diagMsg1, writer1);
                    cachedRole1 = visibleRole;
                    hasCache1 = true;

                    if (writer1.Position > maxSize)
                    {
                        UnityEngine.Debug.LogError($"NetworkConnection.Send: message of type {typeof(RoleSyncInfo)} with a size of {writer1.Position} bytes is larger than the max allowed message size in one batch: {maxSize}.");
                    }
                    else
                    {
                        conn.Send(writer1.ToArraySegment(), Channels.Reliable);
                        sent1++;
                    }

                    prevSent[observer.netId] = visibleRole;
                }
                else if (!hasCache2)
                {
                    writer2 = NetworkWriterPool.Get();
                    diagMsg2 = new RoleSyncInfo(targetHub, visibleRole, observer, null);
                    NetworkMessages.Pack(diagMsg2, writer2);
                    cachedRole2 = visibleRole;
                    hasCache2 = true;

                    if (writer2.Position > maxSize)
                    {
                        UnityEngine.Debug.LogError($"NetworkConnection.Send: message of type {typeof(RoleSyncInfo)} with a size of {writer2.Position} bytes is larger than the max allowed message size in one batch: {maxSize}.");
                    }
                    else
                    {
                        conn.Send(writer2.ToArraySegment(), Channels.Reliable);
                        sent2++;
                    }

                    prevSent[observer.netId] = visibleRole;
                }
                else
                {
                    conn.Send(new RoleSyncInfo(targetHub, visibleRole, observer, null));
                    prevSent[observer.netId] = visibleRole;
                }
            }

            if (sent1 > 0 && _diagOnSendRoleSync != null)
            {
                _diagOnSendRoleSync(diagMsg1, Channels.Reliable, writer1.Position, sent1);
            }

            if (sent2 > 0 && _diagOnSendRoleSync != null)
            {
                _diagOnSendRoleSync(diagMsg2, Channels.Reliable, writer2.Position, sent2);
            }

            return false;
        }
        finally
        {
            if (writer1 != null) NetworkWriterPool.Return(writer1);
            if (writer2 != null) NetworkWriterPool.Return(writer2);
            if (spoofWriter != null) NetworkWriterPool.Return(spoofWriter);

            if (_activeBurst.Active)
            {
                _activeBurst.SendNewRoleInfoTicks += Stopwatch.GetTimestamp() - t0;
            }
        }
    }

    private static bool WaveSyncValuesPrefix(ReferenceHub hub)
    {
        if (!_active || _createWaveUpdateMessage == null)
        {
            return true;
        }

        if (!NetworkServer.active || hub == null)
        {
            return false;
        }

        NetworkConnectionToClient conn = hub.connectionToClient;
        if (hub.Mode == ClientInstanceMode.Dummy || conn == null || conn is DummyNetworkConnection)
        {
            _waveSyncDummySkips++;
            _waveSyncBroadcastsPrevented += WaveManager.Waves.Count;
            return false;
        }

        _waveSyncDirectCalls++;
        if (hub.Mode != ClientInstanceMode.Unverified && !hub.isLocalPlayer)
        {
            List<SpawnableWaveBase> waves = WaveManager.Waves;
            for (int i = 0; i < waves.Count; i++)
            {
                if (waves[i] is not TimeBasedWave wave)
                {
                    continue;
                }

                UpdateMessageFlags flags = UpdateMessageFlags.All;
                if (!wave.Timer.IsPaused)
                {
                    flags &= ~UpdateMessageFlags.Pause;
                }

                WaveUpdateMessage msg = _createWaveUpdateMessage(wave, flags);
                conn.Send(msg);
                _waveSyncBroadcastsPrevented++;
            }
        }

        foreach (KeyValuePair<Faction, float> item in FactionInfluenceManager.Influence)
        {
            conn.Send(new InfluenceUpdateMessage
            {
                Faction = item.Key,
                Influence = item.Value
            });
        }

        return false;
    }

    private static bool SyncedStatOnRoleChangedPrefix(ReferenceHub userHub)
    {
        if (!_active || userHub == null)
        {
            return true;
        }

        if (userHub.Mode == ClientInstanceMode.Dummy || userHub.connectionToClient is DummyNetworkConnection)
        {
            _syncedStatDummySkips++;
            return false;
        }

        return true;
    }

    // =========================================================================
    // Part 5: Non-Conflicting Deep Role-Change Burst Profiler (`slperf roleburst`)
    // =========================================================================

    private static void ServerSetRoleTimingPrefix(RoleChangeReason reason, out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        if (!_active)
        {
            return;
        }

        _totalRoleChangesObserved++;
        int frame = Time.frameCount;
        if (_inRoleChangeDepth == 0)
        {
            _inRoleChangeStartFrame = frame;
        }
        _inRoleChangeDepth++;

        if (!_activeBurst.Active)
        {
            BurstItemDemand.Clear();
            BurstPoolMissByPool.Clear();
            _activeBurst = new BurstAccumulator
            {
                Active = true,
                StartUtc = DateTime.UtcNow,
                StartFrame = frame,
                LastFrame = frame,
                LastTimestamp = __state,
                PrimaryReason = reason
            };
        }
        else
        {
            _activeBurst.LastFrame = frame;
            _activeBurst.LastTimestamp = __state;
        }

        _activeBurst.RoleChanges++;
    }

    private static void ServerSetRoleTimingPostfix(long __state)
    {
        if (!_active)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_inRoleChangeDepth > 0)
        {
            _inRoleChangeDepth--;
        }

        if (_inRoleChangeDepth == 0 && _activeBurst.Active)
        {
            _activeBurst.ServerSetRoleTicks += now - __state;
            _activeBurst.LastTimestamp = now;
            if (_activeBurst.RoleChanges >= MassBurstMinCalls)
            {
                _lastBurstEndTimestamp = now;
            }
        }
    }

    private static void InitNewRoleTimingPrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        if (_active) _inInitRoleDepth++;
    }

    private static Exception InitNewRoleTimingFinalizer(long __state, Exception __exception)
    {
        if (_retainedModel != null)
        {
            FlushRetainedModel();
        }

        if (_active)
        {
            if (_inInitRoleDepth > 0) _inInitRoleDepth--;
            if (_inInitRoleDepth == 0 && _inRoleChangeDepth > 0 && _activeBurst.Active)
            {
                _activeBurst.InitNewRoleTicks += Stopwatch.GetTimestamp() - __state;
            }
        }

        return __exception;
    }

    private static void OnRoleDisabledTimingPrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    private static void OnRoleDisabledTimingPostfix(long __state)
    {
        if (_active && _inDisableRoleDepth > 0 && _inRoleChangeDepth > 0 && _activeBurst.Active)
        {
            _activeBurst.DisableModelRetTicks += Stopwatch.GetTimestamp() - __state;
        }
    }

    private static void DisableRoleTimingPrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        if (_active) _inDisableRoleDepth++;
    }

    private static void DisableRoleTimingPostfix(long __state)
    {
        if (!_active) return;
        if (_inDisableRoleDepth > 0) _inDisableRoleDepth--;
        if (_inDisableRoleDepth == 0 && _inRoleChangeDepth > 0 && _activeBurst.Active)
        {
            _activeBurst.DisableRoleTicks += Stopwatch.GetTimestamp() - __state;
        }
    }

    private static void SetModelTimingPrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        if (_active) _inSetModelDepth++;
    }

    private static void SetModelTimingPostfix(long __state)
    {
        if (!_active) return;
        if (_inSetModelDepth > 0) _inSetModelDepth--;
        if (_inSetModelDepth == 0 && _inRoleChangeDepth > 0 && _activeBurst.Active)
        {
            _activeBurst.SetModelTicks += Stopwatch.GetTimestamp() - __state;
        }
    }

    private static void AnimSetupTimingPrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        if (_active) _inAnimSetupDepth++;
    }

    private static void AnimSetupTimingPostfix(long __state)
    {
        if (!_active) return;
        if (_inAnimSetupDepth > 0) _inAnimSetupDepth--;
        if (_inAnimSetupDepth == 0 && _inRoleChangeDepth > 0 && _activeBurst.Active)
        {
            _activeBurst.AnimSetupTicks += Stopwatch.GetTimestamp() - __state;
        }
    }

    private static void SpawnPosTimingPrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        if (_active) _inSpawnPosDepth++;
    }

    private static void SpawnPosTimingPostfix(long __state)
    {
        if (!_active) return;
        if (_inSpawnPosDepth > 0) _inSpawnPosDepth--;
        if (_inSpawnPosDepth == 0 && _inRoleChangeDepth > 0 && _activeBurst.Active)
        {
            _activeBurst.SpawnPosTicks += Stopwatch.GetTimestamp() - __state;
        }
    }

    private static void GrantLoadoutTimingPrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        if (_active) _inGrantLoadoutDepth++;
    }

    private static void GrantLoadoutTimingPostfix(long __state)
    {
        if (!_active) return;
        if (_inGrantLoadoutDepth > 0) _inGrantLoadoutDepth--;
        if (_inGrantLoadoutDepth == 0 && _inRoleChangeDepth > 0 && _activeBurst.Active)
        {
            _activeBurst.GrantLoadoutTicks += Stopwatch.GetTimestamp() - __state;
        }
    }

    private static void DestroyItemTimingPrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    private static void DestroyItemTimingPostfix(long __state)
    {
        if (_active && _inGrantLoadoutDepth > 0 && _activeBurst.Active)
        {
            _activeBurst.ClearItemsCount++;
            _activeBurst.ClearItemsTicks += Stopwatch.GetTimestamp() - __state;
        }
    }

    private static void CreateItemTimingPrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
        if (_active) _inCreateItemDepth++;
    }

    private static void CreateItemTimingPostfix(long __state)
    {
        if (!_active) return;
        if (_inCreateItemDepth > 0) _inCreateItemDepth--;
        if (_inCreateItemDepth == 0 && _inRoleChangeDepth > 0 && _activeBurst.Active)
        {
            _activeBurst.CreateItemTicks += Stopwatch.GetTimestamp() - __state;
        }
    }

    private static void PoolSpawnObjectPrefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    private static void PoolSpawnObjectPostfix(object __instance, PoolObject __result, long __state)
    {
        if (!_active)
        {
            return;
        }

        if (__result is CharacterModel cm)
        {
            PrepareHeadlessModelWhileInactive(cm);
        }

        if (__instance != null)
        {
            PoolTotalCounts.TryGetValue(__instance, out int c);
            PoolTotalCounts[__instance] = c + 1;
        }

        if (!_isPrewarming && _inRoleChangeDepth > 0)
        {
            _totalPoolMisses++;
            if (_activeBurst.Active)
            {
                _activeBurst.PoolMissCount++;
                _activeBurst.PoolMissTicks += Stopwatch.GetTimestamp() - __state;
                if (__instance != null)
                {
                    BurstPoolMissByPool.TryGetValue(__instance, out int m);
                    BurstPoolMissByPool[__instance] = m + 1;
                }
            }
        }
    }

    private static void CommitActiveBurst()
    {
        if (!_activeBurst.Active)
        {
            return;
        }

        if (_activeBurst.RoleChanges >= MassBurstMinCalls)
        {
            ApplyAdaptiveHighWaterMarks();

            BurstHistory[_burstHistoryWrite] = new RoleBurstSnapshot
            {
                TimestampUtc = _activeBurst.StartUtc,
                RoleChanges = _activeBurst.RoleChanges,
                FrameSpan = Math.Max(1, _activeBurst.LastFrame - _activeBurst.StartFrame + 1),
                PrimaryReason = _activeBurst.PrimaryReason,
                ServerSetRoleMs = _activeBurst.ServerSetRoleTicks * TicksToMs,
                InitNewRoleMs = _activeBurst.InitNewRoleTicks * TicksToMs,
                DisableRoleMs = _activeBurst.DisableRoleTicks * TicksToMs,
                DisableModelRetMs = _activeBurst.DisableModelRetTicks * TicksToMs,
                SetModelMs = _activeBurst.SetModelTicks * TicksToMs,
                SetModelParentMs = _activeBurst.SetModelParentTicks * TicksToMs,
                SetModelActiveMs = _activeBurst.SetModelActiveTicks * TicksToMs,
                SetModelReuseCount = _activeBurst.SetModelReuseCount,
                SetModelParkedCount = _activeBurst.SetModelParkedCount,
                AnimSetupMs = _activeBurst.AnimSetupTicks * TicksToMs,
                SpawnPosMs = _activeBurst.SpawnPosTicks * TicksToMs,
                GrantLoadoutMs = _activeBurst.GrantLoadoutTicks * TicksToMs,
                ClearItemsMs = _activeBurst.ClearItemsTicks * TicksToMs,
                CreateItemMs = _activeBurst.CreateItemTicks * TicksToMs,
                SendNewRoleInfoMs = _activeBurst.SendNewRoleInfoTicks * TicksToMs,
                PoolMissMs = _activeBurst.PoolMissTicks * TicksToMs,
                PoolMissCount = _activeBurst.PoolMissCount,
                ItemReserveHits = _activeBurst.ItemReserveHits,
                ItemReserveMisses = _activeBurst.ItemReserveMisses,
                ClearItemsCount = _activeBurst.ClearItemsCount
            };
            _burstHistoryWrite = (_burstHistoryWrite + 1) % BurstHistoryCapacity;
            if (_burstHistoryCount < BurstHistoryCapacity)
            {
                _burstHistoryCount++;
            }
        }

        BurstItemDemand.Clear();
        BurstPoolMissByPool.Clear();
        _activeBurst = default;
    }

    private static void ApplyAdaptiveHighWaterMarks()
    {
        int slots = GetEffectiveSlotTarget();

        if (_prewarmItemReserveEnabled && _itemPlanBuilt && BurstItemDemand.Count > 0)
        {
            foreach (KeyValuePair<ItemType, int> kv in BurstItemDemand)
            {
                int desired = Math.Min(slots, kv.Value);
                if (ItemReserveMap.TryGetValue(kv.Key, out ItemReserveEntry entry))
                {
                    if (desired > entry.TargetCount)
                    {
                        entry.TargetCount = desired;
                    }
                }
                else if (desired >= 4 && ItemReserveList.Count < 36 &&
                         InventoryItemLoader.AvailableItems.TryGetValue(kv.Key, out ItemBase template) &&
                         template != null)
                {
                    ItemReserveEntry added = new ItemReserveEntry
                    {
                        ItemType = kv.Key,
                        Template = template,
                        TargetCount = desired,
                        IsRoundStartPriority = false
                    };
                    ItemReserveList.Add(added);
                    ItemReserveMap.Add(kv.Key, added);
                }
            }
        }

        if (_prewarmRolePoolsEnabled && _poolPlanBuilt && BurstPoolMissByPool.Count > 0)
        {
            foreach (KeyValuePair<object, int> kv in BurstPoolMissByPool)
            {
                if (PoolTargetByPool.TryGetValue(kv.Key, out PoolPrewarmTarget target))
                {
                    PoolTotalCounts.TryGetValue(kv.Key, out int totalOwned);
                    int desired = Math.Min(slots, Math.Max(totalOwned, target.TargetCount + kv.Value));
                    if (desired > target.TargetCount)
                    {
                        target.TargetCount = desired;
                    }
                }
            }
        }
    }

    // =========================================================================
    // Console Report: `slperf roleburst`
    // =========================================================================

    public static string FormatRoleBurstReport()
    {
        if (_activeBurst.Active && _inRoleChangeDepth == 0 && (Time.frameCount - _activeBurst.LastFrame) >= 1)
        {
            CommitActiveBurst();
        }

        StringBuilder sb = new StringBuilder(2048);
        sb.AppendLine("=== SLPerformance Mass Role-Change & Wave Respawn Report (OPT-21) ===");
        sb.AppendLine($"Module Active        : {_active} | Budget={_prewarmBudgetMs:F1}ms/fr | SlotsScale={(_poolPlanSlots > 0 ? _poolPlanSlots : GetEffectiveSlotTarget())} | SetModelFast={_setModelFastPathCalls} (Reuse={_setModelReuseHits}, Parked={_setModelParkedHits}) | AnimSetupFast={_animSetupFastPathCalls}");
        sb.AppendLine($"Headless Strip/Skip  : ModelsStripped={_headlessModelsStripped} | WarmParkedNow={ParkedActiveModels.Count} | OverrideSkips={_animOverrideSkips} | ChatUiSkips={_globalChatUiSkips}");

        int poolReady = 0;
        int poolTotalTargets = PoolTargets.Count;
        int poolCurObjects = 0;
        int poolGoalObjects = 0;
        for (int i = 0; i < PoolTargets.Count; i++)
        {
            PoolPrewarmTarget t = PoolTargets[i];
            PoolTotalCounts.TryGetValue(t.PoolInstance, out int owned);
            int cur = Math.Max(owned, t.PoolStack != null ? t.PoolStack.Count : 0);
            poolCurObjects += Math.Min(cur, t.TargetCount);
            poolGoalObjects += t.TargetCount;
            if (cur >= t.TargetCount) poolReady++;
        }

        sb.AppendLine(
            $"Role/Model Pools     : {(_prewarmRolePoolsEnabled ? (_poolPlanBuilt ? $"{poolReady}/{poolTotalTargets} ready ({poolCurObjects}/{poolGoalObjects} objs)" : "Waiting for PlayerRoleLoader") : "Disabled")} | Prewarmed={_totalPoolPrewarmed} | RuntimePoolMisses={_totalPoolMisses}");

        int itemReadyTypes = 0;
        int itemCurObjs = 0;
        int itemGoalObjs = 0;
        for (int i = 0; i < ItemReserveList.Count; i++)
        {
            ItemReserveEntry e = ItemReserveList[i];
            int cur = e.Stack.Count;
            itemCurObjs += Math.Min(cur, e.TargetCount);
            itemGoalObjs += e.TargetCount;
            if (cur >= e.TargetCount) itemReadyTypes++;
        }

        long totalItemLookups = _totalItemReserveHits + _totalItemReserveMisses;
        double hitPct = totalItemLookups > 0 ? (_totalItemReserveHits * 100.0 / totalItemLookups) : 0.0;
        sb.AppendLine(
            $"Virgin Item Reserve  : {(_prewarmItemReserveEnabled ? (_itemPlanBuilt ? $"{itemReadyTypes}/{ItemReserveList.Count} types ready ({itemCurObjs}/{itemGoalObjs} virgin items)" : "Waiting for Facility scene") : "Disabled")} | Prewarmed={_totalItemsPrewarmed} | Hits={_totalItemReserveHits}/{totalItemLookups} ({hitPct:F1}%)");

        sb.AppendLine(
            $"Network Burst Accel  : SendRoleCalls={_sendNewRoleInfoCalls} (PackOnce={_sendNewRoleInfoPackOnceSends}, DummySkips={_sendNewRoleInfoDummySkips}) | WaveSyncDirect={_waveSyncDirectCalls} (StormsPrevented={_waveSyncBroadcastsPrevented}) | StatDummySkips={_syncedStatDummySkips}");
        sb.AppendLine($"Role Changes Seen    : {_totalRoleChangesObserved} total | Recorded Mass Bursts (>={MassBurstMinCalls} calls): {_burstHistoryCount}");

        if (_burstHistoryCount == 0)
        {
            sb.AppendLine("  (No mass role-change bursts recorded yet. Start a round, trigger a wave, or run mass forceclass, then run `slperf roleburst` again.)");
            return sb.ToString();
        }

        sb.AppendLine("--- Recent Mass Role-Change Bursts (Newest First) ---");
        for (int i = 0; i < _burstHistoryCount; i++)
        {
            int idx = (_burstHistoryWrite - 1 - i + BurstHistoryCapacity) % BurstHistoryCapacity;
            RoleBurstSnapshot b = BurstHistory[idx];
            double eventsAndOtherMs = Math.Max(0.0, b.ServerSetRoleMs - b.InitNewRoleMs - b.SendNewRoleInfoMs);
            double roleSetupExLoadoutMs = Math.Max(0.0, b.InitNewRoleMs - b.GrantLoadoutMs);
            double roleOtherMs = Math.Max(0.0, roleSetupExLoadoutMs - b.DisableRoleMs - b.SetModelMs - b.SpawnPosMs);
            double loadoutOtherMs = Math.Max(0.0, b.GrantLoadoutMs - b.ClearItemsMs - b.CreateItemMs);
            int itemTotal = b.ItemReserveHits + b.ItemReserveMisses;

            sb.AppendLine(
                $"  #{i + 1} [{b.TimestampUtc.ToLocalTime():HH:mm:ss}] {b.RoleChanges} roles ({b.PrimaryReason}, {b.FrameSpan}fr) | Total={b.ServerSetRoleMs:F2}ms");
            sb.AppendLine(
                $"       RoleSetup={roleSetupExLoadoutMs:F2}ms (Disable={b.DisableRoleMs:F2}ms [ModelRet={b.DisableModelRetMs:F2}ms], SetModel={b.SetModelMs:F2}ms [Parent={b.SetModelParentMs:F2}ms, Active={b.SetModelActiveMs:F2}ms, Anim={b.AnimSetupMs:F2}ms, Reuse={b.SetModelReuseCount}, Parked={b.SetModelParkedCount}], SpawnPos={b.SpawnPosMs:F2}ms, Other/OnRoleChg={roleOtherMs:F2}ms, PoolMiss={b.PoolMissCount}/{b.PoolMissMs:F2}ms)");
            sb.AppendLine(
                $"       Loadout={b.GrantLoadoutMs:F2}ms (Clear={b.ClearItemsCount}/{b.ClearItemsMs:F2}ms, CreateItem={b.CreateItemMs:F2}ms [Hit {b.ItemReserveHits}/{itemTotal}], OnAdded/Attach={loadoutOtherMs:F2}ms) | SendRole={b.SendNewRoleInfoMs:F2}ms | Events={eventsAndOtherMs:F2}ms");
        }

        return sb.ToString();
    }
}
