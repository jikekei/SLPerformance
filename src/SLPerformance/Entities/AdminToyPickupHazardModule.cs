using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using AdminToys;
using HarmonyLib;
using Hazards;
using InventorySystem.Items.Pickups;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Wrappers;
using Mirror;
using NorthwoodLib.Pools;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using SLPerformance.Profiler;
using UnityEngine;

namespace SLPerformance.Entities;

/// <summary>
/// OPT-14: Map entities & hazards optimization:
/// 1. Static AdminToyBase LateUpdate sleep (enabled = false when NetworkServer.active && IsStatic for standard AdminToyBase subclasses)
/// 2. Centralized PickupStandardPhysics updater (eliminates O(N^2) StaticUnityMethods.OnUpdate multicast delegate rebuild churn)
/// 3. EnvironmentalHazard.UpdateTargets: hoists SourcePosition outside AllHubs loop, lazy ListPool & lazy PlayersStayingInHazardEventArgs
/// 4. TeslaGateController.FixedUpdate: lazy EventArgs allocation when LabAPI Tesla events have no subscribers
/// </summary>
public sealed class AdminToyPickupHazardModule : OptimizationModuleBase
{
    public override string Id => "entities.admintoy_pickup_hazard";
    public override string Name => "Static AdminToy Sleep, Pickup Batcher & Hazard/Tesla Fast Path";
    public override OptimizationTier Tier => OptimizationTier.Tier2;
    public override string Description => "Sleeps static AdminToys, centralizes PickupStandardPhysics updates, hoists Hazard SourcePosition, and skips unused Hazard/Tesla EventArgs.";

    // 1. AdminToyBase sleep
    private static readonly Dictionary<Type, bool> SleepableToyTypes = new Dictionary<Type, bool>(16);
    private static readonly HashSet<AdminToyBase> SleepingToys = new HashSet<AdminToyBase>();
    private static bool _adminToySleepActive;

    // 2. Centralized PickupStandardPhysics updater
    private static readonly List<PickupStandardPhysics> ActivePickups = new List<PickupStandardPhysics>(128);
    private static readonly Dictionary<PickupStandardPhysics, int> PickupIndices = new Dictionary<PickupStandardPhysics, int>(128);
    private static MethodInfo _pickupOnUpdateMethod;
    private static Action<PickupStandardPhysics> _callPickupUpdateServer;
    private static Action<PickupStandardPhysics> _callPickupUpdateClient;
    private static bool _pickupCentralActive;

    // 3 & 4. Lazy event subscriber checks
    private static Func<bool> _hasStayingInHazardSubs;
    private static Func<bool> _hasIdlingTeslaSubs;
    private static Func<bool> _hasIdledTeslaSubs;
    private static Func<bool> _hasTriggeringTeslaSubs;
    private static Func<bool> _hasTriggeredTeslaSubs;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.EntitiesAdminToyPickupHazard;

