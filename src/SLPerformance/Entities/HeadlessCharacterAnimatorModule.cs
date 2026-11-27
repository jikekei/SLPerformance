using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirror;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.Wearables;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using UnityEngine;
using UnityEngine.Rendering;

namespace SLPerformance.Entities;

/// <summary>
/// OPT-18: Headless Dedicated Server Character Animator Parallelization & Client-Visual Subcontroller Stripping.
///
/// Live 48/48 profiling proved `CullingSubcontroller.LateUpdate()` is the #1 real-player CPU hotspot (~2.8ms/frame):
/// - `CullingSubcontroller.OnReassigned()` disables `Animator.enabled` and instead runs `_model.Animator.Update(dt)`
///   synchronously & single-threaded on the main thread inside `C# LateUpdate`.
/// - It also invokes 4 purely client-side visual subcontrollers per player:
///   HandPoseSubcontroller (finger layers), FeetStabilizerSubcontroller (visual foot IK),
///   SecondaryRigsSubcontroller (cosmetic rig bone copies), WearableArmor (cosmetic armor bone copies),
///   plus GlowInTheDarkSubcontroller (shader emission) & OffsetableSimpleWearable (hat offsets).
///
/// Optimization (strictly active on headless dedicated servers only; LookatSubcontroller head hitbox IK is preserved 100%):
/// 1. Strips the 6 verified client-only visual subcontrollers on headless servers.
/// 2. When `HeadlessAnimatorParallelNative` is enabled (default), restores `Animator.enabled = true` (`AlwaysAnimate`)
///    so Unity 6's C++ Animation Job System evaluates all player skeletal hitboxes in parallel on worker threads
///    during `PreLateUpdate.DirectorUpdateAnimation`, eliminating single-threaded `CullingSubcontroller.LateUpdate()`.
/// </summary>
public sealed class HeadlessCharacterAnimatorModule : OptimizationModuleBase
{
    public override string Id => "anim.headless_character_animator";
    public override string Name => "Headless Parallel Character Animator & Visual Subcontroller Strip";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Strips 6 client-only character visual subcontrollers and moves headless player Animators onto Unity 6's multi-threaded Animation Job System while preserving hitboxes and Lookat IK.";

    private static Func<CullingSubcontroller, AnimatedCharacterModel> _getCullerModel;
    private static Func<AnimatedCharacterModel, Animator> _getModelAnimator;
    private static readonly HashSet<Animator> ParallelEnabledAnimators = new HashSet<Animator>();
    private static readonly HashSet<MonoBehaviour> SleptSubcontrollers = new HashSet<MonoBehaviour>();
    private static bool _active;
    private static bool _parallelNative;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.AnimHeadlessCharacterAnimator;

    internal static bool IsHeadlessDedicated =>
        NetworkServer.active &&
        (ServerStatic.IsDedicated ||
         Application.isBatchMode ||
         SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null);

    internal static bool IsParallelNativeActive => _active && _parallelNative;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        _getCullerModel = ReflectionHelpers.CreateInstanceFieldGetter<CullingSubcontroller, AnimatedCharacterModel>("_model");
        _getModelAnimator = ReflectionHelpers.CreateInstanceFieldGetter<AnimatedCharacterModel, Animator>("<Animator>k__BackingField");

        MethodInfo cullerReassigned = AccessTools.Method(typeof(CullingSubcontroller), nameof(CullingSubcontroller.OnReassigned), Type.EmptyTypes);
        MethodInfo cullerLateUpdate = AccessTools.Method(typeof(CullingSubcontroller), "LateUpdate", Type.EmptyTypes);

