using System;
using System.Collections.Generic;
using System.Reflection;
using Footprinting;
using HarmonyLib;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using MapGeneration.Distributors;
using Mirror;
using SLPerformance.Compatibility;
using SLPerformance.Core;
using SLPerformance.Patching;
using UnityEngine;

namespace SLPerformance.Entities;

/// <summary>
/// OPT-19: Sleeps idle stationary BasicDoor (BreakableDoor/ElevatorDoor/PryableDoor) and LockerChamber Animators.
/// Live audit showed ~330 AlwaysAnimate Animators active even at 0/48 players (~1.04ms/frame in DirectorUpdateAnimation),
/// ~290 of which are completely stationary doors and lockers.
/// Each Animator wakes immediately before any state change, pry, damage, repair, or locker interaction,
/// and sleeps only after its transition has fully settled with unchanged exact state.
/// </summary>
public sealed class IdleWorldAnimatorSleepModule : OptimizationModuleBase
{
    public override string Id => "entities.idle_world_animator_sleep";
    public override string Name => "Idle Door & Locker Animator Sleep";
    public override OptimizationTier Tier => OptimizationTier.Tier2;
    public override string Description => "Disables Animator.enabled on settled stationary BasicDoor and LockerChamber instances, waking them instantly on any state change or interaction.";

    private struct DoorWatchEntry
    {
        public BasicDoor Door;
        public float SettleTime;
        public float LastExactState;
        public bool HasSampled;
    }

    private struct LockerWatchEntry
    {
        public LockerChamber Chamber;
        public Animator Anim;
        public float SettleTime;
    }

    private struct TimedAnimatorEntry
    {
        public Animator Anim;
        public ElevatorChamber Elevator;
        public float SettleTime;
    }

    private static Func<BasicDoor, Animator> _getDoorAnimator;
    private static Func<BasicDoor, bool> _getDoorNeedsRefresh;
    private static Func<BasicDoor, float> _getDoorCooldown;
    private static Func<BasicDoor, float> _getDoorCooldownDur;
    private static Func<PryableDoor, float> _getPryDuration;
    private static Func<LockerChamber, Animator> _getLockerAnimator;
    private static Func<LockerChamber, float> _getLockerAnimTime;
    private static Func<ElevatorChamber, Animator> _getElevatorDoorAnim;
    private static Func<Scp079Generator, Animator> _getGenDoorAnim;
    private static Func<Scp079Generator, Animator> _getGenLeverAnim;

    private static readonly List<DoorWatchEntry> ActiveDoors = new List<DoorWatchEntry>(256);
    private static readonly Dictionary<BasicDoor, int> DoorIndex = new Dictionary<BasicDoor, int>(256);
    private static readonly List<LockerWatchEntry> ActiveLockers = new List<LockerWatchEntry>(128);
    private static readonly Dictionary<LockerChamber, int> LockerIndex = new Dictionary<LockerChamber, int>(128);
    private static readonly List<TimedAnimatorEntry> ActiveTimedAnims = new List<TimedAnimatorEntry>(64);
    private static readonly Dictionary<Animator, int> TimedAnimIndex = new Dictionary<Animator, int>(64);
    private static readonly HashSet<Animator> SleptAnimators = new HashSet<Animator>();

    private static float _nextCheckTime;
    private static bool _active;

    public override bool IsEnabledInConfig(SLPerformanceConfig config) => config.EntitiesIdleWorldAnimatorSleep;

