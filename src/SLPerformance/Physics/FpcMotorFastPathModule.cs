using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mirror;
using PlayerRoles.FirstPersonControl;
using RelativePositioning;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using SLPerformance.Profiler;
using UnityEngine;

namespace SLPerformance.Physics;

/// <summary>
/// OPT-11: FPC motor & mouse look fast paths:
/// 1. FpcMotor.UpdatePosition: evaluates DesiredMove FIRST (preserving stepOffset, noclip, movement lock & anticheat side effects)
///    and skips Physics.SphereCast completely when DesiredMove == Vector3.zero (or uses OPT-16 batched normal if present).
/// 2. FpcMotor.Move: updates FirstPersonMovementModule._cachedPosition directly instead of writing CachedTransform.position
///    back to the same Transform.position after CharacterController.Move().
/// 3. FpcMouseLook.UpdateRotation: on dedicated server (t2 == 1f), skips reading native transform.rotation/localRotation
///    and skips Quaternion.Lerp(..., 1f), directly writing TargetHubRotation / TargetCamRotation.
/// </summary>
public sealed class FpcMotorFastPathModule : OptimizationModuleBase
{
    public override string Id => "physics.fpc_motor_fastpath";
    public override string Name => "FPC Motor SphereCast Skip & MouseLook Fast Path";
    public override OptimizationTier Tier => OptimizationTier.Tier2;
    public override string Description => "Skips idle ground SphereCasts, eliminates redundant Transform.position write-back in FpcMotor.Move, and skips server Quaternion.Lerp.";

    // FpcMotor private/protected helpers
    private static Func<FpcMotor, float> _getSpeed;
    private static Action<FpcMotor, float> _setLastMaxSpeed;
    private static Func<FpcMotor, int> _getViewMode; // 0=LocalPlayer, 1=Server, 2=Thirdperson
    private static Action<FpcMotor> _callUpdateThirdperson;
    private static Func<FpcMotor, Vector3> _getDesiredMove;
    private static Func<FpcMotor, Vector3> _callGetFrameMove;
    private static Func<FpcMotor, Transform> _getCachedTransform;
    private static Func<FpcMotor, Action<Vector3>> _getOnBeforeMove;
    private static Action<FpcMotor, Vector3> _setVelocity;

    // FirstPersonMovementModule private fields
    private static Action<FirstPersonMovementModule, Vector3> _setCachedPosition;
    private static Func<bool> _getMovementUpdateCycle;

    // FpcMouseLook private fields
    private static Func<FpcMouseLook, ReferenceHub> _getMlHub;
    private static Func<FpcMouseLook, FirstPersonMovementModule> _getMlFpmm;
    private static Func<FpcMouseLook, float> _getMlSyncH;
    private static Func<FpcMouseLook, float> _getMlSyncV;
    private static Func<FpcMouseLook, float> _getMlCurH;
    private static Func<FpcMouseLook, float> _getMlCurV;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.PhysicsFpcMotorFastPath;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        int activeParts = 0;
        var activeNames = new System.Collections.Generic.List<string>(3);
        var skippedNames = new System.Collections.Generic.List<string>(3);

        // 1. FpcMotor.UpdatePosition
        _getSpeed = CompilePropertyGetter<FpcMotor, float>("Speed");
        _setLastMaxSpeed = ReflectionHelpers.CreateInstanceFieldSetter<FpcMotor, float>("_lastMaxSpeed");
        _getViewMode = CompileEnumFieldAsIntGetter<FpcMotor>("ViewMode");
        _callUpdateThirdperson = CompileVoidCall<FpcMotor>("UpdateThirdperson");
        _getDesiredMove = CompilePropertyGetter<FpcMotor, Vector3>("DesiredMove");

        MethodInfo updatePos = AccessTools.Method(typeof(FpcMotor), nameof(FpcMotor.UpdatePosition), new[] { typeof(bool).MakeByRefType() });
        if (_getSpeed != null && _setLastMaxSpeed != null && _getViewMode != null &&
            _callUpdateThirdperson != null && _getDesiredMove != null && updatePos != null)
        {
            if (patchManager.PatchPrefix(Id, updatePos, AccessTools.Method(typeof(FpcMotorFastPathModule), nameof(UpdatePositionPrefix)), guard, config))
            {
                activeParts++;
                activeNames.Add("UpdatePosition");
            }
            else
            {
                skippedNames.Add("UpdatePosition");
            }
        }

        // 2. FpcMotor.Move
        _callGetFrameMove = CompileFuncCall<FpcMotor, Vector3>("GetFrameMove");
        _getCachedTransform = ReflectionHelpers.CreateInstanceFieldGetter<FpcMotor, Transform>("CachedTransform");
        _getOnBeforeMove = ReflectionHelpers.CreateInstanceFieldGetter<FpcMotor, Action<Vector3>>("OnBeforeMove");
        PropertyInfo velProp = AccessTools.Property(typeof(FpcMotor), nameof(FpcMotor.Velocity));
        if (velProp?.GetSetMethod(true) != null)
        {
            _setVelocity = (Action<FpcMotor, Vector3>)Delegate.CreateDelegate(
                typeof(Action<FpcMotor, Vector3>), velProp.GetSetMethod(true));
        }

