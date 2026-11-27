using System;
using System.Collections.Generic;
using System.Reflection;
using Footprinting;
using HarmonyLib;
using Interactables.Interobjects;
using InventorySystem.Items.Firearms.Ammo;
using InventorySystem.Items.Keycards;
using InventorySystem.Items.ThrowableProjectiles;
using InventorySystem.Items.Usables.Scp1576;
using MapGeneration.Decoration;
using Mirror;
using PlayerRoles.PlayableScps.Scp079.Cameras;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using UnityEngine;
using UnityStandardAssets.Utility;

namespace SLPerformance.Entities;

/// <summary>
/// OPT-20: Sleeps idle world MonoBehaviours and strips verified client-only visual Updates/LateUpdates on headless servers:
/// 1. RoomLightController (105x): sleeps while _flickerDuration <= 0; wakes instantly in ServerFlickerLights(dur > 0).
/// 2. TimedGrenadePickup (26x): sleeps while !_replaceNextFrame; wakes instantly in OnExplosionDetected.
/// 3. Scp079Camera (157x): sleeps when !IsActive and all 3 axes converged; wakes instantly on IsActive=true or ApplyAxes.
/// 4. BreakableWindow.LateUpdate (37x, headless): skips redundant MeshRenderer shadow/layer writes while unbroken; runs normally on break.
/// 5. Client-only visual components on headless dedicated server:
///    AmmoPickup.Update (86x digit material), EnvironmentalAlarm (22x + its _lightRotator), LiquidWobble (10x),
///    OneSidedKeycardFlipper (16x), PopupInterobject (24x).
/// Uses a first-tick self-sleep pattern so round restarts and dynamically spawned prefabs sleep automatically with zero scans.
/// </summary>
public sealed class HeadlessWorldSleepModule : OptimizationModuleBase
{
    public override string Id => "entities.headless_world_sleep";
    public override string Name => "Idle World Sleep & Headless Visual Strip";
    public override OptimizationTier Tier => OptimizationTier.Tier2;
    public override string Description => "Sleeps idle RoomLightController, TimedGrenadePickup, and Scp079Camera instances until triggered, and skips verified client-only visual Updates/LateUpdates on headless dedicated servers.";

    private static Func<RoomLightController, float> _getFlickerDuration;
    private static Func<TimedGrenadePickup, bool> _getReplaceNextFrame;
    private static Func<EnvironmentalAlarm, AutoMoveAndRotate> _getAlarmRotator;

    private static readonly HashSet<MonoBehaviour> SleptBehaviours = new HashSet<MonoBehaviour>();
    private static bool _active;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.EntitiesHeadlessWorldSleep;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        _getFlickerDuration = ReflectionHelpers.CreateInstanceFieldGetter<RoomLightController, float>("_flickerDuration");
        _getReplaceNextFrame = ReflectionHelpers.CreateInstanceFieldGetter<TimedGrenadePickup, bool>("_replaceNextFrame");
        _getAlarmRotator = ReflectionHelpers.CreateInstanceFieldGetter<EnvironmentalAlarm, AutoMoveAndRotate>("_lightRotator");

        int patched = 0;

        // 1. RoomLightController (105x)
        MethodInfo lightUpdate = AccessTools.Method(typeof(RoomLightController), "Update", Type.EmptyTypes);
        MethodInfo lightFlicker = AccessTools.Method(typeof(RoomLightController), nameof(RoomLightController.ServerFlickerLights), new[] { typeof(float) });
        if (_getFlickerDuration != null && lightUpdate != null && lightFlicker != null)
        {
            bool ok1 = patchManager.PatchPrefix(Id, lightUpdate, AccessTools.Method(typeof(HeadlessWorldSleepModule), nameof(RoomLightUpdatePrefix)), guard, config);
            bool ok2 = patchManager.PatchPrefix(Id, lightFlicker, AccessTools.Method(typeof(HeadlessWorldSleepModule), nameof(RoomLightFlickerPrefix)), guard, config);
            if (ok1 && ok2) patched++;
        }

        // 2. TimedGrenadePickup (26x)
        MethodInfo grenadeUpdate = AccessTools.Method(typeof(TimedGrenadePickup), "Update", Type.EmptyTypes);
        MethodInfo grenadeExplode = AccessTools.Method(typeof(TimedGrenadePickup), nameof(TimedGrenadePickup.OnExplosionDetected), new[] { typeof(Footprint), typeof(Vector3), typeof(float) });
        if (_getReplaceNextFrame != null && grenadeUpdate != null && grenadeExplode != null)
        {
            bool ok1 = patchManager.PatchPrefix(Id, grenadeUpdate, AccessTools.Method(typeof(HeadlessWorldSleepModule), nameof(TimedGrenadeUpdatePrefix)), guard, config);
            bool ok2 = patchManager.PatchPostfix(Id, grenadeExplode, AccessTools.Method(typeof(HeadlessWorldSleepModule), nameof(TimedGrenadeExplosionPostfix)), guard, config);
            if (ok1 && ok2) patched++;
        }