    protected override bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        _getDoorAnimator = ReflectionHelpers.CreateInstanceFieldGetter<BasicDoor, Animator>("MainAnimator");
        _getDoorNeedsRefresh = ReflectionHelpers.CreateInstanceFieldGetter<BasicDoor, bool>("_animationNeedsRefreshing");
        _getDoorCooldown = ReflectionHelpers.CreateInstanceFieldGetter<BasicDoor, float>("_remainingAnimCooldown");
        _getDoorCooldownDur = ReflectionHelpers.CreateInstanceFieldGetter<BasicDoor, float>("_cooldownDuration");
        _getPryDuration = ReflectionHelpers.CreateInstanceFieldGetter<PryableDoor, float>("_pryAnimDuration");
        _getLockerAnimator = ReflectionHelpers.CreateInstanceFieldGetter<LockerChamber, Animator>("_animator");
        _getLockerAnimTime = ReflectionHelpers.CreateInstanceFieldGetter<LockerChamber, float>("_animationTime");
        _getElevatorDoorAnim = ReflectionHelpers.CreateInstanceFieldGetter<ElevatorChamber, Animator>("_doorAnimator");
        _getGenDoorAnim = ReflectionHelpers.CreateInstanceFieldGetter<Scp079Generator, Animator>("_doorAnimator");
        _getGenLeverAnim = ReflectionHelpers.CreateInstanceFieldGetter<Scp079Generator, Animator>("_leverAnimator");

        if (_getDoorAnimator == null || _getDoorNeedsRefresh == null || _getDoorCooldown == null ||
            _getDoorCooldownDur == null || _getLockerAnimator == null || _getLockerAnimTime == null)
        {
            return MarkIncompatible("Required BasicDoor or LockerChamber Animator fields were not found.");
        }

        int patched = 0;

        MethodInfo doorStart = AccessTools.Method(typeof(BasicDoor), "Start", Type.EmptyTypes);
        MethodInfo doorTargetChanged = AccessTools.Method(typeof(BasicDoor), "TargetStateChanged", Type.EmptyTypes);
        MethodInfo doorApplyAnim = AccessTools.Method(typeof(BasicDoor), "TryApplyNewStateAnimation", Type.EmptyTypes);
        MethodInfo pryTryGate = AccessTools.Method(typeof(PryableDoor), nameof(PryableDoor.TryPryGate), new[] { typeof(ReferenceHub) });
        MethodInfo pryRpcGate = AccessTools.Method(typeof(PryableDoor), "UserCode_RpcPryGate", Type.EmptyTypes);
        MethodInfo breakDamage = AccessTools.Method(typeof(BreakableDoor), nameof(BreakableDoor.ServerDamage), new[] { typeof(float), typeof(DoorDamageType), typeof(Footprint) });
        MethodInfo breakRepair = AccessTools.Method(typeof(BreakableDoor), nameof(BreakableDoor.ServerRepair), Type.EmptyTypes);
        MethodInfo lockerStart = AccessTools.Method(typeof(LockerChamber), "Start", Type.EmptyTypes);
        MethodInfo lockerSetDoor = AccessTools.Method(typeof(LockerChamber), nameof(LockerChamber.SetDoor), new[] { typeof(bool), typeof(AudioClip) });
        MethodInfo elevSetInnerDoor = AccessTools.Method(typeof(ElevatorChamber), "SetInnerDoor", new[] { typeof(bool) });
        MethodInfo elevStart = AccessTools.Method(typeof(ElevatorChamber), "Start", Type.EmptyTypes);
        MethodInfo genSetFlags = AccessTools.PropertySetter(typeof(Scp079Generator), nameof(Scp079Generator.Network_flags));
        MethodInfo genInteract = AccessTools.Method(typeof(Scp079Generator), nameof(Scp079Generator.ServerInteract), new[] { typeof(ReferenceHub), typeof(byte) });
        MethodInfo genStart = AccessTools.Method(typeof(Scp079Generator), "Start", Type.EmptyTypes);

        MethodInfo wakeDoorPrefix = AccessTools.Method(typeof(IdleWorldAnimatorSleepModule), nameof(WakeDoorPrefix));
        MethodInfo wakePryPrefix = AccessTools.Method(typeof(IdleWorldAnimatorSleepModule), nameof(WakePryDoorPrefix));
        MethodInfo wakeLockerPrefix = AccessTools.Method(typeof(IdleWorldAnimatorSleepModule), nameof(WakeLockerPrefix));
        MethodInfo wakeElevatorPrefix = AccessTools.Method(typeof(IdleWorldAnimatorSleepModule), nameof(WakeElevatorPrefix));
        MethodInfo wakeGeneratorPrefix = AccessTools.Method(typeof(IdleWorldAnimatorSleepModule), nameof(WakeGeneratorPrefix));

