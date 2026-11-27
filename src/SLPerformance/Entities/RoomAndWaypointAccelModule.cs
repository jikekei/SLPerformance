using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Interactables.Interobjects;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Handlers;
using MapGeneration;
using MapGeneration.StaticHelpers;
using PlayerRoles;
using RelativePositioning;
using RoundRestarting;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using SLPerformance.Profiler;
using UnityEngine;

namespace SLPerformance.Entities;

/// <summary>
/// OPT-13: Spatial lookup acceleration:
/// - CurrentRoomPlayerCache.ValidateCache: short-circuits dead/spectator players before reading CameraPosition/Transform
/// - RoomUtils.TryGetRoom: bounded secondary grid cache for static multi-cell rooms (never caches ElevatorChamber cells)
/// - WaypointBase.ExtractWaypointData: replaces 254-slot sparse scan with compact active-only waypoint array in exact slot order
/// </summary>
public sealed class RoomAndWaypointAccelModule : OptimizationModuleBase
{
    public override string Id => "entities.room_and_waypoint_accel";
    public override string Name => "Room Cache & Compact Waypoint Scanner";
    public override OptimizationTier Tier => OptimizationTier.Tier2;
    public override string Description => "Skips dead-player room raycasts, caches static multi-cell room coords (bounded), and compacts WaypointBase 254-slot scan.";

    // CurrentRoomPlayerCache private fields
    private static Func<CurrentRoomPlayerCache, PlayerRoleManager> _getRoleMgr;
    private static Func<CurrentRoomPlayerCache, int> _getLastFrame;
    private static Action<CurrentRoomPlayerCache, int> _setLastFrame;
    private static Func<CurrentRoomPlayerCache, RoomIdentifier> _getLastDetected;
    private static Action<CurrentRoomPlayerCache, RoomIdentifier> _setLastDetected;
    private static Action<CurrentRoomPlayerCache, bool> _setLastValid;
    private static Action<CurrentRoomPlayerCache, bool> _setHasAnyValid;
    private static Func<CurrentRoomPlayerCache, Transform> _getTr;

    // RoomUtils static secondary cache
    private static readonly Dictionary<Vector3Int, RoomIdentifier> StaticSecondaryRoomCache = new Dictionary<Vector3Int, RoomIdentifier>(256);
    private static Func<CachedLayerMask> _getRoomDetectionMask;
    private static int _maxCachedCoords = 16384;

    // RoomLightController O(1) zero-darkened-room fast path
    private static readonly HashSet<RoomLightController> DarkenedControllers = new HashSet<RoomLightController>();
    private static bool _darkenedTrackingActive;

    // WaypointBase classified compact active array (exact ascending slot order)
    private const byte WaypointKindNetId = 0;
    private const byte WaypointKindLocker = 1;
    private const byte WaypointKindElevator = 2;
    private const byte WaypointKindOther = 3;

    private struct ActiveWaypointEntry
    {
        public WaypointBase Waypoint;
        public LockerWaypoint LockerWp;
        public ElevatorChamber ElevatorChamber;
        public float PosX;
        public float PosY;
        public float PosZ;
        public byte Id;
        public byte Kind;
    }

    private static readonly ActiveWaypointEntry[] ActiveWaypoints = new ActiveWaypointEntry[255];
    private static int _activeWaypointCount;
    private static Func<bool[]> _getSetWaypoints;
    private static Func<WaypointBase[]> _getAllWaypoints;
    private static Func<WaypointBase, byte> _getWaypointId;
    private static Func<NetIdWaypoint, Vector3> _getNetIdPos;
    private static Func<LockerWaypoint, bool> _getLockerMoving;
    private static Func<ElevatorWaypoint, ElevatorChamber> _getElevatorChamber;
    private static Func<WaypointBase, Vector3, float> _callSqrDistanceTo;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.EntitiesRoomAndWaypointAccel;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        _maxCachedCoords = Math.Max(256, config.MaxCachedStaticRoomCoords);
        int activeParts = 0;

