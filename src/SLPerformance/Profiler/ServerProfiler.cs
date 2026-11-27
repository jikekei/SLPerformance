using System;
using System.Diagnostics;
using System.Text;
using Mirror;
using Mirror.LiteNetLib4Mirror;
using SLPerformance.Core;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace SLPerformance.Profiler;

/// <summary>
/// Low-overhead runtime server profiler hooking Unity PlayerLoop phases and Mono GC metrics.
/// Works reliably in Unity Release Dedicated Server builds where ProfilerRecorder internal counters are stripped.
/// </summary>
public sealed class ServerProfiler : IDisposable
{
    private enum PhaseId
    {
        EarlyUpdate = 0,
        FixedUpdateAll = 1,
        FixedPhysX = 2,
        FixedScript = 3,
        PreUpdate = 4,
        UpdateAll = 5,
        UpdateScript = 6,
        PreLateUpdateAll = 7,
        PreLateAnimator = 8,
        PreLateScript = 9,
        PostLateUpdate = 10,
        Count = 11
    }

    private struct PhaseAccumulator
    {
        public long StartTicks;
        public long FrameTicks;
        public long TotalTicks;
        public long MaxFrameTicks;
        public long FramesWithWork;
    }

    private static readonly string[] PhaseNames =
    {
        "EarlyUpdate",
        "FixedUpdate (Total)",
        "  ├─ PhysX.Simulate",
        "  └─ C# FixedUpdate",
        "PreUpdate (Sync/AI)",
        "Update (Total)",
        "  └─ C# Update",
        "PreLateUpdate (Total)",
        "  ├─ Animator/Director",
        "  └─ C# LateUpdate",
        "PostLateUpdate (Net)"
    };

    private readonly FrameMetricsRing _ring = new FrameMetricsRing(2048);
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private readonly PhaseAccumulator[] _phases = new PhaseAccumulator[(int)PhaseId.Count];

    private ProfilerRecorder _gcReservedRecorder;

    private long _lastFrameStartTicks;
    private long _currentFrameStartTicks;
    private long _lastCpuSampleTicks;
    private long _resetTimestampTicks;
    private TimeSpan _lastTotalCpuTime;
    private double _lastCpuPercentSingleCore;

    // Release-safe Mono allocation & GC spike tracking
    private long _prevManagedBytes;
    private long _monoAllocDeltaBytes;
    private long _monoAllocFrames;
    private long _phaseSampleFrames;
    private int _baseGen0;
    private int _baseGen1;
    private int _baseGen2;
    private int _prevGen0;
    private int _gcSpikeFrames;
    private double _maxFrameOnGcMs;
    private double _maxFrameNoGcMs;
    private double _maxWorkOnGcMs;
    private double _maxWorkNoGcMs;

    private bool _installed;
    private bool _disposed;

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    public void Start()
    {
        if (_installed)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        _resetTimestampTicks = now;
        _baseGen0 = GC.CollectionCount(0);
        _baseGen1 = GC.CollectionCount(1);
        _baseGen2 = GC.CollectionCount(2);
        _prevGen0 = _baseGen0;
        _prevManagedBytes = GC.GetTotalMemory(false);
        _lastFrameStartTicks = now;
        _lastCpuSampleTicks = now;
        _lastTotalCpuTime = _currentProcess.TotalProcessorTime;

        TryStartRecorders();
        InstallPlayerLoopHooks();
        _installed = true;
    }

    public void Reset()
    {
        _ring.Reset();
        MethodSampleTable.Reset();
        Array.Clear(_phases, 0, _phases.Length);
        _phaseSampleFrames = 0;
        _monoAllocDeltaBytes = 0;
        _monoAllocFrames = 0;
        _gcSpikeFrames = 0;
        _maxFrameOnGcMs = 0.0;
        _maxFrameNoGcMs = 0.0;
        _maxWorkOnGcMs = 0.0;
        _maxWorkNoGcMs = 0.0;
        _resetTimestampTicks = Stopwatch.GetTimestamp();
        _baseGen0 = GC.CollectionCount(0);
        _baseGen1 = GC.CollectionCount(1);
        _baseGen2 = GC.CollectionCount(2);
        _prevGen0 = _baseGen0;
        _prevManagedBytes = GC.GetTotalMemory(false);
    }

    private void TryStartRecorders()
    {
        try
        {
            _gcReservedRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");
        }
        catch (Exception ex)
        {
            SLPerformanceLog.Debug($"ProfilerRecorder init note: {ex.Message}");
        }
    }

