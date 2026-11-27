using System;
using System.Collections.Generic;
using CentralAuth;
using CustomPlayerEffects;
using Mirror;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using PlayerRoles.Visibility;
using RelativePositioning;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Network;
using SLPerformance.Patching;
using SLPerformance.Serialization;
using UnityEngine;

namespace SLPerformance.Threading;

/// <summary>
/// OPT-17 (Tier 3, opt-in): Parallel pure-value-type FPC delta encoding & serialization on a BoundedWorkerPool.
/// Zero Unity API calls on worker threads:
/// 1. Main thread flattens per-player pure value state & snapshots per-receiver previous FpcSyncData + previous RoleTypeId.
///    Any non-standard VisibilityController (e.g. SCP-096/106/939) or IObfuscatedRole is evaluated on the main thread into
///    a precomputed override byte array before workers start.
/// 2. Workers run strictly on unmanaged/value-type arrays: visibility distance check, FpcSyncDataFactory.Build,
///    Mirror FpcPositionMessage payload packing into per-receiver reusable NetworkWriters, and role-change detection.
/// 3. Main thread commits updated PreviouslySent entries, invokes SendRole for any detected role changes,
///    and sends pre-packed ArraySegment&lt;byte&gt; directly to each connectionToClient.
/// </summary>
public sealed class FpcParallelEncoder : OptimizationModuleBase
{
    public override string Id => "threading.fpc_parallel_encode";
    public override string Name => "Parallel Pure-Value FPC Delta Encoder (Tier 3)";
    public override OptimizationTier Tier => OptimizationTier.Tier3;
    public override string Description => "Opt-in bounded worker pool parallelization for FpcServerPositionDistributor pure delta encoding & serialization at high player counts.";

    private struct PurePlayerData
    {
        public uint NetId;
        public int PlayerId;
        public bool IsLocalPlayer;
        public bool ShouldReceive;
        public bool IsFpc;
        public PlayerMovementState MovementState;
        public bool IsGrounded;
        public RelativePosition RelativePosition;
        public ushort RotH;
        public ushort RotV;
        public Vector3 WorldPosition;
        public Vector3 ObserverCheckPosition;
        public RoleTypeId RoleId;
        public Team Team;
        public bool IsSpectator;
        public bool HasGameplayDataPerm;
        public bool IsCommunicatingGlobally;
        public bool IsSurfaceZone;
        public InvisibilityFlags IgnoredFlags;
        public bool HasInvisibleEffect;
        public bool HasCustomVis;
        public bool IsStandardFpcVis;
        public bool HasObfuscatedRole;
    }

    private struct RoleChangeRecord
    {
        public int TargetIdx;
        public RoleTypeId NewRole;
    }

    private sealed class ReceiverWorkSlot
    {
        public readonly NetworkWriter Writer = new NetworkWriter();
        public FpcSyncData[] PrevSync = new FpcSyncData[64];
        public FpcSyncData[] NextSync = new FpcSyncData[64];
        public RoleTypeId[] PrevRoles = new RoleTypeId[64];
        public bool[] HasPrevRole = new bool[64];
        public byte[] PrecomputedInvisible = new byte[64]; // 0 = compute pure, 1 = visible, 2 = invisible
        public RoleTypeId[] PrecomputedObfuscatedRoles = new RoleTypeId[64];
        public readonly List<RoleChangeRecord> RoleChanges = new List<RoleChangeRecord>(8);
        public Dictionary<uint, FpcSyncData> ReceiverPrevDict;
        public bool Active;

        public void EnsureCapacity(int count)
        {
            if (PrevSync.Length < count)
            {
                int cap = Math.Max(count + 16, PrevSync.Length * 2);
                PrevSync = new FpcSyncData[cap];
                NextSync = new FpcSyncData[cap];
                PrevRoles = new RoleTypeId[cap];
                HasPrevRole = new bool[cap];
                PrecomputedInvisible = new byte[cap];
                PrecomputedObfuscatedRoles = new RoleTypeId[cap];
            }
        }
    }

