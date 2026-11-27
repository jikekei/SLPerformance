using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using RelativePositioning;

namespace SLPerformance.Serialization;

/// <summary>
/// Zero-allocation compiled constructor and delta helper for readonly struct FpcSyncData,
/// allowing precomputed (rotH, rotV) to bypass repeated O(P^2) FpcMouseLook.GetSyncValues calls.
/// </summary>
public static class FpcSyncDataFactory
{
    public delegate FpcSyncData BuildSyncDataDelegate(
        FpcSyncData prev,
        PlayerMovementState state,
        bool bitCustom,
        RelativePosition pos,
        ushort rotH,
        ushort rotV);

    private static readonly BuildSyncDataDelegate CompiledBuilder;

    public static bool IsSupported => CompiledBuilder != null;

    static FpcSyncDataFactory()
    {
        CompiledBuilder = TryCompileBuilder();
    }

    public static FpcSyncData Build(
        in FpcSyncData prev,
        PlayerMovementState state,
        bool bitCustom,
        in RelativePosition pos,
        ushort rotH,
        ushort rotV)
    {
        return CompiledBuilder(prev, state, bitCustom, pos, rotH, rotV);
    }

    private static BuildSyncDataDelegate TryCompileBuilder()
    {
        Type t = typeof(FpcSyncData);
        FieldInfo fState = AccessTools.Field(t, "_state");
        FieldInfo fPos = AccessTools.Field(t, "_position");
        FieldInfo fRotH = AccessTools.Field(t, "_rotH");
        FieldInfo fRotV = AccessTools.Field(t, "_rotV");
        FieldInfo fBitMouse = AccessTools.Field(t, "_bitMouseLook");
        FieldInfo fBitPos = AccessTools.Field(t, "_bitPosition");
        FieldInfo fBitCustom = AccessTools.Field(t, "_bitCustom");
        MethodInfo relPosInequality = AccessTools.Method(typeof(RelativePosition), "op_Inequality", new[] { typeof(RelativePosition), typeof(RelativePosition) });

        if (fState == null || fPos == null || fRotH == null || fRotV == null ||
            fBitMouse == null || fBitPos == null || fBitCustom == null || relPosInequality == null)
        {
            return null;
        }

        // Signature: FpcSyncData Build(FpcSyncData prev, PlayerMovementState state, bool bitCustom, RelativePosition pos, ushort rotH, ushort rotV)
        DynamicMethod dm = new DynamicMethod(
            "SLPerf_BuildFpcSyncData",
            typeof(FpcSyncData),
            new[] { typeof(FpcSyncData), typeof(PlayerMovementState), typeof(bool), typeof(RelativePosition), typeof(ushort), typeof(ushort) },
            t.Module,
            skipVisibility: true);

        ILGenerator il = dm.GetILGenerator();
        LocalBuilder result = il.DeclareLocal(typeof(FpcSyncData));

        // result = default(FpcSyncData)
        il.Emit(OpCodes.Ldloca_S, result);
        il.Emit(OpCodes.Initobj, typeof(FpcSyncData));

        // result._state = state
        il.Emit(OpCodes.Ldloca_S, result);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, fState);

        // result._bitCustom = bitCustom
        il.Emit(OpCodes.Ldloca_S, result);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Stfld, fBitCustom);

        // result._position = pos
        il.Emit(OpCodes.Ldloca_S, result);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Stfld, fPos);

        // result._rotH = rotH
        il.Emit(OpCodes.Ldloca_S, result);
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Stfld, fRotH);

        // result._rotV = rotV
        il.Emit(OpCodes.Ldloca_S, result);
        il.Emit(OpCodes.Ldarg_S, (byte)5);
        il.Emit(OpCodes.Stfld, fRotV);

        // result._bitPosition = prev._position != pos
        il.Emit(OpCodes.Ldloca_S, result);
        il.Emit(OpCodes.Ldarga_S, (byte)0);
        il.Emit(OpCodes.Ldfld, fPos);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Call, relPosInequality);
        il.Emit(OpCodes.Stfld, fBitPos);

        // result._bitMouseLook = (rotH != prev._rotH) || (rotV != prev._rotV)
        Label setTrue = il.DefineLabel();
        Label doneMouse = il.DefineLabel();

        il.Emit(OpCodes.Ldloca_S, result);
        il.Emit(OpCodes.Ldarg_S, (byte)4);
        il.Emit(OpCodes.Ldarga_S, (byte)0);
        il.Emit(OpCodes.Ldfld, fRotH);
        il.Emit(OpCodes.Bne_Un_S, setTrue);

        il.Emit(OpCodes.Ldarg_S, (byte)5);
        il.Emit(OpCodes.Ldarga_S, (byte)0);
        il.Emit(OpCodes.Ldfld, fRotV);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq); // rotV != prev._rotV
        il.Emit(OpCodes.Br_S, doneMouse);

        il.MarkLabel(setTrue);
        il.Emit(OpCodes.Ldc_I4_1);

        il.MarkLabel(doneMouse);
        il.Emit(OpCodes.Stfld, fBitMouse);

        il.Emit(OpCodes.Ldloc_S, result);
        il.Emit(OpCodes.Ret);

        return (BuildSyncDataDelegate)dm.CreateDelegate(typeof(BuildSyncDataDelegate));
    }
}
