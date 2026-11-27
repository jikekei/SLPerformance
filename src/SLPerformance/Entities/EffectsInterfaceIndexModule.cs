using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using CursorManagement;
using CustomPlayerEffects;
using HarmonyLib;
using InventorySystem.Items;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.PlayableScps;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using UnityEngine;

namespace SLPerformance.Entities;

/// <summary>
/// OPT-07: Pre-indexes PlayerEffectsController.AllEffects by modifier interfaces via ConditionalWeakTable.
/// Eliminates ~40-element linear interface type-check scans across movement speed, input override,
/// stamina use/regen/disable, footsteps, and darkness ambient vision checks (also eliminates VisionInformation LINQ .Any()).
/// </summary>
public sealed class EffectsInterfaceIndexModule : OptimizationModuleBase
{
    public override string Id => "entities.effects_interface_index";
    public override string Name => "PlayerEffectsController Interface Index";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Caches typed interface subsets per PlayerEffectsController to replace repeated 40-element AllEffects scans and VisionInformation LINQ Any().";

    internal sealed class EffectInterfaceIndex
    {
        public StatusEffectBase[] SourceArray;
        public IMovementSpeedModifier[] SpeedModifiers;
        public IMovementInputOverride[] InputOverrides;
        public IStaminaModifier[] StaminaModifiers;
        public StatusEffectBase[] FootstepEffectComponents;
        public IFootstepEffect[] FootstepEffects;
        public StatusEffectBase[] AmbientEffectComponents;
    }

    private static readonly ConditionalWeakTable<PlayerEffectsController, EffectInterfaceIndex> Cache =
        new ConditionalWeakTable<PlayerEffectsController, EffectInterfaceIndex>();

    // Compiled field getters for hot methods
    private static Func<FirstPersonMovementModule, ReferenceHub> _getFpmmHub;
    private static Func<FpcMotor, List<ICursorOverride>> _getCursorOverrides;
    private static Func<FpcStateProcessor, ReferenceHub> _getSpHub;
    private static Func<FpcStateProcessor, float> _getSpRespawnImmunity;
    private static Func<FpcStateProcessor, float> _getSpUseRate;
    private static Func<FpcStateProcessor, AnimationCurve> _getSpRegenCurve;
    private static Func<FpcStateProcessor, Stopwatch> _getSpRegenSw;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.EntitiesEffectsInterfaceIndex;

    internal static EffectInterfaceIndex GetOrBuildIndex(PlayerEffectsController controller)
    {
        StatusEffectBase[] all = controller.AllEffects;
        if (Cache.TryGetValue(controller, out EffectInterfaceIndex existing) && ReferenceEquals(existing.SourceArray, all))
        {
            return existing;
        }

        EffectInterfaceIndex built = BuildIndex(all);
        if (existing != null)
        {
            existing.SourceArray = built.SourceArray;
            existing.SpeedModifiers = built.SpeedModifiers;
            existing.InputOverrides = built.InputOverrides;
            existing.StaminaModifiers = built.StaminaModifiers;
            existing.FootstepEffectComponents = built.FootstepEffectComponents;
            existing.FootstepEffects = built.FootstepEffects;
            existing.AmbientEffectComponents = built.AmbientEffectComponents;
            return existing;
        }

        Cache.Add(controller, built);
        return built;
    }

    internal static EffectInterfaceIndex BuildIndex(StatusEffectBase[] all)
    {
        var speed = new List<IMovementSpeedModifier>(8);
        var input = new List<IMovementInputOverride>(4);
        var stamina = new List<IStaminaModifier>(8);
        var footComp = new List<StatusEffectBase>(4);
        var footIface = new List<IFootstepEffect>(4);
        var ambient = new List<StatusEffectBase>(4);

        if (all != null)
        {
            for (int i = 0; i < all.Length; i++)
            {
                StatusEffectBase eff = all[i];
                if (eff == null)
                {
                    continue;
                }

                if (eff is IMovementSpeedModifier msm)
                {
                    speed.Add(msm);
                }

                if (eff is IMovementInputOverride mio)
                {
                    input.Add(mio);
                }

                if (eff is IStaminaModifier sm)
                {
                    stamina.Add(sm);
                }

                if (eff is IFootstepEffect fe)
                {
                    footComp.Add(eff);
                    footIface.Add(fe);
                }

                if (eff is IAmbientBoostingEffect)
                {
                    ambient.Add(eff);
                }
            }
        }

        return new EffectInterfaceIndex
        {
            SourceArray = all,
            SpeedModifiers = speed.ToArray(),
            InputOverrides = input.ToArray(),
            StaminaModifiers = stamina.ToArray(),
            FootstepEffectComponents = footComp.ToArray(),
            FootstepEffects = footIface.ToArray(),
            AmbientEffectComponents = ambient.ToArray()
        };
    }

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        int patched = 0;

