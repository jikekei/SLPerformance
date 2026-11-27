using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using AdminToys;
using CentralAuth;
using Cryptography;
using CustomPlayerEffects;
using HarmonyLib;
using Hazards;
using InventorySystem;
using InventorySystem.Items.Pickups;
using LabApi.Events;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LiteNetLib;
using MapGeneration;
using Mirror;
using Mirror.LiteNetLib4Mirror;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.PlayableScps;
using PlayerRoles.PlayableScps.Scp096;
using PlayerRoles.PlayableScps.Scp173;
using PlayerRoles.Voice;
using PlayerStatsSystem;
using RelativePositioning;
using SLPerformance.Collections;
using SLPerformance.Core;
using SLPerformance.Entities;
using SLPerformance.Network;
using SLPerformance.Profiler;
using SLPerformance.Serialization;
using SLPerformance.Threading;
using UnityEngine;
using VoiceChat.Networking;

namespace SLPerformance.Tests;

internal static class Program
{
    private const string DefaultManagedDir = @"C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed";
    private const string DefaultHarmonyDir = @"c:\Users\Administrator\Desktop\项目\huanmengyinghecajiang0\PluginDependencies";

    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += ResolveGameAssemblies;

        Console.WriteLine("=====================================================================");
        Console.WriteLine(" SLPerformance Automated Equivalence & Binary Metadata Test Suite");
        Console.WriteLine("=====================================================================");

        RunAll();

        Console.WriteLine("=====================================================================");
        Console.WriteLine($" SUMMARY: {_passed} PASSED, {_failed} FAILED");
        Console.WriteLine("=====================================================================");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunAll()
    {
        RunTest("01. ModuleRegistry & Config Defaults (22 modules)", TestModuleRegistryAndConfig);
        RunTest("02. Live SCP:SL 14.2.9 Reflection Target Signatures Audit", TestLiveMetadataSignatures);
        RunTest("03. StatusEffectBase Reflection Sleep Whitelist Audit", TestStatusEffectSleepWhitelist);
        RunTest("04. AdminToyBase Reflection Sleep Whitelist Audit", TestAdminToySleepWhitelist);
        RunTest("05. OPT-02 FpcSyncDataFactory Bit-Exact Wire Equivalence", TestFpcSyncDataEquivalence);
        RunTest("06. OPT-01/03 Mirror NetworkMessages.Pack Single-Pack Equivalence", TestMirrorSinglePackEquivalence);
        RunTest("07. OPT-06 DelegateInvocationCache Equivalence & Zero-Alloc", TestDelegateInvocationCache);
        RunTest("08. OPT-10 Mirror Batcher & Unbatcher Zero-Copy Wire Equivalence", TestMirrorBatcherZeroCopyEquivalence);
        RunTest("09. OPT-12 LiteNetPeerLookup IPv4 & IPv6 SockAddr Key Equivalence", TestLiteNetPeerKeys);
        RunTest("10. Profiler FrameMetricsRing Percentiles & Wraparound", TestFrameMetricsRing);
        RunTest("11. Collections CompactUintMap<T> CRUD & Swap-Remove", TestCompactUintMap);
        RunTest("12. OPT-17 BoundedWorkerPool Parallel Determinism & Error Handling", TestBoundedWorkerPool);
        RunTest("13. Profiler Breakdown Probes Resolution & Alloc Accounting", TestProfilerProbeTargetsAndAccounting);
        RunTest("14. OPT-21 Mass Role-Change Accessors & WaveUpdateMessage Factory", TestOpt21MassRoleChangeAccessors);
        RunTest("15. OPT-22 Player Join & Async Auth Accessors, Crypto Parity & BanCache", TestOpt22PlayerJoinAccel);
    }