    private void InstallPlayerLoopHooks()
    {
        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();

        // Top-level phase boundaries inserted INSIDE each built-in phase's subSystemList (proven 100% Unity 6 Release compatible)
        WrapChildrenOf(ref loop, typeof(EarlyUpdate),
            typeof(M_EarlyBegin), OnFrameBegin,
            typeof(M_EarlyEnd), () => EndPhase(PhaseId.EarlyUpdate));

        WrapChildrenOf(ref loop, typeof(FixedUpdate),
            typeof(M_FixedBegin), () => BeginPhase(PhaseId.FixedUpdateAll),
            typeof(M_FixedEnd), () => EndPhase(PhaseId.FixedUpdateAll));

        WrapSubSystem(ref loop, typeof(FixedUpdate.PhysicsFixedUpdate),
            typeof(M_PhysXBegin), () => BeginPhase(PhaseId.FixedPhysX),
            typeof(M_PhysXEnd), () => EndPhase(PhaseId.FixedPhysX));

        WrapSubSystem(ref loop, typeof(FixedUpdate.ScriptRunBehaviourFixedUpdate),
            typeof(M_FixedScriptBegin), () => BeginPhase(PhaseId.FixedScript),
            typeof(M_FixedScriptEnd), () => EndPhase(PhaseId.FixedScript));

        WrapChildrenOf(ref loop, typeof(PreUpdate),
            typeof(M_PreUpdateBegin), () => BeginPhase(PhaseId.PreUpdate),
            typeof(M_PreUpdateEnd), () => EndPhase(PhaseId.PreUpdate));

        WrapChildrenOf(ref loop, typeof(Update),
            typeof(M_UpdateBegin), () => BeginPhase(PhaseId.UpdateAll),
            typeof(M_UpdateEnd), () => EndPhase(PhaseId.UpdateAll));

        WrapSubSystem(ref loop, typeof(Update.ScriptRunBehaviourUpdate),
            typeof(M_UpdateScriptBegin), () => BeginPhase(PhaseId.UpdateScript),
            typeof(M_UpdateScriptEnd), () => EndPhase(PhaseId.UpdateScript));

        WrapChildrenOf(ref loop, typeof(PreLateUpdate),
            typeof(M_PreLateBegin), () => BeginPhase(PhaseId.PreLateUpdateAll),
            typeof(M_PreLateEnd), () => EndPhase(PhaseId.PreLateUpdateAll));

        InsertBefore(ref loop, typeof(PreLateUpdate.DirectorUpdateAnimationBegin),
            new PlayerLoopSystem { type = typeof(M_AnimBegin), updateDelegate = () => BeginPhase(PhaseId.PreLateAnimator) });
        InsertAfter(ref loop, typeof(PreLateUpdate.DirectorUpdateAnimationEnd),
            new PlayerLoopSystem { type = typeof(M_AnimEnd), updateDelegate = () => EndPhase(PhaseId.PreLateAnimator) });

        WrapSubSystem(ref loop, typeof(PreLateUpdate.ScriptRunBehaviourLateUpdate),
            typeof(M_LateScriptBegin), () => BeginPhase(PhaseId.PreLateScript),
            typeof(M_LateScriptEnd), () => EndPhase(PhaseId.PreLateScript));

        WrapChildrenOf(ref loop, typeof(PostLateUpdate),
            typeof(M_PostLateBegin), () => BeginPhase(PhaseId.PostLateUpdate),
            typeof(M_PostLateEnd), OnFrameEnd);

        PlayerLoop.SetPlayerLoop(loop);
    }

