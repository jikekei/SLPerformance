using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using LiteNetLib;
using Mirror;
using Mirror.LiteNetLib4Mirror;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using SLPerformance.Profiler;
using UnityEngine;

namespace SLPerformance.Network;

/// <summary>
/// OPT-04: Eliminates LiteNetLib4MirrorTransport's redundant NetworkPollThread (30ms Thread.Sleep jitter)
/// and per-packet closure + Action allocations by calling Host.PollEvents() directly on the Unity main thread
/// during LiteNetLib4MirrorTransport.LateUpdate() (exact same PlayerLoop phase as vanilla).
/// Also wakes ConnectionRequestThread immediately on new connection requests via AutoResetEvent.
/// </summary>
public sealed class TransportDirectPollModule : OptimizationModuleBase
{
    public override string Id => "net.transport_direct_poll";
    public override string Name => "LiteNetLib Direct Main-Thread Poll & Zero-Closure Receive";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Removes 30ms NetworkPollThread sleep bursts and per-packet Action closure allocations while keeping ConnectionRequestThread async.";

    private static int _mainThreadId = -1;
    private static readonly AutoResetEvent ConnectionRequestSignal = new AutoResetEvent(false);
    private static Func<ConcurrentQueue<ConnectionRequest>> _getConnectionRequests;
    private static MethodInfo _processConnectionRequest;
    private static Func<bool> _getPolling;
    private static Func<LiteNetLib4MirrorTransport, ConcurrentQueue<Action>> _getEvents;
    private static Action<LiteNetLib4MirrorTransport, ConcurrentQueue<Action>> _setEvents;
    private static Action<SocketError> _setLastError;
    private static Action<SocketError> _setLastDisconnectError;
    private static Action<DisconnectReason> _setLastDisconnectReason;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.NetTransportDirectPoll;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        Type transportType = typeof(LiteNetLib4MirrorTransport);
        Type serverType = typeof(LiteNetLib4MirrorServer);
        Type coreType = typeof(LiteNetLib4MirrorCore);

        MethodInfo startThreads = AccessTools.Method(transportType, "StartThreads", Type.EmptyTypes);
        MethodInfo lateUpdate = AccessTools.Method(transportType, "LateUpdate", Type.EmptyTypes);
        MethodInfo onPeerConnected = AccessTools.Method(serverType, "OnPeerConnected", new[] { typeof(NetPeer) });
        MethodInfo onNetworkReceive = AccessTools.Method(serverType, "OnNetworkReceive", new[] { typeof(NetPeer), typeof(NetPacketReader), typeof(byte), typeof(DeliveryMethod) });
        MethodInfo onNetworkError = AccessTools.Method(serverType, "OnNetworkError", new[] { typeof(IPEndPoint), typeof(SocketError) });
        MethodInfo onPeerDisconnected = AccessTools.Method(serverType, "OnPeerDisconnected", new[] { typeof(NetPeer), typeof(DisconnectInfo) });
        MethodInfo onConnReq = AccessTools.Method(serverType, "OnConnectionRequest", new[] { typeof(ConnectionRequest) });
        MethodInfo connReqLoop = AccessTools.Method(serverType, "ConnectionRequestLoop", Type.EmptyTypes);

        _getConnectionRequests = ReflectionHelpers.CreateStaticFieldGetter<ConcurrentQueue<ConnectionRequest>>(serverType, "_connectionRequests");
        _processConnectionRequest = AccessTools.Method(transportType, "ProcessConnectionRequest", new[] { typeof(ConnectionRequest) });
        _getPolling = ReflectionHelpers.CreateStaticFieldGetter<bool>(transportType, "Polling");
        _getEvents = ReflectionHelpers.CreateInstanceFieldGetter<LiteNetLib4MirrorTransport, ConcurrentQueue<Action>>(transportType, "Events");
        _setEvents = ReflectionHelpers.CreateInstanceFieldSetter<LiteNetLib4MirrorTransport, ConcurrentQueue<Action>>(transportType, "Events");
        _setLastError = ReflectionHelpers.CreateOpenDelegate<Action<SocketError>>(AccessTools.PropertySetter(coreType, nameof(LiteNetLib4MirrorCore.LastError)));
        _setLastDisconnectError = ReflectionHelpers.CreateOpenDelegate<Action<SocketError>>(AccessTools.PropertySetter(coreType, nameof(LiteNetLib4MirrorCore.LastDisconnectError)));
        _setLastDisconnectReason = ReflectionHelpers.CreateOpenDelegate<Action<DisconnectReason>>(AccessTools.PropertySetter(coreType, nameof(LiteNetLib4MirrorCore.LastDisconnectReason)));

