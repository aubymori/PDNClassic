using HarmonyLib;
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;

internal static class AeroBasicThemeFix
{
    private const string ThemeConfigTypeName = "PaintDotNet.VisualStyling.ThemeConfig";

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

            Type? themeConfigType = assembly.GetType(ThemeConfigTypeName, throwOnError: false, ignoreCase: false);
            if (themeConfigType == null)
            {
                return;
            }

            MethodInfo determineAutoTheme = themeConfigType.GetMethod(
                "DetermineAutoTheme",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException(themeConfigType.FullName, "DetermineAutoTheme");
            MethodInfo prefixFactory = typeof(AeroBasicThemeFix).GetMethod(
                nameof(DetermineAutoThemePrefixFactory),
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(MethodBase) },
                modifiers: null)
                ?? throw new MissingMethodException(typeof(AeroBasicThemeFix).FullName, nameof(DetermineAutoThemePrefixFactory));
            harmony.Patch(determineAutoTheme, prefix: new HarmonyMethod(prefixFactory));
            patched = true;
        }
    }

    private static MethodInfo DetermineAutoThemePrefixFactory(MethodBase original)
    {
        Type themeType = (original as MethodInfo)?.ReturnType
            ?? throw new ArgumentException("DetermineAutoTheme must be a method.", nameof(original));
        if (!themeType.IsEnum || Enum.GetUnderlyingType(themeType) != typeof(int))
        {
            throw new InvalidOperationException("ThemeConfig.DetermineAutoTheme does not return an Int32-backed enum.");
        }

        int aeroThemeValue = Convert.ToInt32(Enum.Parse(themeType, "Aero", ignoreCase: false));
        return CreateDetermineAutoThemePrefix(themeType, aeroThemeValue);
    }

    private static MethodInfo CreateDetermineAutoThemePrefix(Type themeType, int aeroThemeValue)
    {
        DynamicMethod prefix = new(
            "PDNClassic_DetermineAeroBasicTheme",
            typeof(bool),
            new[] { themeType.MakeByRefType() },
            typeof(AeroBasicThemeFix).Module,
            skipVisibility: true);
        prefix.DefineParameter(1, ParameterAttributes.None, "__result");
        MethodInfo shouldUseAeroBasicTheme = typeof(AeroBasicThemeFix).GetMethod(
            nameof(ShouldUseAeroBasicTheme),
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null)
            ?? throw new MissingMethodException(typeof(AeroBasicThemeFix).FullName, nameof(ShouldUseAeroBasicTheme));

        ILGenerator il = prefix.GetILGenerator();
        System.Reflection.Emit.Label runOriginal = il.DefineLabel();
        il.Emit(OpCodes.Call, shouldUseAeroBasicTheme);
        il.Emit(OpCodes.Brfalse, runOriginal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, aeroThemeValue);
        il.Emit(OpCodes.Stind_I4);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(runOriginal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        return prefix;
    }

    private static bool ShouldUseAeroBasicTheme()
    {
        return DwmIsCompositionEnabled(out bool compositionEnabled) >= 0 &&
            ShouldUseAeroBasicTheme(
                SystemInformation.HighContrast,
                VisualStyleInformation.IsSupportedByOS,
                VisualStyleInformation.IsEnabledByUser,
                compositionEnabled);
    }

    internal static bool ShouldUseAeroBasicTheme(
        bool highContrast,
        bool visualStylesSupported,
        bool visualStylesEnabled,
        bool compositionEnabled)
    {
        return !highContrast &&
            visualStylesSupported &&
            visualStylesEnabled &&
            !compositionEnabled;
    }

    internal static bool IsAeroBasicThemeActive()
    {
        return ShouldUseAeroBasicTheme();
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
}
