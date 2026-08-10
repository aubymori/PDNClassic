using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows.Forms;

internal static class AeroBasicBackgroundFix
{
    private const string ToolBarTypeName = "PaintDotNet.Controls.PdnToolBar";
    private const string AeroColorsTypeName = "PaintDotNet.VisualStyling.AeroColors";

    private static readonly object sync = new();
    private static bool patched;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        lock (sync)
        {
            if (patched || !PDNClassicSettingsFix.AeroGlassEnabledAtStartup)
            {
                return;
            }

            Type? toolBarType = assembly.GetType(ToolBarTypeName, throwOnError: false, ignoreCase: false);
            if (toolBarType == null)
            {
                return;
            }

            Type? aeroColorsType = FindLoadedType(AeroColorsTypeName);
            MethodInfo formBackColorGetter = aeroColorsType?.GetProperty(
                "FormBackColor",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetMethod
                ?? throw new MissingMethodException(AeroColorsTypeName, "get_FormBackColor");
            MethodInfo paintBackground = FindPaintBackground(toolBarType);
            MethodInfo transpiler = typeof(AeroBasicBackgroundFix).GetMethod(
                nameof(PaintBackgroundTranspiler),
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(AeroBasicBackgroundFix).FullName, nameof(PaintBackgroundTranspiler));

            harmony.Patch(paintBackground, transpiler: new HarmonyMethod(transpiler));
            patched = true;
        }
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static MethodInfo FindPaintBackground(Type toolBarType)
    {
        foreach (MethodInfo method in toolBarType.GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (method.Name == "PaintBackground" &&
                parameters.Length == 2 &&
                parameters[0].ParameterType == typeof(Graphics) &&
                parameters[1].ParameterType == typeof(Rectangle))
            {
                return method;
            }
        }

        throw new MissingMethodException(toolBarType.FullName, "PaintBackground(Graphics, Rectangle)");
    }

    private static IEnumerable<CodeInstruction> PaintBackgroundTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo getBasicNonCaptionColor = typeof(AeroBasicBackgroundFix).GetMethod(
            nameof(GetBasicNonCaptionColor),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(AeroBasicBackgroundFix).FullName, nameof(GetBasicNonCaptionColor));
        bool replaced = false;

        foreach (CodeInstruction instruction in instructions)
        {
            if (!replaced &&
                instruction.operand is MethodInfo method &&
                method.Name == "get_FormBackColor" &&
                method.DeclaringType?.FullName == AeroColorsTypeName)
            {
                CodeInstruction loadInstance = new(OpCodes.Ldarg_0);
                loadInstance.labels.AddRange(instruction.labels);
                loadInstance.blocks.AddRange(instruction.blocks);
                instruction.labels.Clear();
                instruction.blocks.Clear();
                yield return loadInstance;
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Call, getBasicNonCaptionColor);
                replaced = true;
                continue;
            }

            yield return instruction;
        }

        if (!replaced)
        {
            throw new MissingMethodException("PdnToolBar.PaintBackground", "AeroColors.FormBackColor");
        }
    }

    private static Color GetBasicNonCaptionColor(object toolBar, Color defaultColor)
    {
        if (!AeroBasicThemeFix.IsAeroBasicThemeActive() || toolBar is not Control control)
        {
            return defaultColor;
        }

        Form? form = control.FindForm();
        return SelectNonCaptionColor(isBasicTheme: true, form != null && GetActiveWindow() == form.Handle, defaultColor);
    }

    internal static Color SelectNonCaptionColor(bool isBasicTheme, bool isActive, Color defaultColor)
    {
        if (!isBasicTheme)
        {
            return defaultColor;
        }

        return isActive ? SystemColors.GradientActiveCaption : SystemColors.GradientInactiveCaption;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetActiveWindow();
}
