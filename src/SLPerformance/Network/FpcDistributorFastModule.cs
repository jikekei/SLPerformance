using System;
using System.Collections.Generic;
using System.Reflection;
using CentralAuth;
using HarmonyLib;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using Mirror;
using NetworkManagerUtils.Dummies;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using PlayerRoles.PlayableScps.Scp079;
using PlayerRoles.Visibility;
using RelativePositioning;
using SLPerformance.Collections;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using SLPerformance.Profiler;
using SLPerformance.Serialization;
using SLPerformance.Threading;
using UnityEngine;

namespace SLPerformance.Network;

/// <summary>
/// OPT-02: Optimizes FpcServerPositionDistributor O(P^2) synchronization:
/// - O(P) per-tick player snapshot (eliminates 60x redundant MouseLook.GetSyncValues, IsGrounded, Zone, Permissions)
/// - Single ValidateVisibility call per pair (reuses raw result for GetVisibleRole)
/// - Short-circuits GetVisibleRole dead Vector3.Distance / permission / voice work when target is visible
/// - Lazy PlayerValidatedVisibilityEventArgs construction only when LabAPI event has subscribers
/// - Eliminates per-pair NetworkWriterPool rent/return when unused
/// - Single outer PreviouslySent dictionary lookup per WriteAll + fixes disconnect memory leak
/// </summary>
public sealed class FpcDistributorFastModule : OptimizationModuleBase
{
    public override string Id => "net.fpc_distributor_fast";
    public override string Name => "FPC Position Distributor O(P) Snapshot & Zero-Waste Sync";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Precomputes O(P) FPC snapshots, deduplicates visibility/role checks, skips unused EventArgs/WriterPool allocations, and fixes PreviouslySent leak.";

    private static readonly FpcTickSnapshotBuffer Snapshot = new FpcTickSnapshotBuffer();

    private struct CoexistTargetFpc
    {
        public PlayerMovementState State;
        public bool IsGrounded;
        public RelativePosition RelPos;
        public ushort SyncH;
        public ushort SyncV;
    }

    private static readonly CompactUintMap<CoexistTargetFpc> CoexistTargetCache = new CompactUintMap<CoexistTargetFpc>(64);
    private static int _coexistCacheFrame = -1;

    private static Func<Dictionary<uint, Dictionary<uint, FpcSyncData>>> _getPreviouslySent;
    private static Func<Func<ReferenceHub, ReferenceHub, RoleTypeId, NetworkWriter, RoleTypeId>> _getRoleSyncEvent;
    private static Func<int[]> _getBufferPlayerIds;
    private static Func<FpcSyncData[]> _getBufferSyncData;
    private static Func<float> _getSendCooldown;
    private static Action<float> _setSendCooldown;
    private static Func<bool> _hasValidatedVisibilitySubscribers;
    private static MethodInfo _sendRoleMethod;

    internal static FpcParallelEncoder ParallelEncoder { get; set; }
    private static SLPerformanceConfig _activeConfig;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.NetFpcDistributorFast;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        if (!FpcSyncDataFactory.IsSupported)
        {
            return MarkIncompatible("FpcSyncData private field layout could not be compiled.");
        }

        Type distType = typeof(FpcServerPositionDistributor);
        MethodInfo lateUpdate = AccessTools.Method(distType, "LateUpdate", Type.EmptyTypes);
        MethodInfo writeAll = AccessTools.Method(distType, "WriteAll", new[] { typeof(ReferenceHub), typeof(NetworkWriter) });
        MethodInfo getVisibleRole = AccessTools.Method(distType, "GetVisibleRole", new[] { typeof(ReferenceHub), typeof(ReferenceHub) });
        _sendRoleMethod = AccessTools.Method(distType, "SendRole", new[] { typeof(ReferenceHub), typeof(ReferenceHub), typeof(RoleTypeId), typeof(NetworkWriter) });

