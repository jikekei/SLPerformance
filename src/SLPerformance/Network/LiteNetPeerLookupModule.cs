using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using LiteNetLib;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;

namespace SLPerformance.Network;

/// <summary>
/// OPT-12: Zero-allocation IPv4/IPv6 endpoint -> LiteNetPeer fast lookup table in LiteNetManager.NativeReceiveLogic,
/// avoiding `new IPAddress(...)` allocation on every single incoming UDP packet from already-connected clients.
/// </summary>
public sealed class LiteNetPeerLookupModule : OptimizationModuleBase
{
    public override string Id => "net.litenet_peer_lookup_noalloc";
    public override string Name => "LiteNetLib Zero-Alloc Native Socket Peer Lookup";
    public override OptimizationTier Tier => OptimizationTier.Tier2;
    public override string Description => "Eliminates per-packet IPAddress heap allocations in LiteNetManager.NativeReceiveLogic via raw-address lookup.";

    public readonly struct Ipv6Key : IEquatable<Ipv6Key>
    {
        public readonly ulong Hi;
        public readonly ulong Lo;
        public readonly uint Scope;
        public readonly ushort Port;

        public Ipv6Key(ulong hi, ulong lo, uint scope, ushort port)
        {
            Hi = hi;
            Lo = lo;
            Scope = scope;
            Port = port;
        }

        public bool Equals(Ipv6Key other) => Hi == other.Hi && Lo == other.Lo && Scope == other.Scope && Port == other.Port;
        public override bool Equals(object obj) => obj is Ipv6Key other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)(Hi ^ (Hi >> 32));
                h = (h * 397) ^ (int)(Lo ^ (Lo >> 32));
                h = (h * 397) ^ (int)Scope;
                return (h * 397) ^ Port;
            }
        }
    }

    private sealed class PeerIndex
    {
        public readonly ReaderWriterLockSlim Lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        public readonly Dictionary<ulong, LiteNetPeer> V4 = new Dictionary<ulong, LiteNetPeer>(128);
        public readonly Dictionary<Ipv6Key, LiteNetPeer> V6 = new Dictionary<Ipv6Key, LiteNetPeer>(32);

        public void Upsert(LiteNetPeer peer)
        {
            if (peer == null) return;
            Lock.EnterWriteLock();
            try
            {
                if (TryBuildV4Key(peer.Address, (ushort)peer.Port, out ulong k4))
                {
                    V4[k4] = peer;
                }
                else if (TryBuildV6Key(peer.Address, (ushort)peer.Port, out Ipv6Key k6))
                {
                    V6[k6] = peer;
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
        }

        public void Remove(LiteNetPeer peer)
        {
            if (peer == null) return;
            Lock.EnterWriteLock();
            try
            {
                if (TryBuildV4Key(peer.Address, (ushort)peer.Port, out ulong k4))
                {
                    V4.Remove(k4);
                }
                else if (TryBuildV6Key(peer.Address, (ushort)peer.Port, out Ipv6Key k6))
                {
                    V6.Remove(k6);
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
        }

        public void Clear()
        {
            Lock.EnterWriteLock();
            try
            {
                V4.Clear();
                V6.Clear();
            }
            finally
            {
                Lock.ExitWriteLock();
            }
        }

        public bool TryGetV4(ulong key, out LiteNetPeer peer)
        {
            Lock.EnterReadLock();
            try
            {
                return V4.TryGetValue(key, out peer);
            }
            finally
            {
                Lock.ExitReadLock();
            }
        }

        public bool TryGetV6(in Ipv6Key key, out LiteNetPeer peer)
        {
            Lock.EnterReadLock();
            try
            {
                return V6.TryGetValue(key, out peer);
            }
            finally
            {
                Lock.ExitReadLock();
            }
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LiteNetManager, PeerIndex> Indices =
        new System.Runtime.CompilerServices.ConditionalWeakTable<LiteNetManager, PeerIndex>();

    private static Func<LiteNetManager, Socket> _getUdpV4;
    private static Func<LiteNetManager, Socket> _getUdpV6;
    private static Func<LiteNetManager, bool> _getIsRunning;
    private static Func<LiteNetManager, int, object> _poolGetPacket;
    private static Func<LiteNetManager, SocketException, bool> _processError;
    private static Action<LiteNetManager, object, IPEndPoint> _onMessageReceived;
    private static Func<object, byte[]> _getPacketRawData;
    private static Action<object, int> _setPacketSize;

    private static MethodInfo _nativeRecvFromMethod;
    private static MethodInfo _nativeGetSocketErrorMethod;
    private static bool _nativeUnixMode;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.NetLiteNetPeerLookupNoAlloc;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        Type mgrType = typeof(LiteNetManager);
        Type nativeSocketType = AccessTools.TypeByName("LiteNetLib.NativeSocket");
        Type netPacketType = AccessTools.TypeByName("LiteNetLib.NetPacket");
        if (nativeSocketType == null || netPacketType == null)
        {
            return MarkIncompatible("LiteNetLib.NativeSocket or NetPacket type not found.");
        }

        MethodInfo nativeRecvLogic = AccessTools.Method(mgrType, "NativeReceiveLogic", Type.EmptyTypes);
        MethodInfo addPeer = AccessTools.Method(mgrType, "AddPeer", new[] { typeof(LiteNetPeer) });
        MethodInfo removePeer = AccessTools.Method(mgrType, "RemovePeer", new[] { typeof(LiteNetPeer), typeof(bool) });
        MethodInfo clearPeerSet = AccessTools.Method(mgrType, "ClearPeerSet", Type.EmptyTypes);
        MethodInfo finishEpChange = AccessTools.Method(typeof(LiteNetPeer), "FinishEndPointChange", new[] { typeof(IPEndPoint) });

        _getUdpV4 = ReflectionHelpers.CreateInstanceFieldGetter<LiteNetManager, Socket>(mgrType, "_udpSocketv4");
        _getUdpV6 = ReflectionHelpers.CreateInstanceFieldGetter<LiteNetManager, Socket>(mgrType, "_udpSocketv6");
        _getIsRunning = ReflectionHelpers.CreateInstanceFieldGetter<LiteNetManager, bool>(mgrType, "_isRunning");
        _poolGetPacket = CompilePoolGetPacket(AccessTools.Method(mgrType, "PoolGetPacket", new[] { typeof(int) }));
        _processError = ReflectionHelpers.CreateOpenDelegate<Func<LiteNetManager, SocketException, bool>>(AccessTools.Method(mgrType, "ProcessError", new[] { typeof(SocketException) }));
        _onMessageReceived = CompileOnMessageReceived(AccessTools.Method(mgrType, "OnMessageReceived", new[] { netPacketType, typeof(IPEndPoint) }), netPacketType);
        _getPacketRawData = CompileObjectFieldGetter<byte[]>(netPacketType, "RawData");
        _setPacketSize = CompileObjectFieldSetter<int>(netPacketType, "Size");

        _nativeRecvFromMethod = AccessTools.Method(nativeSocketType, "RecvFrom", new[] { typeof(IntPtr), typeof(byte[]), typeof(int), typeof(byte[]), typeof(int).MakeByRefType() });
        _nativeGetSocketErrorMethod = AccessTools.Method(nativeSocketType, "GetSocketError", Type.EmptyTypes);
        FieldInfo unixModeField = AccessTools.Field(nativeSocketType, "UnixMode");

        if (nativeRecvLogic == null || addPeer == null || removePeer == null || clearPeerSet == null ||
            finishEpChange == null || _getUdpV4 == null || _getUdpV6 == null || _getIsRunning == null ||
            _poolGetPacket == null || _processError == null || _onMessageReceived == null ||
            _getPacketRawData == null || _setPacketSize == null ||
            _nativeRecvFromMethod == null || _nativeGetSocketErrorMethod == null || unixModeField == null)
        {
            return MarkIncompatible("LiteNetManager / NativeSocket / NetPacket internal members not found.");
        }

        _nativeUnixMode = (bool)unixModeField.GetValue(null);

        bool ok =
            patchManager.PatchPostfix(Id, addPeer, AccessTools.Method(typeof(LiteNetPeerLookupModule), nameof(AddPeerPostfix)), guard, config) &&
            patchManager.PatchPrefix(Id, removePeer, AccessTools.Method(typeof(LiteNetPeerLookupModule), nameof(RemovePeerPrefix)), guard, config, checkConflict: false) &&
            patchManager.PatchPostfix(Id, clearPeerSet, AccessTools.Method(typeof(LiteNetPeerLookupModule), nameof(ClearPeerSetPostfix)), guard, config) &&
            patchManager.PatchPrefix(Id, finishEpChange, AccessTools.Method(typeof(LiteNetPeerLookupModule), nameof(FinishEpChangePrefix)), guard, config, checkConflict: false) &&
            patchManager.PatchPostfix(Id, finishEpChange, AccessTools.Method(typeof(LiteNetPeerLookupModule), nameof(FinishEpChangePostfix)), guard, config) &&
            patchManager.PatchPrefix(Id, nativeRecvLogic, AccessTools.Method(typeof(LiteNetPeerLookupModule), nameof(NativeReceiveLogicPrefix)), guard, config);

        if (!ok)
        {
            patchManager.UnpatchModule(Id);
            return MarkConflict("External Harmony patch detected on LiteNetManager.NativeReceiveLogic.");
        }

        StatusDetail = "Active (zero-alloc IPv4/IPv6 sockaddr peer index)";
        return true;
    }

    private static PeerIndex GetIndex(LiteNetManager mgr)
    {
        return Indices.GetOrCreateValue(mgr);
    }

    private static void AddPeerPostfix(LiteNetManager __instance, LiteNetPeer peer)
    {
        GetIndex(__instance).Upsert(peer);
    }

    private static void RemovePeerPrefix(LiteNetManager __instance, LiteNetPeer peer)
    {
        GetIndex(__instance).Remove(peer);
    }

    private static void ClearPeerSetPostfix(LiteNetManager __instance)
    {
        GetIndex(__instance).Clear();
    }

    private static void FinishEpChangePrefix(LiteNetPeer __instance)
    {
        if (__instance?.NetManager != null)
        {
            GetIndex(__instance.NetManager).Remove(__instance);
        }
    }

    private static void FinishEpChangePostfix(LiteNetPeer __instance)
    {
        if (__instance?.NetManager != null)
        {
            GetIndex(__instance.NetManager).Upsert(__instance);
        }
    }

    private delegate int NativeRecvFromDelegate(IntPtr socketHandle, byte[] pinnedBuffer, int len, byte[] socketAddress, ref int socketAddressSize);
    private delegate SocketError NativeGetSocketErrorDelegate();

    private static bool NativeReceiveLogicPrefix(LiteNetManager __instance)
    {
        NativeRecvFromDelegate recvFrom = (NativeRecvFromDelegate)Delegate.CreateDelegate(typeof(NativeRecvFromDelegate), _nativeRecvFromMethod);
        NativeGetSocketErrorDelegate getErr = (NativeGetSocketErrorDelegate)Delegate.CreateDelegate(typeof(NativeGetSocketErrorDelegate), _nativeGetSocketErrorMethod);

        Socket udpV4 = _getUdpV4(__instance);
        Socket udpV6 = _getUdpV6(__instance);
        IntPtr handleV4 = udpV4.Handle;
        IntPtr handleV6 = udpV6 != null ? udpV6.Handle : IntPtr.Zero;
        byte[] addrV4 = new byte[16];
        byte[] addrV6 = new byte[28];
        IPEndPoint tempEndPoint = new IPEndPoint(IPAddress.Any, 0);
        List<Socket> selectList = new List<Socket>(2);
        PeerIndex peerIndex = GetIndex(__instance);
        object packet = _poolGetPacket(__instance, NetConstants.MaxPacketSize);

        while (_getIsRunning(__instance))
        {
            try
            {
                if (udpV6 == null)
                {
                    if (!ReceiveOne(handleV4, addrV4))
                    {
                        break;
                    }
                    continue;
                }

                bool gotAny = false;
                if (udpV4.Available != 0 || selectList.Contains(udpV4))
                {
                    if (!ReceiveOne(handleV4, addrV4))
                    {
                        break;
                    }
                    gotAny = true;
                }

                if (udpV6.Available != 0 || selectList.Contains(udpV6))
                {
                    if (!ReceiveOne(handleV6, addrV6))
                    {
                        break;
                    }
                    gotAny = true;
                }

                selectList.Clear();
                if (!gotAny)
                {
                    selectList.Add(udpV4);
                    selectList.Add(udpV6);
                    Socket.Select(selectList, null, null, __instance.ReceivePollingTime);
                }
            }
            catch (SocketException ex)
            {
                if (_processError(__instance, ex)) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (ThreadAbortException)
            {
                break;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[NM] SocketReceiveThread error: " + ex);
            }
        }

        return false;

        bool ReceiveOne(IntPtr socketHandle, byte[] addrBuf)
        {
            int addrLen = addrBuf.Length;
            int size = recvFrom(socketHandle, _getPacketRawData(packet), NetConstants.MaxPacketSize, addrBuf, ref addrLen);
            _setPacketSize(packet, size);
            if (size == 0)
            {
                return true;
            }

            if (size == -1)
            {
                return !_processError(__instance, new SocketException((int)getErr()));
            }

            short family = (short)((addrBuf[1] << 8) | addrBuf[0]);
            ushort port = (ushort)((addrBuf[2] << 8) | addrBuf[3]);
            bool isV6 = (_nativeUnixMode && family == 10) || (!_nativeUnixMode && family == 23);

            LiteNetPeer peer;
            if (!isV6)
            {
                uint ipv4Raw = (uint)(addrBuf[4] | (addrBuf[5] << 8) | (addrBuf[6] << 16) | (addrBuf[7] << 24));
                ulong key4 = PackV4Key(ipv4Raw, port);
                if (peerIndex.TryGetV4(key4, out peer))
                {
                    _onMessageReceived(__instance, packet, peer);
                    packet = _poolGetPacket(__instance, NetConstants.MaxPacketSize);
                    return true;
                }

                tempEndPoint.Port = port;
                tempEndPoint.Address = new IPAddress(ipv4Raw);
            }
            else
            {
                uint scope = (uint)((addrBuf[27] << 24) | (addrBuf[26] << 16) | (addrBuf[25] << 8) | addrBuf[24]);
                Ipv6Key key6 = PackV6KeyFromSockAddr(addrBuf, scope, port);
                if (peerIndex.TryGetV6(in key6, out peer))
                {
                    _onMessageReceived(__instance, packet, peer);
                    packet = _poolGetPacket(__instance, NetConstants.MaxPacketSize);
                    return true;
                }

                tempEndPoint.Port = port;
                byte[] v6Bytes = new byte[16];
                Buffer.BlockCopy(addrBuf, 8, v6Bytes, 0, 16);
                tempEndPoint.Address = new IPAddress(v6Bytes, scope);
            }

            // Unknown endpoint (new handshake or A2S query): pass tempEndPoint & rotate
            _onMessageReceived(__instance, packet, tempEndPoint);
            tempEndPoint = new IPEndPoint(IPAddress.Any, 0);
            packet = _poolGetPacket(__instance, NetConstants.MaxPacketSize);
            return true;
        }
    }

    public static ulong PackV4Key(uint ipv4Raw, ushort port)
    {
        return ((ulong)ipv4Raw << 16) | port;
    }

    public static bool TryBuildV4Key(IPAddress addr, ushort port, out ulong key)
    {
        if (addr == null || addr.AddressFamily != AddressFamily.InterNetwork)
        {
            key = 0;
            return false;
        }

        byte[] b = addr.GetAddressBytes();
        uint ipv4Raw = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        key = PackV4Key(ipv4Raw, port);
        return true;
    }

    public static Ipv6Key PackV6KeyFromSockAddr(byte[] addrBuf, uint scope, ushort port)
    {
        ulong hi = BitConverter.ToUInt64(addrBuf, 8);
        ulong lo = BitConverter.ToUInt64(addrBuf, 16);
        return new Ipv6Key(hi, lo, scope, port);
    }

    public static bool TryBuildV6Key(IPAddress addr, ushort port, out Ipv6Key key)
    {
        if (addr == null || addr.AddressFamily != AddressFamily.InterNetworkV6)
        {
            key = default;
            return false;
        }

        byte[] b = addr.GetAddressBytes();
        ulong hi = BitConverter.ToUInt64(b, 0);
        ulong lo = BitConverter.ToUInt64(b, 8);
        key = new Ipv6Key(hi, lo, (uint)addr.ScopeId, port);
        return true;
    }

    private static Func<LiteNetManager, int, object> CompilePoolGetPacket(MethodInfo m)
    {
        if (m == null) return null;
        DynamicMethod dm = new DynamicMethod(
            "SLPerf_LiteNet_PoolGetPacket",
            typeof(object),
            new[] { typeof(LiteNetManager), typeof(int) },
            typeof(LiteNetManager).Module,
            skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(m.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, m);
        il.Emit(OpCodes.Ret);
        return (Func<LiteNetManager, int, object>)dm.CreateDelegate(typeof(Func<LiteNetManager, int, object>));
    }

    private static Action<LiteNetManager, object, IPEndPoint> CompileOnMessageReceived(MethodInfo m, Type netPacketType)
    {
        if (m == null) return null;
        DynamicMethod dm = new DynamicMethod(
            "SLPerf_LiteNet_OnMessageReceived",
            typeof(void),
            new[] { typeof(LiteNetManager), typeof(object), typeof(IPEndPoint) },
            typeof(LiteNetManager).Module,
            skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, netPacketType);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(m.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, m);
        il.Emit(OpCodes.Ret);
        return (Action<LiteNetManager, object, IPEndPoint>)dm.CreateDelegate(typeof(Action<LiteNetManager, object, IPEndPoint>));
    }

    private static Func<object, TField> CompileObjectFieldGetter<TField>(Type declaringType, string fieldName)
    {
        FieldInfo f = AccessTools.Field(declaringType, fieldName);
        if (f == null || !typeof(TField).IsAssignableFrom(f.FieldType)) return null;
        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_ObjGet_{declaringType.Name}_{fieldName}",
            typeof(TField),
            new[] { typeof(object) },
            declaringType.Module,
            skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, declaringType);
        il.Emit(OpCodes.Ldfld, f);
        il.Emit(OpCodes.Ret);
        return (Func<object, TField>)dm.CreateDelegate(typeof(Func<object, TField>));
    }

    private static Action<object, TField> CompileObjectFieldSetter<TField>(Type declaringType, string fieldName)
    {
        FieldInfo f = AccessTools.Field(declaringType, fieldName);
        if (f == null || !f.FieldType.IsAssignableFrom(typeof(TField))) return null;
        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_ObjSet_{declaringType.Name}_{fieldName}",
            typeof(void),
            new[] { typeof(object), typeof(TField) },
            declaringType.Module,
            skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, declaringType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, f);
        il.Emit(OpCodes.Ret);
        return (Action<object, TField>)dm.CreateDelegate(typeof(Action<object, TField>));
    }
}
