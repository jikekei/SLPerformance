using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using LabApi.Events;
using Mirror;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using RelativePositioning;
using SLPerformance.Collections;
using SLPerformance.Network;
using SLPerformance.Serialization;
using SLPerformance.Threading;
using UnityEngine;

namespace SLPerformance.Benchmarks;

internal static class Program
{
    private const string DefaultManagedDir = @"C:\Program Files (x86)\Steam\steamapps\common\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed";

    private static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += ResolveGameAssemblies;

        Console.WriteLine("==========================================================================================");
        Console.WriteLine(" SLPerformance Offline A/B Micro-Benchmark Suite (Release / .NET 8 x64 harness)");
        Console.WriteLine(" NOTE: Offline synthetic micro-benchmarks isolating pure algorithmic/memory hot paths.");
        Console.WriteLine("       Live SCP:SL dedicated server TPS/load tests require real server runs.");
        Console.WriteLine("==========================================================================================");
        Console.WriteLine($"CPU Logical Cores: {Environment.ProcessorCount} | 64-bit: {Environment.Is64BitProcess} | GC Server: {System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine();
        Console.WriteLine($"{"Benchmark",-42} | {"Baseline",-13} | {"Optimized",-13} | {"Speedup",-8} | {"Alloc Delta"}");
        Console.WriteLine(new string('-', 106));

        RunBenchmarks();

        Console.WriteLine(new string('-', 106));
        Console.WriteLine("All offline micro-benchmarks completed.");
        return 0;
    }

    private static void RunBenchmarks()
    {
        AccessTools.Method(typeof(NetworkServer).Assembly.GetType("Mirror.GeneratedNetworkCode"), "InitReadWriters")?.Invoke(null, null);
        BenchOpt01RpcPackOnce();
        BenchOpt02FpcSyncDataEncode();
        BenchOpt03DirtyBroadcastSweep();
        BenchOpt06DelegateInvocationCache();
        BenchOpt10BatcherZeroCopy();
        BenchOpt12LiteNetPeerLookup();
        BenchOpt13CompactWaypointScan();
        BenchOpt17ParallelFpcEncode(60);
        BenchOpt17ParallelFpcEncode(100);
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

        string harmonyCandidate = Environment.GetEnvironmentVariable("HarmonyDllPath");
        if (shortName == "0Harmony" && File.Exists(harmonyCandidate)) return alc.LoadFromAssemblyPath(Path.GetFullPath(harmonyCandidate));

        string managedCandidate = Path.Combine(Environment.GetEnvironmentVariable("ScpslManagedDir") ?? DefaultManagedDir, shortName + ".dll");
        if (File.Exists(managedCandidate)) return alc.LoadFromAssemblyPath(managedCandidate);

        return null;
    }

    private static (double usPerOp, long bytesPerOp) Measure(Action action, int warmup, int iterations)
    {
        for (int i = 0; i < warmup; i++) action();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            action();
        }
        sw.Stop();
        long allocAfter = GC.GetTotalAllocatedBytes(precise: true);

