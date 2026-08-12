using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

internal static class MetroIconsFix
{
    private const string ImageStripHelpersTypeName =
        "PaintDotNet.Controls.ImageStripHelpers";
    private const string AnimatedResourcesTypeName =
        "PaintDotNet.Resources.AnimatedResources";
    private const string UIImageResourceTypeName =
        "PaintDotNet.Resources.UIImageResource";

    private static bool closeButtonsPatched;
    private static bool busySpinnerPatched;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        lock (typeof(MetroIconsFix))
        {
            Type? helpersType = assembly.GetType(
                ImageStripHelpersTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (helpersType != null && !closeButtonsPatched)
            {
                MethodInfo getCloseButtonImageResource = helpersType.GetMethod(
                    "GetCloseButtonImageResource",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(
                        helpersType.FullName,
                        "GetCloseButtonImageResource");
                harmony.Patch(
                    getCloseButtonImageResource,
                    transpiler: new HarmonyMethod(
                        GetPatchMethod(nameof(CloseButtonResourceNameTranspiler))));
                closeButtonsPatched = true;
            }

            Type? animatedResourcesType = assembly.GetType(
                AnimatedResourcesTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (animatedResourcesType != null && !busySpinnerPatched)
            {
                MethodInfo createBusySpinnerImages = animatedResourcesType.GetMethod(
                    "CreateBusySpinnerImages",
                    BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(
                        animatedResourcesType.FullName,
                        "CreateBusySpinnerImages");
                harmony.Patch(
                    createBusySpinnerImages,
                    transpiler: new HarmonyMethod(
                        GetPatchMethod(nameof(BusySpinnerResourceNameTranspiler))));
                busySpinnerPatched = true;
            }
        }
    }

    private static IEnumerable<CodeInstruction> CloseButtonResourceNameTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        return ResourceNameTranspiler(
            instructions,
            original,
            GetPatchMethod(nameof(SelectCloseButtonResourceName)));
    }

    private static IEnumerable<CodeInstruction> BusySpinnerResourceNameTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        return ResourceNameTranspiler(
            instructions,
            original,
            GetPatchMethod(nameof(SelectBusySpinnerResourceName)));
    }

    private static IEnumerable<CodeInstruction> ResourceNameTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original,
        MethodInfo selectResourceName)
    {
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

    private static string SelectCloseButtonResourceName(string baseResourceName)
    {
        if (!StatusBarFix.IsAeroTheme())
        {
            return baseResourceName + ".Classic";
        }

        return PDNClassicSettingsFix.MetroIconsEnabledAtStartup
            ? baseResourceName + ".Metro"
            : baseResourceName;
    }

    private static string SelectBusySpinnerResourceName(string baseResourceName)
    {
        return PDNClassicSettingsFix.MetroIconsEnabledAtStartup
            ? baseResourceName + ".Metro"
            : baseResourceName;
    }

    private static MethodInfo GetPatchMethod(string name)
    {
        return typeof(MetroIconsFix).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MetroIconsFix).FullName, name);
    }
}