        if (startThreads == null || lateUpdate == null || onPeerConnected == null || onNetworkReceive == null ||
            onNetworkError == null || onPeerDisconnected == null || onConnReq == null || connReqLoop == null ||
            _getConnectionRequests == null || _processConnectionRequest == null ||
            _getPolling == null || _getEvents == null || _setEvents == null ||
            _setLastError == null || _setLastDisconnectError == null || _setLastDisconnectReason == null)
        {
            return MarkIncompatible("LiteNetLib4MirrorTransport / LiteNetLib4MirrorServer methods not found.");
        }

        bool ok =
            patchManager.PatchPrefix(Id, startThreads, AccessTools.Method(typeof(TransportDirectPollModule), nameof(StartThreadsPrefix)), guard, config) &&
            patchManager.PatchPrefix(Id, lateUpdate, AccessTools.Method(typeof(TransportDirectPollModule), nameof(LateUpdatePrefix)), guard, config) &&
            patchManager.PatchPrefix(Id, onPeerConnected, AccessTools.Method(typeof(TransportDirectPollModule), nameof(OnPeerConnectedPrefix)), guard, config) &&
            patchManager.PatchPrefix(Id, onNetworkReceive, AccessTools.Method(typeof(TransportDirectPollModule), nameof(OnNetworkReceivePrefix)), guard, config) &&
            patchManager.PatchPrefix(Id, onNetworkError, AccessTools.Method(typeof(TransportDirectPollModule), nameof(OnNetworkErrorPrefix)), guard, config) &&
            patchManager.PatchPrefix(Id, onPeerDisconnected, AccessTools.Method(typeof(TransportDirectPollModule), nameof(OnPeerDisconnectedPrefix)), guard, config) &&
            patchManager.PatchPrefix(Id, onConnReq, AccessTools.Method(typeof(TransportDirectPollModule), nameof(OnConnectionRequestPrefix)), guard, config) &&
            patchManager.PatchPrefix(Id, connReqLoop, AccessTools.Method(typeof(TransportDirectPollModule), nameof(ConnectionRequestLoopPrefix)), guard, config);

        if (!ok)
        {
            patchManager.UnpatchModule(Id);
            return MarkConflict("External Harmony patch detected on LiteNetLib4MirrorTransport/Server.");
        }

