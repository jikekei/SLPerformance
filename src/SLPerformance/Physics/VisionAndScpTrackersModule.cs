using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MapGeneration;
using Mirror;
using PlayerRoles.PlayableScps;
using PlayerRoles.PlayableScps.Scp096;
using PlayerRoles.PlayableScps.Scp173;
using PlayerRoles.Spectating;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using SLPerformance.Profiler;
using UnityEngine;

namespace SLPerformance.Physics;

/// <summary>
/// OPT-08: SCP vision & tracker optimizations:
/// 1. Scp096TargetsTracker.Update: skips _markers.ForEachValue closure allocation when _markers.Count == 0
/// 2. Scp096TargetsTracker.UpdateTarget: skips IsObservedBy (and Raycast) when target is already in Targets
/// 3. Scp173ObserversTracker.UpdateObservers: dirty-only ServerSendRpc (sends only on initial sync or count change)
/// 4. VisionInformation.GetVisionInformation: skips CheckIsInDarkness when target already failed earlier checks (flag == false)
///    for internal SCP trackers (which only inspect .IsLooking), or globally if VisionDarknessShortCircuitGlobal is enabled.
/// </summary>
public sealed class VisionAndScpTrackersModule : OptimizationModuleBase
{
    public override string Id => "physics.vision_and_scp_trackers";
    public override string Name => "SCP-096/173 Tracker & Vision Darkness Short-Circuit";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Eliminates empty SCP-096 marker closures, skips already-targeted 096 raycasts, sends SCP-173 observer RPC only on change, and short-circuits dead darkness checks.";

    [ThreadStatic]
    private static bool _internalIsLookingOnlyScope;

    private static bool _globalDarknessShortCircuit;

    // Scp096TargetsTracker helpers
    private static Func<Scp096TargetsTracker, Dictionary<ReferenceHub, GameObject>> _get096Markers;
    private static Action<Scp096TargetsTracker> _call096ServerCheckTargets;

    // Scp173ObserversTracker helpers
    private static Func<Scp173ObserversTracker, int> _get173SimulatedTargets;
    private static Action<Scp173ObserversTracker, int> _set173SimulatedTargets;
    private static Func<Scp173ObserversTracker, ReferenceHub, int> _call173UpdateObserver;
    private static Action<Scp173ObserversTracker, int> _set173CurrentObservers;
    private static Action<Scp173ObserversTracker, bool> _call173ServerSendRpc;

    private sealed class InitialSyncState
    {
        public bool HasSentOnce;
    }

    private static readonly ConditionalWeakTable<Scp173ObserversTracker, InitialSyncState> Scp173SyncState =
        new ConditionalWeakTable<Scp173ObserversTracker, InitialSyncState>();

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.PhysicsVisionAndScpTrackers;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        _globalDarknessShortCircuit = config.VisionDarknessShortCircuitGlobal;
        int activeParts = 0;

        // 1 & 2. Scp096TargetsTracker
        _get096Markers = ReflectionHelpers.CreateInstanceFieldGetter<Scp096TargetsTracker, Dictionary<ReferenceHub, GameObject>>("_markers");
        _call096ServerCheckTargets = CompileVoidCall<Scp096TargetsTracker>("ServerCheckTargets");
        MethodInfo m096Update = AccessTools.Method(typeof(Scp096TargetsTracker), "Update", Type.EmptyTypes);
        MethodInfo m096UpdateTarget = AccessTools.Method(typeof(Scp096TargetsTracker), "UpdateTarget", new[] { typeof(ReferenceHub) });

        if (_get096Markers != null && _call096ServerCheckTargets != null && m096Update != null &&
            patchManager.PatchPrefix(Id, m096Update, AccessTools.Method(typeof(VisionAndScpTrackersModule), nameof(Scp096UpdatePrefix)), guard, config))
        {
            activeParts++;
        }

        if (m096UpdateTarget != null &&
            patchManager.PatchPrefix(Id, m096UpdateTarget, AccessTools.Method(typeof(VisionAndScpTrackersModule), nameof(Scp096UpdateTargetPrefix)), guard, config))
        {
            activeParts++;
        }

        // 3. Scp173ObserversTracker
        _get173SimulatedTargets = ReflectionHelpers.CreateInstanceFieldGetter<Scp173ObserversTracker, int>("_simulatedTargets");
        _set173SimulatedTargets = ReflectionHelpers.CreateInstanceFieldSetter<Scp173ObserversTracker, int>("_simulatedTargets");
        _call173UpdateObserver = CompileIntRefHubCall<Scp173ObserversTracker>("UpdateObserver");
        PropertyInfo curObsProp = AccessTools.Property(typeof(Scp173ObserversTracker), nameof(Scp173ObserversTracker.CurrentObservers));
        if (curObsProp?.GetSetMethod(true) != null)
        {
            _set173CurrentObservers = (Action<Scp173ObserversTracker, int>)Delegate.CreateDelegate(
                typeof(Action<Scp173ObserversTracker, int>), curObsProp.GetSetMethod(true));
        }