        _getPreviouslySent = ReflectionHelpers.CreateStaticFieldGetter<Dictionary<uint, Dictionary<uint, FpcSyncData>>>(distType, "PreviouslySent");
        _getRoleSyncEvent = ReflectionHelpers.CreateStaticFieldGetter<Func<ReferenceHub, ReferenceHub, RoleTypeId, NetworkWriter, RoleTypeId>>(distType, "_roleSyncEvent");
        _getBufferPlayerIds = ReflectionHelpers.CreateStaticFieldGetter<int[]>(distType, "_bufferPlayerIDs");
        _getBufferSyncData = ReflectionHelpers.CreateStaticFieldGetter<FpcSyncData[]>(distType, "_bufferSyncData");
        _getSendCooldown = ReflectionHelpers.CreateStaticFieldGetter<float>(distType, "_sendCooldown");
        _setSendCooldown = ReflectionHelpers.CreateStaticFieldSetter<float>(distType, "_sendCooldown");
        _hasValidatedVisibilitySubscribers = ReflectionHelpers.CreateStaticEventHasSubscribersGetter(typeof(PlayerEvents), "ValidatedVisibility");

        if (lateUpdate == null || writeAll == null || getVisibleRole == null || _sendRoleMethod == null ||
            _getPreviouslySent == null || _getRoleSyncEvent == null || _getBufferPlayerIds == null ||
            _getBufferSyncData == null || _getSendCooldown == null || _setSendCooldown == null ||
            _hasValidatedVisibilitySubscribers == null)
        {
            return MarkIncompatible("Required FpcServerPositionDistributor or PlayerEvents members were not found.");
        }

        string writeAllOwners = string.Empty;
        bool writeAllConflict = config.AutoSkipOnHarmonyConflict &&
                                guard.HasExternalConflict(writeAll, patchManager.HarmonyId, out writeAllOwners);

        if (writeAllConflict)
        {
            // Preserve the existing WriteAll patch and optimize the non-conflicting paths:
            //   1. Skip wasted serialization to DummyNetworkConnection in LateUpdate (DummyNetworkConnection.Send is an empty no-op)
            //   2. Per-frame O(P) target cache + single outer dict lookup in GetNewSyncData (avoids O(P^2) MouseLook/RelativePosition)
            //   3. GetVisibleRole fast-path + PreviouslySent disconnect leak fix
            SLPerformanceLog.Info(
                $"Detected external Harmony patch on FpcServerPositionDistributor.WriteAll ({writeAllOwners}); " +
                "switching 'net.fpc_distributor_fast' to safe coexistence mode (Dummy skip + GetNewSyncData O(P) cache + GetVisibleRole + leak fix; WriteAll untouched).");

            MethodInfo getNewSyncData = AccessTools.Method(
                distType,
                "GetNewSyncData",
                new[] { typeof(ReferenceHub), typeof(ReferenceHub), typeof(FirstPersonMovementModule), typeof(bool) });

            bool okLate = patchManager.PatchPrefix(
                Id,
                lateUpdate,
                AccessTools.Method(typeof(FpcDistributorFastModule), nameof(CoexistLateUpdatePrefix)),
                guard,
                config);

            bool okNewSync = getNewSyncData != null && patchManager.PatchPrefix(
                Id,
                getNewSyncData,
                AccessTools.Method(typeof(FpcDistributorFastModule), nameof(CoexistGetNewSyncDataPrefix)),
                guard,
                config);

            bool okGetVisibleRole = patchManager.PatchPrefix(
                Id,
                getVisibleRole,
                AccessTools.Method(typeof(FpcDistributorFastModule), nameof(GetVisibleRolePrefix)),
                guard,
                config);

            _activeConfig = config;
            ReferenceHub.OnPlayerRemoved += OnPlayerRemovedCleanup;
            StatusDetail =
                $"Active (Coexist: LateUpdateDummySkip={okLate}, GetNewSyncDataCache={okNewSync}, GetVisibleRole={okGetVisibleRole}, LeakFix=True; WriteAll preserved for {writeAllOwners})";
            return true;
        }

