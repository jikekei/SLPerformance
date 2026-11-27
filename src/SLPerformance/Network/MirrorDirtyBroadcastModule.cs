using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirror;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using SLPerformance.Profiler;
using UnityEngine;

namespace SLPerformance.Network;

/// <summary>
/// OPT-03: Replaces Mirror.NetworkServer.Broadcast() O(P * E) outer-connection / inner-entity loop
/// with an entity-first O(E + D * P) dirty sweep:
/// - Reads Time.frameCount & NetworkTime.localTime ONCE per tick instead of P*E times
/// - Skips clean entities in pure managed memory without touching any connection
/// - Packs EntityStateMessage ONCE per dirty entity instead of re-packing for all P observers
/// - Fully preserves CustomInterestManagement / third-party AOI / owner vs observer serialization / syncInterval
/// </summary>
public sealed class MirrorDirtyBroadcastModule : OptimizationModuleBase
{
    public override string Id => "net.mirror_dirty_broadcast";
    public override string Name => "Mirror Entity-First Dirty Broadcast";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Replaces Mirror's O(P*E) Broadcast with O(E + D*P) dirty-entity sweep and single EntityStateMessage packing.";

    private static Func<List<NetworkConnectionToClient>> _getConnectionsCopy;
    private static Func<NetworkBehaviour, ulong> _getSyncVarDirtyBits;
    private static Func<NetworkBehaviour, ulong> _getSyncObjectDirtyBits;
    private static Func<NetworkBehaviour, double> _getLastSyncTime;
    private static Func<NetworkIdentity, NetworkIdentitySerialization> _getLastSerialization;
    private static Func<NetworkIdentity, int, NetworkIdentitySerialization> _getServerSerializationAtTick;
    private static Func<NetworkConnectionToClient, bool> _disconnectIfInactive;
    private static Action<NetworkConnectionToClient> _updateConnection;
    private static Action<EntityStateMessage, int, int, int> _diagOnSendState;

    private static readonly List<NetworkIdentity> SpawnedScratch = new List<NetworkIdentity>(1024);

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.NetMirrorDirtyBroadcast;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        MethodInfo broadcast = AccessTools.Method(typeof(NetworkServer), "Broadcast", Type.EmptyTypes);
        MethodInfo disconnectIfInactive = AccessTools.Method(typeof(NetworkServer), "DisconnectIfInactive", new[] { typeof(NetworkConnectionToClient) });
        MethodInfo getSerAtTick = AccessTools.Method(typeof(NetworkIdentity), "GetServerSerializationAtTick", new[] { typeof(int) });
        MethodInfo connUpdate = AccessTools.Method(typeof(NetworkConnection), "Update", Type.EmptyTypes);

        _getConnectionsCopy = ReflectionHelpers.CreateStaticFieldGetter<List<NetworkConnectionToClient>>(typeof(NetworkServer), "connectionsCopy");
        _getSyncVarDirtyBits = ReflectionHelpers.CreateInstanceFieldGetter<NetworkBehaviour, ulong>(typeof(NetworkBehaviour), "<syncVarDirtyBits>k__BackingField");
        _getSyncObjectDirtyBits = ReflectionHelpers.CreateInstanceFieldGetter<NetworkBehaviour, ulong>(typeof(NetworkBehaviour), "syncObjectDirtyBits");
        _getLastSyncTime = ReflectionHelpers.CreateInstanceFieldGetter<NetworkBehaviour, double>(typeof(NetworkBehaviour), "lastSyncTime");
        _getLastSerialization = ReflectionHelpers.CreateInstanceFieldGetter<NetworkIdentity, NetworkIdentitySerialization>(typeof(NetworkIdentity), "lastSerialization");

        MethodInfo onSendGen = AccessTools.Method(typeof(NetworkDiagnostics), "OnSend");
        if (onSendGen != null && onSendGen.IsGenericMethodDefinition)
        {
            _diagOnSendState = ReflectionHelpers.CreateOpenDelegate<Action<EntityStateMessage, int, int, int>>(
                onSendGen.MakeGenericMethod(typeof(EntityStateMessage)));
        }

        _disconnectIfInactive = ReflectionHelpers.CreateOpenDelegate<Func<NetworkConnectionToClient, bool>>(disconnectIfInactive);
        _getServerSerializationAtTick = ReflectionHelpers.CreateOpenDelegate<Func<NetworkIdentity, int, NetworkIdentitySerialization>>(getSerAtTick);
        _updateConnection = ReflectionHelpers.CreateOpenDelegate<Action<NetworkConnectionToClient>>(connUpdate);

        if (broadcast == null || _getConnectionsCopy == null || _getSyncVarDirtyBits == null ||
            _getSyncObjectDirtyBits == null || _getLastSyncTime == null || _getLastSerialization == null ||
            _disconnectIfInactive == null || _getServerSerializationAtTick == null || _updateConnection == null)
        {
            return MarkIncompatible("Mirror.NetworkServer / NetworkIdentity / NetworkBehaviour internal members mismatch.");
        }

        if (!patchManager.PatchPrefix(Id, broadcast, AccessTools.Method(typeof(MirrorDirtyBroadcastModule), nameof(BroadcastPrefix)), guard, config))
        {
            return MarkConflict("External Harmony patch detected on Mirror.NetworkServer.Broadcast.");
        }