        _call173ServerSendRpc = CompileBoolCall<Scp173ObserversTracker>("ServerSendRpc");

        MethodInfo m173UpdateObs = AccessTools.Method(typeof(Scp173ObserversTracker), nameof(Scp173ObserversTracker.UpdateObservers), Type.EmptyTypes);
        MethodInfo m173Reset = AccessTools.Method(typeof(Scp173ObserversTracker), nameof(Scp173ObserversTracker.ResetObject), Type.EmptyTypes);
        if (_get173SimulatedTargets != null && _set173SimulatedTargets != null &&
            _call173UpdateObserver != null && _set173CurrentObservers != null && _call173ServerSendRpc != null &&
            m173UpdateObs != null && m173Reset != null)
        {
            bool ok1 = patchManager.PatchPrefix(Id, m173UpdateObs, AccessTools.Method(typeof(VisionAndScpTrackersModule), nameof(Scp173UpdateObserversPrefix)), guard, config);
            bool ok2 = patchManager.PatchPostfix(Id, m173Reset, AccessTools.Method(typeof(VisionAndScpTrackersModule), nameof(Scp173ResetPostfix)), guard, config);
            if (ok1 && ok2)
            {
                activeParts++;
            }
        }

        // 4. VisionInformation.GetVisionInformation
        MethodInfo getVision = AccessTools.Method(typeof(VisionInformation), nameof(VisionInformation.GetVisionInformation), new[]
        {
            typeof(ReferenceHub), typeof(Transform), typeof(Vector3),
            typeof(float), typeof(float), typeof(bool), typeof(bool), typeof(int), typeof(bool)
        });
        if (getVision != null &&
            patchManager.PatchPrefix(Id, getVision, AccessTools.Method(typeof(VisionAndScpTrackersModule), nameof(GetVisionInformationPrefix)), guard, config))
        {
            activeParts++;
        }

        if (activeParts == 0)
        {
            patchManager.UnpatchModule(Id);
            return MarkIncompatible("No OPT-08 tracker or vision targets could be patched.");
        }

