using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

internal static class ImageStripCloseButtonFix
{
    private const string ImageStripHelpersTypeName =
        "PaintDotNet.Controls.ImageStripHelpers";
    private const string UIImageResourceTypeName =
        "PaintDotNet.Resources.UIImageResource";

    private static bool patched;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        lock (typeof(ImageStripCloseButtonFix))
        {
            if (patched)
            {
                return;
            }

            Type? helpersType = assembly.GetType(
                ImageStripHelpersTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (helpersType is null)
            {
                return;
            }

            MethodInfo getCloseButtonImageResource = helpersType.GetMethod(
                "GetCloseButtonImageResource",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(
                    helpersType.FullName,
                    "GetCloseButtonImageResource");
            harmony.Patch(
                getCloseButtonImageResource,
                transpiler: new HarmonyMethod(GetPatchMethod(nameof(ResourceNameTranspiler))));
            patched = true;
        }
    }

    private static IEnumerable<CodeInstruction> ResourceNameTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        MethodInfo selectResourceName = GetPatchMethod(nameof(SelectResourceName));
        bool patchedCall = false;
        foreach (CodeInstruction instruction in instructions)
        {
            if (!patchedCall &&
                instruction.opcode == OpCodes.Call &&
                instruction.operand is MethodInfo method &&
                method.Name == "Get" &&
                method.DeclaringType?.FullName == UIImageResourceTypeName &&
                method.GetParameters().Length == 1 &&
                method.GetParameters()[0].ParameterType == typeof(string))
            {
                yield return new CodeInstruction(OpCodes.Call, selectResourceName);
                patchedCall = true;
            }

            yield return instruction;
        }

        if (!patchedCall)
        {
            throw new MissingMethodException(
                original.DeclaringType?.FullName,
                "UIImageResource.Get(string) call");
        }
    }

    private static string SelectResourceName(string baseResourceName)
    {
        if (!StatusBarFix.IsAeroTheme())
        {
            return baseResourceName + ".Classic";
        }

        if (PDNClassicSettingsFix.MetroCloseButtonsEnabledAtStartup)
        {
            return baseResourceName + ".Metro";
        }

        return baseResourceName;
    }

    private static MethodInfo GetPatchMethod(string name)
    {
        return typeof(ImageStripCloseButtonFix).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ImageStripCloseButtonFix).FullName, name);
    }
}
