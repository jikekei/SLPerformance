using System;
using System.Diagnostics;

namespace SLPerformance.Profiler;

public readonly struct FrameMetricsSnapshot
{
    public readonly int SampleCount;
    public readonly double AverageTps;
    public readonly double MeanFrameMs;
    public readonly double P50Ms;
    public readonly double P95Ms;
    public readonly double P99Ms;
    public readonly double P999Ms;
    public readonly double MaxFrameMs;
    public readonly double MeanWorkMs;
    public readonly double P99WorkMs;
    public readonly int SpikesOver16Ms;
    public readonly int SpikesOver33Ms;
    public readonly int SpikesOver50Ms;

    public FrameMetricsSnapshot(
        int sampleCount,
        double averageTps,
        double meanFrameMs,
        double p50Ms,
        double p95Ms,
        double p99Ms,
        double p999Ms,
        double maxFrameMs,
        double meanWorkMs,
        double p99WorkMs,
        int spikesOver16Ms,
        int spikesOver33Ms,
        int spikesOver50Ms)
    {
        SampleCount = sampleCount;
        AverageTps = averageTps;
        MeanFrameMs = meanFrameMs;
        P50Ms = p50Ms;
        P95Ms = p95Ms;
        P99Ms = p99Ms;
        P999Ms = p999Ms;
        MaxFrameMs = maxFrameMs;
        MeanWorkMs = meanWorkMs;
        P99WorkMs = p99WorkMs;
        SpikesOver16Ms = spikesOver16Ms;
        SpikesOver33Ms = spikesOver33Ms;
        SpikesOver50Ms = spikesOver50Ms;
    }
}

/// <summary>
/// Fixed-size power-of-two ring buffer for zero-allocation frame interval & main-thread work time sampling.
/// </summary>
public sealed class FrameMetricsRing
{
    private readonly double[] _frameMs;
    private readonly double[] _workMs;
    private readonly double[] _sortScratch;
    private readonly int _mask;
    private int _writeIndex;
    private int _count;

    public FrameMetricsRing(int capacityPowerOfTwo = 2048)
    {
        if (capacityPowerOfTwo < 16 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of two >= 16.", nameof(capacityPowerOfTwo));
        }

        _frameMs = new double[capacityPowerOfTwo];
        _workMs = new double[capacityPowerOfTwo];
        _sortScratch = new double[capacityPowerOfTwo];
        _mask = capacityPowerOfTwo - 1;
    }

    public void Record(double frameIntervalMs, double workMs)
    {
        int idx = _writeIndex & _mask;
        _frameMs[idx] = frameIntervalMs;
        _workMs[idx] = workMs;
        _writeIndex++;
        if (_count < _frameMs.Length)
        {
            _count++;
        }
    }

    public void Reset()
    {
        _writeIndex = 0;
        _count = 0;
    }

    public FrameMetricsSnapshot ComputeSnapshot()
    {
        int n = _count;
        if (n == 0)
        {
            return default;
        }

        double sumFrame = 0.0;
        double sumWork = 0.0;
        int over16 = 0;
        int over33 = 0;
        int over50 = 0;

        for (int i = 0; i < n; i++)
        {
            double f = _frameMs[i];
            sumFrame += f;
            sumWork += _workMs[i];
            if (f > 16.67) over16++;
            if (f > 33.33) over33++;
            if (f > 50.0) over50++;
            _sortScratch[i] = f;
        }

        Array.Sort(_sortScratch, 0, n);
        double meanFrame = sumFrame / n;
        double avgTps = meanFrame > 0.0 ? 1000.0 / meanFrame : 0.0;
        double p50 = Percentile(_sortScratch, n, 0.50);
        double p95 = Percentile(_sortScratch, n, 0.95);
        double p99 = Percentile(_sortScratch, n, 0.99);
        double p999 = Percentile(_sortScratch, n, 0.999);
        double maxFrame = _sortScratch[n - 1];

        for (int i = 0; i < n; i++)
        {
            _sortScratch[i] = _workMs[i];
        }

        Array.Sort(_sortScratch, 0, n);
        double meanWork = sumWork / n;
        double p99Work = Percentile(_sortScratch, n, 0.99);

        return new FrameMetricsSnapshot(n, avgTps, meanFrame, p50, p95, p99, p999, maxFrame, meanWork, p99Work, over16, over33, over50);
    }