    internal static bool IsToyTypeSleepable(Type toyType)
    {
        if (toyType == null || !typeof(AdminToyBase).IsAssignableFrom(toyType))
        {
            return false;
        }

        if (SleepableToyTypes.TryGetValue(toyType, out bool cached))
        {
            return cached;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo lateUpdate = toyType.GetMethod("LateUpdate", flags, null, Type.EmptyTypes, null);
        bool standardLateUpdate = lateUpdate != null && lateUpdate.DeclaringType == typeof(AdminToyBase);

        bool hasCustomUpdate = HasDeclaredLifecycle(toyType, "Update") ||
                               HasDeclaredLifecycle(toyType, "FixedUpdate") ||
                               HasDeclaredLifecycle(toyType, "OnEnable") ||
                               HasDeclaredLifecycle(toyType, "OnDisable");

        bool sleepable = standardLateUpdate && !hasCustomUpdate;
        SleepableToyTypes[toyType] = sleepable;
        return sleepable;
    }

    private static bool HasDeclaredLifecycle(Type t, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo m = t.GetMethod(name, flags, null, Type.EmptyTypes, null);
        return m != null && m.DeclaringType != typeof(MonoBehaviour) && m.DeclaringType != typeof(Behaviour);
    }

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        int activeParts = 0;

        // 1. Static AdminToyBase sleep + Transform.hasChanged fast path for non-static stationary toys
        MethodInfo onStartServer = AccessTools.Method(typeof(AdminToyBase), nameof(AdminToyBase.OnStartServer), Type.EmptyTypes);
        MethodInfo setNetIsStatic = AccessTools.PropertySetter(typeof(AdminToyBase), nameof(AdminToyBase.NetworkIsStatic));
        MethodInfo adminToyLateUpdate = AccessTools.Method(typeof(AdminToyBase), "LateUpdate", Type.EmptyTypes);
        if (onStartServer != null && setNetIsStatic != null)
        {
            bool ok1 = patchManager.PatchPostfix(Id, onStartServer, AccessTools.Method(typeof(AdminToyPickupHazardModule), nameof(AdminToyStartServerPostfix)), guard, config);
            bool ok2 = patchManager.PatchPostfix(Id, setNetIsStatic, AccessTools.Method(typeof(AdminToyPickupHazardModule), nameof(AdminToySyncSleepPostfix)), guard, config);
            bool ok3 = adminToyLateUpdate != null && patchManager.PatchPrefix(Id, adminToyLateUpdate, AccessTools.Method(typeof(AdminToyPickupHazardModule), nameof(AdminToyLateUpdatePrefix)), guard, config);
            if (ok1 && ok2)
            {
                _adminToySleepActive = true;
                SweepExistingAdminToys();
                activeParts++;
            }

            if (ok3)
            {
                SLPerformanceLog.Debug("OPT-14 AdminToyBase.LateUpdate Transform.hasChanged fast path enabled.");
            }
        }

        // 2. Centralized PickupStandardPhysics updater
        ConstructorInfo pickupCtor = AccessTools.Constructor(typeof(PickupStandardPhysics), new[] { typeof(ItemPickupBase), typeof(PickupStandardPhysics.FreezingMode) });
        MethodInfo pickupDestroy = AccessTools.Method(typeof(PickupStandardPhysics), nameof(PickupStandardPhysics.DestroyModule), Type.EmptyTypes);
        _pickupOnUpdateMethod = AccessTools.Method(typeof(PickupStandardPhysics), "OnUpdate", Type.EmptyTypes);
        _callPickupUpdateServer = CompileVoidCall<PickupStandardPhysics>("UpdateServer");
        _callPickupUpdateClient = CompileVoidCall<PickupStandardPhysics>("UpdateClient");

        if (pickupCtor != null && pickupDestroy != null && _pickupOnUpdateMethod != null &&
            _callPickupUpdateServer != null && _callPickupUpdateClient != null)
        {
            bool okCtor = patchManager.PatchPostfix(Id, pickupCtor, AccessTools.Method(typeof(AdminToyPickupHazardModule), nameof(PickupCtorPostfix)), guard, config);
            bool okDest = patchManager.PatchPrefix(Id, pickupDestroy, AccessTools.Method(typeof(AdminToyPickupHazardModule), nameof(PickupDestroyPrefix)), guard, config);
            if (okCtor && okDest)
            {
                _pickupCentralActive = true;
                StaticUnityMethods.OnUpdate += CentralizedPickupUpdate;
                activeParts++;
            }
        }

        // 3. EnvironmentalHazard.UpdateTargets
        _hasStayingInHazardSubs = ReflectionHelpers.CreateStaticEventHasSubscribersGetter(typeof(PlayerEvents), nameof(PlayerEvents.StayingInHazard));
        MethodInfo hazardUpdate = AccessTools.Method(typeof(EnvironmentalHazard), "UpdateTargets", Type.EmptyTypes);
        if (_hasStayingInHazardSubs != null && hazardUpdate != null &&
            patchManager.PatchPrefix(Id, hazardUpdate, AccessTools.Method(typeof(AdminToyPickupHazardModule), nameof(HazardUpdateTargetsPrefix)), guard, config))
        {
            activeParts++;
        }

        // 4. TeslaGateController.FixedUpdate
        _hasIdlingTeslaSubs = ReflectionHelpers.CreateStaticEventHasSubscribersGetter(typeof(PlayerEvents), nameof(PlayerEvents.IdlingTesla));
        _hasIdledTeslaSubs = ReflectionHelpers.CreateStaticEventHasSubscribersGetter(typeof(PlayerEvents), nameof(PlayerEvents.IdledTesla));
        _hasTriggeringTeslaSubs = ReflectionHelpers.CreateStaticEventHasSubscribersGetter(typeof(PlayerEvents), nameof(PlayerEvents.TriggeringTesla));
        _hasTriggeredTeslaSubs = ReflectionHelpers.CreateStaticEventHasSubscribersGetter(typeof(PlayerEvents), nameof(PlayerEvents.TriggeredTesla));
        MethodInfo teslaFixed = AccessTools.Method(typeof(TeslaGateController), nameof(TeslaGateController.FixedUpdate), Type.EmptyTypes);
        if (_hasIdlingTeslaSubs != null && _hasIdledTeslaSubs != null && _hasTriggeringTeslaSubs != null && _hasTriggeredTeslaSubs != null &&
            teslaFixed != null &&
            patchManager.PatchPrefix(Id, teslaFixed, AccessTools.Method(typeof(AdminToyPickupHazardModule), nameof(TeslaFixedUpdatePrefix)), guard, config))
        {
            activeParts++;
        }

        if (activeParts == 0)
        {
            patchManager.UnpatchModule(Id);
            return MarkIncompatible("No OPT-14 targets could be patched.");
        }

        StatusDetail = $"Active ({activeParts}/4 entity/hazard optimizations)";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        _adminToySleepActive = false;
        foreach (AdminToyBase toy in SleepingToys)
        {
            if (toy != null)
            {
                toy.enabled = true;
            }
        }

        SleepingToys.Clear();

        if (_pickupCentralActive)
        {
            _pickupCentralActive = false;
            StaticUnityMethods.OnUpdate -= CentralizedPickupUpdate;
            // Restore individual StaticUnityMethods.OnUpdate subscriptions for live pickups so revert is seamless.
            if (_pickupOnUpdateMethod != null)
            {
                for (int i = 0; i < ActivePickups.Count; i++)
                {
                    PickupStandardPhysics p = ActivePickups[i];
                    if (p != null && p.Rb != null)
                    {
                        StaticUnityMethods.OnUpdate += (Action)Delegate.CreateDelegate(typeof(Action), p, _pickupOnUpdateMethod);
                    }
                }
            }

            ActivePickups.Clear();
            PickupIndices.Clear();
        }
    }