        // 3. Scp079Camera (157x)
        MethodInfo camUpdate = AccessTools.Method(typeof(Scp079Camera), "Update", Type.EmptyTypes);
        MethodInfo camSetActive = AccessTools.PropertySetter(typeof(Scp079Camera), nameof(Scp079Camera.IsActive));
        MethodInfo camApplyAxes = AccessTools.Method(typeof(Scp079Camera), "ApplyAxes", new[] { typeof(NetworkReader) });
        if (camUpdate != null && camSetActive != null && camApplyAxes != null)
        {
            bool ok1 = patchManager.PatchPrefix(Id, camUpdate, AccessTools.Method(typeof(HeadlessWorldSleepModule), nameof(Scp079CameraUpdatePrefix)), guard, config);
            bool ok2 = patchManager.PatchPostfix(Id, camSetActive, AccessTools.Method(typeof(HeadlessWorldSleepModule), nameof(Scp079CameraWakePostfix)), guard, config);
            bool ok3 = patchManager.PatchPostfix(Id, camApplyAxes, AccessTools.Method(typeof(HeadlessWorldSleepModule), nameof(Scp079CameraWakePostfix)), guard, config);
            if (ok1 && ok2 && ok3) patched++;
        }

        // 4. BreakableWindow.LateUpdate (37x, headless)
        MethodInfo windowLate = AccessTools.Method(typeof(BreakableWindow), "LateUpdate", Type.EmptyTypes);
        if (windowLate != null &&
            patchManager.PatchPrefix(Id, windowLate, AccessTools.Method(typeof(HeadlessWorldSleepModule), nameof(BreakableWindowLateUpdatePrefix)), guard, config))
        {
            patched++;
        }

        // 5. Verified headless client-only visual behaviours
        MethodInfo sleepSelfHeadless = AccessTools.Method(typeof(HeadlessWorldSleepModule), nameof(SelfSleepOnHeadlessPrefix));

        MethodInfo alarmUpdate = AccessTools.Method(typeof(EnvironmentalAlarm), "Update", Type.EmptyTypes);
        if (alarmUpdate != null &&
            patchManager.PatchPrefix(Id, alarmUpdate, AccessTools.Method(typeof(HeadlessWorldSleepModule), nameof(EnvironmentalAlarmUpdatePrefix)), guard, config))
        {
            patched++;
        }

        MethodInfo liquidUpdate = AccessTools.Method(typeof(LiquidWobble), "Update", Type.EmptyTypes);
        if (liquidUpdate != null && patchManager.PatchPrefix(Id, liquidUpdate, sleepSelfHeadless, guard, config)) patched++;

        MethodInfo flipperLate = AccessTools.Method(typeof(OneSidedKeycardFlipper), "LateUpdate", Type.EmptyTypes);
        if (flipperLate != null && patchManager.PatchPrefix(Id, flipperLate, sleepSelfHeadless, guard, config)) patched++;

        MethodInfo popupUpdate = AccessTools.Method(typeof(PopupInterobject), "Update", Type.EmptyTypes);
        if (popupUpdate != null && patchManager.PatchPrefix(Id, popupUpdate, sleepSelfHeadless, guard, config)) patched++;

        MethodInfo ammoUpdate = AccessTools.Method(typeof(AmmoPickup), "Update", Type.EmptyTypes);
        if (ammoUpdate != null &&
            patchManager.PatchPrefix(Id, ammoUpdate, AccessTools.Method(typeof(HeadlessWorldSleepModule), nameof(AmmoPickupUpdatePrefix)), guard, config))
        {
            patched++;
        }

        // 6. Headless UnityEngine.Rendering.Volume (299x post-processing volumes on dedicated server with no GPU/camera!)
        Type volumeType = Type.GetType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime")
                          ?? AccessTools.TypeByName("UnityEngine.Rendering.Volume");
        MethodInfo volumeUpdate = volumeType != null ? AccessTools.Method(volumeType, "Update", Type.EmptyTypes) : null;
        if (volumeUpdate != null && patchManager.PatchPrefix(Id, volumeUpdate, sleepSelfHeadless, guard, config))
        {
            patched++;
        }

