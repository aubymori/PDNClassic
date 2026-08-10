using HarmonyLib;
using PaintDotNet.DirectWrite;
using System;
using System.Reflection;
using System.Windows.Forms;

internal static class GdiClassicTextRenderingFix
{
    private const string DrawingContextTypeName = "PaintDotNet.Direct2D1.DrawingContext";

    private static readonly object sync = new();
    private static bool patched;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        if (!PDNClassicSettingsFix.GdiClassicFontRenderingEnabledAtStartup)
        {
            return;
        }

        lock (sync)
        {
            if (patched)
            {
                return;
            }

            Type? drawingContextType = assembly.GetType(
                DrawingContextTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (drawingContextType == null)
            {
                return;
            }

            MethodInfo getter = drawingContextType.GetMethod(
                "get_DefaultTextRenderingMode",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException(
                    drawingContextType.FullName,
                    "get_DefaultTextRenderingMode");
            MethodInfo postfix = typeof(GdiClassicTextRenderingFix).GetMethod(
                nameof(GetDefaultTextRenderingModePostfix),
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(
                    typeof(GdiClassicTextRenderingFix).FullName,
                    nameof(GetDefaultTextRenderingModePostfix));

            harmony.Patch(getter, postfix: new HarmonyMethod(postfix));
            patched = true;
        }
    }

    private static void GetDefaultTextRenderingModePostfix(ref TextRenderingMode __result)
    {
        __result = SelectRenderingMode(__result, SystemInformation.IsFontSmoothingEnabled);
    }

    internal static TextRenderingMode SelectRenderingMode(
        TextRenderingMode currentMode,
        bool isFontSmoothingEnabled)
    {
        if (!isFontSmoothingEnabled || currentMode == TextRenderingMode.Aliased)
        {
            return currentMode;
        }

        return TextRenderingMode.GdiClassic;
    }
}