        // 1. CurrentRoomPlayerCache.ValidateCache
        _getRoleMgr = ReflectionHelpers.CreateInstanceFieldGetter<CurrentRoomPlayerCache, PlayerRoleManager>("_roleManager");
        _getLastFrame = ReflectionHelpers.CreateInstanceFieldGetter<CurrentRoomPlayerCache, int>("_lastFrame");
        _setLastFrame = ReflectionHelpers.CreateInstanceFieldSetter<CurrentRoomPlayerCache, int>("_lastFrame");
        _getLastDetected = ReflectionHelpers.CreateInstanceFieldGetter<CurrentRoomPlayerCache, RoomIdentifier>("_lastDetected");
        _setLastDetected = ReflectionHelpers.CreateInstanceFieldSetter<CurrentRoomPlayerCache, RoomIdentifier>("_lastDetected");
        _setLastValid = ReflectionHelpers.CreateInstanceFieldSetter<CurrentRoomPlayerCache, bool>("_lastValid");
        _setHasAnyValid = ReflectionHelpers.CreateInstanceFieldSetter<CurrentRoomPlayerCache, bool>("_hasAnyValidRoom");
        _getTr = ReflectionHelpers.CreateInstanceFieldGetter<CurrentRoomPlayerCache, Transform>("_tr");

        MethodInfo validateCache = AccessTools.Method(typeof(CurrentRoomPlayerCache), "ValidateCache", Type.EmptyTypes);
        if (_getRoleMgr != null && _getLastFrame != null && _setLastFrame != null && _getLastDetected != null &&
            _setLastDetected != null && _setLastValid != null && _setHasAnyValid != null && _getTr != null &&
            validateCache != null &&
            patchManager.PatchPrefix(Id, validateCache, AccessTools.Method(typeof(RoomAndWaypointAccelModule), nameof(ValidateCachePrefix)), guard, config))
        {
            activeParts++;
        }

        // 2. RoomUtils.TryGetRoom bounded static secondary cache
        _getRoomDetectionMask = ReflectionHelpers.CreateStaticFieldGetter<CachedLayerMask>(typeof(RoomUtils), "RoomDetectionMask");
        MethodInfo tryGetRoom = AccessTools.Method(typeof(RoomUtils), nameof(RoomUtils.TryGetRoom), new[] { typeof(Vector3), typeof(RoomIdentifier).MakeByRefType() });
        if (_getRoomDetectionMask != null && tryGetRoom != null &&
            patchManager.PatchPrefix(Id, tryGetRoom, AccessTools.Method(typeof(RoomAndWaypointAccelModule), nameof(TryGetRoomPrefix)), guard, config))
        {
            RoomIdentifier.OnRemoved += OnRoomRemoved;
            SeedSynchronizer.OnGenerationStage += OnMapGenStage;
            activeParts++;
        }

        // 3. WaypointBase classified compact active array (inline NetIdWaypoint floats + zero-cost stationary LockerWaypoint skip)
        _getSetWaypoints = ReflectionHelpers.CreateStaticFieldGetter<bool[]>(typeof(WaypointBase), "SetWaypoints");
        _getAllWaypoints = ReflectionHelpers.CreateStaticFieldGetter<WaypointBase[]>(typeof(WaypointBase), "AllWaypoints");
        _getWaypointId = ReflectionHelpers.CreateInstanceFieldGetter<WaypointBase, byte>("_id");
        _getNetIdPos = ReflectionHelpers.CreateInstanceFieldGetter<NetIdWaypoint, Vector3>("_pos");
        _getLockerMoving = ReflectionHelpers.CreateInstanceFieldGetter<LockerWaypoint, bool>("_moving");
        _getElevatorChamber = ReflectionHelpers.CreateInstanceFieldGetter<ElevatorWaypoint, ElevatorChamber>("_elevator");
        _callSqrDistanceTo = CompileSqrDistanceToCaller();

