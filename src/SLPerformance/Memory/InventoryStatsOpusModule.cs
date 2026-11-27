using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using InventorySystem;
using Mirror;
using PlayerRoles.Voice;
using PlayerStatsSystem;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using UnityEngine;
using VoiceChat;
using VoiceChat.Codec;

namespace SLPerformance.Memory;

/// <summary>
/// OPT-15: Low-risk memory & CPU fast-paths:
/// - AhpStat.ServerUpdateProcesses: skip ListPool rent/return when _activeProcesses is empty
/// - Inventory.UpdateObserverItems & RefreshModifiers: skip ListPool / dictionary loops when inventory is empty
/// - SyncedStatBase.Update: O(1) direct send to owner connection for SyncMode.Private instead of O(P) AllHubs scan
/// - VoiceModuleBase.SpawnObject / get_Decoder / ResetObject: lazy OpusDecoder allocation on dedicated server
/// </summary>
public sealed class InventoryStatsOpusModule : OptimizationModuleBase
{
    public override string Id => "memory.inventory_stats_opus";
    public override string Name => "Inventory, Stats & Lazy OpusDecoder Fast Paths";
    public override OptimizationTier Tier => OptimizationTier.Tier1;
    public override string Description => "Eliminates empty AhpStat/Inventory pool churn, replaces Private stat O(P) scans with O(1) send, and lazily allocates OpusDecoder on dedicated server.";

    // AhpStat
    private static Func<AhpStat, ICollection> _getActiveProcesses;

    // Inventory
    private static Action<Inventory, float> _setStaminaMod;
    private static Action<Inventory, float> _setMoveLimit;
    private static Action<Inventory, float> _setMoveMult;
    private static Action<Inventory, float> _setSprintMult;
    private static Action<Inventory, bool> _setSprintDisabled;

    // SyncedStatBase
    private static Func<SyncedStatBase, bool> _getValueDirty;
    private static Action<SyncedStatBase, bool> _setValueDirty;
    private static Func<SyncedStatBase, bool> _getMaxValueDirty;
    private static Action<SyncedStatBase, bool> _setMaxValueDirty;
    private static Action<SyncedStatBase, float> _setLastSent;

    // VoiceModuleBase
    private static Func<VoiceModuleBase, OpusDecoder> _getDefaultDecoder;
    private static Action<VoiceModuleBase, OpusDecoder> _setDefaultDecoder;
    private static Action<VoiceModuleBase, ReferenceHub> _setVoiceOwner;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.MemoryInventoryStatsOpus;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        int activeSubPatches = 0;

        // 1. AhpStat.ServerUpdateProcesses
        _getActiveProcesses = ReflectionHelpers.CreateInstanceFieldGetter<AhpStat, ICollection>("_activeProcesses");
        MethodInfo ahpUpdate = AccessTools.Method(typeof(AhpStat), nameof(AhpStat.ServerUpdateProcesses), new[] { typeof(bool), typeof(bool) });
        if (_getActiveProcesses != null && ahpUpdate != null &&
            patchManager.PatchPrefix(Id, ahpUpdate, AccessTools.Method(typeof(InventoryStatsOpusModule), nameof(AhpUpdatePrefix)), guard, config))
        {
            activeSubPatches++;
        }

        // 2. Inventory.UpdateObserverItems & RefreshModifiers
        MethodInfo updateObsItems = AccessTools.Method(typeof(Inventory), "UpdateObserverItems", Type.EmptyTypes);
        if (updateObsItems != null &&
            patchManager.PatchPrefix(Id, updateObsItems, AccessTools.Method(typeof(InventoryStatsOpusModule), nameof(UpdateObserverItemsPrefix)), guard, config))
        {
            activeSubPatches++;
        }