    private void RemovePlayerLoopHooks()
    {
        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        RemoveSubSystem(ref loop, typeof(M_EarlyBegin));
        RemoveSubSystem(ref loop, typeof(M_EarlyEnd));
        RemoveSubSystem(ref loop, typeof(M_FixedBegin));
        RemoveSubSystem(ref loop, typeof(M_FixedEnd));
        RemoveSubSystem(ref loop, typeof(M_PhysXBegin));
        RemoveSubSystem(ref loop, typeof(M_PhysXEnd));
        RemoveSubSystem(ref loop, typeof(M_FixedScriptBegin));
        RemoveSubSystem(ref loop, typeof(M_FixedScriptEnd));
        RemoveSubSystem(ref loop, typeof(M_PreUpdateBegin));
        RemoveSubSystem(ref loop, typeof(M_PreUpdateEnd));
        RemoveSubSystem(ref loop, typeof(M_UpdateBegin));
        RemoveSubSystem(ref loop, typeof(M_UpdateEnd));
        RemoveSubSystem(ref loop, typeof(M_UpdateScriptBegin));
        RemoveSubSystem(ref loop, typeof(M_UpdateScriptEnd));
        RemoveSubSystem(ref loop, typeof(M_PreLateBegin));
        RemoveSubSystem(ref loop, typeof(M_PreLateEnd));
        RemoveSubSystem(ref loop, typeof(M_AnimBegin));
        RemoveSubSystem(ref loop, typeof(M_AnimEnd));
        RemoveSubSystem(ref loop, typeof(M_LateScriptBegin));
        RemoveSubSystem(ref loop, typeof(M_LateScriptEnd));
        RemoveSubSystem(ref loop, typeof(M_PostLateBegin));
        RemoveSubSystem(ref loop, typeof(M_PostLateEnd));
        PlayerLoop.SetPlayerLoop(loop);
    }

    private void BeginPhase(PhaseId id)
    {
        _phases[(int)id].StartTicks = Stopwatch.GetTimestamp();
    }

    private void EndPhase(PhaseId id)
    {
        ref PhaseAccumulator p = ref _phases[(int)id];
        if (p.StartTicks != 0)
        {
            long delta = Stopwatch.GetTimestamp() - p.StartTicks;
            if (delta > 0)
            {
                p.FrameTicks += delta;
            }
            p.StartTicks = 0;
        }
    }

    private void OnFrameBegin()
    {
        long now = Stopwatch.GetTimestamp();
        _currentFrameStartTicks = now;
        _phases[(int)PhaseId.EarlyUpdate].StartTicks = now;

        if (_lastFrameStartTicks != 0)
        {
            long elapsedCpuCheck = now - _lastCpuSampleTicks;
            if (elapsedCpuCheck >= Stopwatch.Frequency)
            {
                TimeSpan cpuNow = _currentProcess.TotalProcessorTime;
                double wallMs = elapsedCpuCheck * TicksToMs;
                double cpuMs = (cpuNow - _lastTotalCpuTime).TotalMilliseconds;
                _lastCpuPercentSingleCore = wallMs > 0.0 ? (cpuMs / wallMs) * 100.0 : 0.0;
                _lastTotalCpuTime = cpuNow;
                _lastCpuSampleTicks = now;
            }
        }
    }

    private void OnFrameEnd()
    {
        EndPhase(PhaseId.PostLateUpdate);
        long now = Stopwatch.GetTimestamp();

        if (_currentFrameStartTicks == 0)
        {
            _lastFrameStartTicks = now;
            return;
        }

        double frameIntervalMs = (_currentFrameStartTicks - _lastFrameStartTicks) * TicksToMs;
        double workMs = (now - _currentFrameStartTicks) * TicksToMs;
        _lastFrameStartTicks = _currentFrameStartTicks;

        if (frameIntervalMs > 0.0 && frameIntervalMs < 10000.0)
        {
            _ring.Record(frameIntervalMs, workMs);
        }

        // Flush per-frame phase ticks into long-term accumulators
        _phaseSampleFrames++;
        if (MethodSampleTable.Enabled)
        {
            MethodSampleTable.IncrementFrame();
        }
        for (int i = 0; i < (int)PhaseId.Count; i++)
        {
            ref PhaseAccumulator p = ref _phases[i];
            long ft = p.FrameTicks;
            p.FrameTicks = 0;
            if (ft > 0)
            {
                p.TotalTicks += ft;
                p.FramesWithWork++;
                if (ft > p.MaxFrameTicks)
                {
                    p.MaxFrameTicks = ft;
                }
            }
        }

        // Release-safe Mono allocation & GC correlation tracking
        long managedBytes = GC.GetTotalMemory(false);
        int gen0Now = GC.CollectionCount(0);
        if (gen0Now > _prevGen0)
        {
            _gcSpikeFrames++;
            if (frameIntervalMs > _maxFrameOnGcMs && frameIntervalMs < 10000.0) _maxFrameOnGcMs = frameIntervalMs;
            if (workMs > _maxWorkOnGcMs && workMs < 10000.0) _maxWorkOnGcMs = workMs;
            _prevGen0 = gen0Now;
        }
        else
        {
            if (frameIntervalMs > _maxFrameNoGcMs && frameIntervalMs < 10000.0) _maxFrameNoGcMs = frameIntervalMs;
            if (workMs > _maxWorkNoGcMs && workMs < 10000.0) _maxWorkNoGcMs = workMs;

            long delta = managedBytes - _prevManagedBytes;
            if (delta >= 0)
            {
                _monoAllocDeltaBytes += delta;
                _monoAllocFrames++;
            }
        }

        _prevManagedBytes = managedBytes;
    }

