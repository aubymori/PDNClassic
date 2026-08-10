using HarmonyLib;
using System.Collections.Generic;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows.Forms;

internal static class OldThemeColorsFix
{
    private const string AeroColorsTypeName = "PaintDotNet.VisualStyling.AeroColors";
    private const string BlueThemeTypeName = "PaintDotNet.VisualStyling.AeroBlueColorTheme";
    private const string LightThemeTypeName = "PaintDotNet.VisualStyling.AeroLightColorTheme";
    private const string ToolStripRendererTypeName = "PaintDotNet.VisualStyling.PdnToolStripRenderer";
    private const string TrackBarStyleBuilderTypeName =
        "PaintDotNet.Controls.PdnTrackBar+TrackBarStyleBuilder";

    private static readonly object sync = new();
    private static bool themeConstructorsPatched;
    private static bool rendererPatched;
    private static bool trackBarStyleBuilderPatched;
    private static PropertyInfo? currentThemeProperty;
    private static MethodInfo? controlLightLightGetter;
    private static MethodInfo? getTrackBarDefaultColorMethod;

    internal static bool EnabledAtStartup => PDNClassicSettingsFix.OldColorsEnabledAtStartup;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        if (!EnabledAtStartup)
        {
            return;
        }

        lock (sync)
        {
            Type? blueThemeType = assembly.GetType(BlueThemeTypeName, throwOnError: false, ignoreCase: false);
            Type? lightThemeType = assembly.GetType(LightThemeTypeName, throwOnError: false, ignoreCase: false);
            if (!themeConstructorsPatched && blueThemeType != null && lightThemeType != null)
            {
                Type aeroColorsType = assembly.GetType(AeroColorsTypeName, throwOnError: true, ignoreCase: false)!;
                currentThemeProperty = aeroColorsType.GetProperty(
                    "CurrentTheme",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingMemberException(aeroColorsType.FullName, "CurrentTheme");
                MethodInfo constructorPostfix = GetPatchMethod(nameof(ThemeConstructorPostfix));
                harmony.Patch(GetParameterlessConstructor(blueThemeType), postfix: new HarmonyMethod(constructorPostfix));
                harmony.Patch(GetParameterlessConstructor(lightThemeType), postfix: new HarmonyMethod(constructorPostfix));
                themeConstructorsPatched = true;
            }

            Type? rendererType = assembly.GetType(ToolStripRendererTypeName, throwOnError: false, ignoreCase: false);
            if (!rendererPatched && rendererType != null)
            {
                MethodInfo renderBackground = GetDeclaredSingleParameterMethod(
                    rendererType,
                    "OnRenderToolStripBackground");
                harmony.Patch(
                    renderBackground,
                    prefix: new HarmonyMethod(GetPatchMethod(nameof(OnRenderToolStripBackgroundPrefix))));
                rendererPatched = true;
            }

            Type? trackBarStyleBuilderType = assembly.GetType(
                TrackBarStyleBuilderTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (!trackBarStyleBuilderPatched && trackBarStyleBuilderType != null)
            {
                PatchTrackBarStyleBuilder(harmony, trackBarStyleBuilderType);
                trackBarStyleBuilderPatched = true;
            }
        }
    }

    internal static bool IsOldPaletteActive()
    {
        if (!EnabledAtStartup || currentThemeProperty?.GetValue(null) is not object theme)
        {
            return false;
        }

        string? typeName = theme.GetType().FullName;
        return typeName == BlueThemeTypeName || typeName == LightThemeTypeName;
    }

    private static ConstructorInfo GetParameterlessConstructor(Type type)
    {
        return type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)
            ?? throw new MissingMethodException(type.FullName, ".ctor()");
    }

    private static MethodInfo GetDeclaredSingleParameterMethod(Type type, string name)
    {
        MethodInfo? result = null;
        foreach (MethodInfo method in type.GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.Name != name || method.GetParameters().Length != 1)
            {
                continue;
            }

            if (result != null)
            {
                throw new AmbiguousMatchException($"{type.FullName}.{name}");
            }

            result = method;
        }