    private static double Percentile(double[] sorted, int count, double p)
    {
        if (count == 1)
        {
            return sorted[0];
        }

        double pos = p * (count - 1);
        int lo = (int)pos;
        int hi = lo + 1 < count ? lo + 1 : lo;
        double frac = pos - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }
}

public enum ProbeId
{
    MirrorBroadcast = 0,
    FpcDistributor = 1,
    TransportPoll = 2,
    VoiceReceive = 3,
    MirrorRpcSend = 4,
    FpcMotorUpdate = 5,
    StatusEffects = 6,
    RoomLookup = 7,
    WaypointLookup = 8,
    VisionAndScpTrackers = 9,
    AdminToysAndPickups = 10,

    // Breakdown probes (installed lazily while `slperf probes on`)
    StaticLateUpdate = 11,
    FpcDistLateUpdate = 12,
    CullingLateUpdate = 13,
    SubctrlLateUpdate = 14,
    MecLateUpdate = 15,
    SubroutineLateUpdate = 16,
    WaypointFollowLate = 17,
    StaticUpdate = 18,
    MecUpdate = 19,
    AnimModelUpdate = 20,
    SubctrlUpdate = 21,
    InventoryUpdate = 22,
    PlayerStatsUpdate = 23,
    VoicePlaybackUpdate = 24,
    VolumeUpdate = 25,
    InsufficientLight = 26,
    TemplateUpdate = 27,
    DoorUpdate = 28,
    AutosyncLateUpdate = 29,
    ConsoleLateUpdate = 30,
    MainThreadDispatch = 31,
    Count = 32
}

public static class MethodSampleTable
{
    public struct Slot
    {
        public long Calls;
        public long TotalTicks;
        public long MaxTicks;
        public long AllocBytes;
    }

    public struct ProbeToken
    {
        public long StartTicks;
        public long StartMem;
        public int StartGc0;
    }

    private static readonly Slot[] Slots = new Slot[(int)ProbeId.Count];
    public static volatile bool Enabled;
    public static long SampledFrames { get; private set; }

    public static void IncrementFrame()
    {
        if (Enabled)
        {
            SampledFrames++;
        }
    }

    public static long Begin()
    {
        return Enabled ? Stopwatch.GetTimestamp() : 0L;
    }

    public static void End(ProbeId id, long startTimestamp)
    {
        if (!Enabled || startTimestamp == 0L)
        {
            return;
        }

        long delta = Stopwatch.GetTimestamp() - startTimestamp;
        ref Slot s = ref Slots[(int)id];
        s.Calls++;
        s.TotalTicks += delta;
        if (delta > s.MaxTicks)
        {
            s.MaxTicks = delta;
        }
    }

    public static ProbeToken BeginSample()
    {
        if (!Enabled)
        {
            return default;
        }

        return new ProbeToken
        {
            StartTicks = Stopwatch.GetTimestamp(),
            StartMem = GC.GetTotalMemory(false),
            StartGc0 = GC.CollectionCount(0)
        };
    }

    public static void EndSample(ProbeId id, in ProbeToken token)
    {
        if (!Enabled || token.StartTicks == 0L)
        {
            return;
        }

        long deltaTicks = Stopwatch.GetTimestamp() - token.StartTicks;
        long allocDelta = 0L;
        if (GC.CollectionCount(0) == token.StartGc0)
        {
            long memDelta = GC.GetTotalMemory(false) - token.StartMem;
            if (memDelta > 0L)
            {
                allocDelta = memDelta;
            }
        }

        ref Slot s = ref Slots[(int)id];
        s.Calls++;
        s.TotalTicks += deltaTicks;
        if (deltaTicks > s.MaxTicks)
        {
            s.MaxTicks = deltaTicks;
        }

        s.AllocBytes += allocDelta;
    }

    public static Slot ReadSlot(ProbeId id)
    {
        return Slots[(int)id];
    }

    public static void Reset()
    {
        Array.Clear(Slots, 0, Slots.Length);
        SampledFrames = 0;
    }
}