        // 1. FirstPersonMovementModule.VelocityForState
        PropertyInfo hubProp = AccessTools.Property(typeof(FirstPersonMovementModule), "Hub");
        if (hubProp?.GetGetMethod(true) != null)
        {
            _getFpmmHub = (Func<FirstPersonMovementModule, ReferenceHub>)Delegate.CreateDelegate(
                typeof(Func<FirstPersonMovementModule, ReferenceHub>), hubProp.GetGetMethod(true));
        }

        MethodInfo velocityForState = AccessTools.Method(typeof(FirstPersonMovementModule), nameof(FirstPersonMovementModule.VelocityForState), new[] { typeof(PlayerMovementState), typeof(bool) });
        if (_getFpmmHub != null && velocityForState != null &&
            patchManager.PatchPrefix(Id, velocityForState, AccessTools.Method(typeof(EffectsInterfaceIndexModule), nameof(VelocityForStatePrefix)), guard, config))
        {
            patched++;
        }

        // 2. FpcMotor.TryGetOverride
        _getCursorOverrides = ReflectionHelpers.CreateInstanceFieldGetter<FpcMotor, List<ICursorOverride>>("_cursorOverrides");
        MethodInfo tryGetOverride = AccessTools.Method(typeof(FpcMotor), "TryGetOverride", new[] { typeof(Vector3).MakeByRefType() });
        if (_getCursorOverrides != null && tryGetOverride != null &&
            patchManager.PatchPrefix(Id, tryGetOverride, AccessTools.Method(typeof(EffectsInterfaceIndexModule), nameof(TryGetOverridePrefix)), guard, config))
        {
            patched++;
        }

        // 3. FpcStateProcessor properties
        PropertyInfo spHubProp = AccessTools.Property(typeof(FpcStateProcessor), "Hub");
        if (spHubProp?.GetGetMethod(true) != null)
        {
            _getSpHub = (Func<FpcStateProcessor, ReferenceHub>)Delegate.CreateDelegate(
                typeof(Func<FpcStateProcessor, ReferenceHub>), spHubProp.GetGetMethod(true));
        }

        _getSpRespawnImmunity = ReflectionHelpers.CreateInstanceFieldGetter<FpcStateProcessor, float>("_respawnImmunity");
        _getSpUseRate = ReflectionHelpers.CreateInstanceFieldGetter<FpcStateProcessor, float>("_useRate");
        _getSpRegenCurve = ReflectionHelpers.CreateInstanceFieldGetter<FpcStateProcessor, AnimationCurve>("_regenerationOverTime");
        _getSpRegenSw = ReflectionHelpers.CreateInstanceFieldGetter<FpcStateProcessor, Stopwatch>("_regenStopwatch");

        MethodInfo getUseRate = AccessTools.PropertyGetter(typeof(FpcStateProcessor), "ServerUseRate");
        MethodInfo getRegenRate = AccessTools.PropertyGetter(typeof(FpcStateProcessor), "ServerRegenRate");
        MethodInfo getSprintDisabled = AccessTools.PropertyGetter(typeof(FpcStateProcessor), "SprintingDisabled");

        if (_getSpHub != null && _getSpRespawnImmunity != null && _getSpUseRate != null && getUseRate != null &&
            patchManager.PatchPrefix(Id, getUseRate, AccessTools.Method(typeof(EffectsInterfaceIndexModule), nameof(ServerUseRatePrefix)), guard, config))
        {
            patched++;
        }

