using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LabApi.Events;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using SLPerformance.Collections;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;

namespace SLPerformance.Memory;

/// <summary>
/// OPT-06: Eliminates per-invocation Delegate.GetInvocationList() array allocations in LabAPI event dispatch.
/// Uses ConditionalWeakTable keyed by immutable Delegate reference so old delegates are GC-collected automatically.
/// Patches both non-generic EventManager.InvokeEvent(LabEventHandler) and the highest-frequency concrete
/// PlayerEvents dispatchers (avoiding Harmony open-generic limitations on Mono).
/// </summary>
public sealed class LabApiEventCacheModule : OptimizationModuleBase
{
    public override string Id => "memory.labapi_event_cache";
    public override string Name => "LabAPI Event Invocation List Cache";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Caches immutable Delegate[] invocation lists via ConditionalWeakTable in LabAPI event dispatch.";

    private static Func<LabEventHandler<PlayerValidatedVisibilityEventArgs>> _getValidatedVisibility;
    private static Func<LabEventHandler<PlayerReceivingVoiceMessageEventArgs>> _getReceivingVoice;
    private static Func<LabEventHandler<PlayerSendingVoiceMessageEventArgs>> _getSendingVoice;
    private static Func<LabEventHandler<PlayersStayingInHazardEventArgs>> _getStayingInHazard;
    private static Func<LabEventHandler<PlayerIdlingTeslaEventArgs>> _getIdlingTesla;
    private static Func<LabEventHandler<PlayerTriggeringTeslaEventArgs>> _getTriggeringTesla;
    private static Func<LabEventHandler<PlayerRoomChangedEventArgs>> _getRoomChanged;
    private static Func<LabEventHandler<PlayerZoneChangedEventArgs>> _getZoneChanged;
    private static Func<LabEventHandler<PlayerMovementStateChangedEventArgs>> _getMovementStateChanged;
    private static Func<LabEventHandler<PlayerEffectUpdatingEventArgs>> _getUpdatingEffect;
    private static Func<LabEventHandler<PlayerEffectUpdatedEventArgs>> _getUpdatedEffect;
    private static Func<LabEventHandler<PlayerHurtingEventArgs>> _getHurting;
    private static Func<LabEventHandler<PlayerHurtEventArgs>> _getHurt;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.MemoryLabApiEventCache;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        int patched = 0;

        MethodInfo nonGenericInvoke = AccessTools.Method(typeof(EventManager), nameof(EventManager.InvokeEvent), new[] { typeof(LabEventHandler) });
        if (nonGenericInvoke != null)
        {
            if (patchManager.PatchPrefix(Id, nonGenericInvoke, AccessTools.Method(typeof(LabApiEventCacheModule), nameof(InvokeNonGenericPrefix)), guard, config))
            {
                patched++;
            }
        }