    private BoundedWorkerPool _pool;
    private PurePlayerData[] _pureData = new PurePlayerData[64];
    private ReceiverWorkSlot[] _slots = Array.Empty<ReceiverWorkSlot>();
    private int _currentCount;
    private ushort _fpcPositionMsgId;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.ThreadingFpcParallelEncode;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        if (!FpcSyncDataFactory.IsSupported)
        {
            return MarkIncompatible("FpcSyncDataFactory is not supported on this game build.");
        }

        if (!config.NetFpcDistributorFast)
        {
            return MarkIncompatible("Requires OPT-02 (net.fpc_distributor_fast) to be enabled.");
        }

        _fpcPositionMsgId = NetworkMessageId<FpcPositionMessage>.Id;
        int workers = Math.Max(1, Math.Min(4, config.ParallelWorkerThreads));
        _pool = new BoundedWorkerPool(workers, "SLPerf-FpcEnc");
        FpcDistributorFastModule.ParallelEncoder = this;

        StatusDetail = $"Active (workers={workers}, minPlayers={config.ParallelEncodeMinPlayers}, msgId={_fpcPositionMsgId})";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        if (ReferenceEquals(FpcDistributorFastModule.ParallelEncoder, this))
        {
            FpcDistributorFastModule.ParallelEncoder = null;
        }