    private static void SweepExistingAdminToys()
    {
        if (!NetworkServer.active)
        {
            return;
        }

        try
        {
            AdminToyBase[] existing = UnityEngine.Object.FindObjectsByType<AdminToyBase>(FindObjectsSortMode.None);
            for (int i = 0; i < existing.Length; i++)
            {
                AdminToyBase toy = existing[i];
                if (toy == null) continue;
                AdminToySyncSleepPostfix(toy);
                if (!toy.IsStatic && toy.CachedTransform != null)
                {
                    // Sync once via initial state; subsequent stationary frames skip UpdatePositionServer via hasChanged.
                    toy.CachedTransform.hasChanged = true;
                }
            }
        }
        catch (Exception ex)
        {
            SLPerformanceLog.Debug($"OPT-14 existing AdminToy sweep note: {ex.Message}");
        }
    }

    private static void AdminToyStartServerPostfix(AdminToyBase __instance)
    {
        if (__instance == null)
        {
            return;
        }

        if (__instance.CachedTransform != null)
        {
            // OnStartServer already ran UpdatePositionServer() once; mark clean until a plugin/script moves it.
            __instance.CachedTransform.hasChanged = false;
        }

        AdminToySyncSleepPostfix(__instance);
    }

    private static bool AdminToyLateUpdatePrefix(AdminToyBase __instance)
    {
        if (!_adminToySleepActive || !NetworkServer.active || __instance == null)
        {
            return true;
        }

        if (__instance.IsStatic)
        {
            if (IsToyTypeSleepable(__instance.GetType()) && __instance.enabled)
            {
                __instance.enabled = false;
                SleepingToys.Add(__instance);
            }

            return false;
        }

        Transform tr = __instance.CachedTransform;
        if (tr != null)
        {
            if (!tr.hasChanged)
            {
                return false;
            }

            tr.hasChanged = false;
        }

        return true;
    }

