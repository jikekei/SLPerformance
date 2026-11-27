using System;
using System.Collections.Generic;
using System.Reflection;
using CustomPlayerEffects;
using HarmonyLib;
using Interactables.Interobjects.DoorUtils;
using InventorySystem;
using InventorySystem.Items.Autosync;
using InventorySystem.Items.Firearms.Modules;
using MEC;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables;
using PlayerRoles.Subroutines;
using PlayerStatsSystem;
using RelativePositioning;
using SLPerformance.Core;
using VoiceChat.Playbacks;

namespace SLPerformance.Profiler;

/// <summary>
/// On-demand, zero-conflict read-only timing & Mono allocation probes for LateUpdate and Update breakdown.
/// Installed ONLY while `slperf probes on` is active and completely unpatched on `slperf probes off`
/// so production overhead is strictly zero when disabled.
/// Never skips original methods, never modifies arguments/returns, and never touches SimpleAntiCheat-patched targets.
/// </summary>
public static class ProfilerProbeHooks
{
    private const string ProbeHarmonyId = "com.slperformance.profiler.probes";

    private static Harmony _harmony;
    private static bool _installed;

    public static int HookedCount { get; private set; }
    public static bool IsInstalled => _installed;

    internal readonly struct ProbeTarget
    {
        public readonly ProbeId Id;
        public readonly MethodBase Target;
        public readonly MethodInfo Postfix;

        public ProbeTarget(ProbeId id, MethodBase target, MethodInfo postfix)
        {
            Id = id;
            Target = target;
            Postfix = postfix;
        }
    }

    internal static List<ProbeTarget> ResolveTargets()
    {
        var list = new List<ProbeTarget>(24);

        void Add(ProbeId id, Type type, string methodName, string postfixName)
        {
            MethodInfo target = type != null ? AccessTools.Method(type, methodName, Type.EmptyTypes) : null;
            MethodInfo postfix = AccessTools.Method(typeof(ProfilerProbeHooks), postfixName);
            list.Add(new ProbeTarget(id, target, postfix));
        }

        // LateUpdate breakdown targets
        Add(ProbeId.StaticLateUpdate, typeof(StaticUnityMethods), "LateUpdate", nameof(EndStaticLate));
        Add(ProbeId.FpcDistLateUpdate, typeof(FpcServerPositionDistributor), "LateUpdate", nameof(EndFpcDistLate));
        Add(ProbeId.CullingLateUpdate, typeof(CullingSubcontroller), "LateUpdate", nameof(EndCullingLate));
        Add(ProbeId.SubctrlLateUpdate, typeof(GlowInTheDarkSubcontroller), "LateUpdate", nameof(EndSubctrlLate));
        Add(ProbeId.SubctrlLateUpdate, typeof(HandPoseSubcontroller), "LateUpdate", nameof(EndSubctrlLate));
        Add(ProbeId.SubctrlLateUpdate, typeof(SecondaryRigsSubcontroller), "LateUpdate", nameof(EndSubctrlLate));
        Add(ProbeId.SubctrlLateUpdate, typeof(WearableArmor), "LateUpdate", nameof(EndSubctrlLate));
        Add(ProbeId.SubctrlLateUpdate, typeof(OffsetableSimpleWearable), "LateUpdate", nameof(EndSubctrlLate));
        Add(ProbeId.MecLateUpdate, typeof(Timing), "LateUpdate", nameof(EndMecLate));
        Add(ProbeId.SubroutineLateUpdate, typeof(SubroutineBase), "LateUpdate", nameof(EndSubroutineLate));
        Add(ProbeId.WaypointFollowLate, typeof(RigidBodyWaypointFollower), "LateUpdate", nameof(EndWaypointFollowLate));
        Add(ProbeId.WaypointFollowLate, typeof(TransformWaypointFollower), "LateUpdate", nameof(EndWaypointFollowLate));

        // Update breakdown targets
        Add(ProbeId.StaticUpdate, typeof(StaticUnityMethods), "Update", nameof(EndStaticUpdate));
        Add(ProbeId.MecUpdate, typeof(Timing), "Update", nameof(EndMecUpdate));
        Add(ProbeId.AnimModelUpdate, typeof(AnimatedCharacterModel), "Update", nameof(EndAnimModelUpdate));
        Add(ProbeId.SubctrlUpdate, typeof(FeetStabilizerSubcontroller), "Update", nameof(EndSubctrlUpdate));
        Add(ProbeId.SubctrlUpdate, typeof(IdlePoseRetainerSubcontroller), "Update", nameof(EndSubctrlUpdate));
        Add(ProbeId.SubctrlUpdate, typeof(InventorySubcontroller), "Update", nameof(EndSubctrlUpdate));
        Add(ProbeId.InventoryUpdate, typeof(Inventory), "Update", nameof(EndInventoryUpdate));
        Add(ProbeId.PlayerStatsUpdate, typeof(PlayerStats), "Update", nameof(EndPlayerStatsUpdate));
        Add(ProbeId.VoicePlaybackUpdate, typeof(VoiceChatPlaybackBase), "Update", nameof(EndVoicePlaybackUpdate));

        // Round 3 deep X-ray breakdown targets
        Type volumeType = Type.GetType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime")
                          ?? AccessTools.TypeByName("UnityEngine.Rendering.Volume");
        Add(ProbeId.VolumeUpdate, volumeType, "Update", nameof(EndVolumeUpdate));
        Add(ProbeId.InsufficientLight, typeof(InsufficientLighting), "AlwaysUpdate", nameof(EndInsufficientLight));
        Add(ProbeId.TemplateUpdate, typeof(ModularAutosyncItem), "OnTemplateUpdate", nameof(EndTemplateUpdate));
        Add(ProbeId.DoorUpdate, typeof(DoorVariant), "Update", nameof(EndDoorUpdate));
        Add(ProbeId.AutosyncLateUpdate, typeof(AutosyncItem), "LateUpdate", nameof(EndAutosyncLate));
        Add(ProbeId.AutosyncLateUpdate, typeof(EventManagerModule), "LateUpdate", nameof(EndAutosyncLate));
        Add(ProbeId.ConsoleLateUpdate, typeof(GameCore.Console), "LateUpdate", nameof(EndConsoleLate));
        Add(ProbeId.MainThreadDispatch, typeof(MainThreadDispatcher), "Update", nameof(EndMainThreadDispatch));
        Add(ProbeId.MainThreadDispatch, typeof(MainThreadDispatcher), "LateUpdate", nameof(EndMainThreadDispatch));

        return list;
    }

