using System;
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
/// OPT-01: Fixes Mirror.NetworkBehaviour.SendRPCInternal discarding its pooled writer and
/// re-packing RpcMessage separately for every single observer (P+1 packs -> 1 pack).
/// </summary>
public sealed class MirrorRpcPackOnceModule : OptimizationModuleBase
{
    public override string Id => "net.mirror_rpc_pack_once";
    public override string Name => "Mirror ClientRpc Single-Pack Fix";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Packs RpcMessage once instead of P+1 times in Mirror.NetworkBehaviour.SendRPCInternal.";

    private static Action<RpcMessage, int, int, int> _diagOnSendRpc;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.NetMirrorRpcPackOnce;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        MethodInfo target = AccessTools.Method(
            typeof(NetworkBehaviour),
            "SendRPCInternal",
            new[] { typeof(string), typeof(int), typeof(NetworkWriter), typeof(int), typeof(bool) });

        if (target == null)
        {
            return MarkIncompatible("Mirror.NetworkBehaviour.SendRPCInternal signature not found.");
        }

        MethodInfo onSendGen = AccessTools.Method(typeof(NetworkDiagnostics), "OnSend");
        if (onSendGen != null && onSendGen.IsGenericMethodDefinition)
        {
            _diagOnSendRpc = ReflectionHelpers.CreateOpenDelegate<Action<RpcMessage, int, int, int>>(
                onSendGen.MakeGenericMethod(typeof(RpcMessage)));
        }

        MethodInfo prefix = AccessTools.Method(typeof(MirrorRpcPackOnceModule), nameof(SendRPCInternalPrefix));
        if (!patchManager.PatchPrefix(Id, target, prefix, guard, config))
        {
            return MarkConflict("External Harmony patch detected on NetworkBehaviour.SendRPCInternal.");
        }

        StatusDetail = "Active (RpcMessage packed once per broadcast)";
        return true;
    }

    private static bool SendRPCInternalPrefix(
        NetworkBehaviour __instance,
        string functionFullName,
        int functionHashCode,
        NetworkWriter writer,
        int channelId,
        bool includeOwner)
    {
        long t0 = MethodSampleTable.Begin();
        try
        {
            if (!NetworkServer.active)
            {
                Debug.LogError("RPC Function " + functionFullName + " called without an active server.", __instance.gameObject);
                return false;
            }

            if (!__instance.isServer)
            {
                Debug.LogWarning("ClientRpc " + functionFullName + " called on un-spawned object: " + __instance.name, __instance.gameObject);
                return false;
            }

            NetworkIdentity identity = __instance.netIdentity;
            if (identity.observers == null || identity.observers.Count == 0)
            {
                return false;
            }

            RpcMessage rpcMessage = new RpcMessage
            {
                netId = __instance.netId,
                componentIndex = __instance.ComponentIndex,
                functionHash = (ushort)functionHashCode,
                payload = writer.ToArraySegment()
            };

            using NetworkWriterPooled packedWriter = NetworkWriterPool.Get();
            NetworkMessages.Pack(rpcMessage, packedWriter);

            int maxSize = NetworkMessages.MaxMessageSize(channelId);
            if (packedWriter.Position > maxSize)
            {
                Debug.LogError($"NetworkConnection.Send: message of type {typeof(RpcMessage)} with a size of {packedWriter.Position} bytes is larger than the max allowed message size in one batch: {maxSize}.\nThe message was dropped, please make it smaller.");
                return false;
            }

            ArraySegment<byte> segment = packedWriter.ToArraySegment();
            NetworkConnectionToClient ownerConn = identity.connectionToClient;
            int sentCount = 0;

            foreach (NetworkConnectionToClient conn in identity.observers.Values)
            {
                if (((conn != ownerConn) | includeOwner) && conn.isReady)
                {
                    conn.Send(segment, channelId);
                    sentCount++;
                }
            }

            if (sentCount > 0 && _diagOnSendRpc != null)
            {
                _diagOnSendRpc(rpcMessage, channelId, segment.Count, sentCount);
            }

            return false;
        }
        finally
        {
            MethodSampleTable.End(ProbeId.MirrorRpcSend, t0);
        }
    }
}