    private static void AdminToySyncSleepPostfix(AdminToyBase __instance)
    {
        if (!_adminToySleepActive || !NetworkServer.active || __instance == null)
        {
            return;
        }

        if (!IsToyTypeSleepable(__instance.GetType()))
        {
            return;
        }

        if (__instance.IsStatic)
        {
            if (__instance.enabled)
            {
                __instance.enabled = false;
            }

            SleepingToys.Add(__instance);
        }
        else
        {
            if (!__instance.enabled)
            {
                __instance.enabled = true;
            }

            SleepingToys.Remove(__instance);
        }
    }

    private static void PickupCtorPostfix(PickupStandardPhysics __instance)
    {
        if (!_pickupCentralActive || __instance == null)
        {
            return;
        }

        // Remove the per-pickup delegate just added by the vanilla constructor and enroll into the centralized list.
        Action perPickupDelegate = (Action)Delegate.CreateDelegate(typeof(Action), __instance, _pickupOnUpdateMethod);
        StaticUnityMethods.OnUpdate -= perPickupDelegate;

        if (!PickupIndices.ContainsKey(__instance))
        {
            PickupIndices[__instance] = ActivePickups.Count;
            ActivePickups.Add(__instance);
        }
    }

    private static void PickupDestroyPrefix(PickupStandardPhysics __instance)
    {
        if (__instance == null || !PickupIndices.TryGetValue(__instance, out int idx))
        {
            return;
        }

        RemovePickupAt(idx);
    }

    private static void RemovePickupAt(int idx)
    {
        int last = ActivePickups.Count - 1;
        PickupStandardPhysics removed = ActivePickups[idx];
        if (idx != last)
        {
            PickupStandardPhysics moved = ActivePickups[last];
            ActivePickups[idx] = moved;
            PickupIndices[moved] = idx;
        }

        ActivePickups.RemoveAt(last);
        PickupIndices.Remove(removed);
    }

    private static void CentralizedPickupUpdate()
    {
        long t0 = MethodSampleTable.Begin();
        try
        {
            bool isServer = NetworkServer.active;
            Action<PickupStandardPhysics> tick = isServer ? _callPickupUpdateServer : _callPickupUpdateClient;

            // Iterate backward so O(1) swap-remove if DestroySelf() fires inside UpdateServer is completely safe.
            for (int i = ActivePickups.Count - 1; i >= 0; i--)
            {
                if (i >= ActivePickups.Count)
                {
                    continue;
                }

                PickupStandardPhysics p = ActivePickups[i];
                if (p == null || p.Rb == null)
                {
                    RemovePickupAt(i);
                    continue;
                }

                tick(p);
            }
        }
        finally
        {
            MethodSampleTable.End(ProbeId.AdminToysAndPickups, t0);
        }
    }

