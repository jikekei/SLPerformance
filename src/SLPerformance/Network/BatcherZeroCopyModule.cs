using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirror;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using UnityEngine;

namespace SLPerformance.Network;

/// <summary>
/// OPT-10:
/// 1. Eliminates Mirror.NetworkConnection.Update() renting a pooled writer for empty channels
///    and eliminates Batcher.CopyAndReturn's redundant full-batch Array.ConstrainedCopy.
/// 2. Adds a zero-copy fast path to NetworkServer.OnTransportData when !isLoadingScene and Unbatcher is empty,
///    avoiding Unbatcher.AddBatch's full-packet copy into a temporary NetworkWriterPooled.
/// </summary>
public sealed class BatcherZeroCopyModule : OptimizationModuleBase
{
    public override string Id => "net.batcher_unbatcher_zerocopy";
    public override string Name => "Mirror Batcher & Unbatcher Zero-Copy Fast Path";
    public override OptimizationTier Tier => OptimizationTier.Tier2;
    public override string Description => "Flushes Batcher buffers directly to Transport without intermediate copy, and parses inbound batches in-place when unbatcher queue is empty.";

    private static Func<NetworkConnection, Dictionary<int, Batcher>> _getConnBatches;
    private static Action<NetworkConnection, ArraySegment<byte>, int> _sendToTransport;
    private static Func<Batcher, Queue<NetworkWriterPooled>> _getBatchQueue;
    private static Func<Batcher, NetworkWriterPooled> _getCurrentBatch;
    private static Action<Batcher, NetworkWriterPooled> _setCurrentBatch;
    private static Func<NetworkConnectionToClient, NetworkReader, int, bool> _unpackAndInvoke;
    private static Action<NetworkConnection, double> _setRemoteTimeStamp;

    private static readonly NetworkReader InboundFastReader = new NetworkReader(default);

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.NetBatcherUnbatcherZeroCopy;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        MethodInfo connUpdate = AccessTools.Method(typeof(NetworkConnection), "Update", Type.EmptyTypes);
        MethodInfo sendToTransport = AccessTools.Method(typeof(NetworkConnection), "SendToTransport", new[] { typeof(ArraySegment<byte>), typeof(int) });
        MethodInfo onTransportData = AccessTools.Method(typeof(NetworkServer), "OnTransportData", new[] { typeof(int), typeof(ArraySegment<byte>), typeof(int) });
        MethodInfo unpackAndInvoke = AccessTools.Method(typeof(NetworkServer), "UnpackAndInvoke", new[] { typeof(NetworkConnectionToClient), typeof(NetworkReader), typeof(int) });

        _getConnBatches = ReflectionHelpers.CreateInstanceFieldGetter<NetworkConnection, Dictionary<int, Batcher>>(typeof(NetworkConnection), "batches");
        _getBatchQueue = ReflectionHelpers.CreateInstanceFieldGetter<Batcher, Queue<NetworkWriterPooled>>(typeof(Batcher), "batches");
        _getCurrentBatch = ReflectionHelpers.CreateInstanceFieldGetter<Batcher, NetworkWriterPooled>(typeof(Batcher), "batch");
        _setCurrentBatch = ReflectionHelpers.CreateInstanceFieldSetter<Batcher, NetworkWriterPooled>(typeof(Batcher), "batch");
        _sendToTransport = ReflectionHelpers.CreateOpenDelegate<Action<NetworkConnection, ArraySegment<byte>, int>>(sendToTransport);
        _unpackAndInvoke = ReflectionHelpers.CreateOpenDelegate<Func<NetworkConnectionToClient, NetworkReader, int, bool>>(unpackAndInvoke);
        _setRemoteTimeStamp = ReflectionHelpers.CreateOpenDelegate<Action<NetworkConnection, double>>(AccessTools.PropertySetter(typeof(NetworkConnection), nameof(NetworkConnection.remoteTimeStamp)));

        if (connUpdate == null || onTransportData == null || _getConnBatches == null ||
            _getBatchQueue == null || _getCurrentBatch == null || _setCurrentBatch == null ||
            _sendToTransport == null || _unpackAndInvoke == null || _setRemoteTimeStamp == null)
        {
            return MarkIncompatible("Mirror Batcher/NetworkConnection/NetworkServer private members not found.");
        }