        // 7. Headless Scp1576Playback (40x client-only voice playback LateUpdate + Update on dedicated server)
        MethodInfo scp1576Late = AccessTools.Method(typeof(Scp1576Playback), "LateUpdate", Type.EmptyTypes);
        if (scp1576Late != null && patchManager.PatchPrefix(Id, scp1576Late, sleepSelfHeadless, guard, config))
        {
            patched++;
        }

        if (patched == 0)
        {
            patchManager.UnpatchModule(Id);
            return MarkIncompatible("No OPT-20 world sleep targets could be patched.");
        }

        _active = true;
        StatusDetail = $"Active ({patched}/11 idle world & headless visual optimizations)";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        _active = false;
        foreach (MonoBehaviour mb in SleptBehaviours)
        {
            if (mb != null)
            {
                mb.enabled = true;
            }
        }

        SleptBehaviours.Clear();
    }

    private static void Sleep(MonoBehaviour mb)
    {
        if (mb != null && mb.enabled)
        {
            mb.enabled = false;
            SleptBehaviours.Add(mb);
        }
    }

    private static void Wake(MonoBehaviour mb)
    {
        if (mb != null && !mb.enabled)
        {
            mb.enabled = true;
            SleptBehaviours.Remove(mb);
        }
    }

    private static bool RoomLightUpdatePrefix(RoomLightController __instance)
    {
        if (!_active || !NetworkServer.active || __instance == null)
        {
            return true;
        }

        if (_getFlickerDuration(__instance) <= 0f)
        {
            Sleep(__instance);
            return false;
        }

        return true;
    }

    private static void RoomLightFlickerPrefix(RoomLightController __instance, float dur)
    {
        if (_active && NetworkServer.active && dur > 0f)
        {
            Wake(__instance);
        }
    }

    private static bool TimedGrenadeUpdatePrefix(TimedGrenadePickup __instance)
    {
        if (!_active || !NetworkServer.active || __instance == null)
        {
            return true;
        }

        if (!_getReplaceNextFrame(__instance))
        {
            Sleep(__instance);
            return false;
        }

        return true;
    }

    private static void TimedGrenadeExplosionPostfix(TimedGrenadePickup __instance)
    {
        if (_active && NetworkServer.active && __instance != null && _getReplaceNextFrame(__instance))
        {
            Wake(__instance);
        }
    }

    private static bool Scp079CameraUpdatePrefix(Scp079Camera __instance)
    {
        if (!_active || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || __instance == null)
        {
            return true;
        }

        if (!__instance.IsActive &&
            __instance.VerticalAxis.CurValue == __instance.VerticalAxis.TargetValue &&
            __instance.HorizontalAxis.CurValue == __instance.HorizontalAxis.TargetValue &&
            __instance.ZoomAxis.CurValue == __instance.ZoomAxis.TargetValue)
        {
            Sleep(__instance);
            return false;
        }

        return true;
    }

    private static void Scp079CameraWakePostfix(Scp079Camera __instance)
    {
        if (_active && NetworkServer.active && __instance != null)
        {
            Wake(__instance);
        }
    }

    private static bool BreakableWindowLateUpdatePrefix(BreakableWindow __instance)
    {
        if (!_active || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || __instance == null)
        {
            return true;
        }

        // Unbroken glass already has layer 14 and headless servers never cast shadows;
        // once IsBroken flips true, let vanilla LateUpdate run to destroy MeshRenderers and switch layer to 28.
        return __instance.IsBroken;
    }

    private static bool EnvironmentalAlarmUpdatePrefix(EnvironmentalAlarm __instance)
    {
        if (!_active || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || __instance == null)
        {
            return true;
        }

        if (_getAlarmRotator != null)
        {
            AutoMoveAndRotate rot = _getAlarmRotator(__instance);
            if (rot != null)
            {
                Sleep(rot);
            }
        }

        Sleep(__instance);
        return false;
    }

    private static bool SelfSleepOnHeadlessPrefix(MonoBehaviour __instance)
    {
        if (!_active || !HeadlessCharacterAnimatorModule.IsHeadlessDedicated || __instance == null)
        {
            return true;
        }

        Sleep(__instance);
        return false;
    }

    private static bool AmmoPickupUpdatePrefix()
    {
        // AmmoPickup.Update only updates client digit Renderer materials; never disable NetworkBehaviour.enabled here.
        return !(_active && HeadlessCharacterAnimatorModule.IsHeadlessDedicated);
    }
}