        StatusDetail = "Active (direct LateUpdate PollEvents, 0 closure allocs, event-driven handshake wakeup)";
        return true;
    }

    private static bool IsOnMainThread()
    {
        return Thread.CurrentThread.ManagedThreadId == _mainThreadId;
    }

    private static bool StartThreadsPrefix(LiteNetLib4MirrorTransport __instance)
    {
        // Only bypass NetworkPollThread on dedicated/host server; keep original path for pure client.
        if (LiteNetLib4MirrorCore.State != LiteNetLib4MirrorCore.States.Server)
        {
            return true;
        }

        _setEvents(__instance, new ConcurrentQueue<Action>());
        SLPerformanceLog.Debug("Bypassed redundant 30ms NetworkPollThread on server; polling directly in LateUpdate.");
        return false;
    }

    private static bool LateUpdatePrefix(LiteNetLib4MirrorTransport __instance)
    {
        if (!_getPolling())
        {
            return false;
        }

        long t0 = MethodSampleTable.Begin();
        try
        {
            // Drain any external events enqueued by third-party code
            ConcurrentQueue<Action> events = _getEvents(__instance);
            if (events != null)
            {
                while (events.TryDequeue(out Action action))
                {
                    action();
                }
            }

            // Directly poll LiteNetLib thread-safe event queue on main thread
            if (LiteNetLib4MirrorCore.State == LiteNetLib4MirrorCore.States.Server)
            {
                NetManager host = LiteNetLib4MirrorCore.Host;
                if (host != null)
                {
                    host.PollEvents();
                }
            }
        }
        finally
        {
            MethodSampleTable.End(ProbeId.TransportPoll, t0);
        }

        return false;
    }

    private static bool OnPeerConnectedPrefix(NetPeer peer)
    {
        if (!IsOnMainThread())
        {
            return true;
        }

        int connId = peer.Id + 1;
        LiteNetLib4MirrorServer.Peers[connId] = peer;
        LiteNetLib4MirrorTransport.Singleton.OnServerConnected(connId);
        return false;
    }

    private static bool OnNetworkReceivePrefix(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliverymethod)
    {
        if (!IsOnMainThread())
        {
            return true;
        }

        LiteNetLib4MirrorTransport.Singleton.OnServerDataReceived(peer.Id + 1, reader.GetRemainingBytesSegment(), -1);
        reader.Recycle();
        return false;
    }

    private static bool OnNetworkErrorPrefix(IPEndPoint endpoint, SocketError socketerror)
    {
        if (!IsOnMainThread())
        {
            return true;
        }

        _setLastError(socketerror);
        foreach (NetPeer peer in LiteNetLib4MirrorServer.Peers.Values)
        {
            if (peer != null && peer.EndPoint.Equals(endpoint))
            {
                int connId = peer.Id + 1;
                LiteNetLib4MirrorTransport.Singleton.OnServerError(connId, TransportError.Unexpected, $"Socket exception: {(int)socketerror}");
                LiteNetLib4MirrorTransport.Singleton.onServerSocketError?.Invoke(connId, socketerror);
                break;
            }
        }

        return false;
    }

    private static bool OnPeerDisconnectedPrefix(NetPeer peer, DisconnectInfo disconnectinfo)
    {
        if (!IsOnMainThread())
        {
            return true;
        }

        _setLastDisconnectError(disconnectinfo.SocketErrorCode);
        _setLastDisconnectReason(disconnectinfo.Reason);
        int connId = peer.Id + 1;
        LiteNetLib4MirrorTransport.Singleton.OnServerDisconnected(connId);
        LiteNetLib4MirrorServer.Peers.TryRemove(connId, out _);
        return false;
    }

    private static bool OnConnectionRequestPrefix(ConnectionRequest request)
    {
        ConcurrentQueue<ConnectionRequest> queue = _getConnectionRequests();
        queue?.Enqueue(request);
        ConnectionRequestSignal.Set();
        return false;
    }

    private static bool ConnectionRequestLoopPrefix()
    {
        Debug.Log("ConnectionRequestLoop started (SLPerformance event-driven)");
        ConcurrentQueue<ConnectionRequest> queue = _getConnectionRequests();
        object[] invokeArgs = new object[1];

        while (LiteNetLib4MirrorCore.State == LiteNetLib4MirrorCore.States.Server)
        {
            try
            {
                while (queue != null && queue.TryDequeue(out ConnectionRequest req))
                {
                    try
                    {
                        invokeArgs[0] = req;
                        _processConnectionRequest.Invoke(LiteNetLib4MirrorTransport.Singleton, invokeArgs);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("Malformed join request! Rejecting... Error:" + ex.Message + "\n" + ex.StackTrace);
                        req.Reject();
                    }
                }

                ConnectionRequestSignal.WaitOne(30);
            }
            catch (ThreadAbortException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        Debug.Log("ConnectionRequestLoop stopped");
        return false;
    }
}
