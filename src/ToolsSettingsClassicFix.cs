using HarmonyLib;
using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

internal static class ToolsSettingsClassicFix
{
    private const string ToolConfigRowTypeName =
        "PaintDotNet.Settings.UI.ToolsSettingsPage+ToolConfigRow";

    private static bool patched;
    private static FieldInfo? toolConfigStripField;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        lock (typeof(ToolsSettingsClassicFix))
        {
            if (patched)
            {
                return;
            }

            Type? rowType = assembly.GetType(
                ToolConfigRowTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (rowType is null)
            {
                return;
            }

            toolConfigStripField = rowType.GetField(
                "toolConfigStrip",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(rowType.FullName, "toolConfigStrip");

            ConstructorInfo? constructor = null;
            foreach (ConstructorInfo candidate in rowType.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (candidate.GetParameters().Length != 2)
                {
                    continue;
                }

                if (constructor is not null)
                {
                    throw new AmbiguousMatchException($"Multiple two-parameter constructors found on {rowType.FullName}.");
                }

                constructor = candidate;
            }

            if (constructor is null)
            {
                throw new MissingMethodException(rowType.FullName, ".ctor(..., ...)");
            }
            MethodInfo postfix = typeof(ToolsSettingsClassicFix).GetMethod(
                nameof(ToolConfigRowConstructorPostfix),
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(
                    typeof(ToolsSettingsClassicFix).FullName,
                    nameof(ToolConfigRowConstructorPostfix));

            harmony.Patch(constructor, postfix: new HarmonyMethod(postfix));
            patched = true;
        }
    }

    private static void ToolConfigRowConstructorPostfix(object __instance)
    {
        if (StatusBarFix.IsAeroTheme())
        {
            return;
        }

        if (toolConfigStripField?.GetValue(__instance) is not ToolStrip toolConfigStrip)
        {
            throw new InvalidOperationException("ToolsSettingsPage.ToolConfigRow.toolConfigStrip is unavailable.");
        }

        // Paint.NET 4.1.5 never assigns BackColor here. Clearing the explicit
        // yellow value restores ToolStripProfessionalRenderer's system gradient.
        toolConfigStrip.BackColor = Color.Empty;
    }
}