        if (doorStart != null && patchManager.PatchPrefix(Id, doorStart, wakeDoorPrefix, guard, config)) patched++;
        if (doorTargetChanged != null && patchManager.PatchPrefix(Id, doorTargetChanged, wakeDoorPrefix, guard, config)) patched++;
        if (doorApplyAnim != null && patchManager.PatchPrefix(Id, doorApplyAnim, wakeDoorPrefix, guard, config)) patched++;
        if (pryTryGate != null && patchManager.PatchPrefix(Id, pryTryGate, wakePryPrefix, guard, config)) patched++;
        if (pryRpcGate != null && patchManager.PatchPrefix(Id, pryRpcGate, wakePryPrefix, guard, config)) patched++;
        if (breakDamage != null && patchManager.PatchPrefix(Id, breakDamage, wakeDoorPrefix, guard, config)) patched++;
        if (breakRepair != null && patchManager.PatchPrefix(Id, breakRepair, wakeDoorPrefix, guard, config)) patched++;
        if (lockerStart != null && patchManager.PatchPostfix(Id, lockerStart, wakeLockerPrefix, guard, config)) patched++;
        if (lockerSetDoor != null && patchManager.PatchPrefix(Id, lockerSetDoor, wakeLockerPrefix, guard, config)) patched++;
        if (_getElevatorDoorAnim != null && elevStart != null && patchManager.PatchPostfix(Id, elevStart, wakeElevatorPrefix, guard, config)) patched++;
        if (_getElevatorDoorAnim != null && elevSetInnerDoor != null && patchManager.PatchPrefix(Id, elevSetInnerDoor, wakeElevatorPrefix, guard, config)) patched++;
        if (_getGenDoorAnim != null && _getGenLeverAnim != null)
        {
            if (genStart != null && patchManager.PatchPostfix(Id, genStart, wakeGeneratorPrefix, guard, config)) patched++;
            if (genSetFlags != null && patchManager.PatchPrefix(Id, genSetFlags, wakeGeneratorPrefix, guard, config)) patched++;
            if (genInteract != null && patchManager.PatchPrefix(Id, genInteract, wakeGeneratorPrefix, guard, config)) patched++;
        }

        if (patched == 0)
        {
            patchManager.UnpatchModule(Id);
            return MarkIncompatible("No BasicDoor or LockerChamber targets could be patched.");
        }

        _active = true;
        _nextCheckTime = Time.unscaledTime + 1.0f;
        StaticUnityMethods.OnUpdate += PollSettledAnimators;

