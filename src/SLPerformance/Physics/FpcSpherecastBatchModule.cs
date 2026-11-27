using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirror;
using PlayerRoles.FirstPersonControl;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace SLPerformance.Physics;

/// <summary>
/// OPT-16 (Tier 3, opt-in): Batches moving FPC players' ground SphereCasts using Unity's multithreaded
/// SpherecastCommand.ScheduleBatch() right before StaticUnityMethods.OnUpdate invokes FirstPersonMovementModule._activeUpdates.
/// Safety invariant:
/// - Runtime-verifies FpcStateProcessor.Mask does NOT include the "Player" layer (so batching before CharacterController.Move
///   is physically equivalent to sequential per-player SphereCasts).
/// - Requires OPT-11 (FpcMotorFastPathModule) to consume the batched hit normal; otherwise skips cleanly.
/// - Disposes all NativeArrays on RevertCore and Application.quitting to prevent native leaks.
/// </summary>
public sealed class FpcSpherecastBatchModule : OptimizationModuleBase
{
    public override string Id => "physics.fpc_spherecast_batch";
    public override string Name => "Batched Unity SpherecastCommand Ground Queries (Tier 3)";
    public override OptimizationTier Tier => OptimizationTier.Tier3;
    public override string Description => "Opt-in Unity Jobs SpherecastCommand.ScheduleBatch for moving players when Player-Player layer collisions are disabled.";

    private static NativeArray<SpherecastCommand> _commands;
    private static NativeArray<RaycastHit> _results;
    private static int _capacity;

    private static readonly Dictionary<FpcMotor, int> MotorToBatchIndex = new Dictionary<FpcMotor, int>(64);
    private static int _batchFrame = -1;
    private static int _batchCount;
    private static int _minPlayers = 16;
    private static bool _active;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.PhysicsFpcSpherecastBatch;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        if (!config.PhysicsFpcMotorFastPath)
        {
            return MarkIncompatible("Requires OPT-11 (physics.fpc_motor_fastpath) to be enabled.");
        }

        int playerLayer = LayerMask.NameToLayer("Player");
        int mask = FpcStateProcessor.Mask;
        if (playerLayer >= 0 && (mask & (1 << playerLayer)) != 0)
        {
            return MarkIncompatible("FpcStateProcessor.Mask includes Player layer; batched pre-move SphereCast is unsafe and disabled.");
        }

        MethodInfo staticUpdate = AccessTools.Method(typeof(StaticUnityMethods), "Update", Type.EmptyTypes);
        if (staticUpdate == null)
        {
            return MarkIncompatible("StaticUnityMethods.Update not found.");
        }

        if (!patchManager.PatchPrefix(Id, staticUpdate, AccessTools.Method(typeof(FpcSpherecastBatchModule), nameof(BeforeStaticUpdatePrefix)), guard, config))
        {
            return MarkConflict("External Harmony conflict on StaticUnityMethods.Update.");
        }

        _minPlayers = Math.Max(4, config.SpherecastBatchMinPlayers);
        EnsureNativeCapacity(64);
        _active = true;
        FpcMotorFastPathModule.BatchedNormalProvider = TryConsumeBatchedNormal;
        Application.quitting += DisposeNativeBuffers;

        StatusDetail = $"Active (minPlayers={_minPlayers}, playerLayer={playerLayer}, mask=0x{mask:X})";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        _active = false;
        Application.quitting -= DisposeNativeBuffers;
        if (FpcMotorFastPathModule.BatchedNormalProvider == (FpcMotorFastPathModule.BatchedNormalGetter)TryConsumeBatchedNormal)
        {
            FpcMotorFastPathModule.BatchedNormalProvider = null;
        }

        MotorToBatchIndex.Clear();
        _batchCount = 0;
        _batchFrame = -1;
        DisposeNativeBuffers();
    }

    private static void BeforeStaticUpdatePrefix()
    {
        if (!_active || !NetworkServer.active || !StaticUnityMethods.IsPlaying)
        {
            return;
        }

        HashSet<ReferenceHub> hubs = ReferenceHub.AllHubs;
        if (hubs.Count < _minPlayers)
        {
            _batchCount = 0;
            return;
        }

        MotorToBatchIndex.Clear();
        int mask = FpcStateProcessor.Mask;
        QueryParameters qp = new QueryParameters(mask, hitMultipleFaces: false, QueryTriggerInteraction.Ignore, hitBackfaces: false);

        int count = 0;
        foreach (ReferenceHub hub in hubs)
        {
            if (hub == null || hub.isLocalPlayer)
            {
                continue;
            }

            if (hub.roleManager.CurrentRole is not IFpcRole fpcRole)
            {
                continue;
            }

            FirstPersonMovementModule mod = fpcRole.FpcModule;
            if (mod == null || !mod.ModuleReady || !mod.CharControllerSet || mod.Noclip.IsActive)
            {
                continue;
            }

            FpcMotor motor = mod.Motor;
            if (motor == null)
            {
                continue;
            }

            // Quick moving heuristic: only enqueue players whose ReceivedPosition differs horizontally from Position.
            Vector3 curPos = motor.Position;
            Vector3 targetPos = motor.ReceivedPosition.Position;
            float dx = targetPos.x - curPos.x;
            float dz = targetPos.z - curPos.z;
            if (dx * dx + dz * dz < 0.0009f) // 0.03f^2 == MinMoveDiff^2
            {
                continue;
            }

            CharacterController cc = mod.CharController;
            if (cc == null)
            {
                continue;
            }

            if (count >= _capacity)
            {
                EnsureNativeCapacity(Math.Max(count + 16, _capacity * 2));
            }

            _commands[count] = new SpherecastCommand(curPos, cc.radius, Vector3.down, qp, cc.height * 0.5f);
            MotorToBatchIndex[motor] = count;
            count++;
        }

        _batchCount = count;
        if (count == 0)
        {
            return;
        }

        NativeArray<SpherecastCommand> cmdSlice = _commands.GetSubArray(0, count);
        NativeArray<RaycastHit> resSlice = _results.GetSubArray(0, count);
        int minPerJob = Math.Max(1, count / 4);
        JobHandle handle = SpherecastCommand.ScheduleBatch(cmdSlice, resSlice, minPerJob, maxHits: 1);
        handle.Complete();
        _batchFrame = Time.frameCount;
    }

    private static bool TryConsumeBatchedNormal(FpcMotor motor, out Vector3 normal)
    {
        if (!_active || _batchCount == 0 || _batchFrame != Time.frameCount)
        {
            normal = Vector3.zero;
            return false;
        }

        if (MotorToBatchIndex.TryGetValue(motor, out int idx) && idx < _batchCount)
        {
            normal = _results[idx].normal;
            return true;
        }

        normal = Vector3.zero;
        return false;
    }

    private static void EnsureNativeCapacity(int needed)
    {
        if (_capacity >= needed && _commands.IsCreated && _results.IsCreated)
        {
            return;
        }

        DisposeNativeBuffers();
        _commands = new NativeArray<SpherecastCommand>(needed, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _results = new NativeArray<RaycastHit>(needed, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        _capacity = needed;
    }

    private static void DisposeNativeBuffers()
    {
        if (_commands.IsCreated)
        {
            _commands.Dispose();
        }

        if (_results.IsCreated)
        {
            _results.Dispose();
        }

        _capacity = 0;
    }
}