    public string FormatStatsReport()
    {
        FrameMetricsSnapshot snap = _ring.ComputeSnapshot();
        double elapsedSec = Math.Max(0.001, (Stopwatch.GetTimestamp() - _resetTimestampTicks) / (double)Stopwatch.Frequency);
        int cores = Math.Max(1, Environment.ProcessorCount);
        double allCoresPct = _lastCpuPercentSingleCore / cores;

        StringBuilder sb = new StringBuilder(1024);
        sb.AppendLine("=== SLPerformance Runtime Profiler ===");
        sb.AppendLine($"Samples         : {snap.SampleCount} frames ({elapsedSec:F1}s window) | TargetTickrate={ServerStatic.ServerTickrate} | MirrorActualTPS={NetworkServer.actualTickRate}");
        sb.AppendLine($"Measured TPS    : {snap.AverageTps:F2} | MeanFrame={snap.MeanFrameMs:F2}ms | P50={snap.P50Ms:F2}ms | P95={snap.P95Ms:F2}ms | P99={snap.P99Ms:F2}ms | P99.9={snap.P999Ms:F2}ms | Max={snap.MaxFrameMs:F2}ms");
        sb.AppendLine($"MainThread Work : Mean={snap.MeanWorkMs:F2}ms | P99={snap.P99WorkMs:F2}ms | ProcessCPU={_lastCpuPercentSingleCore:F1}% (1 core=100%, {allCoresPct:F1}% of {cores} cores)");
        sb.AppendLine($"Frame Spikes    : >16.67ms={snap.SpikesOver16Ms} | >33.33ms={snap.SpikesOver33Ms} | >50ms={snap.SpikesOver50Ms}");

        double avgAllocKb = _monoAllocFrames > 0 ? (_monoAllocDeltaBytes / (double)_monoAllocFrames) / 1024.0 : 0.0;
        double allocMbPerSec = (avgAllocKb * snap.AverageTps) / 1024.0;
        double reservedMb = _gcReservedRecorder.Valid ? _gcReservedRecorder.LastValue / (1024.0 * 1024.0) : 0.0;
        long managedMb = GC.GetTotalMemory(false) / (1024L * 1024L);
        int g0 = GC.CollectionCount(0) - _baseGen0;
        int g1 = GC.CollectionCount(1) - _baseGen1;
        int g2 = GC.CollectionCount(2) - _baseGen2;
        double gcEverySec = g0 > 0 ? elapsedSec / g0 : 0.0;

        sb.AppendLine($"Memory & GC     : Managed={managedMb}MB | GCReserved={reservedMb:F1}MB | MonoAlloc~{avgAllocKb:F1}KB/frame ({allocMbPerSec:F2}MB/s)");
        sb.AppendLine($"GC Impact       : GC(0/1/2)={g0}/{g1}/{g2} ({(g0 > 0 ? $"every {gcEverySec:F2}s" : "none")}) | MaxWork(GC={_maxWorkOnGcMs:F2}ms, NoGC={_maxWorkNoGcMs:F2}ms) | MaxFrame(GC={_maxFrameOnGcMs:F2}ms, NoGC={_maxFrameNoGcMs:F2}ms)");
        sb.AppendLine($"World State     : Players={ReferenceHub.AllHubs.Count} | MirrorConns={NetworkServer.connections.Count} | SpawnedNetIds={NetworkServer.spawned.Count}");

        if (LiteNetLib4MirrorCore.Host != null && LiteNetLib4MirrorCore.Host.EnableStatistics)
        {
            var stats = LiteNetLib4MirrorCore.Host.Statistics;
            sb.AppendLine($"LiteNetLib Net  : RxPkts={stats.PacketsReceived} TxPkts={stats.PacketsSent} | RxKB={stats.BytesReceived / 1024.0:F1} TxKB={stats.BytesSent / 1024.0:F1} | Loss={stats.PacketLossPercent}%");
        }
        else
        {
            sb.AppendLine("LiteNetLib Net  : Statistics disabled (run `slperf netstats on` to enable LiteNetLib counters).");
        }

        sb.AppendLine("--- PlayerLoop Main-Thread Phase Breakdown (Release-Safe) ---");
        AppendPhaseTable(sb);
        return sb.ToString();
    }