        _setCachedPosition = ReflectionHelpers.CreateInstanceFieldSetter<FirstPersonMovementModule, Vector3>("_cachedPosition");
        _getMovementUpdateCycle = ReflectionHelpers.CreateStaticFieldGetter<bool>(typeof(FirstPersonMovementModule), "_movementUpdateCycle");

        MethodInfo moveMethod = AccessTools.Method(typeof(FpcMotor), nameof(FpcMotor.Move), Type.EmptyTypes);
        if (_callGetFrameMove != null && _getCachedTransform != null && _getOnBeforeMove != null &&
            _setVelocity != null && _setCachedPosition != null && _getMovementUpdateCycle != null &&
            moveMethod != null)
        {
            if (patchManager.PatchPrefix(Id, moveMethod, AccessTools.Method(typeof(FpcMotorFastPathModule), nameof(MovePrefix)), guard, config))
            {
                activeParts++;
                activeNames.Add("Move");
            }
            else
            {
                skippedNames.Add("Move");
            }
        }

        // 3. FpcMouseLook.UpdateRotation
        _getMlHub = ReflectionHelpers.CreateInstanceFieldGetter<FpcMouseLook, ReferenceHub>("_hub");
        _getMlFpmm = ReflectionHelpers.CreateInstanceFieldGetter<FpcMouseLook, FirstPersonMovementModule>("_fpmm");
        _getMlSyncH = ReflectionHelpers.CreateInstanceFieldGetter<FpcMouseLook, float>("_syncHorizontal");
        _getMlSyncV = ReflectionHelpers.CreateInstanceFieldGetter<FpcMouseLook, float>("_syncVertical");
        _getMlCurH = ReflectionHelpers.CreateInstanceFieldGetter<FpcMouseLook, float>("_curHorizontal");
        _getMlCurV = ReflectionHelpers.CreateInstanceFieldGetter<FpcMouseLook, float>("_curVertical");

        MethodInfo updateRot = AccessTools.Method(typeof(FpcMouseLook), nameof(FpcMouseLook.UpdateRotation), Type.EmptyTypes);
        if (_getMlHub != null && _getMlFpmm != null && _getMlSyncH != null && _getMlSyncV != null &&
            _getMlCurH != null && _getMlCurV != null && updateRot != null)
        {
            if (patchManager.PatchPrefix(Id, updateRot, AccessTools.Method(typeof(FpcMotorFastPathModule), nameof(UpdateRotationPrefix)), guard, config))
            {
                activeParts++;
                activeNames.Add("MouseLook");
            }
            else
            {
                skippedNames.Add("MouseLook");
            }
        }

        if (activeParts == 0)
        {
            patchManager.UnpatchModule(Id);
            return MarkIncompatible("No OPT-11 FpcMotor/FpcMouseLook targets could be patched.");
        }

