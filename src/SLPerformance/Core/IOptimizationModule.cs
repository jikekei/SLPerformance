using SLPerformance.Compatibility;
using SLPerformance.Patching;

namespace SLPerformance.Core;

public enum OptimizationTier
{
    Tier1 = 1,
    Tier2 = 2,
    Tier3 = 3
}

public enum ModuleState
{
    Disabled = 0,
    Active = 1,
    SkippedConflict = 2,
    SkippedIncompatible = 3,
    Failed = 4
}

public interface IOptimizationModule
{
    string Id { get; }
    string Name { get; }
    OptimizationTier Tier { get; }
    bool DefaultEnabled { get; }
    string Description { get; }
    ModuleState State { get; }
    string StatusDetail { get; }

    bool IsEnabledInConfig(SLPerformanceConfig config);
    void Apply(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config);
    void Revert(PatchManager patchManager);
}

public abstract class OptimizationModuleBase : IOptimizationModule
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract OptimizationTier Tier { get; }
    public virtual bool DefaultEnabled => Tier != OptimizationTier.Tier3;
    public abstract string Description { get; }

    public ModuleState State { get; protected set; } = ModuleState.Disabled;
    public string StatusDetail { get; protected set; } = "Not initialized";

    public abstract bool IsEnabledInConfig(SLPerformanceConfig config);

    public void Apply(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config)
    {
        if (State == ModuleState.Active)
        {
            return;
        }

        try
        {
            if (ApplyCore(patchManager, guard, config))
            {
                State = ModuleState.Active;
                if (string.IsNullOrEmpty(StatusDetail) || StatusDetail == "Not initialized" || StatusDetail == "Reverted")
                {
                    StatusDetail = "Active";
                }
            }
        }
        catch (System.Exception ex)
        {
            patchManager.UnpatchModule(Id);
            State = ModuleState.Failed;
            StatusDetail = $"Exception: {ex.GetType().Name}: {ex.Message}";
            SLPerformanceLog.Error($"Module '{Id}' failed to apply: {ex}");
        }
    }

    public void Revert(PatchManager patchManager)
    {
        try
        {
            RevertCore(patchManager);
        }
        catch (System.Exception ex)
        {
            SLPerformanceLog.Error($"Module '{Id}' threw during revert: {ex}");
        }
        finally
        {
            patchManager.UnpatchModule(Id);
            State = ModuleState.Disabled;
            StatusDetail = "Reverted";
        }
    }

    protected bool MarkIncompatible(string reason)
    {
        State = ModuleState.SkippedIncompatible;
        StatusDetail = reason;
        SLPerformanceLog.Warn($"Module '{Id}' skipped (incompatible): {reason}");
        return false;
    }

    protected bool MarkConflict(string reason)
    {
        State = ModuleState.SkippedConflict;
        StatusDetail = reason;
        SLPerformanceLog.Warn($"Module '{Id}' skipped (Harmony conflict): {reason}");
        return false;
    }

    protected abstract bool ApplyCore(PatchManager patchManager, CompatibilityGuard guard, SLPerformanceConfig config);

    protected virtual void RevertCore(PatchManager patchManager)
    {
    }
}