    public string FormatPhasesReport()
    {
        StringBuilder sb = new StringBuilder(512);
        sb.AppendLine($"=== SLPerformance PlayerLoop Phases ({_phaseSampleFrames} frames) ===");
        AppendPhaseTable(sb);
        return sb.ToString();
    }

    private void AppendPhaseTable(StringBuilder sb)
    {
        long frames = Math.Max(1, _phaseSampleFrames);
        for (int i = 0; i < (int)PhaseId.Count; i++)
        {
            PhaseAccumulator p = _phases[i];
            double meanMs = (p.TotalTicks * TicksToMs) / frames;
            double maxMs = p.MaxFrameTicks * TicksToMs;
            sb.AppendLine($"  {PhaseNames[i],-24} | Mean={meanMs,7:F3}ms/frame | Max={maxMs,7:F2}ms");
        }
    }

    public string FormatHotspotReport()
    {
        StringBuilder sb = new StringBuilder(1024);
        long frames = Math.Max(1, MethodSampleTable.SampledFrames);
        sb.AppendLine($"=== SLPerformance Method Probes (Enabled={MethodSampleTable.Enabled}, SampledFrames={MethodSampleTable.SampledFrames}, Hooks={ProfilerProbeHooks.HookedCount}) ===");
        double ticksPerUs = Stopwatch.Frequency / 1_000_000.0;

        var rows = new System.Collections.Generic.List<(ProbeId Id, MethodSampleTable.Slot Slot)>((int)ProbeId.Count);
        var zeroNames = new System.Collections.Generic.List<string>(16);
        for (int i = 0; i < (int)ProbeId.Count; i++)
        {
            ProbeId id = (ProbeId)i;
            MethodSampleTable.Slot slot = MethodSampleTable.ReadSlot(id);
            if (slot.Calls > 0)
            {
                rows.Add((id, slot));
            }
            else
            {
                zeroNames.Add(id.ToString());
            }
        }

        rows.Sort((a, b) => b.Slot.TotalTicks.CompareTo(a.Slot.TotalTicks));

        if (rows.Count == 0)
        {
            sb.AppendLine("  (No samples recorded yet. Run `slperf probes on`, wait 15-20s, then run `slperf hotspots`.)");
        }
        else
        {
            sb.AppendLine("  Probe                  | ms/frame  | AllocKB/fr | Calls/fr | Avg(us)  | Max(us)   | Total(ms)");
            for (int i = 0; i < rows.Count; i++)
            {
                var (id, slot) = rows[i];
                double totalMs = slot.TotalTicks * TicksToMs;
                double msPerFrame = totalMs / frames;
                double allocKbPerFrame = (slot.AllocBytes / 1024.0) / frames;
                double callsPerFrame = slot.Calls / (double)frames;
                double avgUs = (slot.TotalTicks / (double)slot.Calls) / ticksPerUs;
                double maxUs = slot.MaxTicks / ticksPerUs;
                sb.AppendLine($"  {id,-22} | {msPerFrame,7:F3}ms | {allocKbPerFrame,8:F2}KB | {callsPerFrame,8:F1} | {avgUs,8:F1} | {maxUs,9:F1} | {totalMs,8:F1}");
            }
        }

        if (zeroNames.Count > 0)
        {
            sb.AppendLine($"  Zero-call probes ({zeroNames.Count}): {string.Join(", ", zeroNames)}");
        }

        sb.AppendLine("  Note: StaticLateUpdate includes FpcDistLateUpdate; StaticUpdate includes FpcMotorUpdate.");
        return sb.ToString();
    }

