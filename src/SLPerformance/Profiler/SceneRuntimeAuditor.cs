using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MEC;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using UnityEngine;

namespace SLPerformance.Profiler;

/// <summary>
/// On-demand scene/runtime census (`slperf audit`) to identify which MonoBehaviours,
/// StaticUnityMethods subscribers, MEC coroutines, and Animators are actively executing.
/// Runs strictly on console command; zero per-frame overhead.
/// </summary>
public static class SceneRuntimeAuditor
{
    private readonly struct LifecycleFlags
    {
        public readonly bool HasUpdate;
        public readonly bool HasLateUpdate;
        public readonly bool HasFixedUpdate;

        public LifecycleFlags(bool u, bool l, bool f)
        {
            HasUpdate = u;
            HasLateUpdate = l;
            HasFixedUpdate = f;
        }
    }

    private static readonly Dictionary<Type, LifecycleFlags> TypeFlagsCache = new Dictionary<Type, LifecycleFlags>(256);

    public static string BuildAuditReport()
    {
        StringBuilder sb = new StringBuilder(4096);
        sb.AppendLine("=== SLPerformance Live Scene & Lifecycle Audit ===");

        AppendMonoBehaviourCensus(sb);
        AppendStaticUnitySubscribers(sb);
        AppendMecCoroutines(sb);
        AppendAnimatorAndCullingCensus(sb);

        return sb.ToString();
    }

