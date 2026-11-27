using System;
using System.Collections.Generic;
using System.Reflection;
using CustomPlayerEffects;
using HarmonyLib;
using Mirror;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;

namespace SLPerformance.Entities;

/// <summary>
/// OPT-09: Puts inactive standard StatusEffectBase MonoBehaviours to sleep (enabled = false) while Intensity == 0.
/// Eliminates ~2,200+ native C++ -> Mono Update() transitions per tick at 60 players.
/// Strict reflection whitelist: only sleeps subclasses that do NOT override Update/LateUpdate/FixedUpdate/OnEnable/OnDisable.
/// </summary>
public sealed class StatusEffectSleepModule : OptimizationModuleBase
{
    public override string Id => "entities.status_effect_sleep";
    public override string Name => "Inactive StatusEffectBase Sleep";
    public override OptimizationTier Tier => OptimizationTier.Tier2;
    public override string Description => "Disables MonoBehaviour.enabled on reflection-verified standard StatusEffectBase instances while Intensity == 0.";

    private static readonly Dictionary<Type, bool> SleepableTypes = new Dictionary<Type, bool>(64);
    private static readonly HashSet<StatusEffectBase> SleepingEffects = new HashSet<StatusEffectBase>();
    private static bool _active;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.EntitiesStatusEffectSleep;

    internal static bool IsTypeSleepable(Type effectType)
    {
        if (effectType == null || !typeof(StatusEffectBase).IsAssignableFrom(effectType))
        {
            return false;
        }

        if (SleepableTypes.TryGetValue(effectType, out bool cached))
        {
            return cached;
        }

        // Explicitly verified idle-safe subclasses whose Update/LateUpdate strictly gate on IsEnabled (Intensity > 0):
        // - Fade: LateUpdate only calls UpdateFade() when IsEnabled; Disabled() already resets fade on disable.
        // - AmnesiaItems: Update only increments _activeTime when IsEnabled.
        if (effectType == typeof(Fade) || effectType == typeof(AmnesiaItems))
        {
            SleepableTypes[effectType] = true;
            return true;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo update = effectType.GetMethod("Update", flags, null, Type.EmptyTypes, null);
        bool standardUpdateOnly = update != null && update.DeclaringType == typeof(StatusEffectBase);

        bool hasExtraLifecycle =
            HasLifecycleOverride(effectType, "LateUpdate") ||
            HasLifecycleOverride(effectType, "FixedUpdate") ||
            HasLifecycleOverride(effectType, "OnEnable") ||
            HasLifecycleOverride(effectType, "OnDisable");

        bool sleepable = standardUpdateOnly && !hasExtraLifecycle;
        SleepableTypes[effectType] = sleepable;
        return sleepable;
    }

    private static bool HasLifecycleOverride(Type effectType, string methodName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo m = effectType.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
        return m != null && m.DeclaringType != typeof(UnityEngine.MonoBehaviour) && m.DeclaringType != typeof(UnityEngine.Behaviour);
    }

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        MethodInfo startMethod = AccessTools.Method(typeof(StatusEffectBase), "Start", Type.EmptyTypes);
        MethodInfo forceIntensity = AccessTools.Method(typeof(StatusEffectBase), nameof(StatusEffectBase.ForceIntensity), new[] { typeof(byte) });
        if (startMethod == null || forceIntensity == null)
        {
            return MarkIncompatible("StatusEffectBase.Start or ForceIntensity not found.");
        }

        if (!patchManager.PatchPostfix(Id, startMethod, AccessTools.Method(typeof(StatusEffectSleepModule), nameof(StartPostfix)), guard, config) ||
            !patchManager.PatchPostfix(Id, forceIntensity, AccessTools.Method(typeof(StatusEffectSleepModule), nameof(ForceIntensityPostfix)), guard, config))
        {
            patchManager.UnpatchModule(Id);
            return MarkConflict("External Harmony conflict on StatusEffectBase.");
        }

        _active = true;

        // Put already-spawned inactive sleepable effects to sleep immediately.
        int putToSleep = 0;
        if (NetworkServer.active)
        {
            foreach (ReferenceHub hub in ReferenceHub.AllHubs)
            {
                if (hub == null || hub.playerEffectsController == null || hub.playerEffectsController.AllEffects == null)
                {
                    continue;
                }

                StatusEffectBase[] effects = hub.playerEffectsController.AllEffects;
                for (int i = 0; i < effects.Length; i++)
                {
                    StatusEffectBase eff = effects[i];
                    if (eff != null && eff.Intensity == 0 && IsTypeSleepable(eff.GetType()))
                    {
                        eff.enabled = false;
                        SleepingEffects.Add(eff);
                        putToSleep++;
                    }
                }
            }
        }

        StatusDetail = $"Active (initial sleeping={putToSleep})";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        _active = false;
        foreach (StatusEffectBase eff in SleepingEffects)
        {
            if (eff != null)
            {
                eff.enabled = true;
            }
        }

        SleepingEffects.Clear();
    }

    private static void StartPostfix(StatusEffectBase __instance)
    {
        if (!_active || !NetworkServer.active || __instance == null)
        {
            return;
        }

        if (__instance.Intensity == 0 && IsTypeSleepable(__instance.GetType()))
        {
            __instance.enabled = false;
            SleepingEffects.Add(__instance);
        }
    }

    private static void ForceIntensityPostfix(StatusEffectBase __instance)
    {
        if (!_active || !NetworkServer.active || __instance == null)
        {
            return;
        }

        if (!IsTypeSleepable(__instance.GetType()))
        {
            return;
        }

        if (__instance.Intensity > 0)
        {
            if (!__instance.enabled)
            {
                __instance.enabled = true;
            }

            SleepingEffects.Remove(__instance);
        }
        else
        {
            if (__instance.enabled)
            {
                __instance.enabled = false;
            }

            SleepingEffects.Add(__instance);
        }
    }
}