        bool ok =
            patchManager.PatchPrefix(Id, connUpdate, AccessTools.Method(typeof(BatcherZeroCopyModule), nameof(ConnectionUpdatePrefix)), guard, config) &&
            patchManager.PatchPrefix(Id, onTransportData, AccessTools.Method(typeof(BatcherZeroCopyModule), nameof(OnTransportDataPrefix)), guard, config);

        if (!ok)
        {
            patchManager.UnpatchModule(Id);
            return MarkConflict("External Harmony patch detected on NetworkConnection.Update or NetworkServer.OnTransportData.");
        }

        StatusDetail = "Active (zero-copy Batcher flush + in-place Unbatcher receive)";
        return true;
    }

    private static bool ConnectionUpdatePrefix(NetworkConnection __instance)
    {
        Dictionary<int, Batcher> batches = _getConnBatches(__instance);
        if (batches == null || batches.Count == 0)
        {
            return false;
        }

        foreach (KeyValuePair<int, Batcher> kvp in batches)
        {
            int channelId = kvp.Key;
            Batcher batcher = kvp.Value;
            Queue<NetworkWriterPooled> queue = _getBatchQueue(batcher);
            NetworkWriterPooled current = _getCurrentBatch(batcher);

            if (queue.Count == 0 && current == null)
            {
                continue;
            }

            while (queue.Count > 0)
            {
                NetworkWriterPooled queuedBatch = queue.Dequeue();
                try
                {
                    _sendToTransport(__instance, queuedBatch.ToArraySegment(), channelId);
                }
                finally
                {
                    NetworkWriterPool.Return(queuedBatch);
                }
            }

            if (current != null)
            {
                _setCurrentBatch(batcher, null);
                try
                {
                    _sendToTransport(__instance, current.ToArraySegment(), channelId);
                }
                finally
                {
                    NetworkWriterPool.Return(current);
                }
            }
        }

        return false;
    }

    private static bool OnTransportDataPrefix(int connectionId, ArraySegment<byte> data, int channelId)
    {
        // Only apply zero-copy fast path when scene is not loading and unbatcher has no queued backlog.
        if (NetworkServer.isLoadingScene)
        {
            return true;
        }

        if (!NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient conn))
        {
            return true;
        }

        if (conn.unbatcher.BatchesCount > 0)
        {
            return true;
        }

        if (data.Count < Batcher.TimestampSize)
        {
            Debug.LogWarning("NetworkServer: received Message was too short (messages should start with message id)");
            conn.Disconnect();
            return false;
        }

        try
        {
            InboundFastReader.SetBuffer(data);
            double remoteTimeStamp = InboundFastReader.ReadDouble();

            while (!NetworkServer.isLoadingScene && InboundFastReader.Remaining > 0)
            {
                int msgSize = (int)Compression.DecompressVarUInt(InboundFastReader);
                if (msgSize < 0 || InboundFastReader.Remaining < msgSize)
                {
                    break;
                }

                ArraySegment<byte> msgSegment = InboundFastReader.ReadBytesSegment(msgSize);
                using NetworkReaderPooled msgReader = NetworkReaderPool.Get(msgSegment);
                if (msgReader.Remaining < NetworkMessages.IdSize)
                {
                    Debug.LogWarning($"NetworkServer: received Message was too short (messages should start with message id). Disconnecting {connectionId}");
                    conn.Disconnect();
                    return false;
                }

                _setRemoteTimeStamp(conn, remoteTimeStamp);
                if (!_unpackAndInvoke(conn, msgReader, channelId))
                {
                    Debug.LogWarning($"NetworkServer: failed to unpack and invoke message. Disconnecting {connectionId}.");
                    conn.Disconnect();
                    return false;
                }
            }

            // If a message triggered scene load mid-batch and unread bytes remain, spill them to Unbatcher preserving timestamp.
            if (NetworkServer.isLoadingScene && InboundFastReader.Remaining > 0)
            {
                using NetworkWriterPooled spill = NetworkWriterPool.Get();
                spill.WriteDouble(remoteTimeStamp);
                int remaining = InboundFastReader.Remaining;
                spill.WriteBytes(data.Array, data.Offset + InboundFastReader.Position, remaining);
                conn.unbatcher.AddBatch(spill.ToArraySegment());
            }

            return false;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"NetworkServer: exception occured during deserialization: {ex.Message}. Disconnecting {connectionId}.");
            conn.Disconnect();
            return false;
        }
    }
}