        _pool?.Dispose();
        _pool = null;
    }

    public bool TryBroadcastParallel(
        FpcPlayerSnapshot[] items,
        int count,
        Dictionary<uint, Dictionary<uint, FpcSyncData>> prevSent)
    {
        BoundedWorkerPool pool = _pool;
        if (pool == null || count <= 1 || prevSent == null)
        {
            return false;
        }

        EnsureBuffers(count);
        _currentCount = count;

        // Phase 1 (Main Thread): Flatten pure value data & evaluate any non-standard VisibilityController / IObfuscatedRole
        for (int i = 0; i < count; i++)
        {
            ref readonly FpcPlayerSnapshot s = ref items[i];
            PurePlayerData p = default;
            p.NetId = s.NetId;
            p.PlayerId = s.PlayerId;
            p.IsLocalPlayer = s.IsLocalPlayer;
            p.ShouldReceive = s.Mode != ClientInstanceMode.Unverified && !s.IsLocalPlayer && s.Hub.connectionToClient != null;
            p.IsFpc = s.IsFpc;
            p.MovementState = s.MovementState;
            p.IsGrounded = s.IsGrounded;
            p.RelativePosition = s.RelativePosition;
            p.RotH = s.RotH;
            p.RotV = s.RotV;
            p.WorldPosition = s.WorldPosition;
            p.ObserverCheckPosition = s.ObserverCheckPosition;
            p.RoleId = s.RoleId;
            p.Team = s.Team;
            p.IsSpectator = s.IsSpectator;
            p.HasGameplayDataPerm = s.HasGameplayDataPerm;
            p.IsCommunicatingGlobally = s.IsCommunicatingGlobally;
            p.IsSurfaceZone = s.IsSurfaceZone;
            p.HasCustomVis = s.VisibilityController != null;
            p.IsStandardFpcVis = s.IsStandardFpcVisibility;
            p.HasObfuscatedRole = s.ObfuscatedRole != null;

            if (p.IsStandardFpcVis)
            {
                p.IgnoredFlags = s.VisibilityController.IgnoredFlags;
                Invisible inv = s.Hub.playerEffectsController.GetEffect<Invisible>();
                p.HasInvisibleEffect = inv != null && inv.IsEnabled;
            }

            _pureData[i] = p;
        }

        for (int r = 0; r < count; r++)
        {
            ReceiverWorkSlot slot = _slots[r];
            slot.RoleChanges.Clear();
            ref readonly PurePlayerData recv = ref _pureData[r];
            if (!recv.ShouldReceive)
            {
                slot.Active = false;
                continue;
            }

            slot.Active = true;
            slot.EnsureCapacity(count);

            if (!prevSent.TryGetValue(recv.NetId, out Dictionary<uint, FpcSyncData> receiverPrev))
            {
                receiverPrev = new Dictionary<uint, FpcSyncData>(count);
                prevSent.Add(recv.NetId, receiverPrev);
            }

            slot.ReceiverPrevDict = receiverPrev;
            VisibilityController recvVis = items[r].VisibilityController;

            for (int t = 0; t < count; t++)
            {
                ref readonly PurePlayerData target = ref _pureData[t];
                if (target.IsLocalPlayer || target.NetId == recv.NetId || !target.IsFpc)
                {
                    continue;
                }

                receiverPrev.TryGetValue(target.NetId, out slot.PrevSync[t]);
                slot.HasPrevRole[t] = items[t].PreviouslySentRole.TryGetValue(recv.NetId, out slot.PrevRoles[t]);

                // Pure-value visibility is exact when receiver has no custom vis OR both sides use standard FpcVisibilityController.
                if (!recv.HasCustomVis)
                {
                    slot.PrecomputedInvisible[t] = 1; // always visible
                }
                else if (recv.IsStandardFpcVis && (!target.HasCustomVis || target.IsStandardFpcVis))
                {
                    slot.PrecomputedInvisible[t] = 0; // workers compute pure FpcVisibilityController math
                }
                else
                {
                    bool vis = recvVis.ValidateVisibility(items[t].Hub);
                    slot.PrecomputedInvisible[t] = vis ? (byte)1 : (byte)2;
                }

                if (target.HasObfuscatedRole)
                {
                    slot.PrecomputedObfuscatedRoles[t] = items[t].ObfuscatedRole.GetRoleForUser(items[r].Hub);
                }
            }
        }

        // Phase 2 (Bounded Worker Pool — ZERO Unity API calls): Encode FpcPositionMessage per receiver in parallel
        pool.ExecuteParallelFor(count, ProcessReceiverChunk);

        // Phase 3 (Main Thread): Commit PreviouslySent, send Role changes, and dispatch pre-serialized packets
        for (int r = 0; r < count; r++)
        {
            ReceiverWorkSlot slot = _slots[r];
            if (!slot.Active)
            {
                continue;
            }

            ReferenceHub recvHub = items[r].Hub;
            Dictionary<uint, FpcSyncData> receiverPrev = slot.ReceiverPrevDict;
            for (int t = 0; t < count; t++)
            {
                ref readonly PurePlayerData target = ref _pureData[t];
                if (target.IsLocalPlayer || target.NetId == _pureData[r].NetId || !target.IsFpc)
                {
                    continue;
                }

                receiverPrev[target.NetId] = slot.NextSync[t];
            }

            List<RoleChangeRecord> roleChanges = slot.RoleChanges;
            for (int c = 0; c < roleChanges.Count; c++)
            {
                RoleChangeRecord rc = roleChanges[c];
                NetworkWriterPooled emptyWriter = NetworkWriterPool.Get();
                try
                {
                    FpcServerPositionDistributor.SendRole(recvHub, items[rc.TargetIdx].Hub, rc.NewRole, emptyWriter);
                }
                finally
                {
                    NetworkWriterPool.Return(emptyWriter);
                }
            }

            recvHub.connectionToClient.Send(slot.Writer.ToArraySegment(), Channels.Reliable);
        }

        return true;
    }

    private void ProcessReceiverChunk(int workerIndex, int startIdx, int endIdx)
    {
        int count = _currentCount;
        PurePlayerData[] data = _pureData;
        ushort msgId = _fpcPositionMsgId;

        for (int r = startIdx; r < endIdx; r++)
        {
            ReceiverWorkSlot slot = _slots[r];
            if (!slot.Active)
            {
                continue;
            }

            ref readonly PurePlayerData recv = ref data[r];
            NetworkWriter writer = slot.Writer;
            writer.Reset();
            writer.WriteUShort(msgId);

            // Reserve 2 bytes for ushort visibleCount; we patch them in place after writing all visible entries!
            int countPos = writer.Position;
            writer.WriteUShort(0);

            ushort visibleCount = 0;
            for (int t = 0; t < count; t++)
            {
                ref readonly PurePlayerData target = ref data[t];
                if (target.IsLocalPlayer || target.NetId == recv.NetId || !target.IsFpc)
                {
                    continue;
                }

                bool rawInvisible;
                byte pre = slot.PrecomputedInvisible[t];
                if (pre == 1)
                {
                    rawInvisible = false;
                }
                else if (pre == 2)
                {
                    rawInvisible = true;
                }
                else
                {
                    rawInvisible = ComputeStandardFpcInvisible(in recv, in target);
                }

                FpcSyncData syncData;
                if (rawInvisible)
                {
                    syncData = default;
                }
                else
                {
                    syncData = FpcSyncDataFactory.Build(
                        in slot.PrevSync[t],
                        target.MovementState,
                        target.IsGrounded,
                        in target.RelativePosition,
                        target.RotH,
                        target.RotV);

                    writer.WriteRecyclablePlayerId(new RecyclablePlayerId(target.PlayerId));
                    syncData.Write(writer);
                    visibleCount++;
                }

                slot.NextSync[t] = syncData;

                // Pure-value visible role check
                RoleTypeId visibleRole = target.HasObfuscatedRole
                    ? slot.PrecomputedObfuscatedRoles[t]
                    : target.RoleId;

                if (rawInvisible &&
                    target.Team != Team.SCPs &&
                    !target.IsCommunicatingGlobally &&
                    !recv.HasGameplayDataPerm &&
                    !recv.IsSpectator)
                {
                    float maxDist = recv.Team == Team.SCPs
                        ? FpcServerPositionDistributor.SCPVisibilityDistance
                        : FpcServerPositionDistributor.HumanVisibilityDistance;

                    float dx = recv.ObserverCheckPosition.x - target.WorldPosition.x;
                    float dy = recv.ObserverCheckPosition.y - target.WorldPosition.y;
                    float dz = recv.ObserverCheckPosition.z - target.WorldPosition.z;
                    if (dx * dx + dy * dy + dz * dz > maxDist * maxDist)
                    {
                        visibleRole = RoleTypeId.Spectator;
                    }
                }

                if (!slot.HasPrevRole[t] || slot.PrevRoles[t] != visibleRole)
                {
                    slot.RoleChanges.Add(new RoleChangeRecord
                    {
                        TargetIdx = t,
                        NewRole = visibleRole
                    });
                }
            }

            // Backpatch visibleCount in little-endian at countPos
            byte[] buf = writer.buffer;
            buf[countPos] = (byte)(visibleCount & 0xFF);
            buf[countPos + 1] = (byte)((visibleCount >> 8) & 0xFF);
        }
    }

    private static bool ComputeStandardFpcInvisible(in PurePlayerData recv, in PurePlayerData target)
    {
        if (!target.HasCustomVis)
        {
            return false;
        }

        InvisibilityFlags flags = InvisibilityFlags.None;
        if (target.HasInvisibleEffect)
        {
            flags |= InvisibilityFlags.Scp268;
        }

        if (recv.IsFpc && target.IsFpc)
        {
            float dx = recv.WorldPosition.x - target.WorldPosition.x;
            float dy = recv.WorldPosition.y - target.WorldPosition.y;
            float dz = recv.WorldPosition.z - target.WorldPosition.z;
            float maxRangeSqr = recv.IsSurfaceZone ? 4900f : 1300f;
            if (dx * dx + dy * dy + dz * dz > maxRangeSqr)
            {
                flags |= InvisibilityFlags.OutOfRange;
            }
        }

        return (flags & ~recv.IgnoredFlags) != 0;
    }

    private void EnsureBuffers(int count)
    {
        if (_pureData.Length < count)
        {
            _pureData = new PurePlayerData[Math.Max(count + 16, _pureData.Length * 2)];
        }

        if (_slots.Length < count)
        {
            int oldLen = _slots.Length;
            int newLen = Math.Max(count + 16, oldLen == 0 ? 64 : oldLen * 2);
            Array.Resize(ref _slots, newLen);
            for (int i = oldLen; i < newLen; i++)
            {
                _slots[i] = new ReceiverWorkSlot();
            }
        }
    }
}