        if (_getSpHub != null && _getSpRegenCurve != null && _getSpRegenSw != null && getRegenRate != null &&
            patchManager.PatchPrefix(Id, getRegenRate, AccessTools.Method(typeof(EffectsInterfaceIndexModule), nameof(ServerRegenRatePrefix)), guard, config))
        {
            patched++;
        }

        if (_getSpHub != null && getSprintDisabled != null &&
            patchManager.PatchPrefix(Id, getSprintDisabled, AccessTools.Method(typeof(EffectsInterfaceIndexModule), nameof(SprintingDisabledPrefix)), guard, config))
        {
            patched++;
        }

        // 4. AnimatedCharacterModel.PlayFootstep
        MethodInfo playFootstep = AccessTools.Method(typeof(AnimatedCharacterModel), "PlayFootstep", Type.EmptyTypes);
        if (playFootstep != null &&
            patchManager.PatchPrefix(Id, playFootstep, AccessTools.Method(typeof(EffectsInterfaceIndexModule), nameof(PlayFootstepPrefix)), guard, config))
        {
            patched++;
        }

        // 5. VisionInformation.CheckIsInDarkness
        MethodInfo checkDarkness = AccessTools.Method(typeof(VisionInformation), nameof(VisionInformation.CheckIsInDarkness), new[] { typeof(ReferenceHub), typeof(Vector3) });
        if (checkDarkness != null &&
            patchManager.PatchPrefix(Id, checkDarkness, AccessTools.Method(typeof(EffectsInterfaceIndexModule), nameof(CheckIsInDarknessPrefix)), guard, config))
        {
            patched++;
        }

        if (patched == 0)
        {
            return MarkIncompatible("No effect-scanning target methods could be patched.");
        }