        _setStaminaMod = ReflectionHelpers.CreateInstanceFieldSetter<Inventory, float>("_staminaModifier");
        _setMoveLimit = ReflectionHelpers.CreateInstanceFieldSetter<Inventory, float>("_movementLimiter");
        _setMoveMult = ReflectionHelpers.CreateInstanceFieldSetter<Inventory, float>("_movementMultiplier");
        _setSprintMult = ReflectionHelpers.CreateInstanceFieldSetter<Inventory, float>("_sprintMultiplier");
        _setSprintDisabled = ReflectionHelpers.CreateInstanceFieldSetter<Inventory, bool>("_sprintingDisabled");
        MethodInfo refreshMods = AccessTools.Method(typeof(Inventory), "RefreshModifiers", Type.EmptyTypes);
        if (_setStaminaMod != null && _setMoveLimit != null && _setMoveMult != null && _setSprintMult != null && _setSprintDisabled != null &&
            refreshMods != null &&
            patchManager.PatchPrefix(Id, refreshMods, AccessTools.Method(typeof(InventoryStatsOpusModule), nameof(RefreshModifiersPrefix)), guard, config))
        {
            activeSubPatches++;
        }

        // 3. SyncedStatBase.Update
        _getValueDirty = ReflectionHelpers.CreateInstanceFieldGetter<SyncedStatBase, bool>("ValueDirty");
        _setValueDirty = ReflectionHelpers.CreateInstanceFieldSetter<SyncedStatBase, bool>("ValueDirty");
        _getMaxValueDirty = ReflectionHelpers.CreateInstanceFieldGetter<SyncedStatBase, bool>("MaxValueDirty");
        _setMaxValueDirty = ReflectionHelpers.CreateInstanceFieldSetter<SyncedStatBase, bool>("MaxValueDirty");
        _setLastSent = ReflectionHelpers.CreateInstanceFieldSetter<SyncedStatBase, float>("_lastSent");
        MethodInfo statUpdate = AccessTools.Method(typeof(SyncedStatBase), "Update", Type.EmptyTypes);
        if (_getValueDirty != null && _setValueDirty != null && _getMaxValueDirty != null && _setMaxValueDirty != null && _setLastSent != null &&
            statUpdate != null &&
            patchManager.PatchPrefix(Id, statUpdate, AccessTools.Method(typeof(InventoryStatsOpusModule), nameof(SyncedStatUpdatePrefix)), guard, config))
        {
            activeSubPatches++;
        }

        // 4. VoiceModuleBase lazy OpusDecoder on dedicated server
        _getDefaultDecoder = ReflectionHelpers.CreateInstanceFieldGetter<VoiceModuleBase, OpusDecoder>("_defaultDecoder");
        _setDefaultDecoder = ReflectionHelpers.CreateInstanceFieldSetter<VoiceModuleBase, OpusDecoder>("_defaultDecoder");
        _setVoiceOwner = ReflectionHelpers.CreateInstanceFieldSetter<VoiceModuleBase, ReferenceHub>("_owner");
        MethodInfo vmSpawn = AccessTools.Method(typeof(VoiceModuleBase), nameof(VoiceModuleBase.SpawnObject), Type.EmptyTypes);
        MethodInfo vmDecoder = AccessTools.PropertyGetter(typeof(VoiceModuleBase), "Decoder");
        MethodInfo vmReset = AccessTools.Method(typeof(VoiceModuleBase), nameof(VoiceModuleBase.ResetObject), Type.EmptyTypes);

        if (_getDefaultDecoder != null && _setDefaultDecoder != null && _setVoiceOwner != null &&
            vmSpawn != null && vmDecoder != null && vmReset != null)
        {
            bool okSpawn = patchManager.PatchPrefix(Id, vmSpawn, AccessTools.Method(typeof(InventoryStatsOpusModule), nameof(VoiceSpawnObjectPrefix)), guard, config);
            bool okDec = patchManager.PatchPrefix(Id, vmDecoder, AccessTools.Method(typeof(InventoryStatsOpusModule), nameof(VoiceGetDecoderPrefix)), guard, config);
            bool okReset = patchManager.PatchPostfix(Id, vmReset, AccessTools.Method(typeof(InventoryStatsOpusModule), nameof(VoiceResetObjectPostfix)), guard, config);
            if (okSpawn && okDec && okReset)
            {
                activeSubPatches++;
            }
        }