    private static bool HazardUpdateTargetsPrefix(EnvironmentalHazard __instance)
    {
        // Hoist native Transform.position read ONCE outside AllHubs loop.
        Vector3 sourcePos = __instance.SourcePosition;
        List<ReferenceHub> staying = null;
        List<ReferenceHub> affected = __instance.AffectedPlayers;

        foreach (ReferenceHub hub in ReferenceHub.AllHubs)
        {
            if (hub.roleManager.CurrentRole is not IFpcRole fpcRole)
            {
                continue;
            }

            bool wasAffected = affected.Contains(hub);
            if (__instance.IsInArea(sourcePos, fpcRole.FpcModule.Position))
            {
                if (!wasAffected)
                {
                    __instance.OnEnter(hub);
                }
                else
                {
                    staying ??= ListPool<ReferenceHub>.Shared.Rent();
                    staying.Add(hub);
                }
            }
            else if (wasAffected)
            {
                __instance.OnExit(hub);
            }
        }

        if (staying == null)
        {
            return false;
        }

        try
        {
            if (_hasStayingInHazardSubs())
            {
                PlayersStayingInHazardEventArgs e = new PlayersStayingInHazardEventArgs(staying, __instance);
                PlayerEvents.OnStayingInHazard(e);
                staying.Clear();
                foreach (Player affectedPlayer in e.AffectedPlayers)
                {
                    staying.Add(affectedPlayer.ReferenceHub);
                }

                ListPool<Player>.Shared.Return(e.AffectedPlayers);
            }

            for (int i = 0; i < staying.Count; i++)
            {
                __instance.OnStay(staying[i]);
            }
        }
        finally
        {
            ListPool<ReferenceHub>.Shared.Return(staying);
        }

        return false;
    }

    private static bool TeslaFixedUpdatePrefix()
    {
        if (!NetworkServer.active)
        {
            foreach (TeslaGate gate in TeslaGate.AllGates)
            {
                gate.ClientSideCode();
            }

            return false;
        }

        bool hasIdlingSubs = _hasIdlingTeslaSubs();
        bool hasIdledSubs = _hasIdledTeslaSubs();
        bool hasTriggeringSubs = _hasTriggeringTeslaSubs();
        bool hasTriggeredSubs = _hasTriggeredTeslaSubs();
        float fixedDt = Time.fixedDeltaTime;

        foreach (TeslaGate gate in TeslaGate.AllGates)
        {
            if (!gate.isActiveAndEnabled)
            {
                continue;
            }

            if (gate.InactiveTime > 0f)
            {
                gate.NetworkInactiveTime = Mathf.Max(0f, gate.InactiveTime - fixedDt);
                continue;
            }

            bool idling = false;
            bool triggering = false;
            ReferenceHub idleHub = null;
            ReferenceHub triggerHub = null;

            foreach (ReferenceHub hub in ReferenceHub.AllHubs)
            {
                if (!hub.IsAlive())
                {
                    continue;
                }

                if (!idling && gate.IsInIdleRange(hub))
                {
                    if (hasIdlingSubs)
                    {
                        PlayerIdlingTeslaEventArgs e = new PlayerIdlingTeslaEventArgs(hub, gate);
                        PlayerEvents.OnIdlingTesla(e);
                        if (e.IsAllowed)
                        {
                            idling = true;
                            idleHub = hub;
                        }
                    }
                    else
                    {
                        idling = true;
                        idleHub = hub;
                    }
                }

                if (!triggering && !gate.InProgress && gate.PlayerInRange(hub))
                {
                    if (hasTriggeringSubs)
                    {
                        PlayerTriggeringTeslaEventArgs e2 = new PlayerTriggeringTeslaEventArgs(hub, gate);
                        PlayerEvents.OnTriggeringTesla(e2);
                        if (e2.IsAllowed)
                        {
                            triggerHub = hub;
                            triggering = true;
                        }
                    }
                    else
                    {
                        triggerHub = hub;
                        triggering = true;
                    }
                }
            }

            if (triggering)
            {
                gate.ServerSideCode();
                if (hasTriggeredSubs)
                {
                    PlayerEvents.OnTriggeredTesla(new PlayerTriggeredTeslaEventArgs(triggerHub, gate));
                }
            }

            if (idling != gate.isIdling)
            {
                gate.ServerSideIdle(idling);
                if (idling && hasIdledSubs)
                {
                    PlayerEvents.OnIdledTesla(new PlayerIdledTeslaEventArgs(idleHub, gate));
                }
            }
        }

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
}