        StatusDetail = skippedNames.Count == 0
            ? $"Active ({activeParts}/3: {string.Join(", ", activeNames)})"
            : $"Active (Coexist {activeParts}/3: {string.Join(", ", activeNames)}; preserved {string.Join(", ", skippedNames)} for external plugin)";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
    }

    private static bool UpdatePositionPrefix(FpcMotor __instance, out bool sendJump)
    {
        long t0 = MethodSampleTable.Begin();
        try
        {
            sendJump = false;
            float maxSpeed = Mathf.Abs(_getSpeed(__instance));
            _setLastMaxSpeed(__instance, maxSpeed);

            if (__instance.MainModule.Noclip.IsActive)
            {
                __instance.MoveDirection = Vector3.zero;
                return false;
            }

            // ViewMode == FpcViewMode.Thirdperson (2)
            if (_getViewMode(__instance) == 2)
            {
                _callUpdateThirdperson(__instance);
                return false;
            }

            // Evaluate DesiredMove BEFORE SphereCast so all stepOffset / movement lock / anticheat side effects remain identical!
            Vector3 desiredMove = _getDesiredMove(__instance);
            CharacterController cc = __instance.MainModule.CharController;

            if (desiredMove.x == 0f && desiredMove.y == 0f && desiredMove.z == 0f)
            {
                // ProjectOnPlane(Vector3.zero, anyNormal).normalized is always Vector3.zero regardless of hitInfo.normal!
                __instance.MoveDirection = new Vector3(0f, __instance.MoveDirection.y, 0f);
            }
            else
            {
                Vector3 hitNormal;
                BatchedNormalGetter batchedProvider = BatchedNormalProvider;
                if (batchedProvider == null || !batchedProvider(__instance, out hitNormal))
                {
                    UnityEngine.Physics.SphereCast(
                        __instance.Position,
                        cc.radius,
                        Vector3.down,
                        out RaycastHit hitInfo,
                        cc.height * 0.5f,
                        FpcStateProcessor.Mask,
                        QueryTriggerInteraction.Ignore);
                    hitNormal = hitInfo.normal;
                }

                Vector3 projected = Vector3.ProjectOnPlane(desiredMove, hitNormal).normalized;
                __instance.MoveDirection = new Vector3(projected.x * maxSpeed, __instance.MoveDirection.y, projected.z * maxSpeed);
            }

            if (cc.isGrounded)
            {
                __instance.UpdateGrounded(ref sendJump, __instance.MainModule.JumpSpeed);
            }
            else
            {
                __instance.UpdateFloating();
            }

            return false;
        }
        finally
        {
            MethodSampleTable.End(ProbeId.FpcMotorUpdate, t0);
        }
    }

    internal delegate bool BatchedNormalGetter(FpcMotor motor, out Vector3 normal);
    internal static BatchedNormalGetter BatchedNormalProvider;

    private static bool MovePrefix(FpcMotor __instance)
    {
        FirstPersonMovementModule mainMod = __instance.MainModule;
        CharacterController cc = mainMod.CharController;
        Vector3 prevPos = __instance.Position;
        Vector3 frameMove = _callGetFrameMove(__instance);

        _getOnBeforeMove(__instance)?.Invoke(frameMove);
        cc.Move(frameMove);

        // Read actual post-move transform position ONCE and update _cachedPosition directly
        // without triggering a redundant native Transform.set_position(samePos) round-trip!
        Vector3 newPos = _getCachedTransform(__instance).position;
        _setCachedPosition(mainMod, newPos);
        if (!_getMovementUpdateCycle() && mainMod.ModuleReady)
        {
            mainMod.CharacterModelInstance.OnPlayerMove();
        }

        __instance.MovementDetected = newPos != prevPos;
        _setVelocity(__instance, (newPos - prevPos) / Time.deltaTime);
        mainMod.IsGrounded = cc.isGrounded;
        return false;
    }

    private static bool UpdateRotationPrefix(FpcMouseLook __instance)
    {
        if (!NetworkServer.active)
        {
            return true;
        }

        ReferenceHub hub = _getMlHub(__instance);
        if (hub.isLocalPlayer)
        {
            return true;
        }

        if (!hub.IsDummy)
        {
            FirstPersonMovementModule fpmm = _getMlFpmm(__instance);
            float syncH = _getMlSyncH(__instance);
            byte wpId = fpmm.Motor.ReceivedPosition.WaypointId;
            if (wpId == 0)
            {
                __instance.CurrentHorizontal = syncH;
            }
            else
            {
                __instance.CurrentHorizontal = WaypointBase.GetWorldRotation(wpId, Quaternion.Euler(0f, syncH, 0f)).eulerAngles.y;
            }

            __instance.CurrentVertical = _getMlSyncV(__instance);
        }

        // On dedicated server, vanilla computes Quaternion.Lerp(cur, target, 1f) == target (ignoring native transform read!).
        float curH = _getMlCurH(__instance);
        float curV = _getMlCurV(__instance);
        hub.transform.rotation = Quaternion.Euler(0f, curH, 0f);
        hub.PlayerCameraReference.localRotation = Quaternion.Euler(-curV, 0f, 0f);
        return false;
    }

    private static Func<TTarget, TProp> CompilePropertyGetter<TTarget, TProp>(string propName)
    {
        PropertyInfo prop = AccessTools.Property(typeof(TTarget), propName);
        MethodInfo getter = prop?.GetGetMethod(true);
        if (getter == null)
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_{typeof(TTarget).Name}_get_{propName}",
            typeof(TProp),
            new[] { typeof(TTarget) },
            typeof(TTarget),
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(getter.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, getter);
        il.Emit(OpCodes.Ret);

        return (Func<TTarget, TProp>)dm.CreateDelegate(typeof(Func<TTarget, TProp>));
    }

    private static Func<TTarget, int> CompileEnumFieldAsIntGetter<TTarget>(string fieldName)
    {
        FieldInfo field = AccessTools.Field(typeof(TTarget), fieldName);
        if (field == null)
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_{typeof(TTarget).Name}_enum_{fieldName}",
            typeof(int),
            new[] { typeof(TTarget) },
            typeof(TTarget),
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);

        return (Func<TTarget, int>)dm.CreateDelegate(typeof(Func<TTarget, int>));
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

    private static Func<TTarget, TRet> CompileFuncCall<TTarget, TRet>(string methodName)
    {
        MethodInfo m = AccessTools.Method(typeof(TTarget), methodName, Type.EmptyTypes);
        if (m == null)
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_{typeof(TTarget).Name}_{methodName}",
            typeof(TRet),
            new[] { typeof(TTarget) },
            typeof(TTarget),
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(m.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, m);
        il.Emit(OpCodes.Ret);

        return (Func<TTarget, TRet>)dm.CreateDelegate(typeof(Func<TTarget, TRet>));
    }
}