        StatusDetail = $"Active ({patched}/7 effect scan call sites indexed)";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
    }

    private static bool VelocityForStatePrefix(FirstPersonMovementModule __instance, PlayerMovementState state, bool applyCrouch, ref float __result)
    {
        ReferenceHub hub = _getFpmmHub(__instance);
        float num = 0f;
        switch (state)
        {
            case PlayerMovementState.Crouching:
                num = __instance.CrouchSpeed;
                break;
            case PlayerMovementState.Sneaking:
                num = __instance.SneakSpeed;
                break;
            case PlayerMovementState.Sprinting:
                num = __instance.SprintSpeed * hub.inventory.SprintSpeedMultiplier;
                break;
            case PlayerMovementState.Walking:
                num = __instance.WalkSpeed;
                break;
        }

        if (applyCrouch)
        {
            num = Mathf.Lerp(num, __instance.CrouchSpeed, __instance.StateProcessor.CrouchPercent);
        }

        num *= hub.inventory.MovementSpeedMultiplier;
        float limit = hub.inventory.MovementSpeedLimit;

        IMovementSpeedModifier[] mods = GetOrBuildIndex(hub.playerEffectsController).SpeedModifiers;
        for (int i = 0; i < mods.Length; i++)
        {
            IMovementSpeedModifier mod = mods[i];
            if (mod.MovementModifierActive)
            {
                limit = Mathf.Min(limit, mod.MovementSpeedLimit);
                num *= mod.MovementSpeedMultiplier;
            }
        }

        float sign = Mathf.Sign(num);
        __result = Mathf.Min(Mathf.Abs(num), limit) * sign;
        return false;
    }

    private static bool TryGetOverridePrefix(FpcMotor __instance, out Vector3 overrideDir, ref bool __result)
    {
        List<ICursorOverride> overrides = _getCursorOverrides(__instance);
        bool result = false;
        overrideDir = Vector3.zero;

        // Zero-allocation cleanup + scan (replaces List.RemoveAll delegate allocation)
        for (int i = overrides.Count - 1; i >= 0; i--)
        {
            ICursorOverride co = overrides[i];
            if (co == null || (co is UnityEngine.Object uo && uo == null))
            {
                overrides.RemoveAt(i);
            }
            else if (co.LockMovement)
            {
                result = true;
            }
        }

        ReferenceHub hub = __instance.Hub;
        if (hub.inventory.CurInstance.GetMobilityController() is IMovementInputOverride itemOv && itemOv.MovementOverrideActive)
        {
            result = true;
            overrideDir = itemOv.MovementOverrideDirection;
        }

        IMovementInputOverride[] effOverrides = GetOrBuildIndex(hub.playerEffectsController).InputOverrides;
        for (int i = 0; i < effOverrides.Length; i++)
        {
            IMovementInputOverride ov = effOverrides[i];
            if (ov.MovementOverrideActive)
            {
                result = true;
                overrideDir += ov.MovementOverrideDirection;
            }
        }

        __result = result;
        return false;
    }

    private static bool ServerUseRatePrefix(FpcStateProcessor __instance, ref float __result)
    {
        ReferenceHub hub = _getSpHub(__instance);
        if (hub.roleManager.CurrentRole.ActiveTime <= _getSpRespawnImmunity(__instance))
        {
            __result = 0f;
            return false;
        }

        float num = _getSpUseRate(__instance) * hub.inventory.StaminaUsageMultiplier;
        IStaminaModifier[] mods = GetOrBuildIndex(hub.playerEffectsController).StaminaModifiers;
        for (int i = 0; i < mods.Length; i++)
        {
            IStaminaModifier mod = mods[i];
            if (mod.StaminaModifierActive)
            {
                num *= mod.StaminaUsageMultiplier;
            }
        }

        __result = num;
        return false;
    }

    private static bool ServerRegenRatePrefix(FpcStateProcessor __instance, ref float __result)
    {
        float num = _getSpRegenCurve(__instance).Evaluate((float)_getSpRegenSw(__instance).Elapsed.TotalSeconds);
        IStaminaModifier[] mods = GetOrBuildIndex(_getSpHub(__instance).playerEffectsController).StaminaModifiers;
        for (int i = 0; i < mods.Length; i++)
        {
            IStaminaModifier mod = mods[i];
            if (mod.StaminaModifierActive)
            {
                num *= mod.StaminaRegenMultiplier;
            }
        }

        __result = num;
        return false;
    }

    private static bool SprintingDisabledPrefix(FpcStateProcessor __instance, ref bool __result)
    {
        ReferenceHub hub = _getSpHub(__instance);
        IStaminaModifier[] mods = GetOrBuildIndex(hub.playerEffectsController).StaminaModifiers;
        for (int i = 0; i < mods.Length; i++)
        {
            IStaminaModifier mod = mods[i];
            if (mod.StaminaModifierActive && mod.SprintingDisabled)
            {
                __result = true;
                return false;
            }
        }

        __result = hub.inventory.SprintingDisabled;
        return false;
    }

    private static bool PlayFootstepPrefix(AnimatedCharacterModel __instance)
    {
        float loudness = __instance.FootstepLoudnessDistance;
        float minOverride = 1f;
        bool allNegative = true;

        EffectInterfaceIndex idx = GetOrBuildIndex(__instance.OwnerHub.playerEffectsController);
        StatusEffectBase[] comps = idx.FootstepEffectComponents;
        IFootstepEffect[] ifaces = idx.FootstepEffects;
        for (int i = 0; i < comps.Length; i++)
        {
            if (comps[i].IsEnabled)
            {
                float ov = ifaces[i].ProcessFootstepOverrides(__instance, loudness);
                if (ov >= 0f)
                {
                    allNegative = false;
                }

                minOverride = Mathf.Min(minOverride, ov);
            }
        }

        if (!allNegative || minOverride >= 0f)
        {
            AnimatedCharacterModel.OnFootstepPlayed?.Invoke(__instance, loudness);
        }

        return false;
    }

    private static bool CheckIsInDarknessPrefix(ReferenceHub hub, Vector3 target, ref bool __result)
    {
        ItemBase cur = hub.inventory.CurInstance;
        if (cur != null && cur is ILightEmittingItem lightItem && lightItem.IsEmittingLight)
        {
            __result = false;
            return false;
        }

        if (!RoomLightController.IsInDarkenedRoom(target))
        {
            __result = false;
            return false;
        }

        StatusEffectBase[] ambient = GetOrBuildIndex(hub.playerEffectsController).AmbientEffectComponents;
        for (int i = 0; i < ambient.Length; i++)
        {
            if (ambient[i].IsEnabled)
            {
                __result = false;
                return false;
            }
        }

        __result = true;
        return false;
    }
}