    public static int Install()
    {
        if (_installed)
        {
            return HookedCount;
        }

        _harmony ??= new Harmony(ProbeHarmonyId);
        MethodInfo prefix = AccessTools.Method(typeof(ProfilerProbeHooks), nameof(BeginPrefix));
        if (prefix == null)
        {
            return 0;
        }

        List<ProbeTarget> targets = ResolveTargets();
        int ok = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            ProbeTarget pt = targets[i];
            if (pt.Target == null || pt.Postfix == null)
            {
                continue;
            }

            try
            {
                _harmony.Patch(
                    pt.Target,
                    prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(pt.Postfix) { priority = Priority.Last });
                ok++;
            }
            catch (Exception ex)
            {
                SLPerformanceLog.Debug($"Probe hook skipped for {pt.Target.DeclaringType?.Name}.{pt.Target.Name}: {ex.Message}");
            }
        }

        HookedCount = ok;
        _installed = true;
        return ok;
    }

    public static void Uninstall()
    {
        if (!_installed)
        {
            return;
        }

        try
        {
            _harmony?.UnpatchAll(ProbeHarmonyId);
        }
        catch (Exception ex)
        {
            SLPerformanceLog.Debug($"Probe unpatch note: {ex.Message}");
        }

        HookedCount = 0;
        _installed = false;
    }

    private static void BeginPrefix(out MethodSampleTable.ProbeToken __state)
    {
        __state = MethodSampleTable.Enabled ? MethodSampleTable.BeginSample() : default;
    }

    private static void EndStaticLate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.StaticLateUpdate, in __state);
    }

    private static void EndFpcDistLate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.FpcDistLateUpdate, in __state);
    }

    private static void EndCullingLate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.CullingLateUpdate, in __state);
    }

    private static void EndSubctrlLate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.SubctrlLateUpdate, in __state);
    }

    private static void EndMecLate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.MecLateUpdate, in __state);
    }

    private static void EndSubroutineLate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.SubroutineLateUpdate, in __state);
    }

    private static void EndWaypointFollowLate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.WaypointFollowLate, in __state);
    }

    private static void EndStaticUpdate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.StaticUpdate, in __state);
    }

    private static void EndMecUpdate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.MecUpdate, in __state);
    }

    private static void EndAnimModelUpdate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.AnimModelUpdate, in __state);
    }

    private static void EndSubctrlUpdate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.SubctrlUpdate, in __state);
    }

    private static void EndInventoryUpdate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.InventoryUpdate, in __state);
    }

    private static void EndPlayerStatsUpdate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.PlayerStatsUpdate, in __state);
    }

    private static void EndVoicePlaybackUpdate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.VoicePlaybackUpdate, in __state);
    }

    private static void EndVolumeUpdate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.VolumeUpdate, in __state);
    }

    private static void EndInsufficientLight(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.InsufficientLight, in __state);
    }

    private static void EndTemplateUpdate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.TemplateUpdate, in __state);
    }

    private static void EndDoorUpdate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.DoorUpdate, in __state);
    }

    private static void EndAutosyncLate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.AutosyncLateUpdate, in __state);
    }

    private static void EndConsoleLate(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.ConsoleLateUpdate, in __state);
    }

    private static void EndMainThreadDispatch(MethodSampleTable.ProbeToken __state)
    {
        if (__state.StartTicks != 0L) MethodSampleTable.EndSample(ProbeId.MainThreadDispatch, in __state);
    }
}