        double us = (sw.Elapsed.TotalMilliseconds * 1000.0) / iterations;
        long bytes = Math.Max(0, (allocAfter - allocBefore) / iterations);
        return (us, bytes);
    }

    private static void PrintRow(string name, (double us, long bytes) baseRes, (double us, long bytes) optRes)
    {
        double speedup = optRes.us > 0 ? baseRes.us / optRes.us : 0;
        Console.WriteLine(
            $"{name,-42} | {baseRes.us,9:F2} us | {optRes.us,9:F2} us | {speedup,6:F2}x | {baseRes.bytes} B -> {optRes.bytes} B");
    }

    private static void BenchOpt01RpcPackOnce()
    {
        const int receivers = 60;
        byte[] payload = new byte[32];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        var rpc = new RpcMessage { netId = 10, componentIndex = 2, functionHash = 0x4567, payload = new ArraySegment<byte>(payload) };
        int sink = 0;

        var baseRes = Measure(() =>
        {
            for (int i = 0; i < receivers; i++)
            {
                NetworkWriterPooled w = NetworkWriterPool.Get();
                NetworkMessages.Pack(rpc, w);
                sink += w.Position;
                NetworkWriterPool.Return(w);
            }
        }, warmup: 200, iterations: 10_000);

        var optRes = Measure(() =>
        {
            NetworkWriterPooled w = NetworkWriterPool.Get();
            NetworkMessages.Pack(rpc, w);
            ArraySegment<byte> seg = w.ToArraySegment();
            for (int i = 0; i < receivers; i++)
            {
                sink += seg.Count;
            }
            NetworkWriterPool.Return(w);
        }, warmup: 200, iterations: 10_000);

        GC.KeepAlive(sink);
        PrintRow("OPT-01 RPC Pack Once (60 receivers)", baseRes, optRes);
    }

    private static void BenchOpt02FpcSyncDataEncode()
    {
        const int players = 60;
        var prev = new FpcSyncData[players];
        var rels = new RelativePosition[players];
        var rotH = new ushort[players];
        var rotV = new ushort[players];
        for (int i = 0; i < players; i++)
        {
            rels[i] = new RelativePosition(1, (short)(i * 10), 0, (short)(i * 5), false);
            rotH[i] = (ushort)(i * 900);
            rotV[i] = 32768;
            prev[i] = FpcSyncDataFactory.Build(in prev[i], PlayerMovementState.Walking, true, in rels[i], rotH[i], rotV[i]);
        }

        // Simulate baseline: 60 receivers x 59 targets doing Dictionary<uint, Dictionary<uint, FpcSyncData>> outer+inner lookup + FpcSyncData build + NetworkWriterPool rent/return (roleSync unused)
        var nested = new Dictionary<uint, Dictionary<uint, FpcSyncData>>(players);
        for (uint r = 0; r < players; r++)
        {
            var inner = new Dictionary<uint, FpcSyncData>(players);
            for (uint t = 0; t < players; t++) inner[t] = prev[t];
            nested[r] = inner;
        }

        var writer = new NetworkWriter();
        var baseRes = Measure(() =>
        {
            for (uint r = 0; r < players; r++)
            {
                writer.Reset();
                for (uint t = 0; t < players; t++)
                {
                    if (r == t) continue;
                    // Baseline: redundant outer dict lookup + unused NetworkWriterPool rent/return per pair + redundant rot math
                    FpcSyncData p = nested[r][t];
                    ushort h = (ushort)Mathf.RoundToInt(Mathf.InverseLerp(0f, 360f, (t * 6f) % 360f) * 65535f);
                    ushort v = (ushort)Mathf.RoundToInt(Mathf.InverseLerp(-88f, 88f, 0f) * 65535f);
                    FpcSyncData next = FpcSyncDataFactory.Build(in p, PlayerMovementState.Walking, true, in rels[t], h, v);
                    nested[r][t] = next;
                    NetworkWriterPooled unused = NetworkWriterPool.Get();
                    NetworkWriterPool.Return(unused);
                    next.Write(writer);
                }
            }
        }, warmup: 20, iterations: 500);

        var optRes = Measure(() =>
        {
            // O(P) precompute rotH/rotV once
            for (int t = 0; t < players; t++)
            {
                rotH[t] = (ushort)Mathf.RoundToInt(Mathf.InverseLerp(0f, 360f, (t * 6f) % 360f) * 65535f);
                rotV[t] = (ushort)Mathf.RoundToInt(Mathf.InverseLerp(-88f, 88f, 0f) * 65535f);
            }
            for (uint r = 0; r < players; r++)
            {
                writer.Reset();
                Dictionary<uint, FpcSyncData> inner = nested[r];
                for (uint t = 0; t < players; t++)
                {
                    if (r == t) continue;
                    inner.TryGetValue(t, out FpcSyncData p);
                    FpcSyncData next = FpcSyncDataFactory.Build(in p, PlayerMovementState.Walking, true, in rels[t], rotH[t], rotV[t]);
                    inner[t] = next;
                    next.Write(writer);
                }
            }
        }, warmup: 20, iterations: 500);

        PrintRow("OPT-02 FPC 60x60 Delta + Snapshot Sync", baseRes, optRes);
    }

    private static void BenchOpt03DirtyBroadcastSweep()
    {
        const int entities = 3000;
        const int dirtyCount = 20;
        const int connections = 60;

        bool[] isDirty = new bool[entities];
        for (int i = 0; i < dirtyCount; i++) isDirty[i * (entities / dirtyCount)] = true;
        int[][] observing = new int[connections][];
        for (int c = 0; c < connections; c++)
        {
            observing[c] = new int[entities];
            for (int e = 0; e < entities; e++) observing[c][e] = e;
        }

        int[] dirtyList = new int[dirtyCount];
        long sink = 0;

        // Baseline: O(P * E) = 60 * 3000 = 180,000 checks per tick
        var baseRes = Measure(() =>
        {
            for (int c = 0; c < connections; c++)
            {
                int[] obs = observing[c];
                for (int i = 0; i < obs.Length; i++)
                {
                    if (isDirty[obs[i]]) sink++;
                }
            }
        }, warmup: 50, iterations: 2000);

        // Optimized: O(E + D * P) = 3000 + 20 * 60 = 4,200 ops per tick
        var optRes = Measure(() =>
        {
            int dCount = 0;
            for (int e = 0; e < entities; e++)
            {
                if (isDirty[e]) dirtyList[dCount++] = e;
            }
            for (int d = 0; d < dCount; d++)
            {
                for (int c = 0; c < connections; c++) sink++;
            }
        }, warmup: 50, iterations: 2000);

        GC.KeepAlive(sink);
        PrintRow("OPT-03 Dirty Sweep (E=3000,D=20,P=60)", baseRes, optRes);
    }

    private static void BenchOpt06DelegateInvocationCache()
    {
        int count = 0;
        LabEventHandler h = () => count++;
        h += () => count++;
        h += () => count++;
        h += () => count++;

        var baseRes = Measure(() =>
        {
            Delegate[] list = h.GetInvocationList();
            for (int i = 0; i < list.Length; i++) ((LabEventHandler)list[i])();
        }, warmup: 1000, iterations: 200_000);

        var optRes = Measure(() =>
        {
            Delegate[] list = DelegateInvocationCache.GetInvocationListCached(h);
            for (int i = 0; i < list.Length; i++) ((LabEventHandler)list[i])();
        }, warmup: 1000, iterations: 200_000);

        GC.KeepAlive(count);
        PrintRow("OPT-06 DelegateInvocationCache (4 subs)", baseRes, optRes);
    }

    private static void BenchOpt10BatcherZeroCopy()
    {
        var batcher = new Batcher(1200);
        byte[] msg = new byte[120];
        var seg = new ArraySegment<byte>(msg);
        var getBatch = SLPerformance.Patching.ReflectionHelpers.CreateInstanceFieldGetter<Batcher, NetworkWriterPooled>(typeof(Batcher), "batch");
        var setBatch = SLPerformance.Patching.ReflectionHelpers.CreateInstanceFieldSetter<Batcher, NetworkWriterPooled>(typeof(Batcher), "batch");
        int sink = 0;

        var baseRes = Measure(() =>
        {
            for (int i = 0; i < 8; i++) batcher.AddMessage(seg, 1.0);
            NetworkWriterPooled outW = NetworkWriterPool.Get();
            while (batcher.GetBatch(outW))
            {
                sink += outW.Position;
                outW.Reset();
            }
            NetworkWriterPool.Return(outW);
        }, warmup: 500, iterations: 50_000);

        var optRes = Measure(() =>
        {
            for (int i = 0; i < 8; i++) batcher.AddMessage(seg, 1.0);
            NetworkWriterPooled cur = getBatch(batcher);
            if (cur != null)
            {
                setBatch(batcher, null);
                sink += cur.Position;
                NetworkWriterPool.Return(cur);
            }
        }, warmup: 500, iterations: 50_000);

        GC.KeepAlive(sink);
        PrintRow("OPT-10 Mirror Batcher Zero-Copy Flush", baseRes, optRes);
    }

    private static void BenchOpt12LiteNetPeerLookup()
    {
        var dictEp = new Dictionary<IPEndPoint, int>(64);
        var dictV4 = new Dictionary<ulong, int>(64);

        byte[] sockAddr = new byte[16];
        sockAddr[0] = 2;
        sockAddr[2] = (byte)(7777 >> 8);
        sockAddr[3] = (byte)(7777 & 0xFF);
        sockAddr[4] = 198;
        sockAddr[5] = 51;
        sockAddr[6] = 100;
        sockAddr[7] = 42;

        var epTarget = new IPEndPoint(IPAddress.Parse("198.51.100.42"), 7777);
        dictEp[epTarget] = 7;
        LiteNetPeerLookupModule.TryBuildV4Key(epTarget.Address, 7777, out ulong k4Target);
        dictV4[k4Target] = 7;

        var tempEp = new IPEndPoint(IPAddress.Any, 0);
        int sink = 0;

        var baseRes = Measure(() =>
        {
            ushort port = (ushort)((sockAddr[2] << 8) | sockAddr[3]);
            uint raw = (uint)(sockAddr[4] | (sockAddr[5] << 8) | (sockAddr[6] << 16) | (sockAddr[7] << 24));
            tempEp.Port = port;
            tempEp.Address = new IPAddress(raw);
            if (dictEp.TryGetValue(tempEp, out int v)) sink += v;
        }, warmup: 1000, iterations: 200_000);

        var optRes = Measure(() =>
        {
            ushort port = (ushort)((sockAddr[2] << 8) | sockAddr[3]);
            uint raw = (uint)(sockAddr[4] | (sockAddr[5] << 8) | (sockAddr[6] << 16) | (sockAddr[7] << 24));
            ulong key = LiteNetPeerLookupModule.PackV4Key(raw, port);
            if (dictV4.TryGetValue(key, out int v)) sink += v;
        }, warmup: 1000, iterations: 200_000);

        GC.KeepAlive(sink);
        PrintRow("OPT-12 LiteNet Raw IPv4 Peer Lookup", baseRes, optRes);
    }

    private static void BenchOpt13CompactWaypointScan()
    {
        bool[] set = new bool[255];
        Vector3[] positions = new Vector3[255];
        int activeCount = 12;
        Vector3[] compact = new Vector3[activeCount];
        for (int i = 0; i < activeCount; i++)
        {
            int slot = 1 + i * 19;
            set[slot] = true;
            positions[slot] = new Vector3(i * 20f, 0f, i * 15f);
            compact[i] = positions[slot];
        }

        Vector3 query = new Vector3(85f, 1.5f, 62f);
        float sink = 0f;

        var baseRes = Measure(() =>
        {
            float best = float.MaxValue;
            for (int b = 1; b < 255; b++)
            {
                if (set[b])
                {
                    float d = (positions[b] - query).sqrMagnitude;
                    if (!(d > best)) best = d;
                }
            }
            sink += best;
        }, warmup: 1000, iterations: 200_000);

        var optRes = Measure(() =>
        {
            float best = float.MaxValue;
            for (int i = 0; i < activeCount; i++)
            {
                float d = (compact[i] - query).sqrMagnitude;
                if (!(d > best)) best = d;
            }
            sink += best;
        }, warmup: 1000, iterations: 200_000);

        GC.KeepAlive(sink);
        PrintRow("OPT-13 Compact Waypoint Scan (12/254)", baseRes, optRes);
    }

    private static void BenchOpt17ParallelFpcEncode(int players)
    {
        var pos = new Vector3[players];
        var rel = new RelativePosition[players];
        var rotH = new ushort[players];
        var rotV = new ushort[players];
        var prev = new FpcSyncData[players][];
        var writers = new NetworkWriter[players];

        for (int i = 0; i < players; i++)
        {
            pos[i] = new Vector3((i % 10) * 5f, 0f, (i / 10) * 5f);
            rel[i] = new RelativePosition(1, (short)(i * 12), 0, (short)(i * 8), false);
            rotH[i] = (ushort)(i * 512);
            rotV[i] = 32768;
            prev[i] = new FpcSyncData[players];
            writers[i] = new NetworkWriter();
        }

        void EncodeRange(int start, int end)
        {
            for (int r = start; r < end; r++)
            {
                NetworkWriter w = writers[r];
                w.Reset();
                w.WriteUShort(0x1234);
                w.WriteUShort(0);
                ushort vis = 0;
                Vector3 rPos = pos[r];
                FpcSyncData[] rPrev = prev[r];

                for (int t = 0; t < players; t++)
                {
                    if (r == t) continue;
                    Vector3 diff = rPos - pos[t];
                    if (diff.x * diff.x + diff.y * diff.y + diff.z * diff.z <= 1300f)
                    {
                        FpcSyncData d = FpcSyncDataFactory.Build(in rPrev[t], PlayerMovementState.Walking, true, in rel[t], rotH[t], rotV[t]);
                        rPrev[t] = d;
                        w.WriteRecyclablePlayerId(new RecyclablePlayerId(t + 1));
                        d.Write(w);
                        vis++;
                    }
                }
                w.buffer[2] = (byte)(vis & 0xFF);
                w.buffer[3] = (byte)(vis >> 8);
            }
        }

        var baseRes = Measure(() => EncodeRange(0, players), warmup: 30, iterations: 400);

        using (var pool = new BoundedWorkerPool(Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2)), "SLPerf-Bench"))
        {
            var optRes = Measure(() =>
            {
                pool.ExecuteParallelFor(players, (worker, start, end) => EncodeRange(start, end));
            }, warmup: 30, iterations: 400);

            PrintRow($"OPT-17 Parallel Pure FPC Encode (P={players})", baseRes, optRes);
        }
    }
}