        StatusDetail = $"Active ({activeParts}/4 vision/tracker hooks, globalDarknessShortCircuit={_globalDarknessShortCircuit})";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        _globalDarknessShortCircuit = false;
    }

    private static bool Scp096UpdatePrefix(Scp096TargetsTracker __instance)
    {
        long t0 = MethodSampleTable.Begin();
        try
        {
            Dictionary<ReferenceHub, GameObject> markers = _get096Markers(__instance);
            if (markers.Count > 0)
            {
                ReferenceHub owner = __instance.Owner;
                bool visible = owner.isLocalPlayer || owner.IsLocallySpectated();
                foreach (KeyValuePair<ReferenceHub, GameObject> kvp in markers)
                {
                    kvp.Value.SetActive(visible);
                }
            }

            if (NetworkServer.active)
            {
                bool prev = _internalIsLookingOnlyScope;
                _internalIsLookingOnlyScope = true;
                try
                {
                    _call096ServerCheckTargets(__instance);
                }
                finally
                {
                    _internalIsLookingOnlyScope = prev;
                }
            }

            return false;
        }
        finally
        {
            MethodSampleTable.End(ProbeId.VisionAndScpTrackers, t0);
        }
    }

    private static bool Scp096UpdateTargetPrefix(Scp096TargetsTracker __instance, ReferenceHub target)
    {
        if (!HitboxIdentity.IsEnemy(__instance.Owner, target))
        {
            __instance.RemoveTarget(target);
            return false;
        }

        // Skip redundant vision + Raycast if target is ALREADY in Targets (AddTarget would immediately return false).
        if (__instance.Targets.Contains(target))
        {
            return false;
        }

        if (!__instance.CastRole.IsAbilityState(Scp096AbilityState.Charging) && __instance.IsObservedBy(target))
        {
            __instance.AddTarget(target, isLooking: true);
        }

        return false;
    }

    private static bool Scp173UpdateObserversPrefix(Scp173ObserversTracker __instance)
    {
        if (!NetworkServer.active)
        {
            return false;
        }

        long t0 = MethodSampleTable.Begin();
        try
        {
            int prevObservers = __instance.CurrentObservers;
            int cur = prevObservers;
            int sim = __instance.SimulatedStare > 0f ? 1 : 0;
            int prevSim = _get173SimulatedTargets(__instance);
            if (prevSim != sim)
            {
                cur += sim - prevSim;
                _set173SimulatedTargets(__instance, sim);
            }

            bool prevScope = _internalIsLookingOnlyScope;
            _internalIsLookingOnlyScope = true;
            try
            {
                foreach (ReferenceHub hub in ReferenceHub.AllHubs)
                {
                    cur += _call173UpdateObserver(__instance, hub);
                }
            }
            finally
            {
                _internalIsLookingOnlyScope = prevScope;
            }

            _set173CurrentObservers(__instance, cur);

            if (!__instance.Owner.isLocalPlayer)
            {
                InitialSyncState state = Scp173SyncState.GetOrCreateValue(__instance);
                if (!state.HasSentOnce || cur != prevObservers)
                {
                    state.HasSentOnce = true;
                    _call173ServerSendRpc(__instance, true);
                }
            }

            return false;
        }
        finally
        {
            MethodSampleTable.End(ProbeId.VisionAndScpTrackers, t0);
        }
    }

    private static void Scp173ResetPostfix(Scp173ObserversTracker __instance)
    {
        if (Scp173SyncState.TryGetValue(__instance, out InitialSyncState state))
        {
            state.HasSentOnce = false;
        }
    }

    private static bool GetVisionInformationPrefix(
        ReferenceHub source,
        Transform sourceCam,
        Vector3 target,
        float targetRadius,
        float visionTriggerDistance,
        bool checkFog,
        bool checkLineOfSight,
        int maskLayer,
        bool checkInDarkness,
        ref VisionInformation __result)
    {
        if (!checkInDarkness || (!_globalDarknessShortCircuit && !_internalIsLookingOnlyScope))
        {
            return true;
        }

        Vector3 camPos = sourceCam.position;
        bool isOnSameFloor = false;
        bool flag = false;
        if (VisionInformation.TargetOnTheSameFloor(camPos, target))
        {
            isOnSameFloor = true;
            flag = true;
        }

        bool inDistance = visionTriggerDistance == 0f;
        Vector3 directionToTarget = target - camPos;
        float magnitude = directionToTarget.magnitude;
        if (flag && visionTriggerDistance > 0f)
        {
            bool onSurface = target.GetZone() == FacilityZone.Surface;
            float maxDist = checkFog
                ? (onSurface ? visionTriggerDistance : (visionTriggerDistance / 2f))
                : visionTriggerDistance;

            if (magnitude <= maxDist)
            {
                inDistance = true;
            }

            flag = inDistance;
        }

        float lookingAmount = 1f;
        if (flag)
        {
            flag = false;
            if (magnitude < targetRadius)
            {
                if (Vector3.Dot(source.transform.forward, (target - source.transform.position).normalized) > 0f)
                {
                    flag = true;
                    lookingAmount = 1f;
                }
            }
            else
            {
                flag = VisionInformation.TargetInViewDirection(source, target, targetRadius, out lookingAmount);
            }
        }

        bool lineOfSight = !checkLineOfSight;
        if (flag && checkLineOfSight)
        {
            lineOfSight = VisionInformation.TargetVisibilityUnobstructed(sourceCam, directionToTarget, maskLayer);
            flag = lineOfSight;
        }

        bool isInDarkness = false;
        // Key optimization: only run CheckIsInDarkness (which triggers RoomUtils + RoomLightController + effects)
        // when the target passed floor, distance, FOV, and LOS checks!
        if (flag)
        {
            isInDarkness = VisionInformation.CheckIsInDarkness(source, target);
            flag = !isInDarkness;
        }

        __result = new VisionInformation(source, target, flag, isOnSameFloor, lookingAmount, magnitude, lineOfSight, isInDarkness, inDistance);
        return false;
    }

    private static Action<TTarget> CompileVoidCall<TTarget>(string methodName)
    {
        MethodInfo m = AccessTools.Method(typeof(TTarget), methodName, Type.EmptyTypes);
        if (m == null)
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_{typeof(TTarget).Name}_{methodName}",
            typeof(void),
            new[] { typeof(TTarget) },
            typeof(TTarget),
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(m.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, m);
        il.Emit(OpCodes.Ret);

        return (Action<TTarget>)dm.CreateDelegate(typeof(Action<TTarget>));
    }

    private static Func<TTarget, ReferenceHub, int> CompileIntRefHubCall<TTarget>(string methodName)
    {
        MethodInfo m = AccessTools.Method(typeof(TTarget), methodName, new[] { typeof(ReferenceHub) });
        if (m == null)
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_{typeof(TTarget).Name}_{methodName}",
            typeof(int),
            new[] { typeof(TTarget), typeof(ReferenceHub) },
            typeof(TTarget),
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(m.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, m);
        il.Emit(OpCodes.Ret);

        return (Func<TTarget, ReferenceHub, int>)dm.CreateDelegate(typeof(Func<TTarget, ReferenceHub, int>));
    }

    private static Action<TTarget, bool> CompileBoolCall<TTarget>(string methodName)
    {
        MethodInfo m = AccessTools.Method(typeof(TTarget), methodName, new[] { typeof(bool) });
        if (m == null)
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_{typeof(TTarget).Name}_{methodName}",
            typeof(void),
            new[] { typeof(TTarget), typeof(bool) },
            typeof(TTarget),
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(m.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, m);
        il.Emit(OpCodes.Ret);

        return (Action<TTarget, bool>)dm.CreateDelegate(typeof(Action<TTarget, bool>));
    }
}
