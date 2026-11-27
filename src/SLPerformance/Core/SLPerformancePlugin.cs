using System;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Plugins.Enums;
using SLPerformance.Compatibility;
using SLPerformance.Entities;
using SLPerformance.Memory;
using SLPerformance.Network;
using SLPerformance.Patching;
using SLPerformance.Physics;
using SLPerformance.Profiler;
using SLPerformance.Threading;

namespace SLPerformance.Core;

/// <summary>
/// Main LabAPI plugin entry point for SLPerformance.
/// Loads with LoadPriority.Lowest so Harmony conflict detection sees patches installed by EXILED / AntiCheat / other plugins first.
/// </summary>
public sealed class SLPerformancePlugin : Plugin<SLPerformanceConfig>
{
    public static SLPerformancePlugin Instance { get; private set; }

    public override string Name => "SLPerformance";
    public override string Description => "Lithium-style native server performance optimization suite for SCP:SL Dedicated Server.";
    public override string Author => "SLPerformance Research";
    public override Version Version => new Version(0, 18, 0);
    public override Version RequiredApiVersion => new Version(1, 1, 0);

    // Load last so CompatibilityGuard detects any Harmony patches applied earlier by other plugins.
    public override LoadPriority Priority => LoadPriority.Lowest;

    public PatchManager Patches { get; private set; }
    public CompatibilityGuard Compatibility { get; private set; }
    public ModuleRegistry Registry { get; private set; }
    public ServerProfiler Profiler { get; private set; }

    public override void Enable()
    {
        Instance = this;
        SLPerformanceLog.DebugEnabled = Config.Debug;

        if (!Config.Enabled)
        {
            SLPerformanceLog.Info("Master switch is disabled in config.yml; skipping all modules.");
            return;
        }

        Patches = new PatchManager("com.slperformance.server");
        Compatibility = new CompatibilityGuard();
        Registry = new ModuleRegistry();

        RegisterAllModules(Registry);

        if (Config.EnableProfiler)
        {
            Profiler = new ServerProfiler();
            Profiler.Start();
            if (Config.EnableMethodProbes)
            {
                ProfilerProbeHooks.Install();
                MethodSampleTable.Enabled = true;
            }
            else
            {
                MethodSampleTable.Enabled = false;
            }
        }

        Registry.ApplyConfigured(Patches, Compatibility, Config);
        SLPerformanceLog.Info(
            $"Initialized on SCP:SL {Compatibility.DetectedGameVersion} (Unity {UnityEngine.Application.unityVersion}). " +
            "Use `slperf status` or `slperf stats` in server console for live diagnostics.");
    }

    public override void Disable()
    {
        try
        {
            MethodSampleTable.Enabled = false;
            ProfilerProbeHooks.Uninstall();
            Registry?.RevertAll(Patches);
            Patches?.UnpatchAll();
            Profiler?.Dispose();
            Profiler = null;
        }
        finally
        {
            Instance = null;
        }
    }

    public static void RegisterAllModules(ModuleRegistry registry)
    {
        // --- Tier 1: High ROI, exact equivalence, low risk ---
        registry.Register(new MirrorRpcPackOnceModule());          // OPT-01
        registry.Register(new FpcDistributorFastModule());         // OPT-02
        registry.Register(new MirrorDirtyBroadcastModule());       // OPT-03
        registry.Register(new TransportDirectPollModule());        // OPT-04
        registry.Register(new VoiceBatchModule());                 // OPT-05
        registry.Register(new LabApiEventCacheModule());           // OPT-06
        registry.Register(new EffectsInterfaceIndexModule());      // OPT-07
        registry.Register(new VisionAndScpTrackersModule());       // OPT-08
        registry.Register(new InventoryStatsOpusModule());         // OPT-15
        registry.Register(new HeadlessCharacterAnimatorModule());  // OPT-18
        registry.Register(new MassRoleChangeAccelModule());        // OPT-21
        registry.Register(new PlayerJoinAccelModule());            // OPT-22

        // --- Tier 2: High ROI, medium complexity ---
        registry.Register(new StatusEffectSleepModule());          // OPT-09
        registry.Register(new BatcherZeroCopyModule());            // OPT-10
        registry.Register(new FpcMotorFastPathModule());           // OPT-11
        registry.Register(new LiteNetPeerLookupModule());          // OPT-12
        registry.Register(new RoomAndWaypointAccelModule());       // OPT-13
        registry.Register(new AdminToyPickupHazardModule());       // OPT-14
        registry.Register(new IdleWorldAnimatorSleepModule());     // OPT-19
        registry.Register(new HeadlessWorldSleepModule());         // OPT-20

        // --- Tier 3: Opt-in experimental parallelism / physics batching ---
        registry.Register(new FpcSpherecastBatchModule());         // OPT-16
        registry.Register(new FpcParallelEncoder());               // OPT-17
    }
}
