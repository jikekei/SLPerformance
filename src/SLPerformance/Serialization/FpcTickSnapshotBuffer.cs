using System;
using System.Collections.Generic;
using CentralAuth;
using MapGeneration;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using PlayerRoles.PlayableScps.Scp079;
using PlayerRoles.Visibility;
using PlayerRoles.Voice;
using RelativePositioning;
using UnityEngine;
using VoiceChat;

namespace SLPerformance.Serialization;

/// <summary>
/// Precomputed O(P) per-tick snapshot of all ReferenceHubs for FpcServerPositionDistributor.
/// Eliminates O(P^2) Unity Transform/CharacterController/MouseLook/Zone/Permission native calls.
/// </summary>
public struct FpcPlayerSnapshot
{
    public ReferenceHub Hub;
    public uint NetId;
    public int PlayerId;
    public bool IsLocalPlayer;
    public ClientInstanceMode Mode;
    public bool IsFpc;
    public FirstPersonMovementModule FpcModule;
    public PlayerMovementState MovementState;
    public bool IsGrounded;
    public RelativePosition RelativePosition;
    public ushort RotH;
    public ushort RotV;
    public Vector3 WorldPosition;
    public Vector3 ObserverCheckPosition;
    public RoleTypeId RoleId;
    public Team Team;
    public bool IsSpectator;
    public bool HasGameplayDataPerm;
    public bool IsCommunicatingGlobally;
    public bool IsSurfaceZone;
    public IObfuscatedRole ObfuscatedRole;
    public VisibilityController VisibilityController;
    public Dictionary<uint, RoleTypeId> PreviouslySentRole;
    public bool IsStandardFpcVisibility;
}

public sealed class FpcTickSnapshotBuffer
{
    public FpcPlayerSnapshot[] Items = new FpcPlayerSnapshot[64];
    public int Count;
    public bool Active;

    public void CaptureAll()
    {
        HashSet<ReferenceHub> allHubs = ReferenceHub.AllHubs;
        int needed = allHubs.Count;
        if (Items.Length < needed)
        {
            int newSize = Math.Max(needed + 16, Items.Length * 2);
            Items = new FpcPlayerSnapshot[newSize];
        }

        int idx = 0;
        foreach (ReferenceHub hub in allHubs)
        {
            if (hub == null)
            {
                continue;
            }

            PlayerRoleManager roleMgr = hub.roleManager;
            PlayerRoleBase currentRole = roleMgr.CurrentRole;
            RoleTypeId roleId = currentRole.RoleTypeId;
            Team team = currentRole.Team;

            FpcPlayerSnapshot snap = default;
            snap.Hub = hub;
            snap.NetId = hub.netId;
            snap.PlayerId = hub.PlayerId;
            snap.IsLocalPlayer = hub.isLocalPlayer;
            snap.Mode = hub.Mode;
            snap.RoleId = roleId;
            snap.Team = team;
            snap.IsSpectator = roleId == RoleTypeId.Spectator;
            snap.HasGameplayDataPerm = PermissionsHandler.IsPermitted(hub.serverRoles.Permissions, PlayerPermissions.GameplayData);
            snap.ObfuscatedRole = currentRole as IObfuscatedRole;
            snap.PreviouslySentRole = roleMgr.PreviouslySentRole;

            if (currentRole is ICustomVisibilityRole customVis)
            {
                snap.VisibilityController = customVis.VisibilityController;
                snap.IsStandardFpcVisibility = snap.VisibilityController != null &&
                                               snap.VisibilityController.GetType() == typeof(FpcVisibilityController);
            }

            if (currentRole is IFpcRole fpcRole)
            {
                FirstPersonMovementModule fpmm = fpcRole.FpcModule;
                snap.IsFpc = true;
                snap.FpcModule = fpmm;
                snap.MovementState = fpmm.SyncMovementState;
                snap.IsGrounded = fpmm.IsGrounded;
                RelativePosition relPos = fpmm.RelativePosition;
                snap.RelativePosition = relPos;
                fpmm.MouseLook.GetSyncValues(relPos.WaypointId, out snap.RotH, out snap.RotV);
                snap.WorldPosition = fpmm.Position;
                snap.ObserverCheckPosition = snap.WorldPosition;
            }
            else
            {
                snap.WorldPosition = hub.transform.position;
                snap.ObserverCheckPosition = currentRole is Scp079Role scp079
                    ? scp079.CameraPosition
                    : snap.WorldPosition;
            }

            snap.IsSurfaceZone = hub.GetCurrentZone() == FacilityZone.Surface;
            snap.IsCommunicatingGlobally = ComputeCommunicatingGlobally(hub, currentRole);

            Items[idx++] = snap;
        }

        Count = idx;
        Active = true;
    }

    public void EndTick()
    {
        Active = false;
        // Clear managed references to avoid retaining destroyed ReferenceHubs across round transitions.
        for (int i = 0; i < Count; i++)
        {
            Items[i].Hub = null;
            Items[i].FpcModule = null;
            Items[i].ObfuscatedRole = null;
            Items[i].VisibilityController = null;
            Items[i].PreviouslySentRole = null;
        }
        Count = 0;
    }

    public int FindIndex(ReferenceHub hub)
    {
        uint netId = hub.netId;
        FpcPlayerSnapshot[] items = Items;
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            if (items[i].NetId == netId && ReferenceEquals(items[i].Hub, hub))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool ComputeCommunicatingGlobally(ReferenceHub hub, PlayerRoleBase currentRole)
    {
        if (currentRole is not IVoiceRole voiceRole)
        {
            return false;
        }

        VoiceModuleBase vm = voiceRole.VoiceModule;
        if (!vm.ServerIsSending)
        {
            return false;
        }

        if (Intercom.CheckPerms(hub))
        {
            return true;
        }

        return vm.CurrentChannel == VoiceChatChannel.Radio;
    }
}
