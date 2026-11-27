using System;
using System.Text;
using CommandSystem;
using Mirror.LiteNetLib4Mirror;
using SLPerformance.Core;
using SLPerformance.Entities;
using SLPerformance.Network;
using SLPerformance.Profiler;

namespace SLPerformance.Diagnostics;

[CommandHandler(typeof(GameConsoleCommandHandler))]
[CommandHandler(typeof(RemoteAdminCommandHandler))]
public sealed class SlPerfCommand : ICommand
{
    public string Command => "slperf";
    public string[] Aliases { get; } = { "slp", "perf" };
    public string Description => "Inspect and control SLPerformance optimization modules and runtime profiler.";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (sender is RemoteAdmin.PlayerCommandSender playerSender && !playerSender.FullPermissions)
        {
            response = "Insufficient permissions. Requires Server Console or full RemoteAdmin permissions.";
            return false;
        }

        SLPerformancePlugin plugin = SLPerformancePlugin.Instance;
        if (plugin == null)
        {
            response = "SLPerformance plugin is not currently enabled.";
            return false;
        }

        if (arguments.Count == 0)
        {
            response = Usage();
            return true;
        }

        string sub = arguments.At(0).ToLowerInvariant();
        switch (sub)
        {
            case "status":
                response = plugin.Compatibility.BuildDiagnosticReport(plugin.Registry.Modules);
                return true;

            case "stats":
                response = plugin.Profiler != null
                    ? plugin.Profiler.FormatStatsReport()
                    : "Profiler is disabled in config.";
                return true;

            case "phases":
                response = plugin.Profiler != null
                    ? plugin.Profiler.FormatPhasesReport()
                    : "Profiler is disabled in config.";
                return true;

            case "hotspots":
                response = plugin.Profiler != null
                    ? plugin.Profiler.FormatHotspotReport()
                    : "Profiler is disabled in config.";
                return true;

            case "audit":
                response = SceneRuntimeAuditor.BuildAuditReport();
                return true;

            case "roleburst":
                response = MassRoleChangeAccelModule.FormatRoleBurstReport();
                return true;

            case "joinstats":
                response = PlayerJoinAccelModule.FormatJoinStatsReport();
                return true;

            case "probes":
                if (arguments.Count < 2)
                {
                    response = $"Method probes currently: {(MethodSampleTable.Enabled ? "ON" : "OFF")} (Hooks={ProfilerProbeHooks.HookedCount}). Usage: slperf probes on|off";
                    return true;
                }

                string mode = arguments.At(1).ToLowerInvariant();
                if (mode == "on" || mode == "true" || mode == "1")
                {
                    int hooked = ProfilerProbeHooks.Install();
                    MethodSampleTable.Reset();
                    MethodSampleTable.Enabled = true;
                    response = $"SLPerformance method probes ENABLED ({hooked} breakdown hooks installed; counters reset). Wait 15-20s, run `slperf hotspots`, then `slperf probes off`.";
                    return true;
                }

                if (mode == "off" || mode == "false" || mode == "0")
                {
                    MethodSampleTable.Enabled = false;
                    ProfilerProbeHooks.Uninstall();
                    response = "SLPerformance method probes DISABLED (breakdown hooks removed; zero runtime overhead).";
                    return true;
                }

                response = "Invalid argument. Use: slperf probes on|off";
                return false;

            case "netstats":
                if (LiteNetLib4MirrorCore.Host == null)
                {
                    response = "LiteNetLib host is not active.";
                    return false;
                }

                if (arguments.Count >= 2)
                {
                    string arg = arguments.At(1).ToLowerInvariant();
                    LiteNetLib4MirrorCore.Host.EnableStatistics = arg == "on" || arg == "true" || arg == "1";
                }

                response = $"LiteNetLib EnableStatistics = {LiteNetLib4MirrorCore.Host.EnableStatistics}";
                return true;

            case "reset":
                plugin.Profiler?.Reset();
                response = "SLPerformance profiler counters and histograms reset.";
                return true;

            case "toggle":
                if (arguments.Count < 2)
                {
                    response = "Usage: slperf toggle <moduleId>";
                    return false;
                }

                string id = arguments.At(1);
                if (!plugin.Registry.TryGet(id, out IOptimizationModule mod))
                {
                    response = $"Unknown module '{id}'. Run `slperf status` to see valid module IDs.";
                    return false;
                }

                if (mod.State == ModuleState.Active)
                {
                    mod.Revert(plugin.Patches);
                    response = $"Module '{mod.Id}' REVERTED -> State={mod.State}.";
                }
                else
                {
                    mod.Apply(plugin.Patches, plugin.Compatibility, plugin.Config);
                    response = $"Module '{mod.Id}' APPLY attempted -> State={mod.State} ({mod.StatusDetail}).";
                }

                return true;

            case "verify":
                response = BuildVerifySummary(plugin);
                return true;

            default:
                response = Usage();
                return false;
        }
    }

    private static string BuildVerifySummary(SLPerformancePlugin plugin)
    {
        StringBuilder sb = new StringBuilder(512);
        sb.AppendLine("=== SLPerformance Runtime Verification ===");
        sb.AppendLine($"FpcStateProcessor.Mask = 0x{(int)PlayerRoles.FirstPersonControl.FpcStateProcessor.Mask:X8}");
        int playerLayer = UnityEngine.LayerMask.NameToLayer("Player");
        bool includesPlayer = playerLayer >= 0 && (((int)PlayerRoles.FirstPersonControl.FpcStateProcessor.Mask & (1 << playerLayer)) != 0);
        sb.AppendLine($"Player Layer Index     = {playerLayer} | MaskIncludesPlayer={includesPlayer}");
        sb.AppendLine($"RoomsByCoords Count    = {MapGeneration.RoomIdentifier.RoomsByCoords.Count} | AllRooms={MapGeneration.RoomIdentifier.AllRoomIdentifiers.Count}");
        sb.AppendLine($"Active Modules         = {CountActive(plugin)}/{plugin.Registry.Modules.Count}");
        return sb.ToString();
    }

    private static int CountActive(SLPerformancePlugin plugin)
    {
        int n = 0;
        for (int i = 0; i < plugin.Registry.Modules.Count; i++)
        {
            if (plugin.Registry.Modules[i].State == ModuleState.Active) n++;
        }

        return n;
    }

    private static string Usage()
    {
        return "SLPerformance Commands:\n" +
               "  slperf status             - Show version compatibility and all module states\n" +
               "  slperf stats              - Show TPS, Mean/P95/P99/P99.9 frame times, CPU, GC, PlayerLoop phases & net stats\n" +
               "  slperf phases             - Show Release-safe Unity PlayerLoop phase breakdown (PhysX/Update/Animator/LateUpdate)\n" +
               "  slperf hotspots           - Show per-hotspot ms/frame and Mono allocation breakdown\n" +
               "  slperf audit              - Inspect live active MonoBehaviours (Update/LateUpdate), StaticUnityMethods, MEC coroutines & Animators\n" +
               "  slperf roleburst          - Inspect OPT-21 pool/item pre-warm readiness and recent mass role-change / wave respawn burst timings\n" +
               "  slperf joinstats          - Inspect OPT-22 ECDH pool, async auth crypto offload, BanCache & recent real-player join timings\n" +
               "  slperf probes on|off      - Lazily install/remove breakdown timing & alloc probes (zero overhead when off)\n" +
               "  slperf netstats on|off    - Toggle LiteNetLib internal packet/byte counters\n" +
               "  slperf toggle <moduleId>  - Dynamically enable or revert an optimization module at runtime\n" +
               "  slperf verify             - Inspect runtime invariants (LayerMask, room counts, module status)\n" +
               "  slperf reset              - Reset profiler ring buffer and probe counters";
    }
}