        MethodInfo handEval = AccessTools.Method(typeof(HandPoseSubcontroller), "Evaluate", Type.EmptyTypes);
        MethodInfo handLate = AccessTools.Method(typeof(HandPoseSubcontroller), "LateUpdate", Type.EmptyTypes);
        MethodInfo feetWeight = AccessTools.Method(typeof(FeetStabilizerSubcontroller), "UpdateWeight", Type.EmptyTypes);
        MethodInfo feetUpdate = AccessTools.Method(typeof(FeetStabilizerSubcontroller), "Update", Type.EmptyTypes);
        MethodInfo feetIk = AccessTools.Method(typeof(FeetStabilizerSubcontroller), "OnAnimatorIK", new[] { typeof(int) });
        MethodInfo secMatch = AccessTools.Method(typeof(SecondaryRigsSubcontroller), nameof(SecondaryRigsSubcontroller.MatchAll), Type.EmptyTypes);
        MethodInfo secLate = AccessTools.Method(typeof(SecondaryRigsSubcontroller), "LateUpdate", Type.EmptyTypes);
        MethodInfo armorMatch = AccessTools.Method(typeof(WearableArmor), "MatchAllBones", Type.EmptyTypes);
        MethodInfo armorLate = AccessTools.Method(typeof(WearableArmor), "LateUpdate", Type.EmptyTypes);
        MethodInfo glowLate = AccessTools.Method(typeof(GlowInTheDarkSubcontroller), "LateUpdate", Type.EmptyTypes);
        MethodInfo hatLate = AccessTools.Method(typeof(OffsetableSimpleWearable), "LateUpdate", Type.EmptyTypes);

        if (_getCullerModel == null || _getModelAnimator == null || cullerReassigned == null || cullerLateUpdate == null)
        {
            return MarkIncompatible("Required CullingSubcontroller or AnimatedCharacterModel members were not found.");
        }

        MethodInfo skipOnHeadless = AccessTools.Method(typeof(HeadlessCharacterAnimatorModule), nameof(SkipOnHeadlessPrefix));
        MethodInfo sleepBehaviourOnHeadless = AccessTools.Method(typeof(HeadlessCharacterAnimatorModule), nameof(SleepBehaviourOnHeadlessPrefix));

        int visualPatched = 0;
        // Pure event/callback targets (skip without touching MonoBehaviour.enabled)
        MethodInfo[] skipOnlyTargets =
        {
            handEval, feetWeight, feetIk, secMatch, armorMatch, armorLate, glowLate, hatLate
        };

        for (int i = 0; i < skipOnlyTargets.Length; i++)
        {
            if (skipOnlyTargets[i] != null && patchManager.PatchPrefix(Id, skipOnlyTargets[i], skipOnHeadless, guard, config))
            {
                visualPatched++;
            }
        }

        // Verified OnEnable/OnDisable-free visual subcontrollers: disable MonoBehaviour.enabled on first Update/LateUpdate
        // to eliminate native C++ -> Mono dispatch overhead on headless servers.
        MethodInfo[] sleepableLifecycleTargets = { handLate, feetUpdate, secLate };
        for (int i = 0; i < sleepableLifecycleTargets.Length; i++)
        {
            if (sleepableLifecycleTargets[i] != null && patchManager.PatchPrefix(Id, sleepableLifecycleTargets[i], sleepBehaviourOnHeadless, guard, config))
            {
                visualPatched++;
            }
        }

        _parallelNative = config.HeadlessAnimatorParallelNative;
        bool parallelPatched = false;
        if (_parallelNative)
        {
            bool okReassign = patchManager.PatchPostfix(
                Id,
                cullerReassigned,
                AccessTools.Method(typeof(HeadlessCharacterAnimatorModule), nameof(CullerOnReassignedPostfix)),
                guard,
                config);

            bool okLate = patchManager.PatchPrefix(
                Id,
                cullerLateUpdate,
                AccessTools.Method(typeof(HeadlessCharacterAnimatorModule), nameof(CullerLateUpdatePrefix)),
                guard,
                config);

            parallelPatched = okReassign && okLate;
            if (!parallelPatched)
            {
                _parallelNative = false;
            }
        }

        if (visualPatched == 0 && !parallelPatched)
        {
            patchManager.UnpatchModule(Id);
            return MarkConflict("External Harmony conflict on CullingSubcontroller and character subcontrollers.");
        }

        _active = true;