    private static Assembly ResolveGameAssemblies(AssemblyLoadContext alc, AssemblyName name)
    {
        string shortName = name.Name;
        if (string.IsNullOrEmpty(shortName) ||
            shortName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
            shortName.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
            shortName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            shortName.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string harmonyCandidate = Path.Combine(DefaultHarmonyDir, shortName + ".dll");
        if (File.Exists(harmonyCandidate))
        {
            return alc.LoadFromAssemblyPath(harmonyCandidate);
        }

        string managedCandidate = Path.Combine(DefaultManagedDir, shortName + ".dll");
        if (File.Exists(managedCandidate))
        {
            return alc.LoadFromAssemblyPath(managedCandidate);
        }

        return null;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null);
        }
    }

    private static void RunTest(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}");
            Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"       Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("Assertion failed: " + message);
        }
    }

    private static void TestModuleRegistryAndConfig()
    {
        var registry = new ModuleRegistry();
        SLPerformancePlugin.RegisterAllModules(registry);
        var cfg = new SLPerformanceConfig();

        Assert(registry.Modules.Count == 22, $"Expected 22 modules, got {registry.Modules.Count}");

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int t1 = 0, t2 = 0, t3 = 0;
        int defaultEnabled = 0;
        foreach (IOptimizationModule m in registry.Modules)
        {
            Assert(!string.IsNullOrWhiteSpace(m.Id), "Module ID must not be empty");
            Assert(seenIds.Add(m.Id), $"Duplicate module ID: {m.Id}");
            if (m.Tier == OptimizationTier.Tier1) t1++;
            else if (m.Tier == OptimizationTier.Tier2) t2++;
            else if (m.Tier == OptimizationTier.Tier3) t3++;

            bool enabled = m.IsEnabledInConfig(cfg);
            if (m.Tier == OptimizationTier.Tier3)
            {
                Assert(!enabled, $"Tier 3 module {m.Id} must be opt-in (false by default)");
            }
            else
            {
                Assert(enabled, $"Tier 1/2 module {m.Id} should be enabled by default");
            }

            if (enabled) defaultEnabled++;
        }

        Assert(t1 == 12, $"Expected 12 Tier 1 modules, got {t1}");
        Assert(t2 == 8, $"Expected 8 Tier 2 modules, got {t2}");
        Assert(t3 == 2, $"Expected 2 Tier 3 modules, got {t3}");
        Assert(defaultEnabled == 20, $"Expected 20 default-enabled modules, got {defaultEnabled}");
    }

    private static void TestLiveMetadataSignatures()
    {
        var missing = new List<string>();

        void CheckMethod(Type t, string name, Type[] args = null)
        {
            MethodInfo m = t == null ? null : (args == null ? AccessTools.Method(t, name) : AccessTools.Method(t, name, args));
            if (m == null) missing.Add($"Method {t?.FullName ?? "<null>"}.{name}");
        }

        void CheckField(Type t, string name)
        {
            FieldInfo f = t == null ? null : AccessTools.Field(t, name);
            if (f == null) missing.Add($"Field {t?.FullName ?? "<null>"}.{name}");
        }

        void CheckProp(Type t, string name)
        {
            PropertyInfo p = t == null ? null : AccessTools.Property(t, name);
            if (p == null) missing.Add($"Property {t?.FullName ?? "<null>"}.{name}");
        }

        // OPT-01 Mirror RPC Pack Once
        CheckMethod(typeof(NetworkBehaviour), "SendRPCInternal", new[] { typeof(string), typeof(int), typeof(NetworkWriter), typeof(int), typeof(bool) });
        CheckMethod(typeof(NetworkDiagnostics), "OnSend");

        // OPT-02 FPC Distributor
        Assert(FpcSyncDataFactory.IsSupported, "FpcSyncDataFactory failed to compile against real FpcSyncData fields");
        CheckMethod(typeof(FpcServerPositionDistributor), "LateUpdate", Type.EmptyTypes);
        CheckMethod(typeof(FpcServerPositionDistributor), "WriteAll", new[] { typeof(ReferenceHub), typeof(NetworkWriter) });
        CheckMethod(typeof(FpcServerPositionDistributor), "GetVisibleRole", new[] { typeof(ReferenceHub), typeof(ReferenceHub) });
        CheckMethod(typeof(FpcServerPositionDistributor), "SendRole", new[] { typeof(ReferenceHub), typeof(ReferenceHub), typeof(RoleTypeId), typeof(NetworkWriter) });
        CheckField(typeof(FpcServerPositionDistributor), "PreviouslySent");
        CheckField(typeof(FpcServerPositionDistributor), "_roleSyncEvent");
        CheckField(typeof(FpcServerPositionDistributor), "_bufferPlayerIDs");
        CheckField(typeof(FpcServerPositionDistributor), "_bufferSyncData");
        CheckField(typeof(FpcServerPositionDistributor), "_sendCooldown");
        CheckField(typeof(PlayerEvents), nameof(PlayerEvents.ValidatedVisibility));

        // OPT-03 Mirror Dirty Broadcast
        CheckMethod(typeof(NetworkServer), "Broadcast", Type.EmptyTypes);
        CheckMethod(typeof(NetworkServer), "DisconnectIfInactive", new[] { typeof(NetworkConnectionToClient) });
        CheckField(typeof(NetworkServer), "connectionsCopy");
        CheckMethod(typeof(NetworkIdentity), "GetServerSerializationAtTick", new[] { typeof(int) });
        CheckField(typeof(NetworkIdentity), "lastSerialization");
        CheckField(typeof(NetworkBehaviour), "<syncVarDirtyBits>k__BackingField");
        CheckField(typeof(NetworkBehaviour), "syncObjectDirtyBits");
        CheckField(typeof(NetworkBehaviour), "lastSyncTime");

        // OPT-04 Transport Direct Poll
        CheckMethod(typeof(LiteNetLib4MirrorTransport), "StartThreads", Type.EmptyTypes);
        CheckMethod(typeof(LiteNetLib4MirrorTransport), "LateUpdate", Type.EmptyTypes);
        CheckMethod(typeof(LiteNetLib4MirrorTransport), "ProcessConnectionRequest", new[] { typeof(ConnectionRequest) });
        CheckField(typeof(LiteNetLib4MirrorTransport), "Polling");
        CheckField(typeof(LiteNetLib4MirrorTransport), "Events");
        CheckMethod(typeof(LiteNetLib4MirrorServer), "OnPeerConnected", new[] { typeof(NetPeer) });
        CheckMethod(typeof(LiteNetLib4MirrorServer), "OnNetworkReceive", new[] { typeof(NetPeer), typeof(NetPacketReader), typeof(byte), typeof(DeliveryMethod) });
        CheckMethod(typeof(LiteNetLib4MirrorServer), "OnNetworkError", new[] { typeof(IPEndPoint), typeof(System.Net.Sockets.SocketError) });
        CheckMethod(typeof(LiteNetLib4MirrorServer), "OnPeerDisconnected", new[] { typeof(NetPeer), typeof(DisconnectInfo) });
        CheckMethod(typeof(LiteNetLib4MirrorServer), "OnConnectionRequest", new[] { typeof(ConnectionRequest) });
        CheckMethod(typeof(LiteNetLib4MirrorServer), "ConnectionRequestLoop", Type.EmptyTypes);
        CheckField(typeof(LiteNetLib4MirrorServer), "_connectionRequests");
        CheckProp(typeof(LiteNetLib4MirrorCore), "Host");
        CheckProp(typeof(LiteNetLib4MirrorCore), "LastError");
        CheckProp(typeof(LiteNetLib4MirrorCore), "LastDisconnectError");
        CheckProp(typeof(LiteNetLib4MirrorCore), "LastDisconnectReason");
        CheckField(typeof(LiteNetLib4MirrorServer), "Peers");

        // OPT-05 Voice Batch
        CheckMethod(typeof(VoiceTransceiver), "ServerReceiveMessage", new[] { typeof(NetworkConnection), typeof(VoiceMessage) });
        CheckField(typeof(VoiceTransceiver), "OnVoiceMessageReceiving");
        CheckProp(typeof(VoiceModuleBase), nameof(VoiceModuleBase.CurrentChannel));
        CheckField(typeof(PlayerEvents), nameof(PlayerEvents.ReceivingVoiceMessage));
        CheckField(typeof(PlayerEvents), nameof(PlayerEvents.SendingVoiceMessage));

        // OPT-06 LabAPI Event Cache
        CheckMethod(typeof(EventManager), nameof(EventManager.InvokeEvent), new[] { typeof(LabEventHandler) });
        CheckMethod(typeof(PlayerEvents), nameof(PlayerEvents.OnValidatedVisibility), new[] { typeof(PlayerValidatedVisibilityEventArgs) });
        CheckMethod(typeof(PlayerEvents), nameof(PlayerEvents.OnStayingInHazard), new[] { typeof(PlayersStayingInHazardEventArgs) });

        // OPT-07 Effects Interface Index
        CheckMethod(typeof(FirstPersonMovementModule), nameof(FirstPersonMovementModule.VelocityForState), new[] { typeof(PlayerMovementState), typeof(bool) });
        CheckMethod(typeof(FpcMotor), "TryGetOverride", new[] { typeof(Vector3).MakeByRefType() });
        CheckField(typeof(FpcMotor), "_cursorOverrides");
        CheckProp(typeof(FpcStateProcessor), "ServerUseRate");
        CheckProp(typeof(FpcStateProcessor), "ServerRegenRate");
        CheckProp(typeof(FpcStateProcessor), "SprintingDisabled");
        CheckField(typeof(FpcStateProcessor), "_respawnImmunity");
        CheckField(typeof(FpcStateProcessor), "_useRate");
        CheckField(typeof(FpcStateProcessor), "_regenerationOverTime");
        CheckField(typeof(FpcStateProcessor), "_regenStopwatch");
        CheckMethod(typeof(AnimatedCharacterModel), "PlayFootstep", Type.EmptyTypes);
        CheckMethod(typeof(VisionInformation), nameof(VisionInformation.CheckIsInDarkness), new[] { typeof(ReferenceHub), typeof(Vector3) });

        // OPT-08 Vision & SCP Trackers
        CheckMethod(typeof(Scp096TargetsTracker), "Update", Type.EmptyTypes);
        CheckMethod(typeof(Scp096TargetsTracker), "UpdateTarget", new[] { typeof(ReferenceHub) });
        CheckField(typeof(Scp096TargetsTracker), "_markers");
        CheckMethod(typeof(Scp096TargetsTracker), "ServerCheckTargets", Type.EmptyTypes);
        CheckMethod(typeof(Scp173ObserversTracker), nameof(Scp173ObserversTracker.UpdateObservers), Type.EmptyTypes);
        CheckMethod(typeof(Scp173ObserversTracker), nameof(Scp173ObserversTracker.ResetObject), Type.EmptyTypes);
        CheckMethod(typeof(Scp173ObserversTracker), "UpdateObserver", new[] { typeof(ReferenceHub) });
        CheckMethod(typeof(Scp173ObserversTracker), "ServerSendRpc", new[] { typeof(bool) });
        CheckField(typeof(Scp173ObserversTracker), "_simulatedTargets");
        CheckMethod(typeof(VisionInformation), nameof(VisionInformation.GetVisionInformation));

        // OPT-09 StatusEffect Sleep
        CheckMethod(typeof(StatusEffectBase), "Start", Type.EmptyTypes);
        CheckMethod(typeof(StatusEffectBase), nameof(StatusEffectBase.ForceIntensity), new[] { typeof(byte) });

        // OPT-10 Batcher/Unbatcher Zero-Copy
        CheckMethod(typeof(NetworkConnection), "Update", Type.EmptyTypes);
        CheckMethod(typeof(NetworkConnection), "SendToTransport", new[] { typeof(ArraySegment<byte>), typeof(int) });
        CheckProp(typeof(NetworkConnection), nameof(NetworkConnection.remoteTimeStamp));
        CheckMethod(typeof(NetworkServer), "OnTransportData", new[] { typeof(int), typeof(ArraySegment<byte>), typeof(int) });
        CheckMethod(typeof(NetworkServer), "UnpackAndInvoke", new[] { typeof(NetworkConnectionToClient), typeof(NetworkReader), typeof(int) });
        CheckField(typeof(NetworkConnection), "batches");
        CheckField(typeof(Batcher), "batches");
        CheckField(typeof(Batcher), "batch");

        // OPT-11 FPC Motor Fast Path
        CheckMethod(typeof(FpcMotor), nameof(FpcMotor.UpdatePosition), new[] { typeof(bool).MakeByRefType() });
        CheckMethod(typeof(FpcMotor), nameof(FpcMotor.Move), Type.EmptyTypes);
        CheckMethod(typeof(FpcMotor), "UpdateThirdperson", Type.EmptyTypes);
        CheckMethod(typeof(FpcMotor), "GetFrameMove", Type.EmptyTypes);
        CheckProp(typeof(FpcMotor), "Speed");
        CheckProp(typeof(FpcMotor), "DesiredMove");
        CheckField(typeof(FpcMotor), "_lastMaxSpeed");
        CheckField(typeof(FpcMotor), "ViewMode");
        CheckField(typeof(FpcMotor), "CachedTransform");
        CheckField(typeof(FpcMotor), "OnBeforeMove");
        CheckField(typeof(FirstPersonMovementModule), "_cachedPosition");
        CheckField(typeof(FirstPersonMovementModule), "_movementUpdateCycle");
        CheckMethod(typeof(FpcMouseLook), nameof(FpcMouseLook.UpdateRotation), Type.EmptyTypes);
        CheckField(typeof(FpcMouseLook), "_hub");
        CheckField(typeof(FpcMouseLook), "_fpmm");
        CheckField(typeof(FpcMouseLook), "_syncHorizontal");
        CheckField(typeof(FpcMouseLook), "_syncVertical");
        CheckField(typeof(FpcMouseLook), "_curHorizontal");
        CheckField(typeof(FpcMouseLook), "_curVertical");

        // OPT-12 LiteNet Peer Lookup
        Type nativeSocketType = AccessTools.TypeByName("LiteNetLib.NativeSocket");
        Type netPacketType = AccessTools.TypeByName("LiteNetLib.NetPacket");
        if (nativeSocketType == null) missing.Add("Type LiteNetLib.NativeSocket");
        if (netPacketType == null) missing.Add("Type LiteNetLib.NetPacket");
        CheckMethod(typeof(LiteNetManager), "NativeReceiveLogic", Type.EmptyTypes);
        CheckMethod(typeof(LiteNetManager), "AddPeer", new[] { typeof(LiteNetPeer) });
        CheckMethod(typeof(LiteNetManager), "RemovePeer", new[] { typeof(LiteNetPeer), typeof(bool) });
        CheckMethod(typeof(LiteNetManager), "ClearPeerSet", Type.EmptyTypes);
        CheckMethod(typeof(LiteNetPeer), "FinishEndPointChange", new[] { typeof(IPEndPoint) });
        CheckField(typeof(LiteNetManager), "_udpSocketv4");
        CheckField(typeof(LiteNetManager), "_udpSocketv6");
        CheckField(typeof(LiteNetManager), "_isRunning");
        CheckMethod(typeof(LiteNetManager), "PoolGetPacket", new[] { typeof(int) });
        CheckMethod(typeof(LiteNetManager), "ProcessError", new[] { typeof(System.Net.Sockets.SocketException) });
        if (netPacketType != null)
        {
            CheckMethod(typeof(LiteNetManager), "OnMessageReceived", new[] { netPacketType, typeof(IPEndPoint) });
            CheckField(netPacketType, "RawData");
            CheckField(netPacketType, "Size");
        }
        if (nativeSocketType != null)
        {
            CheckMethod(nativeSocketType, "RecvFrom", new[] { typeof(IntPtr), typeof(byte[]), typeof(int), typeof(byte[]), typeof(int).MakeByRefType() });
            CheckMethod(nativeSocketType, "GetSocketError", Type.EmptyTypes);
            CheckField(nativeSocketType, "UnixMode");
        }

        // OPT-13 Room & Waypoint
        CheckMethod(typeof(CurrentRoomPlayerCache), "ValidateCache", Type.EmptyTypes);
        CheckField(typeof(CurrentRoomPlayerCache), "_roleManager");
        CheckField(typeof(CurrentRoomPlayerCache), "_lastFrame");
        CheckField(typeof(CurrentRoomPlayerCache), "_lastDetected");
        CheckField(typeof(CurrentRoomPlayerCache), "_lastValid");
        CheckField(typeof(CurrentRoomPlayerCache), "_hasAnyValidRoom");
        CheckField(typeof(CurrentRoomPlayerCache), "_tr");
        CheckMethod(typeof(RoomUtils), nameof(RoomUtils.TryGetRoom), new[] { typeof(Vector3), typeof(RoomIdentifier).MakeByRefType() });
        CheckField(typeof(RoomUtils), "RoomDetectionMask");
        CheckMethod(typeof(WaypointBase), "AssignSelfToList", Type.EmptyTypes);
        CheckMethod(typeof(WaypointBase), "RemoveSelfFromList", Type.EmptyTypes);
        CheckMethod(typeof(WaypointBase), "SetId", new[] { typeof(byte) });
        CheckMethod(typeof(WaypointBase), "ExtractWaypointData");
        CheckMethod(typeof(WaypointBase), "SqrDistanceTo", new[] { typeof(Vector3) });
        CheckField(typeof(WaypointBase), "SetWaypoints");
        CheckField(typeof(WaypointBase), "AllWaypoints");
        CheckField(typeof(WaypointBase), "_id");

        // OPT-14 AdminToy, Pickup, Hazard, Tesla
        CheckMethod(typeof(AdminToyBase), nameof(AdminToyBase.OnStartServer), Type.EmptyTypes);
        CheckProp(typeof(AdminToyBase), nameof(AdminToyBase.NetworkIsStatic));
        if (AccessTools.Constructor(typeof(PickupStandardPhysics), new[] { typeof(ItemPickupBase), typeof(PickupStandardPhysics.FreezingMode) }) == null)
            missing.Add("Ctor PickupStandardPhysics(ItemPickupBase, FreezingMode)");
        CheckMethod(typeof(PickupStandardPhysics), nameof(PickupStandardPhysics.DestroyModule), Type.EmptyTypes);
        CheckMethod(typeof(PickupStandardPhysics), "OnUpdate", Type.EmptyTypes);
        CheckMethod(typeof(PickupStandardPhysics), "UpdateServer", Type.EmptyTypes);
        CheckMethod(typeof(PickupStandardPhysics), "UpdateClient", Type.EmptyTypes);
        CheckMethod(typeof(EnvironmentalHazard), "UpdateTargets", Type.EmptyTypes);
        CheckField(typeof(PlayerEvents), nameof(PlayerEvents.StayingInHazard));
        CheckMethod(typeof(TeslaGateController), nameof(TeslaGateController.FixedUpdate), Type.EmptyTypes);
        CheckField(typeof(PlayerEvents), nameof(PlayerEvents.IdlingTesla));
        CheckField(typeof(PlayerEvents), nameof(PlayerEvents.IdledTesla));
        CheckField(typeof(PlayerEvents), nameof(PlayerEvents.TriggeringTesla));
        CheckField(typeof(PlayerEvents), nameof(PlayerEvents.TriggeredTesla));

        // OPT-15 Inventory / Stats / Opus
        CheckMethod(typeof(AhpStat), nameof(AhpStat.ServerUpdateProcesses), new[] { typeof(bool), typeof(bool) });
        CheckField(typeof(AhpStat), "_activeProcesses");
        CheckMethod(typeof(Inventory), "UpdateObserverItems", Type.EmptyTypes);
        CheckMethod(typeof(Inventory), "RefreshModifiers", Type.EmptyTypes);
        CheckField(typeof(Inventory), "_staminaModifier");
        CheckField(typeof(Inventory), "_movementLimiter");
        CheckField(typeof(Inventory), "_movementMultiplier");
        CheckField(typeof(Inventory), "_sprintMultiplier");
        CheckField(typeof(Inventory), "_sprintingDisabled");
        CheckMethod(typeof(SyncedStatBase), "Update", Type.EmptyTypes);
        CheckField(typeof(SyncedStatBase), "ValueDirty");
        CheckField(typeof(SyncedStatBase), "MaxValueDirty");
        CheckField(typeof(SyncedStatBase), "_lastSent");
        CheckMethod(typeof(VoiceModuleBase), nameof(VoiceModuleBase.SpawnObject), Type.EmptyTypes);
        CheckProp(typeof(VoiceModuleBase), "Decoder");
        CheckMethod(typeof(VoiceModuleBase), nameof(VoiceModuleBase.ResetObject), Type.EmptyTypes);
        CheckField(typeof(VoiceModuleBase), "_defaultDecoder");
        CheckField(typeof(VoiceModuleBase), "_owner");

        // OPT-16 Spherecast Batch
        CheckMethod(typeof(StaticUnityMethods), "Update", Type.EmptyTypes);

        // OPT-02 Coexist GetNewSyncData & OPT-14 AdminToy LateUpdate
        CheckMethod(typeof(FpcServerPositionDistributor), "GetNewSyncData", new[] { typeof(ReferenceHub), typeof(ReferenceHub), typeof(FirstPersonMovementModule), typeof(bool) });
        CheckMethod(typeof(AdminToyBase), "LateUpdate", Type.EmptyTypes);

        // OPT-18 Headless Character Animator & Visual Subcontrollers
        CheckField(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.CullingSubcontroller), "_model");
        CheckField(typeof(AnimatedCharacterModel), "<Animator>k__BackingField");
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.CullingSubcontroller), "OnReassigned", Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.CullingSubcontroller), "LateUpdate", Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.HandPoseSubcontroller), "Evaluate", Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.FeetStabilizerSubcontroller), "OnAnimatorIK", new[] { typeof(int) });
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.SecondaryRigsSubcontroller), "MatchAll", Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor), "MatchAllBones", Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.GlowInTheDarkSubcontroller), "LateUpdate", Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.OffsetableSimpleWearable), "LateUpdate", Type.EmptyTypes);

        // OPT-19 Idle World Animator Sleep
        CheckField(typeof(Interactables.Interobjects.BasicDoor), "MainAnimator");
        CheckField(typeof(Interactables.Interobjects.BasicDoor), "_animationNeedsRefreshing");
        CheckField(typeof(Interactables.Interobjects.BasicDoor), "_remainingAnimCooldown");
        CheckField(typeof(Interactables.Interobjects.BasicDoor), "_cooldownDuration");
        CheckMethod(typeof(Interactables.Interobjects.BasicDoor), "TryApplyNewStateAnimation", Type.EmptyTypes);
        CheckMethod(typeof(Interactables.Interobjects.PryableDoor), nameof(Interactables.Interobjects.PryableDoor.TryPryGate), new[] { typeof(ReferenceHub) });
        CheckMethod(typeof(Interactables.Interobjects.BreakableDoor), nameof(Interactables.Interobjects.BreakableDoor.ServerDamage), new[] { typeof(float), typeof(Interactables.Interobjects.DoorUtils.DoorDamageType), typeof(Footprinting.Footprint) });
        CheckField(typeof(MapGeneration.Distributors.LockerChamber), "_animator");
        CheckField(typeof(MapGeneration.Distributors.LockerChamber), "_animationTime");
        CheckMethod(typeof(MapGeneration.Distributors.LockerChamber), nameof(MapGeneration.Distributors.LockerChamber.SetDoor), new[] { typeof(bool), typeof(AudioClip) });

        // OPT-20 Idle World Sleep & Headless Visual Strip
        CheckField(typeof(RoomLightController), "_flickerDuration");
        CheckMethod(typeof(RoomLightController), nameof(RoomLightController.ServerFlickerLights), new[] { typeof(float) });
        CheckField(typeof(InventorySystem.Items.ThrowableProjectiles.TimedGrenadePickup), "_replaceNextFrame");
        CheckMethod(typeof(InventorySystem.Items.ThrowableProjectiles.TimedGrenadePickup), nameof(InventorySystem.Items.ThrowableProjectiles.TimedGrenadePickup.OnExplosionDetected), new[] { typeof(Footprinting.Footprint), typeof(Vector3), typeof(float) });
        CheckMethod(typeof(PlayerRoles.PlayableScps.Scp079.Cameras.Scp079Camera), "ApplyAxes", new[] { typeof(NetworkReader) });
        CheckMethod(typeof(BreakableWindow), "LateUpdate", Type.EmptyTypes);
        CheckMethod(typeof(MapGeneration.Decoration.EnvironmentalAlarm), "Update", Type.EmptyTypes);
        CheckField(typeof(MapGeneration.Decoration.EnvironmentalAlarm), "_lightRotator");
        CheckMethod(typeof(LiquidWobble), "Update", Type.EmptyTypes);
        CheckMethod(typeof(InventorySystem.Items.Keycards.OneSidedKeycardFlipper), "LateUpdate", Type.EmptyTypes);
        CheckMethod(typeof(Interactables.Interobjects.PopupInterobject), "Update", Type.EmptyTypes);
        CheckMethod(typeof(InventorySystem.Items.Firearms.Ammo.AmmoPickup), "Update", Type.EmptyTypes);

        // Round 3 additional targets (OPT-13/19/20 upgrades)
        CheckField(typeof(NetIdWaypoint), "_pos");
        CheckMethod(typeof(NetIdWaypoint), "SetPosition", Type.EmptyTypes);
        CheckField(typeof(LockerWaypoint), "_moving");
        CheckField(typeof(ElevatorWaypoint), "_elevator");
        CheckProp(typeof(RoomLightController), nameof(RoomLightController.NetworkLightsEnabled));
        CheckMethod(typeof(RoomLightController), nameof(RoomLightController.IsInDarkenedRoom), new[] { typeof(Vector3) });
        CheckMethod(typeof(RoomLightController), nameof(RoomLightController.IsInDarkenedRoom), new[] { typeof(RoomIdentifier), typeof(Vector3) });
        CheckField(typeof(Interactables.Interobjects.ElevatorChamber), "_doorAnimator");
        CheckMethod(typeof(Interactables.Interobjects.ElevatorChamber), "SetInnerDoor", new[] { typeof(bool) });
        CheckField(typeof(MapGeneration.Distributors.Scp079Generator), "_doorAnimator");
        CheckField(typeof(MapGeneration.Distributors.Scp079Generator), "_leverAnimator");
        CheckProp(typeof(MapGeneration.Distributors.Scp079Generator), nameof(MapGeneration.Distributors.Scp079Generator.Network_flags));
        CheckMethod(typeof(InventorySystem.Items.Usables.Scp1576.Scp1576Playback), "LateUpdate", Type.EmptyTypes);
        Type volumeType = Type.GetType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime")
                          ?? AccessTools.TypeByName("UnityEngine.Rendering.Volume");
        if (volumeType == null) missing.Add("Type UnityEngine.Rendering.Volume");
        else CheckMethod(volumeType, "Update", Type.EmptyTypes);

        // OPT-21 Mass Role-Change & Wave Respawn Accelerator
        Type poolType = typeof(GameObjectPools.PoolManager).Assembly.GetType("GameObjectPools.Pool");
        if (poolType == null) missing.Add("Type GameObjectPools.Pool");
        else
        {
            CheckField(poolType, "_pool");
            CheckField(poolType, "parentTransform");
            CheckField(poolType, "Prefab");
            CheckMethod(poolType, "AddNewPrefabToPool", Type.EmptyTypes);
            CheckMethod(poolType, "TryGetPoolableObject", new[] { typeof(GameObjectPools.PoolObject).MakeByRefType() });
            CheckMethod(poolType, "SpawnObject", Type.EmptyTypes);
            CheckMethod(poolType, "ResetObject", new[] { typeof(GameObjectPools.PoolObject) });
        }
        CheckField(typeof(GameObjectPools.PoolManager), "_poolLookup");
        CheckField(typeof(GameObjectPools.PoolManager), "PoolObjectLookup");
        CheckField(typeof(GameObjectPools.PoolObject), "<_myPool>k__BackingField");
        if (poolType != null) CheckMethod(typeof(GameObjectPools.PoolObject), "InitializePoolObject", new[] { poolType });
        CheckField(typeof(PlayerRoleLoader), "_loaded");
        CheckField(typeof(InventoryItemLoader), "_loaded");
        CheckField(typeof(Inventory), "_hub");
        CheckMethod(typeof(Inventory), nameof(Inventory.CreateItemInstance), new[] { typeof(InventorySystem.Items.ItemIdentifier), typeof(bool) });
        CheckProp(typeof(InventorySystem.Items.ItemBase), nameof(InventorySystem.Items.ItemBase.Owner));
        CheckProp(typeof(InventorySystem.Items.ItemBase), nameof(InventorySystem.Items.ItemBase.ItemSerial));
        CheckMethod(typeof(PlayerRoleManager), "SendNewRoleInfo", Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoleManager), nameof(PlayerRoleManager.ServerSetRole), new[] { typeof(RoleTypeId), typeof(RoleChangeReason), typeof(RoleSpawnFlags), typeof(Action<PlayerRoleBase>) });
        CheckMethod(typeof(PlayerRoleManager), nameof(PlayerRoleManager.InitializeNewRole), new[] { typeof(RoleTypeId), typeof(RoleChangeReason), typeof(RoleSpawnFlags), typeof(NetworkReader) });
        CheckMethod(typeof(PlayerRoleBase), nameof(PlayerRoleBase.DisableRole), new[] { typeof(RoleTypeId) });
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Spawnpoints.RoleSpawnpointManager), "SetPosition", new[] { typeof(ReferenceHub), typeof(PlayerRoleBase) });
        CheckMethod(typeof(FirstPersonMovementModule), "SetModel", new[] { typeof(GameObject) });
        CheckMethod(typeof(FirstPersonMovementModule), "OnRoleDisabled", new[] { typeof(RoleTypeId) });
        CheckProp(typeof(FirstPersonMovementModule), "Hub");
        CheckProp(typeof(FirstPersonMovementModule), "Role");
        CheckProp(typeof(FirstPersonMovementModule), nameof(FirstPersonMovementModule.CharacterModelInstance));
        CheckProp(typeof(FirstPersonMovementModule), nameof(FirstPersonMovementModule.CharControllerSet));
        CheckMethod(typeof(InventoryItemProvider), nameof(InventoryItemProvider.ServerGrantLoadout), new[] { typeof(ReferenceHub), typeof(PlayerRoleBase), typeof(bool) });
        CheckMethod(typeof(Inventory), nameof(Inventory.DestroyItemInstance), new[] { typeof(ushort), typeof(ItemPickupBase), typeof(InventorySystem.Items.ItemBase).MakeByRefType() });
        CheckMethod(typeof(CharacterModel), nameof(CharacterModel.Setup), new[] { typeof(ReferenceHub), typeof(IFpcRole), typeof(Vector3), typeof(Quaternion) });
        CheckMethod(typeof(CharacterModel), "PrepareFadeableMaterials", Type.EmptyTypes);
        CheckField(typeof(CharacterModel), "_fadeableMaterials");
        CheckMethod(typeof(AnimatedCharacterModel), nameof(AnimatedCharacterModel.Setup), new[] { typeof(ReferenceHub), typeof(IFpcRole), typeof(Vector3), typeof(Quaternion) });
        CheckMethod(typeof(AnimatedCharacterModel), nameof(AnimatedCharacterModel.ResetObject), Type.EmptyTypes);
        CheckProp(typeof(AnimatedCharacterModel), "Role");
        CheckProp(typeof(AnimatedCharacterModel), "FpcModule");
        CheckField(typeof(AnimatedCharacterModel), "<AnimatorOverride>k__BackingField");
        CheckField(typeof(AnimatedCharacterModel), "_subcontrollers");
        CheckMethod(typeof(AnimatedCharacterModel), "OnGrounded", Type.EmptyTypes);
        CheckMethod(typeof(InventorySystem.Items.Thirdperson.ThirdpersonItemAnimationManager), nameof(InventorySystem.Items.Thirdperson.ThirdpersonItemAnimationManager.ResetOverrides), new[] { typeof(AnimatedCharacterModel), typeof(Dictionary<AnimationClip, AnimationClip>) });
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.EmotionSubcontroller), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.EmotionSubcontroller.ResetObject), Type.EmptyTypes);
        CheckProp(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.DisplayableWearableBase), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.DisplayableWearableBase.Subcontroller));
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor.Initialize), new[] { typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableSubcontroller) });
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor), "SetArmor", new[] { typeof(ItemType) });
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor.UpdateVisibility), Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor.SetFade), new[] { typeof(float) });
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor.ApplySyncvars), new[] { typeof(NetworkReader) });
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableArmor.OnWornStatusChanged), Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableSubcontroller), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableSubcontroller.OnReassigned), Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableSubcontroller), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableSubcontroller.ResetObject), Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableSubcontroller), "SetWearables", new[] { typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableElements) });
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableSubcontroller), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableSubcontroller.ClientReceiveWearables), new[] { typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.WearableElements), typeof(NetworkReader) });
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.SimpleWearable), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.SimpleWearable.UpdateVisibility), Type.EmptyTypes);
        CheckMethod(typeof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.SimpleWearable), nameof(PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables.SimpleWearable.SetFade), new[] { typeof(float) });
        CheckMethod(typeof(VoiceChat.GlobalChatIndicatorManager), nameof(VoiceChat.GlobalChatIndicatorManager.Subscribe));
        CheckMethod(typeof(VoiceChat.GlobalChatIndicatorManager), nameof(VoiceChat.GlobalChatIndicatorManager.Unsubscribe));
        CheckMethod(typeof(VoiceChat.GlobalChatIndicatorManager), "LateUpdate", Type.EmptyTypes);
        CheckMethod(typeof(SyncedStatMessages), "OnRoleChanged", new[] { typeof(ReferenceHub), typeof(PlayerRoleBase), typeof(PlayerRoleBase) });
        CheckMethod(typeof(Respawning.WaveManager), "SyncValues", new[] { typeof(ReferenceHub) });
        if (AccessTools.Constructor(typeof(Respawning.WaveUpdateMessage), new[] { typeof(Respawning.Waves.SpawnableWaveBase), typeof(Respawning.UpdateMessageFlags) }) == null)
            missing.Add("Ctor WaveUpdateMessage(SpawnableWaveBase, UpdateMessageFlags)");

        // OPT-22 Player Join & Async Auth Spike Accelerator
        CheckMethod(typeof(EncryptedChannelManager), "PrepareExchange", Type.EmptyTypes);
        CheckMethod(typeof(EncryptedChannelManager), "ServerProcessExchange", new[] { typeof(string) });
        CheckField(typeof(EncryptedChannelManager), "_exchange");
        CheckField(typeof(EncryptedChannelManager), "<EcdhKeys>k__BackingField");
        CheckField(typeof(EncryptedChannelManager), "EncryptionKey");
        CheckField(typeof(EncryptedChannelManager), "CryptographyDebug");
        CheckMethod(typeof(ECDSA), nameof(ECDSA.KeyToString), new[] { typeof(Org.BouncyCastle.Crypto.AsymmetricKeyParameter) });
        CheckMethod(typeof(PlayerAuthenticationManager), "RequestAuthentication", Type.EmptyTypes);
        CheckMethod(typeof(PlayerAuthenticationManager), "ProcessAuthenticationResponse", new[] { typeof(AuthenticationResponse) });
        CheckMethod(typeof(PlayerAuthenticationManager), "RejectAuthentication", new[] { typeof(string), typeof(string), typeof(bool) });
        CheckMethod(typeof(PlayerAuthenticationManager), "RejectBadgeToken", new[] { typeof(string) });
        CheckMethod(typeof(PlayerAuthenticationManager), "FinalizeAuthentication", Type.EmptyTypes);
        CheckMethod(typeof(PlayerAuthenticationManager), "CheckBans", new[] { typeof(AuthenticationToken), typeof(string) });
        CheckField(typeof(PlayerAuthenticationManager), "_authenticationRequested");
        CheckField(typeof(PlayerAuthenticationManager), "_challenge");
        CheckField(typeof(PlayerAuthenticationManager), "_timeoutTimer");
        CheckField(typeof(PlayerAuthenticationManager), "_hub");
        CheckField(typeof(PlayerAuthenticationManager), "<AuthenticationResponse>k__BackingField");
        CheckField(typeof(PlayerAuthenticationManager), "_privUserId");
        CheckField(typeof(PlayerAuthenticationManager), "_targetInstanceMode");
        CheckField(typeof(PlayerAuthenticationManager), "<DoNotTrack>k__BackingField");
        CheckMethod(typeof(PlayerAuthenticationManager), "RefreshSyncedId", Type.EmptyTypes);
        CheckField(typeof(PlayerAuthenticationManager), "AllowSameAccountJoining");
        CheckField(typeof(PlayerAuthenticationManager), nameof(PlayerAuthenticationManager.OnSyncedUserIdAssigned));
        CheckField(typeof(PlayerAuthenticationManager), nameof(PlayerAuthenticationManager.OnInstanceModeChanged));
        CheckField(typeof(PlayerEvents), nameof(PlayerEvents.Joined));
        CheckMethod(typeof(PlayerEvents), nameof(PlayerEvents.OnJoined), new[] { typeof(PlayerJoinedEventArgs) });
        CheckProp(typeof(AuthenticationResponse), nameof(AuthenticationResponse.AuthToken));
        CheckProp(typeof(AuthenticationResponse), nameof(AuthenticationResponse.BadgeToken));
        CheckMethod(typeof(ServerRoles), "Update", Type.EmptyTypes);
        CheckField(typeof(ServerRoles), "_hub");
        CheckField(typeof(ServerRoles), "_prevBadge");
        CheckField(typeof(ServerRoles), "_bgc");
        CheckField(typeof(ServerRoles), "_bgt");
        CheckField(typeof(ServerRoles), "_authorizeBadge");
        CheckField(typeof(ServerRoles), "<MyText>k__BackingField");
        CheckField(typeof(ServerRoles), "BadgeCover");
        CheckField(typeof(ServerRoles), "GlobalPerms");
        CheckMethod(typeof(BanHandler), nameof(BanHandler.QueryBan), new[] { typeof(string), typeof(string) });
        CheckMethod(typeof(BanHandler), nameof(BanHandler.IssueBan), new[] { typeof(BanDetails), typeof(BanHandler.BanType), typeof(bool) });
        CheckMethod(typeof(BanHandler), nameof(BanHandler.RemoveBan), new[] { typeof(string), typeof(BanHandler.BanType), typeof(bool) });
        CheckMethod(typeof(BanHandler), nameof(BanHandler.ValidateBans), new[] { typeof(BanHandler.BanType) });
        CheckMethod(typeof(BanHandler), nameof(BanHandler.Init), Type.EmptyTypes);
        CheckMethod(typeof(NicknameSync), "Start", Type.EmptyTypes);
        CheckMethod(typeof(NicknameSync), "SetNick", new[] { typeof(string) });
        CheckField(typeof(NicknameSync), "_hub");
        CheckField(typeof(NicknameSync), "_nickFilter");
        CheckField(typeof(NicknameSync), "_replacement");
        CheckMethod(typeof(NetworkServer), "SendSpawnMessage", new[] { typeof(NetworkIdentity), typeof(NetworkConnection) });
        CheckMethod(typeof(NetworkServer), "SpawnObserversForConnection", new[] { typeof(NetworkConnectionToClient) });
        CheckMethod(typeof(NetworkManager), nameof(NetworkManager.OnServerAddPlayer), new[] { typeof(NetworkConnectionToClient) });
        CheckMethod(typeof(NetworkIdentity), "ValidateComponents", Type.EmptyTypes);
        CheckMethod(typeof(NetworkBehaviour), "Serialize", new[] { typeof(NetworkWriter), typeof(bool) });

        Assert(missing.Count == 0, $"Missing {missing.Count} reflection targets:\n       - " + string.Join("\n       - ", missing));
    }

    private static void TestStatusEffectSleepWhitelist()
    {
        Assembly asm = typeof(StatusEffectBase).Assembly;
        Type[] effectTypes = GetLoadableTypes(asm)
            .Where(t => !t.IsAbstract && typeof(StatusEffectBase).IsAssignableFrom(t))
            .ToArray();

        Assert(effectTypes.Length >= 30, $"Expected >=30 StatusEffectBase subclasses, got {effectTypes.Length}");

        var mustNotSleep = new HashSet<string>
        {
            "Flashed", "OrangeCandy", "Prismatic",
            "Scp1344Detected", "Scp1853", "SugarHigh", "SugarRush", "WhiteCandy"
        };

        var verifiedSleepableOverrides = new HashSet<string> { "Fade", "AmnesiaItems" };

        int sleepableCount = 0;
        foreach (Type t in effectTypes)
        {
            bool sleepable = StatusEffectSleepModule.IsTypeSleepable(t);
            if (mustNotSleep.Contains(t.Name))
            {
                Assert(!sleepable, $"Effect {t.Name} overrides Update/LateUpdate and MUST NOT sleep!");
            }

            if (verifiedSleepableOverrides.Contains(t.Name))
            {
                Assert(sleepable, $"Effect {t.Name} is explicitly verified idle-safe and should sleep!");
            }

            if (sleepable)
            {
                sleepableCount++;
            }
        }

        Assert(sleepableCount >= 25, $"Expected >=25 sleepable standard effects, got {sleepableCount}");
    }

    private static void TestAdminToySleepWhitelist()
    {
        Assembly asm = typeof(AdminToyBase).Assembly;
        Type[] toyTypes = GetLoadableTypes(asm)
            .Where(t => !t.IsAbstract && typeof(AdminToyBase).IsAssignableFrom(t))
            .ToArray();

        Assert(toyTypes.Length >= 5, $"Expected >=5 AdminToyBase subclasses, got {toyTypes.Length}");
        foreach (Type t in toyTypes)
        {
            bool sleepable = AdminToyPickupHazardModule.IsToyTypeSleepable(t);
            if (t.Name == "WaypointToy")
            {
                Assert(!sleepable, "WaypointToy overrides LateUpdate and MUST NOT sleep!");
            }
            else if (t.Name == "PrimitiveObjectToy" || t.Name == "LightSourceToy" || t.Name == "TextToy")
            {
                Assert(sleepable, $"Standard toy {t.Name} should be sleepable when static");
            }
        }
    }

    private static void TestFpcSyncDataEquivalence()
    {
        Assert(FpcSyncDataFactory.IsSupported, "FpcSyncDataFactory must be supported");

        RelativePosition pos1 = new RelativePosition(3, 100, -200, 350, false);
        RelativePosition pos2 = new RelativePosition(3, 125, -200, 380, false);

        // Case 1: First packet from default prev -> both pos & rot changed (12 bytes)
        FpcSyncData prev0 = default;
        FpcSyncData s1 = FpcSyncDataFactory.Build(in prev0, PlayerMovementState.Walking, true, in pos1, 12345, 32768);
        VerifyFpcWireRoundTrip(s1, expectPos: true, expectRot: true, expectCustom: true, PlayerMovementState.Walking);

        // Case 2: Stationary next tick -> pos & rot unchanged (ONLY 1 byte written!)
        FpcSyncData s2 = FpcSyncDataFactory.Build(in s1, PlayerMovementState.Walking, true, in pos1, 12345, 32768);
        VerifyFpcWireRoundTrip(s2, expectPos: false, expectRot: false, expectCustom: true, PlayerMovementState.Walking);

        // Case 3: Position changed only
        FpcSyncData s3 = FpcSyncDataFactory.Build(in s2, PlayerMovementState.Sprinting, true, in pos2, 12345, 32768);
        VerifyFpcWireRoundTrip(s3, expectPos: true, expectRot: false, expectCustom: true, PlayerMovementState.Sprinting);

        // Case 4: Rotation changed only + airborne (customBit = false)
        FpcSyncData s4 = FpcSyncDataFactory.Build(in s3, PlayerMovementState.Crouching, false, in pos2, 54321, 11111);
        VerifyFpcWireRoundTrip(s4, expectPos: false, expectRot: true, expectCustom: false, PlayerMovementState.Crouching);

        // Case 5: Verify equality between two identically built structs
        FpcSyncData s4Copy = FpcSyncDataFactory.Build(in s3, PlayerMovementState.Crouching, false, in pos2, 54321, 11111);
        Assert(s4.Equals(s4Copy), "Identically constructed FpcSyncData must be equal");
    }

    private static void VerifyFpcWireRoundTrip(
        FpcSyncData data,
        bool expectPos,
        bool expectRot,
        bool expectCustom,
        PlayerMovementState expectedState)
    {
        var writer = new NetworkWriter();
        data.Write(writer);

        int expectedBytes = 1 + (expectPos ? 7 : 0) + (expectRot ? 4 : 0);
        Assert(writer.Position == expectedBytes, $"Expected {expectedBytes} wire bytes, wrote {writer.Position}");

        var reader = new NetworkReader(writer.ToArraySegment());
        FpcSyncData decoded = new FpcSyncData(reader);
        Assert(reader.Remaining == 0, $"Reader had {reader.Remaining} trailing bytes");

        // Verify exact decoded flag bits and state; when both pos & rot were transmitted, full struct equality holds.
        Assert((bool)AccessTools.Field(typeof(FpcSyncData), "_bitPosition").GetValue(decoded) == expectPos, "_bitPosition mismatch");
        Assert((bool)AccessTools.Field(typeof(FpcSyncData), "_bitMouseLook").GetValue(decoded) == expectRot, "_bitMouseLook mismatch");
        Assert((bool)AccessTools.Field(typeof(FpcSyncData), "_bitCustom").GetValue(decoded) == expectCustom, "_bitCustom mismatch");
        Assert((PlayerMovementState)AccessTools.Field(typeof(FpcSyncData), "_state").GetValue(decoded) == expectedState, "_state mismatch");
        if (expectPos && expectRot)
        {
            Assert(decoded.Equals(data), "Full-update decoded FpcSyncData must equal encoded FpcSyncData");
        }

        // Re-serialize decoded and assert byte-for-byte wire identity
        var writer2 = new NetworkWriter();
        decoded.Write(writer2);
        byte[] b1 = writer.ToArray();
        byte[] b2 = writer2.ToArray();
        Assert(b1.SequenceEqual(b2), "Re-serialized bytes differ from original");
    }

    private static void TestMirrorSinglePackEquivalence()
    {
        // Initialize Mirror's generated Read/Write delegates (normally invoked by Unity RuntimeInitializeOnLoadMethod).
        AccessTools.Method(typeof(NetworkServer).Assembly.GetType("Mirror.GeneratedNetworkCode"), "InitReadWriters")?.Invoke(null, null);

        byte[] payload = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0xAA, 0xBB, 0xCC };
        var rpc = new RpcMessage
        {
            netId = 42,
            componentIndex = 3,
            functionHash = 0x1234,
            payload = new ArraySegment<byte>(payload)
        };

        var singleWriter = new NetworkWriter();
        NetworkMessages.Pack(rpc, singleWriter);
        byte[] singlePacked = singleWriter.ToArray();

        for (int i = 0; i < 60; i++)
        {
            var w = new NetworkWriter();
            NetworkMessages.Pack(rpc, w);
            Assert(singlePacked.SequenceEqual(w.ToArray()), $"Receiver {i} RPC bytes mismatch");
        }

        var state = new EntityStateMessage
        {
            netId = 99,
            payload = new ArraySegment<byte>(payload)
        };
        var stateWriter = new NetworkWriter();
        NetworkMessages.Pack(state, stateWriter);
        byte[] singleState = stateWriter.ToArray();

        var stateWriter2 = new NetworkWriter();
        NetworkMessages.Pack(state, stateWriter2);
        Assert(singleState.SequenceEqual(stateWriter2.ToArray()), "EntityStateMessage pack mismatch");
    }

    private static void TestDelegateInvocationCache()
    {
        // Null check
        Assert(DelegateInvocationCache.GetInvocationListCached(null).Length == 0, "Null delegate should return empty array");

        var trace = new List<int>();
        LabEventHandler handler = () => trace.Add(1);
        handler += () => throw new InvalidOperationException("intentional test exception");
        handler += () => trace.Add(3);

        Delegate[] list1 = DelegateInvocationCache.GetInvocationListCached(handler);
        Delegate[] list2 = DelegateInvocationCache.GetInvocationListCached(handler);
        Assert(ReferenceEquals(list1, list2), "Same immutable Delegate reference must return cached array instance");
        Assert(list1.Length == 3, "Expected 3 handlers");

        // Exception isolation equivalence
        int caught = 0;
        for (int i = 0; i < list1.Length; i++)
        {
            try
            {
                ((LabEventHandler)list1[i])();
            }
            catch (InvalidOperationException)
            {
                caught++;
            }
        }

        Assert(caught == 1 && trace.SequenceEqual(new[] { 1, 3 }), "Exception isolation failed");

        // Dynamic subscribe invalidates cleanly (new Delegate reference -> new cached array)
        LabEventHandler added = () => trace.Add(4);
        handler += added;
        Delegate[] list3 = DelegateInvocationCache.GetInvocationListCached(handler);
        Assert(!ReferenceEquals(list1, list3) && list3.Length == 4, "Subscribed delegate must produce new invocation array");

        // Unsubscribe
        handler -= added;
        Delegate[] list4 = DelegateInvocationCache.GetInvocationListCached(handler);
        Assert(list4.Length == 3, "Unsubscribed delegate must reflect removal");

        // Steady-state zero allocation check
        _ = DelegateInvocationCache.GetInvocationListCached(handler);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;
        for (int i = 0; i < 50_000; i++)
        {
            sum += DelegateInvocationCache.GetInvocationListCached(handler).Length;
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert(sum == 150_000, "Unexpected sum");
        Assert(after - before == 0, $"Expected 0 bytes allocated in steady state, got {after - before} bytes");
    }

    private static void TestMirrorBatcherZeroCopyEquivalence()
    {
        var batcherVanilla = new Batcher(1200);
        var batcherZeroCopy = new Batcher(1200);

        byte[][] msgs =
        {
            new byte[] { 1, 2, 3, 4, 5 },
            new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 },
            new byte[] { 99 }
        };

        for (int i = 0; i < msgs.Length; i++)
        {
            batcherVanilla.AddMessage(new ArraySegment<byte>(msgs[i]), 123.456);
            batcherZeroCopy.AddMessage(new ArraySegment<byte>(msgs[i]), 123.456);
        }

        // Vanilla path: GetBatch(writer)
        var outWriter = new NetworkWriter();
        Assert(batcherVanilla.GetBatch(outWriter), "Vanilla batcher should return a batch");
        byte[] vanillaBytes = outWriter.ToArray();

        // Zero-copy path: directly take internal current batch buffer without CopyAndReturn
        FieldInfo batchField = AccessTools.Field(typeof(Batcher), "batch");
        NetworkWriterPooled internalBatch = (NetworkWriterPooled)batchField.GetValue(batcherZeroCopy);
        Assert(internalBatch != null, "Internal batch must exist");
        byte[] zeroCopyBytes = internalBatch.ToArraySegment().ToArray();
        NetworkWriterPool.Return(internalBatch);

        Assert(vanillaBytes.SequenceEqual(zeroCopyBytes), "Zero-copy batch bytes differ from vanilla GetBatch bytes");

        // Verify in-place Unbatcher parsing produces exact same messages and timestamp as Mirror.Unbatcher
        var mirrorUnbatcher = new Unbatcher();
        Assert(mirrorUnbatcher.AddBatch(new ArraySegment<byte>(vanillaBytes)), "Mirror Unbatcher failed to add batch");

        var directReader = new NetworkReader(new ArraySegment<byte>(zeroCopyBytes));
        double directTimestamp = directReader.ReadDouble();

        int idx = 0;
        while (directReader.Remaining > 0)
        {
            int msgSize = (int)Compression.DecompressVarUInt(directReader);
            ArraySegment<byte> directSlice = directReader.ReadBytesSegment(msgSize);

            Assert(mirrorUnbatcher.GetNextMessage(out ArraySegment<byte> mirrorSlice, out double mirrorTs), "Mirror Unbatcher ran out of messages early");
            Assert(Math.Abs(mirrorTs - directTimestamp) < 1e-9, "Timestamps differ");
            Assert(mirrorSlice.SequenceEqual(directSlice), $"Message {idx} slice mismatch");
            Assert(directSlice.SequenceEqual(msgs[idx]), $"Message {idx} content mismatch");
            idx++;
        }

        Assert(idx == msgs.Length, $"Expected {msgs.Length} messages, decoded {idx}");
        Assert(!mirrorUnbatcher.GetNextMessage(out _, out _), "Mirror Unbatcher had unexpected trailing message");
    }

    private static void TestLiteNetPeerKeys()
    {
        // IPv4 test: 203.0.113.42:7777
        IPAddress ip4 = IPAddress.Parse("203.0.113.42");
        ushort port = 7777;
        Assert(LiteNetPeerLookupModule.TryBuildV4Key(ip4, port, out ulong k4FromIp), "TryBuildV4Key failed");

        byte[] sockAddr4 = new byte[16];
        sockAddr4[0] = 2; // AF_INET
        sockAddr4[2] = (byte)(port >> 8);
        sockAddr4[3] = (byte)(port & 0xFF);
        sockAddr4[4] = 203;
        sockAddr4[5] = 0;
        sockAddr4[6] = 113;
        sockAddr4[7] = 42;
        uint raw4 = (uint)(sockAddr4[4] | (sockAddr4[5] << 8) | (sockAddr4[6] << 16) | (sockAddr4[7] << 24));
        ushort decodedPort = (ushort)((sockAddr4[2] << 8) | sockAddr4[3]);
        ulong k4FromSock = LiteNetPeerLookupModule.PackV4Key(raw4, decodedPort);
        Assert(k4FromIp == k4FromSock, $"IPv4 key mismatch: {k4FromIp:X} vs {k4FromSock:X}");

        // IPv6 test: 2001:db8::abcd:1234:5678, scope 7, port 44321
        IPAddress ip6 = IPAddress.Parse("2001:db8::abcd:1234:5678");
        ip6.ScopeId = 7;
        ushort port6 = 44321;
        Assert(LiteNetPeerLookupModule.TryBuildV6Key(ip6, port6, out LiteNetPeerLookupModule.Ipv6Key k6FromIp), "TryBuildV6Key failed");

        byte[] sockAddr6 = new byte[28];
        byte[] raw16 = ip6.GetAddressBytes();
        Buffer.BlockCopy(raw16, 0, sockAddr6, 8, 16);
        sockAddr6[24] = 7; // scope LE
        LiteNetPeerLookupModule.Ipv6Key k6FromSock = LiteNetPeerLookupModule.PackV6KeyFromSockAddr(sockAddr6, 7u, port6);
        Assert(k6FromIp.Equals(k6FromSock) && k6FromIp.GetHashCode() == k6FromSock.GetHashCode(), "IPv6 key/hash mismatch");
    }

    private static void TestFrameMetricsRing()
    {
        var ring = new FrameMetricsRing();
        // Push 3000 samples (wraps around 2048 ring buffer cleanly): 2000 frames of 10ms, then 48 frames of 25ms
        for (int i = 0; i < 952; i++) ring.Record(1.0, 0.5); // will be overwritten
        for (int i = 0; i < 2000; i++) ring.Record(10.0, 4.0);
        for (int i = 0; i < 48; i++) ring.Record(25.0, 12.0);

        FrameMetricsSnapshot snap = ring.ComputeSnapshot();
        Assert(snap.SampleCount == 2048, $"Expected 2048 samples, got {snap.SampleCount}");
        Assert(snap.SpikesOver16Ms == 48, $"Expected 48 spikes >16ms, got {snap.SpikesOver16Ms}");
        Assert(Math.Abs(snap.P50Ms - 10.0) < 0.01, $"Expected P50=10ms, got {snap.P50Ms}");
        Assert(Math.Abs(snap.P95Ms - 10.0) < 0.01, $"Expected P95=10ms, got {snap.P95Ms}");
        Assert(Math.Abs(snap.P99Ms - 25.0) < 0.01, $"Expected P99=25ms, got {snap.P99Ms}");
        Assert(Math.Abs(snap.MaxFrameMs - 25.0) < 0.01, $"Expected Max=25ms, got {snap.MaxFrameMs}");
    }

    private static void TestCompactUintMap()
    {
        var map = new CompactUintMap<int>(8);
        for (uint i = 1; i <= 50; i++)
        {
            map.Set(i * 10, (int)(i * 100));
        }

        Assert(map.Count == 50, $"Expected 50 items, got {map.Count}");
        for (uint i = 1; i <= 50; i++)
        {
            Assert(map.TryGetValue(i * 10, out int v) && v == (int)(i * 100), $"Lookup failed for {i * 10}");
        }

        // Overwrite & swap-remove
        map.Set(20, 9999);
        Assert(map.TryGetValue(20, out int ov) && ov == 9999, "Overwrite failed");
        Assert(map.Remove(10) && !map.TryGetValue(10, out _), "Remove first element failed");
        Assert(map.Remove(500) && !map.TryGetValue(500, out _), "Remove last element failed");
        Assert(map.Count == 48, $"Expected 48 items after removals, got {map.Count}");
        Assert(map.TryGetValue(20, out int ov2) && ov2 == 9999, "Remaining element corrupted after swap-remove");
        map.Clear();
        Assert(map.Count == 0, "Clear failed");
    }

    private static void TestBoundedWorkerPool()
    {
        using (var pool = new BoundedWorkerPool(4, "SLPerf-TestPool"))
        {
            int[] counts = new int[257];
            for (int run = 0; run < 200; run++)
            {
                Array.Clear(counts, 0, counts.Length);
                pool.ExecuteParallelFor(counts.Length, (workerIdx, start, end) =>
                {
                    for (int i = start; i < end; i++)
                    {
                        Interlocked.Increment(ref counts[i]);
                    }
                });

                for (int i = 0; i < counts.Length; i++)
                {
                    Assert(counts[i] == 1, $"Run {run} item {i} processed {counts[i]} times (expected 1)");
                }
            }

            // Verify deterministic exception propagation
            bool threw = false;
            try
            {
                pool.ExecuteParallelFor(64, (workerIdx, start, end) =>
                {
                    if (start <= 33 && end > 33)
                    {
                        throw new InvalidOperationException("worker failure");
                    }
                });
            }
            catch (AggregateException agg)
            {
                threw = agg.InnerException is InvalidOperationException;
            }

            Assert(threw, "Expected AggregateException wrapping worker InvalidOperationException");
        }
    }

    private static void TestProfilerProbeTargetsAndAccounting()
    {
        var targets = ProfilerProbeHooks.ResolveTargets();
        Assert(targets.Count == 30, $"Expected 30 breakdown probe targets, got {targets.Count}");

        var missing = new List<string>();
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (t.Target == null || t.Postfix == null)
            {
                missing.Add($"Target[{i}] ProbeId={t.Id} Target={(t.Target != null ? t.Target.Name : "<null>")} Postfix={(t.Postfix != null ? t.Postfix.Name : "<null>")}");
            }
        }

        Assert(missing.Count == 0, "Missing profiler probe targets:\n       - " + string.Join("\n       - ", missing));

        // Verify zero-conflict guarantee: none of the breakdown probes target FpcServerPositionDistributor.WriteAll or FpcMotor.Move
        foreach (var t in targets)
        {
            string full = $"{t.Target.DeclaringType?.FullName}.{t.Target.Name}";
            Assert(!full.EndsWith("FpcServerPositionDistributor.WriteAll", StringComparison.Ordinal), "Probe must never hook WriteAll");
            Assert(!full.EndsWith("FpcMotor.Move", StringComparison.Ordinal), "Probe must never hook FpcMotor.Move");
        }

        // Verify MethodSampleTable accounting (calls, ticks, sampled frames, allocations)
        MethodSampleTable.Reset();
        MethodSampleTable.Enabled = true;
        try
        {
            MethodSampleTable.IncrementFrame();
            MethodSampleTable.IncrementFrame();
            var tok = MethodSampleTable.BeginSample();
            byte[] scratch = new byte[65536];
            scratch[0] = 1;
            MethodSampleTable.EndSample(ProbeId.CullingLateUpdate, in tok);

            var slot = MethodSampleTable.ReadSlot(ProbeId.CullingLateUpdate);
            Assert(MethodSampleTable.SampledFrames == 2, $"Expected 2 sampled frames, got {MethodSampleTable.SampledFrames}");
            Assert(slot.Calls == 1, $"Expected 1 call, got {slot.Calls}");
            Assert(slot.TotalTicks >= 0, "TotalTicks must be non-negative");
        }
        finally
        {
            MethodSampleTable.Enabled = false;
            MethodSampleTable.Reset();
        }
    }

    private static void TestOpt21MassRoleChangeAccessors()
    {
        Assert(MassRoleChangeAccelModule.CompileAccessors(out string err), $"OPT-21 CompileAccessors failed: {err}");

        // Exercise compiled Pool._pool DynamicMethod against a real GameObjectPools.Pool instance
        Type poolType = typeof(GameObjectPools.PoolManager).Assembly.GetType("GameObjectPools.Pool");
        Assert(poolType != null, "GameObjectPools.Pool type not found");
        object samplePool = Activator.CreateInstance(poolType, nonPublic: true);
        Assert(MassRoleChangeAccelModule.TestVerifyCompiledDelegates(samplePool, out Stack<GameObjectPools.PoolObject> stack),
            "One or more OPT-21 compiled DynamicMethod delegates were null");
        Assert(stack != null && stack.Count == 0, "Expected non-null empty Stack<PoolObject> from GameObjectPools.Pool._pool");

        // Verify RoleSyncInfo pack-once wire equivalence across distinct non-owner receiverNetIds
        Writer<RoleSyncInfo>.write = (w, m) => m.Write(w);
        object boxed1 = default(RoleSyncInfo);
        AccessTools.Field(typeof(RoleSyncInfo), "_targetNetId").SetValue(boxed1, 42u);
        AccessTools.Field(typeof(RoleSyncInfo), "_receiverNetId").SetValue(boxed1, 100u);
        AccessTools.Field(typeof(RoleSyncInfo), "_targetRole").SetValue(boxed1, RoleTypeId.ClassD);
        RoleSyncInfo msg1 = (RoleSyncInfo)boxed1;

        object boxed2 = default(RoleSyncInfo);
        AccessTools.Field(typeof(RoleSyncInfo), "_targetNetId").SetValue(boxed2, 42u);
        AccessTools.Field(typeof(RoleSyncInfo), "_receiverNetId").SetValue(boxed2, 200u);
        AccessTools.Field(typeof(RoleSyncInfo), "_targetRole").SetValue(boxed2, RoleTypeId.ClassD);
        RoleSyncInfo msg2 = (RoleSyncInfo)boxed2;

        var w1 = new NetworkWriter();
        var w2 = new NetworkWriter();
        NetworkMessages.Pack(msg1, w1);
        NetworkMessages.Pack(msg2, w2);
        Assert(w1.Position > 0 && w1.ToArray().SequenceEqual(w2.ToArray()),
            "RoleSyncInfo pack-once wire bytes differed between distinct non-owner receivers");
    }

    private static void TestOpt22PlayerJoinAccel()
    {
        Assert(PlayerJoinAccelModule.EnsureAccessorsCompiled(), "OPT-22 EnsureAccessorsCompiled returned false");

        // 1. Verify BouncyCastle NIST P-384 ECDSA / ECDH parity + pure verifier equivalence
        var sw = Stopwatch.StartNew();
        var serverEcdhKeys = ECDH.GenerateKeys();
        double keygenMs = sw.Elapsed.TotalMilliseconds;

        var clientEcdhKeys = ECDH.GenerateKeys();
        string clientEcdhPem = ECDSA.KeyToString(clientEcdhKeys.Public);
        string serverEcdhPem = ECDSA.KeyToString(serverEcdhKeys.Public);

        sw.Restart();
        var parsedRemotePub = ECDSA.PublicKeyFromString(clientEcdhPem);
        double pemParseMs = sw.Elapsed.TotalMilliseconds;

        var serverAgreement = ECDH.Init(serverEcdhKeys);
        var clientAgreement = ECDH.Init(clientEcdhKeys);
        sw.Restart();
        byte[] serverSecret = ECDH.DeriveKey(serverAgreement, parsedRemotePub);
        double deriveMs = sw.Elapsed.TotalMilliseconds;
        byte[] clientSecret = ECDH.DeriveKey(clientAgreement, ECDSA.PublicKeyFromString(serverEcdhPem));
        Assert(serverSecret.Length == 32 && serverSecret.SequenceEqual(clientSecret),
            "ECDH P-384 derived AES-256 keys did not match between server and client");

        var signingKeys = ECDSA.GenerateKeys();
        string payload = "SLPerformance-Auth-Challenge-Payload-Test-1234567890";
        byte[] sig = ECDSA.SignBytes(payload, signingKeys.Private);
        Assert(sig != null && sig.Length > 0, "ECDSA.SignBytes returned empty signature");

        sw.Restart();
        bool pureOk = PlayerJoinAccelModule.VerifySignaturePure(payload, sig, signingKeys.Public, out Exception pureErr);
        double verifyMs = sw.Elapsed.TotalMilliseconds;
        bool vanillaOk = ECDSA.VerifyBytes(payload, sig, signingKeys.Public);
        Assert(pureOk && vanillaOk && pureErr == null, "VerifySignaturePure did not match vanilla ECDSA.VerifyBytes on valid signature");

        bool pureTampered = PlayerJoinAccelModule.VerifySignaturePure(payload + "x", sig, signingKeys.Public, out _);
        Assert(!pureTampered, "VerifySignaturePure accepted tampered payload");

        Console.WriteLine($"       [Info] .NET 8 P-384 timings: KeyGen={keygenMs:F2}ms | PemParse={pemParseMs:F2}ms | Verify={verifyMs:F2}ms | Derive={deriveMs:F2}ms (Mono server is typically 3-6x slower)");

        // 2. Verify BanCacheBucket lookup, @steam normalization, first-match parity, and mtime invalidation
        string tmpDir = Path.Combine(Path.GetTempPath(), "slperf_opt22_bantest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        string banFile = Path.Combine(tmpDir, "UserIdBans.txt");
        try
        {
            long futureTicks = DateTime.UtcNow.AddDays(7).Ticks;
            long issuedTicks = DateTime.UtcNow.Ticks;
            File.WriteAllLines(banFile, new[]
            {
                "malformed_line_without_semicolons",
                $"PlayerOne;76561198000000001;{futureTicks};FirstReason;Admin;{issuedTicks}",
                $"PlayerOneDup;76561198000000001@steam;{futureTicks};SecondReason;Admin;{issuedTicks}",
                $"PlayerTwo;76561198000000002@steam;{futureTicks};TwoReason;Admin;{issuedTicks}"
            });

            var bucket = new PlayerJoinAccelModule.BanCacheBucket(BanHandler.BanType.UserId);
            BanDetails hit1 = PlayerJoinAccelModule.LookupBanInBucket(bucket, banFile, "76561198000000001@steam");
            Assert(hit1 != null && hit1.Reason == "FirstReason", "BanCache failed first-match or @steam normalization parity");

            BanDetails hit2 = PlayerJoinAccelModule.LookupBanInBucket(bucket, banFile, "76561198000000002@steam");
            Assert(hit2 != null && hit2.Reason == "TwoReason", "BanCache failed lookup for second entry");

            BanDetails miss = PlayerJoinAccelModule.LookupBanInBucket(bucket, banFile, "76561198999999999@steam");
            Assert(miss == null, "BanCache returned non-null for unbanned user");

            // Modify file + force reload (simulates IssueBan/RemoveBan Postfix invalidation)
            File.WriteAllLines(banFile, new[]
            {
                $"PlayerThree;76561198000000003@steam;{futureTicks};NewReason;Admin;{issuedTicks}"
            });
            Interlocked.Exchange(ref bucket.ForceReload, 1);

            BanDetails afterRemove = PlayerJoinAccelModule.LookupBanInBucket(bucket, banFile, "76561198000000001@steam");
            BanDetails afterAdd = PlayerJoinAccelModule.LookupBanInBucket(bucket, banFile, "76561198000000003@steam");
            Assert(afterRemove == null && afterAdd != null && afterAdd.Reason == "NewReason",
                "BanCache invalidation did not reflect updated ban file");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }
}