        if (activeSubPatches == 0)
        {
            patchManager.UnpatchModule(Id);
            return MarkIncompatible("No OPT-15 targets could be patched.");
        }

        StatusDetail = $"Active ({activeSubPatches}/5 fast paths active)";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
    }

    private static bool AhpUpdatePrefix(AhpStat __instance, bool removeInactive, bool updateCurValue)
    {
        ICollection active = _getActiveProcesses(__instance);
        if (active.Count != 0)
        {
            return true;
        }

        if (updateCurValue)
        {
            float clamped = Mathf.Clamp(0f, __instance.MinValue, __instance.MaxValue);
            if (__instance.CurValue != clamped)
            {
                __instance.CurValue = clamped;
            }
        }

        return false;
    }

    private static bool UpdateObserverItemsPrefix(Inventory __instance)
    {
        return __instance.UserInventory.Items.Count > 0;
    }

    private static bool RefreshModifiersPrefix(Inventory __instance)
    {
        if (__instance.UserInventory.Items.Count > 0)
        {
            return true;
        }

        _setStaminaMod(__instance, 1f);
        _setMoveLimit(__instance, float.MaxValue);
        _setMoveMult(__instance, 1f);
        _setSprintMult(__instance, 1f);
        _setSprintDisabled(__instance, false);

        if (NetworkServer.active)
        {
            __instance.Network_syncStaminaModifier = 1f;
            __instance.Network_syncMovementMultiplier = 1f;
            __instance.Network_syncMovementLimiter = float.MaxValue;
            __instance.Network_syncSprintMultiplier = 1f;
        }

        return false;
    }

    private static bool SyncedStatUpdatePrefix(SyncedStatBase __instance)
    {
        if (!NetworkServer.active)
        {
            return false;
        }

        bool valDirty = _getValueDirty(__instance);
        bool maxDirty = _getMaxValueDirty(__instance);
        if (!valDirty && !maxDirty)
        {
            return false;
        }

        if (__instance.Mode != SyncedStatBase.SyncMode.Private)
        {
            return true;
        }

        ReferenceHub owner = __instance.Hub;
        NetworkConnectionToClient conn = owner != null && !owner.isLocalPlayer ? owner.connectionToClient : null;

        if (valDirty)
        {
            float cur = __instance.CurValue;
            if (conn != null)
            {
                conn.Send(new SyncedStatMessages.StatMessage
                {
                    Stat = __instance,
                    Type = SyncedStatMessages.StatMessageType.CurrentValue,
                    SyncedValue = cur
                });
            }

            _setValueDirty(__instance, false);
            _setLastSent(__instance, cur);
        }

        if (maxDirty)
        {
            if (conn != null)
            {
                conn.Send(new SyncedStatMessages.StatMessage
                {
                    Stat = __instance,
                    Type = SyncedStatMessages.StatMessageType.MaxValue,
                    SyncedValue = __instance.MaxValue
                });
            }

            _setMaxValueDirty(__instance, false);
        }

        return false;
    }

    private static bool VoiceSpawnObjectPrefix(VoiceModuleBase __instance)
    {
        if (!ServerStatic.IsDedicated)
        {
            return true;
        }

        if (__instance.Role.TryGetOwner(out ReferenceHub owner))
        {
            _setVoiceOwner(__instance, owner);
            // Defer OpusDecoder native allocation until Decoder getter is actually accessed.
            _setDefaultDecoder(__instance, null);
            if (NetworkServer.active)
            {
                __instance.ReceiveFlags = VoiceChatReceivePrefs.GetFlagsForUser(owner);
            }
        }

        return false;
    }

    private static bool VoiceGetDecoderPrefix(VoiceModuleBase __instance, ref OpusDecoder __result)
    {
        OpusDecoder dec = _getDefaultDecoder(__instance);
        if (dec == null)
        {
            dec = new OpusDecoder();
            _setDefaultDecoder(__instance, dec);
        }

        __result = dec;
        return false;
    }

    private static void VoiceResetObjectPostfix(VoiceModuleBase __instance)
    {
        _setDefaultDecoder(__instance, null);
    }
}