        StatusDetail = "Active (O(E + D*P) dirty sweep + single EntityStateMessage pack)";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        SpawnedScratch.Clear();
    }

    private static bool BroadcastPrefix()
    {
        long t0 = MethodSampleTable.Begin();
        try
        {
            List<NetworkConnectionToClient> connectionsCopy = _getConnectionsCopy();
            connectionsCopy.Clear();
            foreach (NetworkConnectionToClient conn in NetworkServer.connections.Values)
            {
                connectionsCopy.Add(conn);
            }

            int activeConnCount = 0;
            bool anyReady = false;

            // Phase 1: Inactive disconnect check & TimeSnapshotMessage
            for (int i = 0; i < connectionsCopy.Count; i++)
            {
                NetworkConnectionToClient conn = connectionsCopy[i];
                if (_disconnectIfInactive(conn))
                {
                    connectionsCopy[i] = null;
                    continue;
                }

                activeConnCount++;
                if (conn.isReady)
                {
                    conn.Send<TimeSnapshotMessage>(default, 1);
                    anyReady = true;
                }
            }

            // Phase 2: Entity-first dirty sweep (O(E + D * P))
            if (anyReady && NetworkServer.spawned.Count > 0)
            {
                int tick = Time.frameCount;
                double localTime = NetworkTime.localTime;
                int maxReliableMsgSize = NetworkMessages.MaxMessageSize(0);
                Action<EntityStateMessage, int, int, int> diagOnSend = _diagOnSendState;

                SpawnedScratch.Clear();
                foreach (NetworkIdentity id in NetworkServer.spawned.Values)
                {
                    SpawnedScratch.Add(id);
                }

                int entityCount = SpawnedScratch.Count;
                for (int i = 0; i < entityCount; i++)
                {
                    NetworkIdentity identity = SpawnedScratch[i];
                    if (identity == null)
                    {
                        continue;
                    }

                    Dictionary<int, NetworkConnectionToClient> observers = identity.observers;
                    if (observers.Count == 0)
                    {
                        continue;
                    }

                    // Fast managed dirty check before calling GetServerSerializationAtTick
                    if (!IsIdentityPotentiallyDirty(identity, tick, localTime))
                    {
                        continue;
                    }

                    NetworkIdentitySerialization ser = _getServerSerializationAtTick(identity, tick);
                    NetworkWriter ownerWriter = ser.ownerWriter;
                    NetworkWriter observersWriter = ser.observersWriter;
                    NetworkConnectionToClient ownerConn = identity.connectionToClient;

                    bool hasObserverPayload = observersWriter != null && observersWriter.Position > 0;
                    bool hasOwnerPayload = ownerWriter != null && ownerWriter.Position > 0 && ownerConn != null && ownerConn.isReady;

                    if (hasObserverPayload)
                    {
                        EntityStateMessage msg = new EntityStateMessage
                        {
                            netId = identity.netId,
                            payload = observersWriter.ToArraySegment()
                        };

                        using NetworkWriterPooled packed = NetworkWriterPool.Get();
                        NetworkMessages.Pack(msg, packed);
                        if (packed.Position <= maxReliableMsgSize)
                        {
                            ArraySegment<byte> segment = packed.ToArraySegment();
                            int sentCount = 0;
                            foreach (NetworkConnectionToClient obs in observers.Values)
                            {
                                if (obs != ownerConn && obs.isReady)
                                {
                                    obs.Send(segment, 0);
                                    sentCount++;
                                }
                            }

                            if (sentCount > 0 && diagOnSend != null)
                            {
                                diagOnSend(msg, 0, segment.Count, sentCount);
                            }
                        }
                        else
                        {
                            Debug.LogError($"NetworkConnection.Send: message of type {typeof(EntityStateMessage)} with a size of {packed.Position} bytes is larger than max allowed {maxReliableMsgSize}.");
                        }
                    }

                    if (hasOwnerPayload && observers.ContainsKey(ownerConn.connectionId))
                    {
                        EntityStateMessage ownerMsg = new EntityStateMessage
                        {
                            netId = identity.netId,
                            payload = ownerWriter.ToArraySegment()
                        };
                        ownerConn.Send(ownerMsg, 0);
                    }
                }

                SpawnedScratch.Clear();
            }

            // Phase 3: Flush Batchers for all active connections
            if (activeConnCount > 0)
            {
                for (int i = 0; i < connectionsCopy.Count; i++)
                {
                    NetworkConnectionToClient conn = connectionsCopy[i];
                    if (conn != null)
                    {
                        _updateConnection(conn);
                    }
                }
            }

            return false;
        }
        finally
        {
            MethodSampleTable.End(ProbeId.MirrorBroadcast, t0);
        }
    }

    private static bool IsIdentityPotentiallyDirty(NetworkIdentity identity, int tick, double localTime)
    {
        NetworkIdentitySerialization lastSer = _getLastSerialization(identity);
        if (lastSer.tick == tick)
        {
            return (lastSer.observersWriter != null && lastSer.observersWriter.Position > 0) ||
                   (lastSer.ownerWriter != null && lastSer.ownerWriter.Position > 0);
        }

        NetworkBehaviour[] behaviours = identity.NetworkBehaviours;
        if (behaviours == null)
        {
            return false;
        }

        for (int i = 0; i < behaviours.Length; i++)
        {
            NetworkBehaviour nb = behaviours[i];
            if ((_getSyncVarDirtyBits(nb) | _getSyncObjectDirtyBits(nb)) != 0UL)
            {
                float interval = nb.syncInterval;
                if (interval <= 0f || localTime - _getLastSyncTime(nb) >= (double)interval)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