        int enrolled = EnrollExistingWorldAnimators();
        StatusDetail = $"Active (Hooks={patched}, EnrolledNow={enrolled})";
        return true;
    }

    protected override void RevertCore(PatchManager patchManager)
    {
        _active = false;
        StaticUnityMethods.OnUpdate -= PollSettledAnimators;

        foreach (Animator anim in SleptAnimators)
        {
            if (anim != null)
            {
                anim.enabled = true;
            }
        }

        SleptAnimators.Clear();
        ActiveDoors.Clear();
        DoorIndex.Clear();
        ActiveLockers.Clear();
        LockerIndex.Clear();
        ActiveTimedAnims.Clear();
        TimedAnimIndex.Clear();
    }

    private static int EnrollExistingWorldAnimators()
    {
        if (!NetworkServer.active)
        {
            return 0;
        }

        int total = 0;
        try
        {
            float settleAt = Time.unscaledTime + 2.0f;
            BasicDoor[] doors = UnityEngine.Object.FindObjectsByType<BasicDoor>(FindObjectsSortMode.None);
            for (int i = 0; i < doors.Length; i++)
            {
                BasicDoor d = doors[i];
                if (d == null) continue;
                Animator anim = _getDoorAnimator(d);
                if (anim == null) continue;
                EnqueueDoor(d, settleAt);
                total++;
            }

            LockerChamber[] chambers = UnityEngine.Object.FindObjectsByType<LockerChamber>(FindObjectsSortMode.None);
            for (int i = 0; i < chambers.Length; i++)
            {
                LockerChamber c = chambers[i];
                if (c == null) continue;
                Animator anim = _getLockerAnimator(c);
                if (anim == null) continue;
                EnqueueLocker(c, anim, settleAt);
                total++;
            }

            if (_getElevatorDoorAnim != null)
            {
                ElevatorChamber[] elevs = UnityEngine.Object.FindObjectsByType<ElevatorChamber>(FindObjectsSortMode.None);
                for (int i = 0; i < elevs.Length; i++)
                {
                    ElevatorChamber ec = elevs[i];
                    if (ec == null) continue;
                    Animator anim = _getElevatorDoorAnim(ec);
                    if (anim == null) continue;
                    EnqueueTimedAnim(anim, ec, settleAt + 1.0f);
                    total++;
                }
            }

            if (_getGenDoorAnim != null && _getGenLeverAnim != null)
            {
                Scp079Generator[] gens = UnityEngine.Object.FindObjectsByType<Scp079Generator>(FindObjectsSortMode.None);
                for (int i = 0; i < gens.Length; i++)
                {
                    Scp079Generator g = gens[i];
                    if (g == null) continue;
                    Animator dAnim = _getGenDoorAnim(g);
                    if (dAnim != null) { EnqueueTimedAnim(dAnim, null, settleAt); total++; }
                    Animator lAnim = _getGenLeverAnim(g);
                    if (lAnim != null) { EnqueueTimedAnim(lAnim, null, settleAt); total++; }
                }
            }
        }
        catch (Exception ex)
        {
            SLPerformanceLog.Debug($"OPT-19 existing animator enroll note: {ex.Message}");
        }

        return total;
    }

    private static void WakeElevatorPrefix(ElevatorChamber __instance)
    {
        if (!_active || !NetworkServer.active || __instance == null || _getElevatorDoorAnim == null)
        {
            return;
        }

        Animator anim = _getElevatorDoorAnim(__instance);
        if (anim == null) return;
        if (!anim.enabled)
        {
            anim.enabled = true;
            SleptAnimators.Remove(anim);
        }

        EnqueueTimedAnim(anim, __instance, Time.unscaledTime + 4.5f);
    }

    private static void WakeGeneratorPrefix(Scp079Generator __instance)
    {
        if (!_active || !NetworkServer.active || __instance == null)
        {
            return;
        }

        float until = Time.unscaledTime + 4.5f;
        Animator dAnim = _getGenDoorAnim?.Invoke(__instance);
        if (dAnim != null)
        {
            if (!dAnim.enabled) { dAnim.enabled = true; SleptAnimators.Remove(dAnim); }
            EnqueueTimedAnim(dAnim, null, until);
        }

        Animator lAnim = _getGenLeverAnim?.Invoke(__instance);
        if (lAnim != null)
        {
            if (!lAnim.enabled) { lAnim.enabled = true; SleptAnimators.Remove(lAnim); }
            EnqueueTimedAnim(lAnim, null, until);
        }
    }

    private static void EnqueueTimedAnim(Animator anim, ElevatorChamber elev, float settleTime)
    {
        if (TimedAnimIndex.TryGetValue(anim, out int idx))
        {
            TimedAnimatorEntry e = ActiveTimedAnims[idx];
            if (settleTime > e.SettleTime) e.SettleTime = settleTime;
            ActiveTimedAnims[idx] = e;
            return;
        }

        TimedAnimIndex[anim] = ActiveTimedAnims.Count;
        ActiveTimedAnims.Add(new TimedAnimatorEntry { Anim = anim, Elevator = elev, SettleTime = settleTime });
    }

    private static void WakeDoorPrefix(BasicDoor __instance)
    {
        if (!_active || !NetworkServer.active || __instance == null)
        {
            return;
        }

        Animator anim = _getDoorAnimator(__instance);
        if (anim == null)
        {
            return;
        }

        if (!anim.enabled)
        {
            anim.enabled = true;
            SleptAnimators.Remove(anim);
        }

        float dur = Mathf.Max(_getDoorCooldownDur(__instance) + 2.5f, 4.5f);
        EnqueueDoor(__instance, Time.unscaledTime + dur);
    }

    private static void WakePryDoorPrefix(PryableDoor __instance)
    {
        if (!_active || !NetworkServer.active || __instance == null)
        {
            return;
        }

        Animator anim = _getDoorAnimator(__instance);
        if (anim == null)
        {
            return;
        }

        if (!anim.enabled)
        {
            anim.enabled = true;
            SleptAnimators.Remove(anim);
        }

        float pryDur = _getPryDuration != null ? _getPryDuration(__instance) : 3.0f;
        float dur = Mathf.Max(pryDur + 2.5f, 5.5f);
        EnqueueDoor(__instance, Time.unscaledTime + dur);
    }

    private static void WakeLockerPrefix(LockerChamber __instance)
    {
        if (!_active || !NetworkServer.active || __instance == null)
        {
            return;
        }

        Animator anim = _getLockerAnimator(__instance);
        if (anim == null)
        {
            return;
        }

        if (!anim.enabled)
        {
            anim.enabled = true;
            SleptAnimators.Remove(anim);
        }

        float dur = Mathf.Max(_getLockerAnimTime(__instance) + 2.0f, 3.5f);
        EnqueueLocker(__instance, anim, Time.unscaledTime + dur);
    }

    private static void EnqueueDoor(BasicDoor door, float settleTime)
    {
        if (DoorIndex.TryGetValue(door, out int idx))
        {
            DoorWatchEntry e = ActiveDoors[idx];
            if (settleTime > e.SettleTime)
            {
                e.SettleTime = settleTime;
            }

            e.HasSampled = false;
            ActiveDoors[idx] = e;
            return;
        }

        DoorIndex[door] = ActiveDoors.Count;
        ActiveDoors.Add(new DoorWatchEntry
        {
            Door = door,
            SettleTime = settleTime,
            LastExactState = -1f,
            HasSampled = false
        });
    }

    private static void EnqueueLocker(LockerChamber chamber, Animator anim, float settleTime)
    {
        if (LockerIndex.TryGetValue(chamber, out int idx))
        {
            LockerWatchEntry e = ActiveLockers[idx];
            if (settleTime > e.SettleTime)
            {
                e.SettleTime = settleTime;
            }

            ActiveLockers[idx] = e;
            return;
        }

        LockerIndex[chamber] = ActiveLockers.Count;
        ActiveLockers.Add(new LockerWatchEntry
        {
            Chamber = chamber,
            Anim = anim,
            SettleTime = settleTime
        });
    }

    private static void PollSettledAnimators()
    {
        if (!_active || !NetworkServer.active)
        {
            return;
        }

        float now = Time.unscaledTime;
        if (now < _nextCheckTime)
        {
            return;
        }

        _nextCheckTime = now + 0.25f;

        // 1. Check active doors
        for (int i = ActiveDoors.Count - 1; i >= 0; i--)
        {
            DoorWatchEntry e = ActiveDoors[i];
            BasicDoor door = e.Door;
            if (door == null)
            {
                RemoveDoorAt(i);
                continue;
            }

            Animator anim = _getDoorAnimator(door);
            if (anim == null || !anim.gameObject.activeInHierarchy)
            {
                RemoveDoorAt(i);
                continue;
            }

            if (now < e.SettleTime)
            {
                continue;
            }

            if (_getDoorNeedsRefresh(door) || _getDoorCooldown(door) > 0f)
            {
                continue;
            }

            if (door is PryableDoor pry && pry.IsBeingPried)
            {
                continue;
            }

            if (anim.IsInTransition(0))
            {
                continue;
            }

            float exact = door.GetExactState();
            if (!e.HasSampled)
            {
                e.LastExactState = exact;
                e.HasSampled = true;
                ActiveDoors[i] = e;
                continue;
            }

            if (Mathf.Abs(exact - e.LastExactState) > 0.0005f)
            {
                e.LastExactState = exact;
                ActiveDoors[i] = e;
                continue;
            }

            if (anim.enabled)
            {
                anim.enabled = false;
                SleptAnimators.Add(anim);
            }

            RemoveDoorAt(i);
        }

        // 2. Check active locker chambers
        for (int i = ActiveLockers.Count - 1; i >= 0; i--)
        {
            LockerWatchEntry e = ActiveLockers[i];
            if (e.Chamber == null || e.Anim == null || !e.Anim.gameObject.activeInHierarchy)
            {
                RemoveLockerAt(i);
                continue;
            }

            if (now < e.SettleTime)
            {
                continue;
            }

            if (!e.Chamber.CanInteract || e.Anim.IsInTransition(0))
            {
                continue;
            }

            if (e.Anim.enabled)
            {
                e.Anim.enabled = false;
                SleptAnimators.Add(e.Anim);
            }

            RemoveLockerAt(i);
        }

        // 3. Check elevator chamber doors & SCP-079 generator animators
        for (int i = ActiveTimedAnims.Count - 1; i >= 0; i--)
        {
            TimedAnimatorEntry e = ActiveTimedAnims[i];
            if (e.Anim == null || !e.Anim.gameObject.activeInHierarchy)
            {
                RemoveTimedAnimAt(i);
                continue;
            }

            if (now < e.SettleTime)
            {
                continue;
            }

            if (e.Elevator != null && !e.Elevator.IsReady)
            {
                continue;
            }

            if (e.Anim.IsInTransition(0))
            {
                continue;
            }

            if (e.Anim.enabled)
            {
                e.Anim.enabled = false;
                SleptAnimators.Add(e.Anim);
            }

            RemoveTimedAnimAt(i);
        }
    }

    private static void RemoveTimedAnimAt(int idx)
    {
        int last = ActiveTimedAnims.Count - 1;
        Animator removed = ActiveTimedAnims[idx].Anim;
        if (idx != last)
        {
            TimedAnimatorEntry moved = ActiveTimedAnims[last];
            ActiveTimedAnims[idx] = moved;
            if (moved.Anim != null)
            {
                TimedAnimIndex[moved.Anim] = idx;
            }
        }

        ActiveTimedAnims.RemoveAt(last);
        if (removed != null)
        {
            TimedAnimIndex.Remove(removed);
        }
    }

    private static void RemoveDoorAt(int idx)
    {
        int last = ActiveDoors.Count - 1;
        BasicDoor removed = ActiveDoors[idx].Door;
        if (idx != last)
        {
            DoorWatchEntry moved = ActiveDoors[last];
            ActiveDoors[idx] = moved;
            if (moved.Door != null)
            {
                DoorIndex[moved.Door] = idx;
            }
        }

        ActiveDoors.RemoveAt(last);
        if (removed != null)
        {
            DoorIndex.Remove(removed);
        }
    }

    private static void RemoveLockerAt(int idx)
    {
        int last = ActiveLockers.Count - 1;
        LockerChamber removed = ActiveLockers[idx].Chamber;
        if (idx != last)
        {
            LockerWatchEntry moved = ActiveLockers[last];
            ActiveLockers[idx] = moved;
            if (moved.Chamber != null)
            {
                LockerIndex[moved.Chamber] = idx;
            }
        }

        ActiveLockers.RemoveAt(last);
        if (removed != null)
        {
            LockerIndex.Remove(removed);
        }
    }
}