        Type pe = typeof(PlayerEvents);
        _getValidatedVisibility = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerValidatedVisibilityEventArgs>>(pe, nameof(PlayerEvents.ValidatedVisibility));
        _getReceivingVoice = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerReceivingVoiceMessageEventArgs>>(pe, nameof(PlayerEvents.ReceivingVoiceMessage));
        _getSendingVoice = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerSendingVoiceMessageEventArgs>>(pe, nameof(PlayerEvents.SendingVoiceMessage));
        _getStayingInHazard = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayersStayingInHazardEventArgs>>(pe, nameof(PlayerEvents.StayingInHazard));
        _getIdlingTesla = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerIdlingTeslaEventArgs>>(pe, nameof(PlayerEvents.IdlingTesla));
        _getTriggeringTesla = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerTriggeringTeslaEventArgs>>(pe, nameof(PlayerEvents.TriggeringTesla));
        _getRoomChanged = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerRoomChangedEventArgs>>(pe, nameof(PlayerEvents.RoomChanged));
        _getZoneChanged = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerZoneChangedEventArgs>>(pe, nameof(PlayerEvents.ZoneChanged));
        _getMovementStateChanged = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerMovementStateChangedEventArgs>>(pe, nameof(PlayerEvents.MovementStateChanged));
        _getUpdatingEffect = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerEffectUpdatingEventArgs>>(pe, nameof(PlayerEvents.UpdatingEffect));
        _getUpdatedEffect = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerEffectUpdatedEventArgs>>(pe, nameof(PlayerEvents.UpdatedEffect));
        _getHurting = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerHurtingEventArgs>>(pe, nameof(PlayerEvents.Hurting));
        _getHurt = ReflectionHelpers.CreateStaticFieldGetter<LabEventHandler<PlayerHurtEventArgs>>(pe, nameof(PlayerEvents.Hurt));

        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnValidatedVisibility), typeof(PlayerValidatedVisibilityEventArgs), nameof(OnValidatedVisibilityPrefix), _getValidatedVisibility != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnReceivingVoiceMessage), typeof(PlayerReceivingVoiceMessageEventArgs), nameof(OnReceivingVoicePrefix), _getReceivingVoice != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnSendingVoiceMessage), typeof(PlayerSendingVoiceMessageEventArgs), nameof(OnSendingVoicePrefix), _getSendingVoice != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnStayingInHazard), typeof(PlayersStayingInHazardEventArgs), nameof(OnStayingInHazardPrefix), _getStayingInHazard != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnIdlingTesla), typeof(PlayerIdlingTeslaEventArgs), nameof(OnIdlingTeslaPrefix), _getIdlingTesla != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnTriggeringTesla), typeof(PlayerTriggeringTeslaEventArgs), nameof(OnTriggeringTeslaPrefix), _getTriggeringTesla != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnRoomChanged), typeof(PlayerRoomChangedEventArgs), nameof(OnRoomChangedPrefix), _getRoomChanged != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnZoneChanged), typeof(PlayerZoneChangedEventArgs), nameof(OnZoneChangedPrefix), _getZoneChanged != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnMovementStateChanged), typeof(PlayerMovementStateChangedEventArgs), nameof(OnMovementStateChangedPrefix), _getMovementStateChanged != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnUpdatingEffect), typeof(PlayerEffectUpdatingEventArgs), nameof(OnUpdatingEffectPrefix), _getUpdatingEffect != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnUpdatedEffect), typeof(PlayerEffectUpdatedEventArgs), nameof(OnUpdatedEffectPrefix), _getUpdatedEffect != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnHurting), typeof(PlayerHurtingEventArgs), nameof(OnHurtingPrefix), _getHurting != null);
        patched += TryPatchConcrete(patchManager, guard, config, pe, nameof(PlayerEvents.OnHurt), typeof(PlayerHurtEventArgs), nameof(OnHurtPrefix), _getHurt != null);

        if (patched == 0)
        {
            return MarkIncompatible("No LabAPI EventManager or PlayerEvents dispatch targets could be patched.");
        }

        StatusDetail = $"Active ({patched} event dispatchers cached)";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
    }

    private int TryPatchConcrete(
        PatchManager patchManager,
        CompatibilityGuard guard,
        SLPerformanceConfig config,
        Type hostType,
        string methodName,
        Type argType,
        string prefixName,
        bool getterReady)
    {
        if (!getterReady)
        {
            return 0;
        }

        MethodInfo target = AccessTools.Method(hostType, methodName, new[] { argType });
        if (target == null)
        {
            return 0;
        }

        return patchManager.PatchPrefix(Id, target, AccessTools.Method(typeof(LabApiEventCacheModule), prefixName), guard, config) ? 1 : 0;
    }

    private static bool InvokeNonGenericPrefix(LabEventHandler eventHandler)
    {
        if (eventHandler == null)
        {
            return false;
        }

        Delegate[] list = DelegateInvocationCache.GetInvocationListCached(eventHandler);
        for (int i = 0; i < list.Length; i++)
        {
            try
            {
                if (list[i] is LabEventHandler h)
                {
                    h();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(EventManager.FormatErrorMessage(eventHandler, ex));
            }
        }

        return false;
    }

    public static void DispatchCached<TEventArgs>(LabEventHandler<TEventArgs> eventHandler, TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (eventHandler == null)
        {
            return;
        }

        Delegate[] list = DelegateInvocationCache.GetInvocationListCached(eventHandler);
        for (int i = 0; i < list.Length; i++)
        {
            try
            {
                if (list[i] is LabEventHandler<TEventArgs> h)
                {
                    h(args);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(EventManager.FormatErrorMessage(eventHandler, ex));
            }
        }
    }

    private static bool OnValidatedVisibilityPrefix(PlayerValidatedVisibilityEventArgs ev)
    {
        DispatchCached(_getValidatedVisibility(), ev);
        return false;
    }

    private static bool OnReceivingVoicePrefix(PlayerReceivingVoiceMessageEventArgs ev)
    {
        DispatchCached(_getReceivingVoice(), ev);
        return false;
    }

    private static bool OnSendingVoicePrefix(PlayerSendingVoiceMessageEventArgs ev)
    {
        DispatchCached(_getSendingVoice(), ev);
        return false;
    }

    private static bool OnStayingInHazardPrefix(PlayersStayingInHazardEventArgs ev)
    {
        DispatchCached(_getStayingInHazard(), ev);
        return false;
    }

    private static bool OnIdlingTeslaPrefix(PlayerIdlingTeslaEventArgs ev)
    {
        DispatchCached(_getIdlingTesla(), ev);
        return false;
    }

    private static bool OnTriggeringTeslaPrefix(PlayerTriggeringTeslaEventArgs ev)
    {
        DispatchCached(_getTriggeringTesla(), ev);
        return false;
    }

    private static bool OnRoomChangedPrefix(PlayerRoomChangedEventArgs ev)
    {
        DispatchCached(_getRoomChanged(), ev);
        return false;
    }

    private static bool OnZoneChangedPrefix(PlayerZoneChangedEventArgs ev)
    {
        DispatchCached(_getZoneChanged(), ev);
        return false;
    }

    private static bool OnMovementStateChangedPrefix(PlayerMovementStateChangedEventArgs ev)
    {
        DispatchCached(_getMovementStateChanged(), ev);
        return false;
    }

    private static bool OnUpdatingEffectPrefix(PlayerEffectUpdatingEventArgs ev)
    {
        DispatchCached(_getUpdatingEffect(), ev);
        return false;
    }

    private static bool OnUpdatedEffectPrefix(PlayerEffectUpdatedEventArgs ev)
    {
        DispatchCached(_getUpdatedEffect(), ev);
        return false;
    }

    private static bool OnHurtingPrefix(PlayerHurtingEventArgs ev)
    {
        DispatchCached(_getHurting(), ev);
        return false;
    }

    private static bool OnHurtPrefix(PlayerHurtEventArgs ev)
    {
        DispatchCached(_getHurt(), ev);
        return false;
    }
}
