using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows.Forms;

internal static class OldToolWindowPositioningFix
{
    private const string FloatingToolFormTypeName = "PaintDotNet.Dialogs.FloatingToolForm";
    private const string AppWorkspaceTypeName = "PaintDotNet.Controls.AppWorkspace";
    private const int LegacySnapDistance = 3;

    private static readonly object sync = new();
    private static bool floatingToolFormPatched;
    private static bool appWorkspacePatched;
    private static MethodInfo? highContrastGetter;
    private static MethodInfo? useLegacyWorkspaceBoundsMethod;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        if (!PDNClassicSettingsFix.OldToolWindowPositioningEnabledAtStartup)
        {
            return;
        }

        lock (sync)
        {
            Type? floatingToolFormType = assembly.GetType(
                FloatingToolFormTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (!floatingToolFormPatched && floatingToolFormType != null)
            {
                PatchFloatingToolForm(harmony, floatingToolFormType);
                floatingToolFormPatched = true;
            }

            Type? appWorkspaceType = assembly.GetType(
                AppWorkspaceTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (!appWorkspacePatched && appWorkspaceType != null)
            {
                PatchAppWorkspace(harmony, appWorkspaceType);
                appWorkspacePatched = true;
            }
        }
    }

    private static void PatchFloatingToolForm(Harmony harmony, Type floatingToolFormType)
    {
        MethodInfo getSnapDistance = floatingToolFormType.GetMethod(
            "GetSnapDistance",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(floatingToolFormType.FullName, "GetSnapDistance");
        harmony.Patch(
            getSnapDistance,
            prefix: new HarmonyMethod(GetPatchMethod(nameof(GetSnapDistancePrefix))));
    }

    private static void PatchAppWorkspace(Harmony harmony, Type appWorkspaceType)
    {
        MethodInfo updateSnapObstacle = appWorkspaceType.GetMethod(
            "UpdateSnapObstacle",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(appWorkspaceType.FullName, "UpdateSnapObstacle");
        highContrastGetter = typeof(SystemInformation).GetProperty(
            nameof(SystemInformation.HighContrast),
            BindingFlags.Static | BindingFlags.Public)?.GetMethod
            ?? throw new MissingMethodException(typeof(SystemInformation).FullName, "get_HighContrast");
        useLegacyWorkspaceBoundsMethod = GetPatchMethod(nameof(UseLegacyWorkspaceBounds));
        harmony.Patch(
            updateSnapObstacle,
            transpiler: new HarmonyMethod(GetPatchMethod(nameof(UpdateSnapObstacleTranspiler))));
    }

    private static MethodInfo GetPatchMethod(string name)
    {
        return typeof(OldToolWindowPositioningFix).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(OldToolWindowPositioningFix).FullName, name);
    }

    private static bool GetSnapDistancePrefix(ref int __result)
    {
        __result = LegacySnapDistance;
        return false;
    }

    private static bool UseLegacyWorkspaceBounds()
    {
        return true;
    }

    private static IEnumerable<CodeInstruction> UpdateSnapObstacleTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo currentHighContrastGetter = highContrastGetter
            ?? throw new InvalidOperationException("SystemInformation.HighContrast is unavailable.");
        MethodInfo replacement = useLegacyWorkspaceBoundsMethod
            ?? throw new InvalidOperationException("Legacy workspace bounds method is unavailable.");
        int replacementCount = 0;

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(currentHighContrastGetter))
            {
                ++replacementCount;
                yield return new CodeInstruction(OpCodes.Call, replacement)
                    .MoveLabelsFrom(instruction)
                    .MoveBlocksFrom(instruction);
            }
            else
            {
                yield return instruction;
            }
        }

        if (replacementCount != 1)
        {
            throw new InvalidOperationException(
                $"Expected one AppWorkspace.UpdateSnapObstacle HighContrast check, found {replacementCount}.");
        }
    }
}