        MethodInfo assignMethod = AccessTools.Method(typeof(WaypointBase), "AssignSelfToList", Type.EmptyTypes);
        MethodInfo removeMethod = AccessTools.Method(typeof(WaypointBase), "RemoveSelfFromList", Type.EmptyTypes);
        MethodInfo setIdMethod = AccessTools.Method(typeof(WaypointBase), "SetId", new[] { typeof(byte) });
        MethodInfo netSetPosMethod = AccessTools.Method(typeof(NetIdWaypoint), "SetPosition", Type.EmptyTypes);
        MethodInfo extractMethod = AccessTools.Method(typeof(WaypointBase), "ExtractWaypointData", new[]
        {
            typeof(Vector3), typeof(bool), typeof(Quaternion), typeof(bool),
            typeof(byte).MakeByRefType(), typeof(Vector3).MakeByRefType(), typeof(Quaternion).MakeByRefType()
        });

        if (_getSetWaypoints != null && _getAllWaypoints != null && _getWaypointId != null && _callSqrDistanceTo != null &&
            assignMethod != null && removeMethod != null && setIdMethod != null && extractMethod != null)
        {
            bool okA = patchManager.PatchPostfix(Id, assignMethod, AccessTools.Method(typeof(RoomAndWaypointAccelModule), nameof(RebuildActiveWaypointsPostfix)), guard, config);
            bool okR = patchManager.PatchPostfix(Id, removeMethod, AccessTools.Method(typeof(RoomAndWaypointAccelModule), nameof(RebuildActiveWaypointsPostfix)), guard, config);
            bool okS = patchManager.PatchPostfix(Id, setIdMethod, AccessTools.Method(typeof(RoomAndWaypointAccelModule), nameof(RebuildActiveWaypointsPostfix)), guard, config);
            if (netSetPosMethod != null)
            {
                patchManager.PatchPostfix(Id, netSetPosMethod, AccessTools.Method(typeof(RoomAndWaypointAccelModule), nameof(RebuildActiveWaypointsPostfix)), guard, config);
            }
            bool okE = patchManager.PatchPrefix(Id, extractMethod, AccessTools.Method(typeof(RoomAndWaypointAccelModule), nameof(ExtractWaypointDataPrefix)), guard, config);
            if (okA && okR && okS && okE)
            {
                RebuildActiveWaypoints();
                activeParts++;
            }
        }

        // 4. RoomLightController O(1) zero-darkened-room fast path (accelerates 49x InsufficientLighting + darkness checks)
        MethodInfo setNetLights = AccessTools.PropertySetter(typeof(RoomLightController), nameof(RoomLightController.NetworkLightsEnabled));
        MethodInfo rlcDestroy = AccessTools.Method(typeof(RoomLightController), "OnDestroy", Type.EmptyTypes);
        MethodInfo isDarkVec = AccessTools.Method(typeof(RoomLightController), nameof(RoomLightController.IsInDarkenedRoom), new[] { typeof(Vector3) });
        MethodInfo isDarkRoom = AccessTools.Method(typeof(RoomLightController), nameof(RoomLightController.IsInDarkenedRoom), new[] { typeof(RoomIdentifier), typeof(Vector3) });
        if (setNetLights != null && rlcDestroy != null && isDarkVec != null && isDarkRoom != null)
        {
            bool ok1 = patchManager.PatchPostfix(Id, setNetLights, AccessTools.Method(typeof(RoomAndWaypointAccelModule), nameof(RoomLightsChangedPostfix)), guard, config);
            bool ok2 = patchManager.PatchPostfix(Id, rlcDestroy, AccessTools.Method(typeof(RoomAndWaypointAccelModule), nameof(RoomLightsDestroyPostfix)), guard, config);
            bool ok3 = patchManager.PatchPrefix(Id, isDarkVec, AccessTools.Method(typeof(RoomAndWaypointAccelModule), nameof(IsInDarkenedRoomFastPrefix)), guard, config);
            bool ok4 = patchManager.PatchPrefix(Id, isDarkRoom, AccessTools.Method(typeof(RoomAndWaypointAccelModule), nameof(IsInDarkenedRoomFastPrefix)), guard, config);
            if (ok1 && ok2 && ok3 && ok4)
            {
                DarkenedControllers.Clear();
                foreach (RoomLightController rlc in RoomLightController.Instances)
                {
                    if (rlc != null && !rlc.LightsEnabled)
                    {
                        DarkenedControllers.Add(rlc);
                    }
                }

                _darkenedTrackingActive = true;
                activeParts++;
            }
        }