    private static void AppendMonoBehaviourCensus(StringBuilder sb)
    {
        try
        {
            MonoBehaviour[] all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            var lateCounts = new Dictionary<Type, int>(64);
            var updateCounts = new Dictionary<Type, int>(128);
            var pluginCounts = new Dictionary<Type, int>(32);
            int totalActive = 0;
            int totalLate = 0;
            int totalUpdate = 0;
            int totalFixed = 0;

            for (int i = 0; i < all.Length; i++)
            {
                MonoBehaviour mb = all[i];
                if (mb == null || !mb.isActiveAndEnabled)
                {
                    continue;
                }

                totalActive++;
                Type t = mb.GetType();
                LifecycleFlags flags = GetLifecycleFlags(t);

                if (flags.HasLateUpdate)
                {
                    totalLate++;
                    lateCounts.TryGetValue(t, out int c);
                    lateCounts[t] = c + 1;
                }

                if (flags.HasUpdate)
                {
                    totalUpdate++;
                    updateCounts.TryGetValue(t, out int c);
                    updateCounts[t] = c + 1;
                }

                if (flags.HasFixedUpdate)
                {
                    totalFixed++;
                }

                if (flags.HasUpdate || flags.HasLateUpdate || flags.HasFixedUpdate)
                {
                    string asmName = t.Assembly.GetName().Name;
                    if (!IsVanillaOrUnityAssembly(asmName))
                    {
                        pluginCounts.TryGetValue(t, out int c);
                        pluginCounts[t] = c + 1;
                    }
                }
            }

            sb.AppendLine($"Active MonoBehaviours: Total={totalActive} | WithLateUpdate={totalLate} ({lateCounts.Count} types) | WithUpdate={totalUpdate} ({updateCounts.Count} types) | WithFixedUpdate={totalFixed}");

            sb.AppendLine("--- Top 20 Active MonoBehaviours with LateUpdate() ---");
            foreach (var kv in lateCounts.OrderByDescending(x => x.Value).Take(20))
            {
                sb.AppendLine($"  {kv.Value,5}x | {kv.Key.FullName} [{kv.Key.Assembly.GetName().Name}]");
            }

            sb.AppendLine("--- Top 20 Active MonoBehaviours with Update() ---");
            foreach (var kv in updateCounts.OrderByDescending(x => x.Value).Take(20))
            {
                sb.AppendLine($"  {kv.Value,5}x | {kv.Key.FullName} [{kv.Key.Assembly.GetName().Name}]");
            }

            if (pluginCounts.Count > 0)
            {
                sb.AppendLine("--- Non-Vanilla (Plugin) Active MonoBehaviours with Update/LateUpdate/FixedUpdate ---");
                foreach (var kv in pluginCounts.OrderByDescending(x => x.Value).Take(25))
                {
                    LifecycleFlags f = GetLifecycleFlags(kv.Key);
                    string kinds = (f.HasUpdate ? "U" : "") + (f.HasLateUpdate ? "L" : "") + (f.HasFixedUpdate ? "F" : "");
                    sb.AppendLine($"  {kv.Value,5}x [{kinds,-3}] | {kv.Key.FullName} [{kv.Key.Assembly.GetName().Name}]");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[MonoBehaviour Census Error] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AppendStaticUnitySubscribers(StringBuilder sb)
    {
        sb.AppendLine("--- StaticUnityMethods Event Subscribers ---");
        AppendEventField(sb, "OnLateUpdate");
        AppendEventField(sb, "OnUpdate");
        AppendEventField(sb, "OnFixedUpdate");
    }

    private static void AppendEventField(StringBuilder sb, string fieldName)
    {
        try
        {
            FieldInfo fi = AccessTools.Field(typeof(StaticUnityMethods), fieldName);
            Delegate del = fi?.GetValue(null) as Delegate;
            if (del == null)
            {
                sb.AppendLine($"  {fieldName}: (0 subscribers)");
                return;
            }

            Delegate[] list = del.GetInvocationList();
            var grouped = new Dictionary<string, int>(16);
            for (int i = 0; i < list.Length; i++)
            {
                MethodInfo m = list[i].Method;
                string key = $"{m.DeclaringType?.FullName ?? "<null>"}.{m.Name} [{m.DeclaringType?.Assembly.GetName().Name ?? "?"}]";
                grouped.TryGetValue(key, out int c);
                grouped[key] = c + 1;
            }

            sb.AppendLine($"  {fieldName} ({list.Length} total, {grouped.Count} unique):");
            foreach (var kv in grouped.OrderByDescending(x => x.Value).Take(20))
            {
                sb.AppendLine($"    {kv.Value,4}x | {kv.Key}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  {fieldName}: [Error: {ex.Message}]");
        }
    }

    private static void AppendMecCoroutines(StringBuilder sb)
    {
        sb.AppendLine("--- MEC.Timing Active Coroutines ---");
        try
        {
            Timing[] instances = UnityEngine.Object.FindObjectsByType<Timing>(FindObjectsSortMode.None);
            if (instances.Length == 0)
            {
                sb.AppendLine("  No active MEC.Timing instances found.");
                return;
            }

            for (int i = 0; i < instances.Length; i++)
            {
                Timing t = instances[i];
                if (t == null) continue;
                sb.AppendLine(
                    $"  Timing[{i}] ({t.name}): Update={t.UpdateCoroutines} | LateUpdate={t.LateUpdateCoroutines} | " +
                    $"Fixed={t.FixedUpdateCoroutines} | Slow={t.SlowUpdateCoroutines} | Realtime={t.RealtimeUpdateCoroutines} | EOF={t.EndOfFrameCoroutines}");

                AppendProcessArray(sb, t, "LateUpdateProcesses", "LateUpdate");
                AppendProcessArray(sb, t, "UpdateProcesses", "Update");
                AppendProcessArray(sb, t, "SlowUpdateProcesses", "SlowUpdate");
                AppendProcessArray(sb, t, "FixedUpdateProcesses", "FixedUpdate");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  [MEC Audit Error] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AppendProcessArray(StringBuilder sb, Timing timing, string fieldName, string label)
    {
        FieldInfo fi = AccessTools.Field(typeof(Timing), fieldName);
        if (fi?.GetValue(timing) is not IEnumerator<float>[] arr)
        {
            return;
        }

        var counts = new Dictionary<string, int>(16);
        int active = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            IEnumerator<float> proc = arr[i];
            if (proc == null) continue;
            active++;
            Type pt = proc.GetType();
            string key = $"{pt.FullName} [{pt.Assembly.GetName().Name}]";
            counts.TryGetValue(key, out int c);
            counts[key] = c + 1;
        }

        if (active == 0) return;

        sb.AppendLine($"    [{label} Top Coroutines ({active} active)]:");
        foreach (var kv in counts.OrderByDescending(x => x.Value).Take(10))
        {
            sb.AppendLine($"      {kv.Value,4}x | {kv.Key}");
        }
    }

    private static void AppendAnimatorAndCullingCensus(StringBuilder sb)
    {
        sb.AppendLine("--- Animator & CullingSubcontroller Census ---");
        try
        {
            Animator[] anims = UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsSortMode.None);
            int enabledCount = 0;
            int alwaysAnim = 0;
            int cullUpdate = 0;
            int cullComplete = 0;
            var activeAnimOwners = new Dictionary<string, int>(32);
            for (int i = 0; i < anims.Length; i++)
            {
                Animator a = anims[i];
                if (a == null) continue;
                if (a.isActiveAndEnabled)
                {
                    enabledCount++;
                    string owner = DescribeAnimatorOwner(a);
                    activeAnimOwners.TryGetValue(owner, out int cnt);
                    activeAnimOwners[owner] = cnt + 1;
                }
                switch (a.cullingMode)
                {
                    case AnimatorCullingMode.AlwaysAnimate: alwaysAnim++; break;
                    case AnimatorCullingMode.CullUpdateTransforms: cullUpdate++; break;
                    case AnimatorCullingMode.CullCompletely: cullComplete++; break;
                }
            }

            sb.AppendLine($"  Animators: Total={anims.Length} | ActiveEnabled={enabledCount} | Disabled={anims.Length - enabledCount} | Modes(Always={alwaysAnim}, CullTx={cullUpdate}, CullAll={cullComplete})");
            if (activeAnimOwners.Count > 0)
            {
                sb.AppendLine("  Top 10 ActiveEnabled Animator Owners:");
                foreach (var kv in activeAnimOwners.OrderByDescending(x => x.Value).Take(10))
                {
                    sb.AppendLine($"    {kv.Value,4}x | {kv.Key}");
                }
            }

            CullingSubcontroller[] cullers = UnityEngine.Object.FindObjectsByType<CullingSubcontroller>(FindObjectsSortMode.None);
            FieldInfo modField = AccessTools.Field(typeof(CullingSubcontroller), "_updateModuloFrames");
            int activeCullers = 0;
            int animCulledTrue = 0;
            var modDist = new Dictionary<int, int>(8);
            for (int i = 0; i < cullers.Length; i++)
            {
                CullingSubcontroller c = cullers[i];
                if (c == null || !c.isActiveAndEnabled) continue;
                activeCullers++;
                if (c.AnimCulled) animCulledTrue++;
                if (modField != null)
                {
                    int mod = (int)modField.GetValue(c);
                    modDist.TryGetValue(mod, out int cnt);
                    modDist[mod] = cnt + 1;
                }
            }

            string modSummary = modDist.Count > 0
                ? string.Join(", ", modDist.OrderBy(x => x.Key).Select(x => $"mod{x.Key}={x.Value}"))
                : "n/a";
            sb.AppendLine($"  CullingSubcontroller: Active={activeCullers} | AnimCulled={animCulledTrue} | Unculled={activeCullers - animCulledTrue} | ModuloDist=[{modSummary}]");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  [Animator Census Error] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsVanillaOrUnityAssembly(string asmName)
    {
        if (string.IsNullOrEmpty(asmName)) return true;
        return asmName.Equals("Assembly-CSharp", StringComparison.Ordinal) ||
               asmName.Equals("Assembly-CSharp-firstpass", StringComparison.Ordinal) ||
               asmName.Equals("Mirror", StringComparison.Ordinal) ||
               asmName.Equals("Mirror.Components", StringComparison.Ordinal) ||
               asmName.StartsWith("Unity.", StringComparison.Ordinal) ||
               asmName.StartsWith("UnityEngine", StringComparison.Ordinal);
    }

    private static string DescribeAnimatorOwner(Animator a)
    {
        string ctrl = a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "<no-ctrl>";
        string goName = a.gameObject != null ? a.gameObject.name : "<null>";
        Transform p = a.transform != null ? a.transform.parent : null;
        string parentName = p != null ? p.name : "<root>";
        return $"{ctrl} ({parentName}/{goName})";
    }

    private static LifecycleFlags GetLifecycleFlags(Type type)
    {
        if (TypeFlagsCache.TryGetValue(type, out LifecycleFlags cached))
        {
            return cached;
        }

        bool hasUpdate = HasUnityMessage(type, "Update");
        bool hasLate = HasUnityMessage(type, "LateUpdate");
        bool hasFixed = HasUnityMessage(type, "FixedUpdate");
        var flags = new LifecycleFlags(hasUpdate, hasLate, hasFixed);
        TypeFlagsCache[type] = flags;
        return flags;
    }

    private static bool HasUnityMessage(Type type, string methodName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (Type cur = type; cur != null && cur != typeof(MonoBehaviour) && cur != typeof(Behaviour) && cur != typeof(Component) && cur != typeof(object); cur = cur.BaseType)
        {
            if (cur.GetMethod(methodName, flags, null, Type.EmptyTypes, null) != null)
            {
                return true;
            }
        }

        return false;
    }
}