        int migrated = 0;
        if (_parallelNative && IsHeadlessDedicated)
        {
            migrated = MigrateExistingCullers(enableNativeAnimator: true);
        }

        StatusDetail = _parallelNative
            ? $"Active (ParallelNative=True, VisualHooks={visualPatched}/11, MigratedNow={migrated})"
            : $"Active (ThrottledStripped, VisualHooks={visualPatched}/11)";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        _active = false;
        foreach (MonoBehaviour mb in SleptSubcontrollers)
        {
            if (mb != null)
            {
                mb.enabled = true;
            }
        }

        SleptSubcontrollers.Clear();

        if (_parallelNative)
        {
            MigrateExistingCullers(enableNativeAnimator: false);
            foreach (Animator anim in ParallelEnabledAnimators)
            {
                if (anim != null)
                {
                    anim.enabled = false;
                }
            }

            ParallelEnabledAnimators.Clear();
            _parallelNative = false;
        }
    }

    private static int MigrateExistingCullers(bool enableNativeAnimator)
    {
        int count = 0;
        try
        {
            CullingSubcontroller[] cullers = UnityEngine.Object.FindObjectsByType<CullingSubcontroller>(FindObjectsSortMode.None);
            for (int i = 0; i < cullers.Length; i++)
            {
                CullingSubcontroller c = cullers[i];
                if (c == null) continue;
                AnimatedCharacterModel model = _getCullerModel(c);
                if (model == null || model.Pooled) continue;
                Animator anim = _getModelAnimator(model);
                if (anim == null) continue;

                if (enableNativeAnimator)
                {
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    anim.enabled = true;
                    ParallelEnabledAnimators.Add(anim);
                    if (c.enabled)
                    {
                        c.enabled = false;
                        SleptSubcontrollers.Add(c);
                    }
                }
                else
                {
                    anim.enabled = false;
                    ParallelEnabledAnimators.Remove(anim);
                    if (!c.enabled)
                    {
                        c.enabled = true;
                    }
                }

                count++;
            }
        }
        catch (Exception ex)
        {
            SLPerformanceLog.Debug($"OPT-18 culler migration note: {ex.Message}");
        }

        return count;
    }

    private static void CullerOnReassignedPostfix(CullingSubcontroller __instance)
    {
        if (!_active || !_parallelNative || !IsHeadlessDedicated || __instance == null)
        {
            return;
        }

        AnimatedCharacterModel model = _getCullerModel(__instance);
        Animator anim = model != null ? _getModelAnimator(model) : null;
        if (anim != null)
        {
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            anim.enabled = true;
            ParallelEnabledAnimators.Add(anim);
            if (__instance.enabled)
            {
                __instance.enabled = false;
                SleptSubcontrollers.Add(__instance);
            }
        }
    }

    private static bool CullerLateUpdatePrefix(CullingSubcontroller __instance)
    {
        if (!_active || !_parallelNative || !IsHeadlessDedicated)
        {
            return true;
        }

        // Ensure Animator is enabled and sleep CullingSubcontroller to eliminate C++ -> Mono LateUpdate calls.
        AnimatedCharacterModel model = _getCullerModel(__instance);
        Animator anim = model != null ? _getModelAnimator(model) : null;
        if (anim != null && !anim.enabled)
        {
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            anim.enabled = true;
            ParallelEnabledAnimators.Add(anim);
        }

        if (__instance != null && __instance.enabled)
        {
            __instance.enabled = false;
            SleptSubcontrollers.Add(__instance);
        }

        return false;
    }

    private static bool SkipOnHeadlessPrefix()
    {
        return !(_active && IsHeadlessDedicated);
    }

    private static bool SleepBehaviourOnHeadlessPrefix(MonoBehaviour __instance)
    {
        if (!_active || !IsHeadlessDedicated)
        {
            return true;
        }

        if (__instance != null && __instance.enabled)
        {
            __instance.enabled = false;
            SleptSubcontrollers.Add(__instance);
        }

        return false;
    }
}
