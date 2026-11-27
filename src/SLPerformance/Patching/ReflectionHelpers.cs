using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SLPerformance.Patching;

/// <summary>
/// Compiles zero-reflection hot-path field getters/setters and private method delegates once at module startup.
/// </summary>
public static class ReflectionHelpers
{
    public static Func<TField> CreateStaticFieldGetter<TField>(Type declaringType, string fieldName)
    {
        FieldInfo field = AccessTools.Field(declaringType, fieldName);
        if (field == null || !field.IsStatic || !typeof(TField).IsAssignableFrom(field.FieldType))
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_GetStatic_{declaringType.Name}_{fieldName}",
            typeof(TField),
            Type.EmptyTypes,
            declaringType.Module,
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ret);
        return (Func<TField>)dm.CreateDelegate(typeof(Func<TField>));
    }

    public static Action<TField> CreateStaticFieldSetter<TField>(Type declaringType, string fieldName)
    {
        FieldInfo field = AccessTools.Field(declaringType, fieldName);
        if (field == null || !field.IsStatic || !field.FieldType.IsAssignableFrom(typeof(TField)))
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_SetStatic_{declaringType.Name}_{fieldName}",
            typeof(void),
            new[] { typeof(TField) },
            declaringType.Module,
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);
        return (Action<TField>)dm.CreateDelegate(typeof(Action<TField>));
    }

    public static Func<TInstance, TField> CreateInstanceFieldGetter<TInstance, TField>(string fieldName)
    {
        return CreateInstanceFieldGetter<TInstance, TField>(typeof(TInstance), fieldName);
    }

    public static Func<TInstance, TField> CreateInstanceFieldGetter<TInstance, TField>(Type declaringType, string fieldName)
    {
        FieldInfo field = AccessTools.Field(declaringType, fieldName);
        if (field == null || field.IsStatic || !typeof(TField).IsAssignableFrom(field.FieldType))
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_Get_{declaringType.Name}_{fieldName}",
            typeof(TField),
            new[] { typeof(TInstance) },
            declaringType.Module,
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return (Func<TInstance, TField>)dm.CreateDelegate(typeof(Func<TInstance, TField>));
    }

    public static Action<TInstance, TField> CreateInstanceFieldSetter<TInstance, TField>(string fieldName)
    {
        return CreateInstanceFieldSetter<TInstance, TField>(typeof(TInstance), fieldName);
    }

    public static Action<TInstance, TField> CreateInstanceFieldSetter<TInstance, TField>(Type declaringType, string fieldName)
    {
        FieldInfo field = AccessTools.Field(declaringType, fieldName);
        if (field == null || field.IsStatic || !field.FieldType.IsAssignableFrom(typeof(TField)))
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_Set_{declaringType.Name}_{fieldName}",
            typeof(void),
            new[] { typeof(TInstance), typeof(TField) },
            declaringType.Module,
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        return (Action<TInstance, TField>)dm.CreateDelegate(typeof(Action<TInstance, TField>));
    }

    public static Func<bool> CreateStaticEventHasSubscribersGetter(Type declaringType, string eventFieldName)
    {
        FieldInfo field = AccessTools.Field(declaringType, eventFieldName);
        if (field == null || !field.IsStatic || !typeof(Delegate).IsAssignableFrom(field.FieldType))
        {
            return null;
        }

        DynamicMethod dm = new DynamicMethod(
            $"SLPerf_HasSub_{declaringType.Name}_{eventFieldName}",
            typeof(bool),
            Type.EmptyTypes,
            declaringType.Module,
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Cgt_Un);
        il.Emit(OpCodes.Ret);
        return (Func<bool>)dm.CreateDelegate(typeof(Func<bool>));
    }

    public static TDelegate CreateOpenDelegate<TDelegate>(MethodInfo method) where TDelegate : Delegate
    {
        if (method == null)
        {
            return null;
        }

        return (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method, throwOnBindFailure: false);
    }
}
