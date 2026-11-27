using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace SLPerformance.Compatibility;

public sealed class CompatibilityGuard
{
    public const string TestedGameVersion = "14.2.9";

    public string DetectedGameVersion { get; }
    public string DetectedLabApiVersion { get; }
    public bool IsExactTestedVersion { get; }

    public CompatibilityGuard()
    {
        DetectedGameVersion = ReadGameVersionSafe();
        DetectedLabApiVersion = ReadLabApiVersionSafe();
        IsExactTestedVersion = string.Equals(DetectedGameVersion, TestedGameVersion, StringComparison.OrdinalIgnoreCase);
    }

    public bool HasExternalConflict(MethodBase method, string ourHarmonyId, out string conflictOwners)
    {
        conflictOwners = string.Empty;
        if (method == null)
        {
            return false;
        }

        Patches info = Harmony.GetPatchInfo(method);
        if (info == null)
        {
            return false;
        }

        List<string> owners = null;
        CollectOwners(info.Prefixes, "Prefix", ourHarmonyId, ref owners);
        CollectOwners(info.Transpilers, "Transpiler", ourHarmonyId, ref owners);

        if (owners == null || owners.Count == 0)
        {
            return false;
        }

        conflictOwners = string.Join(", ", owners);
        return true;
    }

    private static void CollectOwners(IReadOnlyCollection<Patch> patches, string kind, string ourHarmonyId, ref List<string> owners)
    {
        if (patches == null)
        {
            return;
        }

        foreach (Patch patch in patches)
        {
            if (string.Equals(patch.owner, ourHarmonyId, StringComparison.Ordinal))
            {
                continue;
            }

            owners ??= new List<string>(2);
            string tag = $"{patch.owner} [{kind}]";
            if (!owners.Contains(tag))
            {
                owners.Add(tag);
            }
        }
    }

    public string BuildDiagnosticReport(IReadOnlyList<Core.IOptimizationModule> modules)
    {
        StringBuilder sb = new StringBuilder(512);
        sb.AppendLine("=== SLPerformance Compatibility Report ===");
        sb.AppendLine($"Tested Game Version   : {TestedGameVersion}");
        sb.AppendLine($"Detected Game Version : {DetectedGameVersion} (ExactMatch={IsExactTestedVersion})");
        sb.AppendLine($"Detected LabAPI       : {DetectedLabApiVersion}");
        sb.AppendLine($"Runtime               : {Environment.Version} | 64-bit={Environment.Is64BitProcess} | CPUs={Environment.ProcessorCount}");
        sb.AppendLine("Modules:");
        for (int i = 0; i < modules.Count; i++)
        {
            Core.IOptimizationModule m = modules[i];
            sb.AppendLine($"  [{m.Tier}] {m.Id,-34} | {m.State,-19} | {m.StatusDetail}");
        }

        return sb.ToString();
    }

    private static string ReadGameVersionSafe()
    {
        try
        {
            return GameCore.Version.VersionString ?? "unknown";
        }
        catch
        {
            return "offline/unavailable";
        }
    }

    private static string ReadLabApiVersionSafe()
    {
        try
        {
            return LabApi.Features.LabApiProperties.CompiledVersion ?? "unknown";
        }
        catch
        {
            return "offline/unavailable";
        }
    }
}
