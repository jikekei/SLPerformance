using System;
using System.Collections.Generic;
using SLPerformance.Compatibility;
using SLPerformance.Patching;

namespace SLPerformance.Core;

public static class SLPerformanceLog
{
    public static bool DebugEnabled { get; set; }

    public static void Info(string message)
    {
        try
        {
            LabApi.Features.Console.Logger.Info(message);
        }
        catch
        {
            Console.WriteLine("[INFO] [SLPerformance] " + message);
        }
    }

    public static void Warn(string message)
    {
        try
        {
            LabApi.Features.Console.Logger.Warn(message);
        }
        catch
        {
            Console.WriteLine("[WARN] [SLPerformance] " + message);
        }
    }

    public static void Error(string message)
    {
        try
        {
            LabApi.Features.Console.Logger.Error(message);
        }
        catch
        {
            Console.WriteLine("[ERROR] [SLPerformance] " + message);
        }
    }

    public static void Debug(string message)
    {
        if (!DebugEnabled)
        {
            return;
        }

        try
        {
            LabApi.Features.Console.Logger.Debug(message);
        }
        catch
        {
            Console.WriteLine("[DEBUG] [SLPerformance] " + message);
        }
    }
}

public sealed class ModuleRegistry
{
    private readonly List<IOptimizationModule> _modules = new List<IOptimizationModule>(20);
    private readonly Dictionary<string, IOptimizationModule> _byId = new Dictionary<string, IOptimizationModule>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IOptimizationModule> Modules => _modules;

    public void Register(IOptimizationModule module)
    {
        if (module == null)
        {
            throw new ArgumentNullException(nameof(module));
        }

        if (_byId.ContainsKey(module.Id))
        {
            throw new InvalidOperationException($"Duplicate SLPerformance module id: '{module.Id}'.");
        }

        _modules.Add(module);
        _byId.Add(module.Id, module);
    }

    public bool TryGet(string id, out IOptimizationModule module)
    {
        return _byId.TryGetValue(id, out module);
    }

    public void ApplyConfigured(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        int active = 0;
        int skipped = 0;
        int failed = 0;

        for (int i = 0; i < _modules.Count; i++)
        {
            IOptimizationModule module = _modules[i];
            if (!module.IsEnabledInConfig(config))
            {
                continue;
            }

            module.Apply(patchManager, guard, config);
            switch (module.State)
            {
                case ModuleState.Active:
                    active++;
                    break;
                case ModuleState.SkippedConflict:
                case ModuleState.SkippedIncompatible:
                    skipped++;
                    break;
                case ModuleState.Failed:
                    failed++;
                    break;
            }
        }

        SLPerformanceLog.Info($"Modules initialized: {active} active, {skipped} skipped, {failed} failed (total {_modules.Count} registered).");
    }

    public void RevertAll(PatchManager patchManager)
    {
        for (int i = _modules.Count - 1; i >= 0; i--)
        {
            IOptimizationModule module = _modules[i];
            if (module.State == ModuleState.Active)
            {
                module.Revert(patchManager);
            }
        }
    }
}