        if (activeParts == 0)
        {
            patchManager.UnpatchModule(Id);
            return MarkIncompatible("No OPT-13 targets could be patched.");
        }

        StatusDetail = $"Active ({activeParts}/4 spatial & light caches, waypoints={_activeWaypointCount}, maxRoomCells={_maxCachedCoords})";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        _darkenedTrackingActive = false;
        DarkenedControllers.Clear();
        RoomIdentifier.OnRemoved -= OnRoomRemoved;
        SeedSynchronizer.OnGenerationStage -= OnMapGenStage;
        StaticSecondaryRoomCache.Clear();
        Array.Clear(ActiveWaypoints, 0, ActiveWaypoints.Length);
        _activeWaypointCount = 0;
    }

    private static void RoomLightsChangedPostfix(RoomLightController __instance, bool value)
    {
        if (!_darkenedTrackingActive || __instance == null)
        {
            return;
        }

        if (!value)
        {
            DarkenedControllers.Add(__instance);
        }
        else
        {
            DarkenedControllers.Remove(__instance);
        }
    }

    private static void RoomLightsDestroyPostfix(RoomLightController __instance)
    {
        if (__instance != null)
        {
            DarkenedControllers.Remove(__instance);
        }
    }

    private static bool IsInDarkenedRoomFastPrefix(ref bool __result)
    {
        if (_darkenedTrackingActive && DarkenedControllers.Count == 0)
        {
            __result = false;
            return false;
        }

        return true;
    }

    private static void OnRoomRemoved(RoomIdentifier _)
    {
        StaticSecondaryRoomCache.Clear();
    }

    private static void OnMapGenStage(MapGenerationPhase phase)
    {
        if (phase == MapGenerationPhase.RoomCoordsRegistrations)
        {
            StaticSecondaryRoomCache.Clear();
        }
    }

    private static bool ValidateCachePrefix(CurrentRoomPlayerCache __instance)
    {
        int frame = Time.frameCount;
        if (_getLastFrame(__instance) == frame || RoundRestart.IsRoundRestarting)
        {
            return false;
        }

        PlayerRoleManager roleMgr = _getRoleMgr(__instance);
        ReferenceHub hub = roleMgr.Hub;
        RoomIdentifier lastDetected = _getLastDetected(__instance);
        RoomIdentifier current;

        if (!hub.IsAlive())
        {
            _setLastValid(__instance, false);
            _setLastDetected(__instance, null);
            _setHasAnyValid(__instance, false);
            current = null;
        }
        else
        {
            Vector3 worldPos = roleMgr.CurrentRole is ICameraController camCtrl
                ? camCtrl.CameraPosition
                : _getTr(__instance).position;

            if (worldPos.TryGetRoom(out RoomIdentifier room))
            {
                _setLastDetected(__instance, room);
                _setLastValid(__instance, true);
                _setHasAnyValid(__instance, true);
                current = room;
            }
            else
            {
                _setLastValid(__instance, false);
                _setLastDetected(__instance, null);
                _setHasAnyValid(__instance, false);
                current = null;
            }
        }

        if (lastDetected != current)
        {
            PlayerEvents.OnRoomChanged(new PlayerRoomChangedEventArgs(hub, lastDetected, current));
            if (lastDetected != null && current != null && lastDetected.Zone != current.Zone)
            {
                PlayerEvents.OnZoneChanged(new PlayerZoneChangedEventArgs(hub, lastDetected.Zone, current.Zone));
            }
        }

        _setLastFrame(__instance, frame);
        return false;
    }

    private static bool TryGetRoomPrefix(Vector3 worldPos, out RoomIdentifier room, ref bool __result)
    {
        long t0 = MethodSampleTable.Begin();
        try
        {
            Vector3Int key = RoomUtils.PositionToCoords(worldPos);
            if (RoomIdentifier.RoomsByCoords.TryGetValue(key, out room))
            {
                __result = true;
                return false;
            }

            if (StaticSecondaryRoomCache.TryGetValue(key, out RoomIdentifier cached))
            {
                if (cached != null)
                {
                    room = cached;
                    __result = true;
                    return false;
                }

                StaticSecondaryRoomCache.Remove(key);
            }

            if (TryRaycastRoomClassified(worldPos, Vector3.up, out room, out bool isStaticRoom))
            {
                if (isStaticRoom && SeedSynchronizer.MapGenerated && StaticSecondaryRoomCache.Count < _maxCachedCoords)
                {
                    StaticSecondaryRoomCache[key] = room;
                }

                __result = true;
                return false;
            }

            if (TryRaycastRoomClassified(worldPos, Vector3.down, out room, out isStaticRoom))
            {
                if (isStaticRoom && SeedSynchronizer.MapGenerated && StaticSecondaryRoomCache.Count < _maxCachedCoords)
                {
                    StaticSecondaryRoomCache[key] = room;
                }

                __result = true;
                return false;
            }

            room = null;
            __result = false;
            return false;
        }
        finally
        {
            MethodSampleTable.End(ProbeId.RoomLookup, t0);
        }
    }

    private static bool TryRaycastRoomClassified(Vector3 pos, Vector3 dir, out RoomIdentifier room, out bool isStaticRoom)
    {
        isStaticRoom = false;
        if (UnityEngine.Physics.Raycast(new Ray(pos, dir), out RaycastHit hitInfo, 15f, _getRoomDetectionMask()) && hitInfo.collider != null)
        {
            Transform tr = hitInfo.collider.transform;
            if (tr.TryGetComponentInParent<IRoomObject>(out IRoomObject comp))
            {
                room = comp.OriginalRoom;
                if (room != null)
                {
                    // Only cache if not inside an elevator shaft/chamber.
                    isStaticRoom = tr.GetComponentInParent<ElevatorChamber>() == null;
                    return true;
                }

                return false;
            }

            if (tr.TryGetComponentInParent<RoomIdentifier>(out room))
            {
                if (room != null)
                {
                    isStaticRoom = tr.GetComponentInParent<ElevatorChamber>() == null;
                    return true;
                }

                return false;
            }

            if (tr.TryGetComponentInParent<ElevatorChamber>(out ElevatorChamber chamber))
            {
                room = chamber.CurrentRoom;
                isStaticRoom = false; // Elevators move across rooms; never cache!
                return room != null;
            }
        }

        room = null;
        return false;
    }

    private static void RebuildActiveWaypointsPostfix()
    {
        RebuildActiveWaypoints();
    }

    private static void RebuildActiveWaypoints()
    {
        bool[] set = _getSetWaypoints();
        WaypointBase[] all = _getAllWaypoints();
        int count = 0;

        for (byte b = 1; b < byte.MaxValue; b++)
        {
            if (set[b])
            {
                WaypointBase wp = all[b];
                if (wp != null)
                {
                    ActiveWaypointEntry entry = new ActiveWaypointEntry
                    {
                        Waypoint = wp,
                        Id = _getWaypointId(wp),
                        Kind = WaypointKindOther
                    };

                    if (wp is NetIdWaypoint netWp && _getNetIdPos != null)
                    {
                        Vector3 p = _getNetIdPos(netWp);
                        entry.PosX = p.x;
                        entry.PosY = p.y;
                        entry.PosZ = p.z;
                        entry.Kind = WaypointKindNetId;
                    }
                    else if (wp is LockerWaypoint lockerWp && _getLockerMoving != null)
                    {
                        entry.LockerWp = lockerWp;
                        entry.Kind = WaypointKindLocker;
                    }
                    else if (wp is ElevatorWaypoint elevWp && _getElevatorChamber != null)
                    {
                        ElevatorChamber chamber = _getElevatorChamber(elevWp);
                        if (chamber != null)
                        {
                            entry.ElevatorChamber = chamber;
                            entry.Kind = WaypointKindElevator;
                        }
                    }

                    ActiveWaypoints[count++] = entry;
                }
            }
        }

        for (int i = count; i < _activeWaypointCount; i++)
        {
            ActiveWaypoints[i] = default;
        }

        _activeWaypointCount = count;
    }

    private static bool ExtractWaypointDataPrefix(
        Vector3 worldPoint,
        bool extractPoint,
        Quaternion worldRot,
        bool extractRot,
        out byte closestId,
        out Vector3 relPoint,
        out Quaternion relRot)
    {
        long t0 = MethodSampleTable.Begin();
        try
        {
            float bestDist = float.MaxValue;
            relPoint = Vector3.zero;
            relRot = Quaternion.identity;
            closestId = 0;
            WaypointBase best = null;

            int count = _activeWaypointCount;
            ActiveWaypointEntry[] entries = ActiveWaypoints;
            Func<WaypointBase, Vector3, float> sqrDist = _callSqrDistanceTo;
            float wx = worldPoint.x;
            float wy = worldPoint.y;
            float wz = worldPoint.z;

            // Exact ascending slot-order iteration preserves vanilla !(d > bestDist) tie-breaking.
            for (int i = 0; i < count; i++)
            {
                ref readonly ActiveWaypointEntry e = ref entries[i];
                float d;
                switch (e.Kind)
                {
                    case WaypointKindNetId:
                    {
                        float dx = wx - e.PosX;
                        float dy = wy - e.PosY;
                        float dz = wz - e.PosZ;
                        d = dx * dx + dy * dy + dz * dz;
                        break;
                    }
                    case WaypointKindLocker:
                    {
                        if (!_getLockerMoving(e.LockerWp))
                        {
                            d = float.MaxValue;
                        }
                        else
                        {
                            d = sqrDist(e.Waypoint, worldPoint);
                        }
                        break;
                    }
                    case WaypointKindElevator:
                    {
                        d = e.ElevatorChamber.WorldspaceBounds.Contains(worldPoint) ? -1f : float.MaxValue;
                        break;
                    }
                    default:
                    {
                        d = sqrDist(e.Waypoint, worldPoint);
                        break;
                    }
                }

                if (!(d > bestDist))
                {
                    bestDist = d;
                    best = e.Waypoint;
                    closestId = e.Id;
                }
            }

            if (closestId != 0 && best != null)
            {
                if (extractPoint)
                {
                    relPoint = best.GetRelativePosition(worldPoint);
                }

                if (extractRot)
                {
                    relRot = best.GetRelativeRotation(worldRot);
                }
            }

            return false;
        }
        finally
        {
            MethodSampleTable.End(ProbeId.WaypointLookup, t0);
        }
    }

    private static Func<WaypointBase, Vector3, float> CompileSqrDistanceToCaller()
    {
        MethodInfo m = AccessTools.Method(typeof(WaypointBase), "SqrDistanceTo", new[] { typeof(Vector3) });
        if (m == null)
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            "SLPerf_Waypoint_SqrDistanceTo",
            typeof(float),
            new[] { typeof(WaypointBase), typeof(Vector3) },
            typeof(WaypointBase),
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, m);
        il.Emit(OpCodes.Ret);

        return (Func<WaypointBase, Vector3, float>)dm.CreateDelegate(typeof(Func<WaypointBase, Vector3, float>));
    }
}
