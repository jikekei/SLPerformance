using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SLPerformance.Compatibility;
using SLPerformance.Core;

namespace SLPerformance.Patching;

public sealed class PatchManager
{
    private readonly struct PatchEntry
    {
        public readonly MethodBase Original;
        public readonly MethodInfo Patch;

        public PatchEntry(MethodBase original, MethodInfo patch)
        {
            Original = original;
            Patch = patch;
        }
    }

    private readonly Harmony _harmony;
    private readonly Dictionary<string, List<PatchEntry>> _modulePatches = new Dictionary<string, List<PatchEntry>>(StringComparer.OrdinalIgnoreCase);

    public string HarmonyId { get; }

    public PatchManager(string harmonyId)
    {
        HarmonyId = harmonyId;
        _harmony = new Harmony(harmonyId);
    }

    public bool PatchPrefix(
        string moduleId,
        MethodBase original,
        MethodInfo prefix,
        CompatibilityGuard guard,
        SLPerformanceConfig config,
        int priority = Priority.Normal,
        bool checkConflict = true)
    {
        return ApplyPatch(moduleId, original, prefix, HarmonyPatchType.Prefix, guard, config, priority, checkConflict);
    }

    public bool PatchPostfix(
        string moduleId,
        MethodBase original,
        MethodInfo postfix,
        CompatibilityGuard guard,
        SLPerformanceConfig config,
        int priority = Priority.Normal,
        bool checkConflict = false)
    {
        return ApplyPatch(moduleId, original, postfix, HarmonyPatchType.Postfix, guard, config, priority, checkConflict);
    }

    public bool PatchTranspiler(
        string moduleId,
        MethodBase original,
        MethodInfo transpiler,
        CompatibilityGuard guard,
        SLPerformanceConfig config,
        int priority = Priority.Normal,
        bool checkConflict = true)
    {
        return ApplyPatch(moduleId, original, transpiler, HarmonyPatchType.Transpiler, guard, config, priority, checkConflict);
    }

    public bool PatchFinalizer(
        string moduleId,
        MethodBase original,
        MethodInfo finalizer,
        CompatibilityGuard guard,
        SLPerformanceConfig config,
        int priority = Priority.Normal,
        bool checkConflict = false)
    {
        return ApplyPatch(moduleId, original, finalizer, HarmonyPatchType.Finalizer, guard, config, priority, checkConflict);
    }

    private bool ApplyPatch(
        string moduleId,
        MethodBase original,
        MethodInfo patchMethod,
        HarmonyPatchType kind,
        CompatibilityGuard guard,
        SLPerformanceConfig config,
        int priority,
        bool checkConflict)
    {
        if (original == null || patchMethod == null)
        {
            return false;
        }

        if (checkConflict && config.AutoSkipOnHarmonyConflict &&
            guard.HasExternalConflict(original, HarmonyId, out string owners))
        {
            SLPerformanceLog.Warn($"Harmony conflict on {original.DeclaringType?.FullName}.{original.Name} (owners: {owners}) for module '{moduleId}'.");
            return false;
        }

        HarmonyMethod hm = new HarmonyMethod(patchMethod) { priority = priority };
        switch (kind)
        {
            case HarmonyPatchType.Prefix:
                _harmony.Patch(original, prefix: hm);
                break;
            case HarmonyPatchType.Postfix:
                _harmony.Patch(original, postfix: hm);
                break;
            case HarmonyPatchType.Transpiler:
                _harmony.Patch(original, transpiler: hm);
                break;
            case HarmonyPatchType.Finalizer:
                _harmony.Patch(original, finalizer: hm);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!_modulePatches.TryGetValue(moduleId, out List<PatchEntry> list))
        {
            list = new List<PatchEntry>(4);
            _modulePatches.Add(moduleId, list);
        }

        list.Add(new PatchEntry(original, patchMethod));
        SLPerformanceLog.Debug($"Patched [{kind}] {original.DeclaringType?.FullName}.{original.Name} for '{moduleId}'.");
        return true;
    }

    public void UnpatchModule(string moduleId)
    {
        if (!_modulePatches.TryGetValue(moduleId, out List<PatchEntry> list))
        {
            return;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            PatchEntry entry = list[i];
            try
            {
                _harmony.Unpatch(entry.Original, entry.Patch);
            }
            catch (Exception ex)
            {
                SLPerformanceLog.Error($"Failed to unpatch {entry.Original?.Name} for '{moduleId}': {ex.Message}");
            }
        }

        list.Clear();
        _modulePatches.Remove(moduleId);
    }

    public void UnpatchAll()
    {
        _harmony.UnpatchAll(HarmonyId);
        _modulePatches.Clear();
    }

    public int GetPatchCount(string moduleId)
    {
        return _modulePatches.TryGetValue(moduleId, out List<PatchEntry> list) ? list.Count : 0;
    }
}