    private static bool WrapChildrenOf(
        ref PlayerLoopSystem loop,
        Type parentType,
        Type beginMarker,
        PlayerLoopSystem.UpdateFunction onBegin,
        Type endMarker,
        PlayerLoopSystem.UpdateFunction onEnd)
    {
        if (loop.type == parentType)
        {
            PlayerLoopSystem[] existing = loop.subSystemList ?? Array.Empty<PlayerLoopSystem>();
            PlayerLoopSystem[] updated = new PlayerLoopSystem[existing.Length + 2];
            updated[0] = new PlayerLoopSystem { type = beginMarker, updateDelegate = onBegin };
            Array.Copy(existing, 0, updated, 1, existing.Length);
            updated[updated.Length - 1] = new PlayerLoopSystem { type = endMarker, updateDelegate = onEnd };
            loop.subSystemList = updated;
            return true;
        }

        if (loop.subSystemList != null)
        {
            for (int i = 0; i < loop.subSystemList.Length; i++)
            {
                if (WrapChildrenOf(ref loop.subSystemList[i], parentType, beginMarker, onBegin, endMarker, onEnd))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool WrapSubSystem(
        ref PlayerLoopSystem loop,
        Type targetType,
        Type beginMarker,
        PlayerLoopSystem.UpdateFunction onBegin,
        Type endMarker,
        PlayerLoopSystem.UpdateFunction onEnd)
    {
        bool b = InsertBefore(ref loop, targetType, new PlayerLoopSystem { type = beginMarker, updateDelegate = onBegin });
        bool e = InsertAfter(ref loop, targetType, new PlayerLoopSystem { type = endMarker, updateDelegate = onEnd });
        return b && e;
    }

    private static bool InsertBefore(ref PlayerLoopSystem loop, Type targetType, PlayerLoopSystem toInsert)
    {
        if (loop.subSystemList == null)
        {
            return false;
        }

        for (int i = 0; i < loop.subSystemList.Length; i++)
        {
            if (loop.subSystemList[i].type == targetType)
            {
                PlayerLoopSystem[] existing = loop.subSystemList;
                PlayerLoopSystem[] updated = new PlayerLoopSystem[existing.Length + 1];
                Array.Copy(existing, 0, updated, 0, i);
                updated[i] = toInsert;
                Array.Copy(existing, i, updated, i + 1, existing.Length - i);
                loop.subSystemList = updated;
                return true;
            }

            if (InsertBefore(ref loop.subSystemList[i], targetType, toInsert))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InsertAfter(ref PlayerLoopSystem loop, Type targetType, PlayerLoopSystem toInsert)
    {
        if (loop.subSystemList == null)
        {
            return false;
        }

        for (int i = 0; i < loop.subSystemList.Length; i++)
        {
            if (loop.subSystemList[i].type == targetType)
            {
                PlayerLoopSystem[] existing = loop.subSystemList;
                PlayerLoopSystem[] updated = new PlayerLoopSystem[existing.Length + 1];
                Array.Copy(existing, 0, updated, 0, i + 1);
                updated[i + 1] = toInsert;
                Array.Copy(existing, i + 1, updated, i + 2, existing.Length - (i + 1));
                loop.subSystemList = updated;
                return true;
            }

            if (InsertAfter(ref loop.subSystemList[i], targetType, toInsert))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveSubSystem(ref PlayerLoopSystem loop, Type markerType)
    {
        if (loop.subSystemList == null)
        {
            return;
        }

        int removeCount = 0;
        for (int i = 0; i < loop.subSystemList.Length; i++)
        {
            if (loop.subSystemList[i].type == markerType)
            {
                removeCount++;
            }
            else
            {
                RemoveSubSystem(ref loop.subSystemList[i], markerType);
            }
        }

        if (removeCount == 0)
        {
            return;
        }

        PlayerLoopSystem[] updated = new PlayerLoopSystem[loop.subSystemList.Length - removeCount];
        int dst = 0;
        for (int i = 0; i < loop.subSystemList.Length; i++)
        {
            if (loop.subSystemList[i].type != markerType)
            {
                updated[dst++] = loop.subSystemList[i];
            }
        }

        loop.subSystemList = updated;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_installed)
        {
            try
            {
                RemovePlayerLoopHooks();
            }
            catch
            {
            }

            _installed = false;
        }

        if (_gcReservedRecorder.Valid) _gcReservedRecorder.Dispose();
        _currentProcess.Dispose();
    }

    private struct M_EarlyBegin { }
    private struct M_EarlyEnd { }
    private struct M_FixedBegin { }
    private struct M_FixedEnd { }
    private struct M_PhysXBegin { }
    private struct M_PhysXEnd { }
    private struct M_FixedScriptBegin { }
    private struct M_FixedScriptEnd { }
    private struct M_PreUpdateBegin { }
    private struct M_PreUpdateEnd { }
    private struct M_UpdateBegin { }
    private struct M_UpdateEnd { }
    private struct M_UpdateScriptBegin { }
    private struct M_UpdateScriptEnd { }
    private struct M_PreLateBegin { }
    private struct M_PreLateEnd { }
    private struct M_AnimBegin { }
    private struct M_AnimEnd { }
    private struct M_LateScriptBegin { }
    private struct M_LateScriptEnd { }
    private struct M_PostLateBegin { }
    private struct M_PostLateEnd { }
}
