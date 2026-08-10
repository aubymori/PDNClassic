using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

internal static class OldIconAccommodationsFix
{
    private const string FontStyleButtonGroupTypeName =
        "PaintDotNet.Controls.ToolConfigUI.FontStyleFlagsButtonGroup";

    private static bool patched;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        if (!PDNClassicSettingsFix.OldIconAccommodationsEnabledAtStartup)
        {
            return;
        }

        lock (typeof(OldIconAccommodationsFix))
        {
            if (patched)
            {
                return;
            }

            Type? buttonGroupType = assembly.GetType(
                FontStyleButtonGroupTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (buttonGroupType is null)
            {
                return;
            }

            MethodInfo getButtonImage = buttonGroupType.GetMethod(
                "GetButtonImage",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null,
                types: new[] { typeof(Enum) },
                modifiers: null)
                ?? throw new MissingMethodException(buttonGroupType.FullName, "GetButtonImage(Enum)");

            harmony.Patch(
                getButtonImage,
                transpiler: new HarmonyMethod(GetPatchMethod(nameof(GetButtonImageTranspiler))));
            patched = true;
        }
    }

    private static IEnumerable<CodeInstruction> GetButtonImageTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        _ = instructions;
        Type baseType = original.DeclaringType?.BaseType
            ?? throw new MissingMemberException(original.DeclaringType?.FullName, "BaseType");
        MethodInfo baseGetButtonImage = baseType.GetMethod(
            "GetButtonImage",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            binder: null,
            types: new[] { typeof(Enum) },
            modifiers: null)
            ?? throw new MissingMethodException(baseType.FullName, "GetButtonImage(Enum)");

        // The base implementation resolves Icons.Enum.FontStyle.{value}, matching
        // the bitmap resource names used by Paint.NET 4.1.5. Bypass 5.1.12's
        // FontStyle-specific DirectWrite renderer.
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Ldarg_1);
        yield return new CodeInstruction(OpCodes.Call, baseGetButtonImage);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static MethodInfo GetPatchMethod(string name)
    {
        return typeof(OldIconAccommodationsFix).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(OldIconAccommodationsFix).FullName, name);
    }
}