        if (!patchManager.PatchPrefix(Id, lateUpdate, AccessTools.Method(typeof(FpcDistributorFastModule), nameof(LateUpdatePrefix)), guard, config) ||
            !patchManager.PatchPrefix(Id, writeAll, AccessTools.Method(typeof(FpcDistributorFastModule), nameof(WriteAllPrefix)), guard, config) ||
            !patchManager.PatchPrefix(Id, getVisibleRole, AccessTools.Method(typeof(FpcDistributorFastModule), nameof(GetVisibleRolePrefix)), guard, config))
        {
            patchManager.UnpatchModule(Id);
            return MarkConflict("External Harmony patch detected on FpcServerPositionDistributor.");
        }

        _activeConfig = config;
        ReferenceHub.OnPlayerRemoved += OnPlayerRemovedCleanup;
        StatusDetail = "Active (O(P) snapshot + single visibility + lazy EventArgs + leak fix)";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        ReferenceHub.OnPlayerRemoved -= OnPlayerRemovedCleanup;
        Snapshot.EndTick();
        CoexistTargetCache.Clear();
        _coexistCacheFrame = -1;
        _activeConfig = null;
    }

    private static void OnPlayerRemovedCleanup(ReferenceHub hub)
    {
        if (hub == null || _getPreviouslySent == null)
        {
            return;
        }

        Dictionary<uint, Dictionary<uint, FpcSyncData>> prevSent = _getPreviouslySent();
        if (prevSent == null)
        {
            return;
        }

        uint netId = hub.netId;
        prevSent.Remove(netId);
        foreach (KeyValuePair<uint, Dictionary<uint, FpcSyncData>> pair in prevSent)
        {
            pair.Value.Remove(netId);
        }
    }

    private static bool LateUpdatePrefix()
    {
        if (!NetworkServer.active || !StaticUnityMethods.IsPlaying)
        {
            return false;
        }

        float sendRate = 1f / (float)Mathf.Clamp(ServerStatic.ServerTickrate, 10, 60);
        float cooldown = _getSendCooldown() + Time.deltaTime;
        if (cooldown < sendRate)
        {
            _setSendCooldown(cooldown);
            return false;
        }

        _setSendCooldown(cooldown - sendRate);

        long t0 = MethodSampleTable.Begin();
        try
        {
            Snapshot.CaptureAll();
            int count = Snapshot.Count;
            FpcPlayerSnapshot[] items = Snapshot.Items;

            // Optional Tier-3 OPT-17 parallel pure-value-type encoding path
            FpcParallelEncoder parallel = ParallelEncoder;
            if (parallel != null && _activeConfig != null && _activeConfig.ThreadingFpcParallelEncode &&
                count >= _activeConfig.ParallelEncodeMinPlayers &&
                !_hasValidatedVisibilitySubscribers() &&
                _getRoleSyncEvent() == null &&
                parallel.TryBroadcastParallel(items, count, _getPreviouslySent()))
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                ref readonly FpcPlayerSnapshot recv = ref items[i];
                if (recv.Mode != ClientInstanceMode.Unverified &&
                    recv.Mode != ClientInstanceMode.Dummy &&
                    !recv.IsLocalPlayer &&
                    recv.Hub.connectionToClient != null &&
                    !(recv.Hub.connectionToClient is DummyNetworkConnection))
                {
                    recv.Hub.connectionToClient.Send(new FpcPositionMessage(recv.Hub));
                }
            }
        }
        finally
        {
            Snapshot.EndTick();
            MethodSampleTable.End(ProbeId.FpcDistributor, t0);
        }

        return false;
    }

    private static bool CoexistLateUpdatePrefix()
    {
        if (!NetworkServer.active || !StaticUnityMethods.IsPlaying)
        {
            return false;
        }

        float sendRate = 1f / (float)Mathf.Clamp(ServerStatic.ServerTickrate, 10, 60);
        float cooldown = _getSendCooldown() + Time.deltaTime;
        if (cooldown < sendRate)
        {
            _setSendCooldown(cooldown);
            return false;
        }

        _setSendCooldown(cooldown - sendRate);

        long t0 = MethodSampleTable.Begin();
        try
        {
            CoexistTargetCache.Clear();
            _coexistCacheFrame = Time.frameCount;

            foreach (ReferenceHub hub in ReferenceHub.AllHubs)
            {
                if (hub.Mode != ClientInstanceMode.Unverified &&
                    hub.Mode != ClientInstanceMode.Dummy &&
                    !hub.isLocalPlayer &&
                    hub.connectionToClient != null &&
                    !(hub.connectionToClient is DummyNetworkConnection))
                {
                    hub.connectionToClient.Send(new FpcPositionMessage(hub));
                }
            }
        }
        finally
        {
            MethodSampleTable.End(ProbeId.FpcDistributor, t0);
        }

        return false;
    }

    private static bool CoexistGetNewSyncDataPrefix(
        ReferenceHub receiver,
        ReferenceHub target,
        FirstPersonMovementModule fpmm,
        bool isInvisible,
        ref FpcSyncData __result)
    {
        Dictionary<uint, Dictionary<uint, FpcSyncData>> prevSent = _getPreviouslySent();
        uint recvId = receiver.netId;
        uint targetId = target.netId;

        if (!prevSent.TryGetValue(recvId, out Dictionary<uint, FpcSyncData> receiverPrev))
        {
            receiverPrev = new Dictionary<uint, FpcSyncData>(ReferenceHub.AllHubs.Count);
            prevSent.Add(recvId, receiverPrev);
        }

        receiverPrev.TryGetValue(targetId, out FpcSyncData prevData);

        FpcSyncData nextData;
        if (isInvisible)
        {
            nextData = default;
        }
        else
        {
            int frame = Time.frameCount;
            if (frame != _coexistCacheFrame)
            {
                CoexistTargetCache.Clear();
                _coexistCacheFrame = frame;
            }

            if (!CoexistTargetCache.TryGetValue(targetId, out CoexistTargetFpc cached))
            {
                RelativePosition relPos = fpmm.RelativePosition;
                fpmm.MouseLook.GetSyncValues(relPos.WaypointId, out ushort h, out ushort v);
                cached = new CoexistTargetFpc
                {
                    State = fpmm.SyncMovementState,
                    IsGrounded = fpmm.IsGrounded,
                    RelPos = relPos,
                    SyncH = h,
                    SyncV = v
                };
                CoexistTargetCache.Set(targetId, cached);
            }

            nextData = FpcSyncDataFactory.Build(in prevData, cached.State, cached.IsGrounded, in cached.RelPos, cached.SyncH, cached.SyncV);
        }

        receiverPrev[targetId] = nextData;
        __result = nextData;
        return false;
    }

    private static bool WriteAllPrefix(ReferenceHub receiver, NetworkWriter writer)
    {
        if (!Snapshot.Active)
        {
            // Direct out-of-band WriteAll call (e.g., from a third-party plugin): run safe standalone fast path.
            WriteAllStandalone(receiver, writer);
            return false;
        }

        int recvIdx = Snapshot.FindIndex(receiver);
        if (recvIdx < 0)
        {
            WriteAllStandalone(receiver, writer);
            return false;
        }

        ref readonly FpcPlayerSnapshot recv = ref Snapshot.Items[recvIdx];
        Dictionary<uint, Dictionary<uint, FpcSyncData>> prevSent = _getPreviouslySent();
        if (!prevSent.TryGetValue(recv.NetId, out Dictionary<uint, FpcSyncData> receiverPrev))
        {
            receiverPrev = new Dictionary<uint, FpcSyncData>(Snapshot.Count);
            prevSent.Add(recv.NetId, receiverPrev);
        }

        int[] bufferIds = _getBufferPlayerIds();
        FpcSyncData[] bufferData = _getBufferSyncData();
        bool hasCustomVis = recv.VisibilityController != null;
        VisibilityController visCtrl = recv.VisibilityController;
        bool hasVisEvent = _hasValidatedVisibilitySubscribers();
        Func<ReferenceHub, ReferenceHub, RoleTypeId, NetworkWriter, RoleTypeId> roleSyncEvent = _getRoleSyncEvent();

        ushort visibleCount = 0;
        int total = Snapshot.Count;
        FpcPlayerSnapshot[] items = Snapshot.Items;

        for (int i = 0; i < total; i++)
        {
            ref readonly FpcPlayerSnapshot target = ref items[i];
            if (target.IsLocalPlayer || target.NetId == recv.NetId || !target.IsFpc)
            {
                continue;
            }

            // 1. Validate visibility ONCE (rawInvisible is before LabAPI event override, preserving vanilla GetVisibleRole semantics)
            bool rawInvisible = hasCustomVis && !visCtrl.ValidateVisibility(target.Hub);
            bool isInvisible = rawInvisible;

            if (hasVisEvent)
            {
                PlayerValidatedVisibilityEventArgs ev = new PlayerValidatedVisibilityEventArgs(receiver, target.Hub, !isInvisible);
                PlayerEvents.OnValidatedVisibility(ev);
                isInvisible = !ev.IsVisible;
            }

            // 2. Single dictionary lookup + precomputed FpcSyncData construction
            FpcSyncData syncData;
            if (isInvisible)
            {
                syncData = default;
            }
            else
            {
                receiverPrev.TryGetValue(target.NetId, out FpcSyncData prevData);
                syncData = FpcSyncDataFactory.Build(
                    in prevData,
                    target.MovementState,
                    target.IsGrounded,
                    in target.RelativePosition,
                    target.RotH,
                    target.RotV);

                if (visibleCount < bufferIds.Length)
                {
                    bufferIds[visibleCount] = target.PlayerId;
                    bufferData[visibleCount] = syncData;
                    visibleCount++;
                }
            }
            receiverPrev[target.NetId] = syncData;

            // 3. Optimized GetVisibleRole using precomputed snapshot & rawInvisible short-circuit
            RoleTypeId visibleRole = ComputeVisibleRoleFromSnapshot(in recv, in target, rawInvisible);

            if (roleSyncEvent != null)
            {
                NetworkWriterPooled spoofedWriter = NetworkWriterPool.Get();
                try
                {
                    RoleTypeId? overridden = roleSyncEvent(target.Hub, receiver, visibleRole, spoofedWriter);
                    if (overridden.HasValue)
                    {
                        visibleRole = overridden.Value;
                    }

                    if (!target.PreviouslySentRole.TryGetValue(recv.NetId, out RoleTypeId prevRole) || prevRole != visibleRole)
                    {
                        FpcServerPositionDistributor.SendRole(receiver, target.Hub, visibleRole, spoofedWriter);
                    }
                }
                finally
                {
                    NetworkWriterPool.Return(spoofedWriter);
                }
            }
            else if (!target.PreviouslySentRole.TryGetValue(recv.NetId, out RoleTypeId prevRole) || prevRole != visibleRole)
            {
                NetworkWriterPooled emptyWriter = NetworkWriterPool.Get();
                try
                {
                    FpcServerPositionDistributor.SendRole(receiver, target.Hub, visibleRole, emptyWriter);
                }
                finally
                {
                    NetworkWriterPool.Return(emptyWriter);
                }
            }
        }

        writer.WriteUShort(visibleCount);
        for (int i = 0; i < visibleCount; i++)
        {
            writer.WriteRecyclablePlayerId(new RecyclablePlayerId(bufferIds[i]));
            bufferData[i].Write(writer);
        }

        return false;
    }

    private static RoleTypeId ComputeVisibleRoleFromSnapshot(
        in FpcPlayerSnapshot recv,
        in FpcPlayerSnapshot target,
        bool rawInvisible)
    {
        RoleTypeId result = target.RoleId;
        if (target.IsLocalPlayer || recv.IsLocalPlayer)
        {
            return result;
        }

        if (target.ObfuscatedRole != null)
        {
            result = target.ObfuscatedRole.GetRoleForUser(recv.Hub);
        }

        if (recv.NetId == target.NetId)
        {
            return result;
        }

        // Exact mathematical short-circuit: in vanilla lines 95-98, result is ONLY modified to Spectator
        // if (rawInvisible && !inRange && !hasPerm && !isSpectator) and target is not SCP and not communicating globally.
        if (!rawInvisible)
        {
            return result;
        }

        if (target.Team == Team.SCPs || target.IsCommunicatingGlobally || recv.HasGameplayDataPerm || recv.IsSpectator)
        {
            return result;
        }

        float maxDist = recv.Team == Team.SCPs
            ? FpcServerPositionDistributor.SCPVisibilityDistance
            : FpcServerPositionDistributor.HumanVisibilityDistance;

        Vector3 diff = recv.ObserverCheckPosition - target.WorldPosition;
        if (diff.sqrMagnitude > maxDist * maxDist)
        {
            result = RoleTypeId.Spectator;
        }

        return result;
    }

    /// <summary>
    /// Fast standalone replacement for FpcServerPositionDistributor.GetVisibleRole when called outside WriteAll (e.g., SendNewRoleInfo).
    /// Eliminates dead Vector3.Distance, permission, and voice queries when target is visible.
    /// </summary>
    private static bool GetVisibleRolePrefix(ReferenceHub receiver, ReferenceHub target, ref RoleTypeId __result)
    {
        RoleTypeId result = target.GetRoleId();
        if (target.isLocalPlayer || receiver.isLocalPlayer)
        {
            __result = result;
            return false;
        }

        if (target.roleManager.CurrentRole is IObfuscatedRole obfuscatedRole)
        {
            result = obfuscatedRole.GetRoleForUser(receiver);
        }

        if (receiver == target)
        {
            __result = result;
            return false;
        }

        bool isInvisible = false;
        if (receiver.roleManager.CurrentRole is ICustomVisibilityRole customVis)
        {
            isInvisible = !customVis.VisibilityController.ValidateVisibility(target);
        }

        if (!isInvisible)
        {
            __result = result;
            return false;
        }

        if (target.GetTeam() == Team.SCPs ||
            receiver.GetRoleId() == RoleTypeId.Spectator ||
            PermissionsHandler.IsPermitted(receiver.serverRoles.Permissions, PlayerPermissions.GameplayData) ||
            IsCommunicatingGlobally(target))
        {
            __result = result;
            return false;
        }

        Vector3 observerPos = receiver.roleManager.CurrentRole is Scp079Role scp079
            ? scp079.CameraPosition
            : receiver.transform.position;
        Vector3 targetPos = target.roleManager.CurrentRole is IFpcRole fpcTarget
            ? fpcTarget.FpcModule.Position
            : target.transform.position;

        float maxDist = receiver.GetTeam() == Team.SCPs
            ? FpcServerPositionDistributor.SCPVisibilityDistance
            : FpcServerPositionDistributor.HumanVisibilityDistance;

        if ((observerPos - targetPos).sqrMagnitude > maxDist * maxDist)
        {
            result = RoleTypeId.Spectator;
        }

        __result = result;
        return false;
    }

    private static bool IsCommunicatingGlobally(ReferenceHub target)
    {
        if (target.roleManager.CurrentRole is not PlayerRoles.Voice.IVoiceRole voiceRole)
        {
            return false;
        }

        if (!voiceRole.VoiceModule.ServerIsSending)
        {
            return false;
        }

        if (PlayerRoles.Voice.Intercom.CheckPerms(target))
        {
            return true;
        }

        return voiceRole.VoiceModule.CurrentChannel == VoiceChat.VoiceChatChannel.Radio;
    }

    private static void WriteAllStandalone(ReferenceHub receiver, NetworkWriter writer)
    {
        Dictionary<uint, Dictionary<uint, FpcSyncData>> prevSent = _getPreviouslySent();
        if (!prevSent.TryGetValue(receiver.netId, out Dictionary<uint, FpcSyncData> receiverPrev))
        {
            receiverPrev = new Dictionary<uint, FpcSyncData>(ReferenceHub.AllHubs.Count);
            prevSent.Add(receiver.netId, receiverPrev);
        }

        int[] bufferIds = _getBufferPlayerIds();
        FpcSyncData[] bufferData = _getBufferSyncData();
        VisibilityController visCtrl = receiver.roleManager.CurrentRole is ICustomVisibilityRole cv ? cv.VisibilityController : null;
        bool hasVisEvent = _hasValidatedVisibilitySubscribers();
        Func<ReferenceHub, ReferenceHub, RoleTypeId, NetworkWriter, RoleTypeId> roleSyncEvent = _getRoleSyncEvent();

        ushort num = 0;
        foreach (ReferenceHub target in ReferenceHub.AllHubs)
        {
            if (target.isLocalPlayer || target.netId == receiver.netId || target.roleManager.CurrentRole is not IFpcRole fpcRole)
            {
                continue;
            }

            bool rawInvisible = visCtrl != null && !visCtrl.ValidateVisibility(target);
            bool isInvisible = rawInvisible;
            if (hasVisEvent)
            {
                PlayerValidatedVisibilityEventArgs ev = new PlayerValidatedVisibilityEventArgs(receiver, target, !isInvisible);
                PlayerEvents.OnValidatedVisibility(ev);
                isInvisible = !ev.IsVisible;
            }

            FpcSyncData newSyncData;
            if (isInvisible)
            {
                newSyncData = default;
            }
            else
            {
                receiverPrev.TryGetValue(target.netId, out FpcSyncData prevData);
                FirstPersonMovementModule fpmm = fpcRole.FpcModule;
                newSyncData = new FpcSyncData(prevData, fpmm.SyncMovementState, fpmm.IsGrounded, fpmm.RelativePosition, fpmm.MouseLook);
                if (num < bufferIds.Length)
                {
                    bufferIds[num] = target.PlayerId;
                    bufferData[num] = newSyncData;
                    num++;
                }
            }
            receiverPrev[target.netId] = newSyncData;

            RoleTypeId roleId = FpcServerPositionDistributor.GetVisibleRole(receiver, target);
            if (roleSyncEvent != null)
            {
                NetworkWriterPooled w = NetworkWriterPool.Get();
                try
                {
                    RoleTypeId? ov = roleSyncEvent(target, receiver, roleId, w);
                    if (ov.HasValue) roleId = ov.Value;
                    if (!target.roleManager.PreviouslySentRole.TryGetValue(receiver.netId, out RoleTypeId prevRole) || prevRole != roleId)
                    {
                        FpcServerPositionDistributor.SendRole(receiver, target, roleId, w);
                    }
                }
                finally
                {
                    NetworkWriterPool.Return(w);
                }
            }
            else if (!target.roleManager.PreviouslySentRole.TryGetValue(receiver.netId, out RoleTypeId prevRole) || prevRole != roleId)
            {
                NetworkWriterPooled w = NetworkWriterPool.Get();
                try
                {
                    FpcServerPositionDistributor.SendRole(receiver, target, roleId, w);
                }
                finally
                {
                    NetworkWriterPool.Return(w);
                }
            }
        }

        writer.WriteUShort(num);
        for (int i = 0; i < num; i++)
        {
            writer.WriteRecyclablePlayerId(new RecyclablePlayerId(bufferIds[i]));
            bufferData[i].Write(writer);
        }
    }
}