        return result ?? throw new MissingMethodException(type.FullName, name);
    }

    private static MethodInfo GetPatchMethod(string name)
    {
        return typeof(OldThemeColorsFix).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(OldThemeColorsFix).FullName, name);
    }

    private static void PatchTrackBarStyleBuilder(Harmony harmony, Type styleBuilderType)
    {
        controlLightLightGetter = typeof(SystemColors).GetProperty(
            nameof(SystemColors.ControlLightLight),
            BindingFlags.Static | BindingFlags.Public)?.GetMethod
            ?? throw new MissingMethodException(typeof(SystemColors).FullName, "get_ControlLightLight");
        getTrackBarDefaultColorMethod = GetPatchMethod(nameof(GetTrackBarDefaultColor));

        int constructorCount = 0;
        foreach (ConstructorInfo constructor in styleBuilderType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            if (constructor.GetParameters().Length != 1)
            {
                continue;
            }

            harmony.Patch(
                constructor,
                transpiler: new HarmonyMethod(GetPatchMethod(nameof(TrackBarStyleBuilderTranspiler))));
            ++constructorCount;
        }

        if (constructorCount != 2)
        {
            throw new InvalidOperationException(
                $"Expected two {styleBuilderType.FullName} constructors, found {constructorCount}.");
        }
    }

    private static Color GetTrackBarDefaultColor()
    {
        return IsOldPaletteActive() ? SystemColors.Control : SystemColors.ControlLightLight;
    }

    private static IEnumerable<CodeInstruction> TrackBarStyleBuilderTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo currentGetter = controlLightLightGetter
            ?? throw new InvalidOperationException("SystemColors.ControlLightLight is unavailable.");
        MethodInfo replacement = getTrackBarDefaultColorMethod
            ?? throw new InvalidOperationException("Trackbar default color method is unavailable.");
        int replacementCount = 0;

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(currentGetter))
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

        if (replacementCount != 2)
        {
            throw new InvalidOperationException(
                $"Expected two default trackbar color calls, found {replacementCount}.");
        }
    }

    private static void ThemeConstructorPostfix(object __instance)
    {
        bool isBlue = __instance.GetType().FullName == BlueThemeTypeName;
        SetColor(__instance, "BorderOuterColor", 255, 151, 151, 151);
        SetColor(__instance, "BorderInnerColor", 255, 245, 245, 245);
        SetColor(__instance, "MenuSeparatorColor", 255, 224, 224, 224);
        SetColor(__instance, "ImageMarginBackgroundColor", Color.White);
        SetColor(__instance, "ImageMarginSeparatorColor", 255, 226, 227, 227);
        SetColor(__instance, "StatusBackFillColor", isBlue
            ? Color.FromArgb(255, 220, 231, 245)
            : Color.White);
        SetColor(__instance, "StatusBorderColor1", 255, 159, 174, 194);
        SetColor(__instance, "StatusBorderColor2", Color.White);
        SetColor(__instance, "OverflowArrowColor", 255, 160, 160, 160);
        SetColor(__instance, "SeparatorTopColor", 255, 174, 191, 211);
        SetColor(__instance, "SeparatorBottomColor", 255, 165, 184, 208);
        SetColor(__instance, "SplitButtonArrowColor", Color.Black);
        SetColor(__instance, "MenuItemBackFillColor", isBlue
            ? Color.FromArgb(255, 251, 253, 255)
            : Color.White);
        SetColor(__instance, "MenuFillColor", isBlue
            ? Color.FromArgb(255, 251, 253, 255)
            : Color.White);
        SetColor(__instance, "MenuTextColor", Color.Black);
        SetColor(__instance, "ToolBarBackFillGradTopColor", isBlue
            ? Color.FromArgb(255, 251, 253, 255)
            : Color.White);
        SetColor(__instance, "ToolBarBackFillGradMidColor", isBlue
            ? Color.FromArgb(255, 220, 231, 245)
            : Color.White);
        SetColor(__instance, "ToolBarBackFillGradBottomColor", isBlue
            ? Color.FromArgb(255, 220, 231, 245)
            : Color.White);
        SetColor(__instance, "ToolBarOutlineColor", isBlue
            ? Color.FromArgb(255, 54, 93, 144)
            : Color.FromArgb(217, 217, 217, 217));
        SetColor(__instance, "ToolBarButtonTextColor", Color.Black);
        SetColor(__instance, "ToolStripArrowColor", Color.Black);
        SetColor(__instance, "CanvasBackFillColor", 255, 207, 207, 207);
        SetColor(__instance, "DropShadowColor", Color.Black);
        SetColor(__instance, "SeparatorLineLineColor", 255, 213, 223, 229);
        SetColor(__instance, "RulerLineColor", 255, 130, 144, 163);
        SetColor(__instance, "RulerTextColor", 255, 56, 68, 84);
        SetColor(__instance, "FormBackColor", Color.White);
        SetColor(__instance, "FormTextColor", Color.Black);
        SetColor(__instance, "ContentBackColor", Color.White);
        SetColor(__instance, "SliderBackColor", Color.White);
    }

    private static void SetColor(object theme, string propertyName, int alpha, int red, int green, int blue)
    {
        SetColor(theme, propertyName, Color.FromArgb(alpha, red, green, blue));
    }

    private static void SetColor(object theme, string propertyName, Color color)
    {
        PropertyInfo property = theme.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(theme.GetType().FullName, propertyName);
        property.SetValue(theme, color);
    }

    private static bool OnRenderToolStripBackgroundPrefix(object __0)
    {
        Type eventArgsType = __0.GetType();
        ToolStrip? toolStrip = eventArgsType.GetProperty("ToolStrip")?.GetValue(__0) as ToolStrip;
        if (!IsOldPaletteActive() || !StatusBarFix.IsAeroTheme() || toolStrip is not StatusStrip)
        {
            return true;
        }

        Graphics graphics = eventArgsType.GetProperty("Graphics")?.GetValue(__0) as Graphics
            ?? throw new MissingMemberException(eventArgsType.FullName, "Graphics");
        object? boundsValue = eventArgsType.GetProperty("AffectedBounds")?.GetValue(__0);
        if (boundsValue is not Rectangle bounds)
        {
            throw new MissingMemberException(eventArgsType.FullName, "AffectedBounds");
        }

        bool isBlue = currentThemeProperty!.GetValue(null)!.GetType().FullName == BlueThemeTypeName;
        Color topColor = isBlue ? Color.FromArgb(255, 251, 253, 255) : Color.White;
        Color bottomColor = isBlue ? Color.FromArgb(255, 220, 231, 245) : Color.White;
        if (bounds.Height > 1)
        {
            ++bounds.Y;
            --bounds.Height;
        }

        using LinearGradientBrush brush = new(bounds, topColor, bottomColor, LinearGradientMode.Vertical);
        graphics.FillRectangle(brush, bounds);
        return false;
    }
}
