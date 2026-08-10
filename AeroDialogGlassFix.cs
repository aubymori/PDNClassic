using HarmonyLib;
using System.Collections.Generic;
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows.Forms;

internal static class AeroDialogGlassFix
{
    private const string PdnBaseFormTypeName = "PaintDotNet.PdnBaseForm";
    private const string EffectConfigFormTypeName = "PaintDotNet.Effects.EffectConfigForm";
    private const string SettingsDialogTypeName = "PaintDotNet.Settings.UI.SettingsDialog";

    private static bool patched;
    private static bool effectConfigFormPatched;
    private static bool settingsDialogPatched;
    private static PropertyInfo? isGlassDesiredProperty;
    private static PropertyInfo? glassInsetProperty;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        if (!PDNClassicSettingsFix.AeroGlassEnabledAtStartup)
        {
            return;
        }

        lock (typeof(AeroDialogGlassFix))
        {
            Type? baseFormType = assembly.GetType(PdnBaseFormTypeName, throwOnError: false, ignoreCase: false);
            if (baseFormType != null && !patched)
            {
                isGlassDesiredProperty = baseFormType.GetProperty(
                    "IsGlassDesired",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMemberException(baseFormType.FullName, "IsGlassDesired");
                glassInsetProperty = baseFormType.GetProperty(
                    "GlassInset",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMemberException(baseFormType.FullName, "GlassInset");

                MethodInfo onShown = baseFormType.GetMethod(
                    "OnShown",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: new[] { typeof(EventArgs) },
                    modifiers: null)
                    ?? throw new MissingMethodException(baseFormType.FullName, "OnShown(EventArgs)");
                MethodInfo onLayout = baseFormType.GetMethod(
                    "OnLayout",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: new[] { typeof(LayoutEventArgs) },
                    modifiers: null)
                    ?? throw new MissingMethodException(baseFormType.FullName, "OnLayout(LayoutEventArgs)");
                MethodInfo glassInsetSetter = glassInsetProperty.GetSetMethod(nonPublic: true)
                    ?? throw new MissingMethodException(baseFormType.FullName, "set_GlassInset(Padding)");
                MethodInfo onShownPrefix = GetPatchMethod(nameof(OnShownPrefix));
                MethodInfo onLayoutPostfix = GetPatchMethod(nameof(OnLayoutPostfix));
                harmony.Patch(onShown, prefix: new HarmonyMethod(onShownPrefix));
                harmony.Patch(onLayout, postfix: new HarmonyMethod(onLayoutPostfix));
                harmony.Patch(
                    glassInsetSetter,
                    prefix: new HarmonyMethod(GetPatchMethod(nameof(GlassInsetSetterPrefix))));
                patched = true;
            }

            Type? effectConfigFormType = assembly.GetType(
                EffectConfigFormTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (effectConfigFormType != null && !effectConfigFormPatched)
            {
                MethodInfo onLoad = effectConfigFormType.GetMethod(
                    "OnLoad",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: new[] { typeof(EventArgs) },
                    modifiers: null)
                    ?? throw new MissingMethodException(effectConfigFormType.FullName, "OnLoad(EventArgs)");
                harmony.Patch(onLoad, prefix: new HarmonyMethod(GetPatchMethod(nameof(OnShownPrefix))));
                effectConfigFormPatched = true;
            }

            Type? settingsDialogType = assembly.GetType(
                SettingsDialogTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (settingsDialogType != null && !settingsDialogPatched)
            {
                MethodInfo onShown = settingsDialogType.GetMethod(
                    "OnShown",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: new[] { typeof(EventArgs) },
                    modifiers: null)
                    ?? throw new MissingMethodException(settingsDialogType.FullName, "OnShown(EventArgs)");
                MethodInfo onLayout = settingsDialogType.GetMethod(
                    "OnLayout",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: new[] { typeof(LayoutEventArgs) },
                    modifiers: null)
                    ?? throw new MissingMethodException(settingsDialogType.FullName, "OnLayout(LayoutEventArgs)");
                harmony.Patch(onShown, prefix: new HarmonyMethod(GetPatchMethod(nameof(OnShownPrefix))));
                harmony.Patch(
                    onLayout,
                    transpiler: new HarmonyMethod(GetPatchMethod(nameof(SettingsOnLayoutTranspiler))));
                settingsDialogPatched = true;
            }
        }
    }


    private static MethodInfo GetPatchMethod(string name)
    {
        return typeof(AeroDialogGlassFix).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(AeroDialogGlassFix).FullName, name);
    }

    private static void OnShownPrefix(object __instance)
    {
        if (!ShouldEnableFor(__instance) || __instance is not Control control)
        {
            return;
        }

        isGlassDesiredProperty?.SetValue(__instance, true);
        control.PerformLayout();
        control.Invalidate(invalidateChildren: true);
    }

    private static bool GlassInsetSetterPrefix(object __instance, Padding value)
    {
        if (value != Padding.Empty ||
            !IsInstanceOfType(__instance, SettingsDialogTypeName) ||
            !ShouldEnableFor(__instance) ||
            isGlassDesiredProperty?.GetValue(__instance) is not true)
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<CodeInstruction> SettingsOnLayoutTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo heightSetter = typeof(Control).GetProperty(nameof(Control.Height))?.SetMethod
            ?? throw new MissingMemberException(typeof(Control).FullName, "set_Height");
        MethodInfo correctedHeightSetter = GetPatchMethod(nameof(SetSettingsContentHeight));
        int replacementCount = 0;

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(heightSetter))
            {
                CodeInstruction replacement = new(OpCodes.Call, correctedHeightSetter);
                replacement.labels.AddRange(instruction.labels);
                replacement.blocks.AddRange(instruction.blocks);
                instruction.labels.Clear();
                instruction.blocks.Clear();
                yield return replacement;
                ++replacementCount;
            }
            else
            {
                yield return instruction;
            }
        }

        if (replacementCount != 2)
        {
            throw new InvalidOperationException(
                $"Expected 2 SettingsDialog.OnLayout Height setters, found {replacementCount}.");
        }
    }

    private static void SetSettingsContentHeight(Control control, int height)
    {
        Form? form = control.FindForm();
        if (form != null &&
            IsInstanceOfType(form, SettingsDialogTypeName) &&
            ShouldEnableFor(form) &&
            FindProperty(form.GetType(), "IsGlassEffectivelyEnabled")?.GetValue(form) is true)
        {
            int gap = Math.Max(1, (7 * form.DeviceDpi + 48) / 96);
            height = Math.Max(0, height - gap);
        }

        control.Height = height;
    }

    private static void OnLayoutPostfix(object __instance)
    {
        if (!ShouldEnableFor(__instance) || __instance is not Form form)
        {
            return;
        }

        PropertyInfo? effectivelyEnabledProperty = FindProperty(__instance.GetType(), "IsGlassEffectivelyEnabled");
        if (effectivelyEnabledProperty?.GetValue(__instance) is not true)
        {
            return;
        }

        int footerTop;
        if (IsInstanceOfType(form, SettingsDialogTypeName))
        {
            int buttonTop = FindButtonTop(form);
            int footerGap = Math.Max(1, (7 * form.DeviceDpi + 48) / 96);
            footerTop = buttonTop < 0 ? -1 : Math.Max(0, buttonTop - footerGap);
        }
        else
        {
            footerTop = FindFooterTop(form);
        }
        if (footerTop < 0 || footerTop >= form.ClientSize.Height)
        {
            return;
        }

        Padding inset = new(0, 0, 0, form.ClientSize.Height - footerTop);
        glassInsetProperty?.SetValue(__instance, inset);
        form.SizeGripStyle = SizeGripStyle.Hide;
    }

    private static int FindFooterTop(Form form)
    {
        Control? separator = form.Controls
            .Cast<Control>()
            .Where(control => control.GetType().Name.Contains("Separator", StringComparison.Ordinal))
            .OrderByDescending(control => control.Top)
            .FirstOrDefault();
        if (separator != null && separator.Height > 0)
        {
            separator.Visible = false;
            return separator.Top;
        }

        int buttonTop = FindButtonTop(form);
        if (buttonTop < 0)
        {
            return -1;
        }

        int footerPadding = Math.Max(1, (8 * form.DeviceDpi + 48) / 96);
        return Math.Max(0, buttonTop - footerPadding);
    }

    private static int FindButtonTop(Form form)
    {
        return form.Controls
            .Cast<Control>()
            .Where(control =>
                control.Visible &&
                (control is ButtonBase ||
                 control.GetType().Name.Contains("Button", StringComparison.Ordinal)))
            .Select(control => control.Top)
            .DefaultIfEmpty(-1)
            .Max();
    }

    private static bool IsInstanceOfType(object instance, string fullName)
    {
        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            if (type.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldEnableFor(object instance)
    {
        if (!PDNClassicSettingsFix.AeroGlassEnabledAtStartup)
        {
            return false;
        }

        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            string? name = type.FullName;
            if (name == "PaintDotNet.Dialogs.AboutDialog" ||
                name == "PaintDotNet.Dialogs.ImageSizeDialog" ||
                name == "PaintDotNet.Dialogs.IndirectUIDialogBase" ||
                name == "PaintDotNet.Dialogs.SaveConfigDialog" ||
                name == "PaintDotNet.Dialogs.TaskProgressDialog" ||
                name == "PaintDotNet.Settings.UI.SettingsDialog" ||
                name == "PaintDotNet.Updates.UpdatesDialog" ||
                name == "PaintDotNet.Effects.EffectConfigDialog" ||
                name == "PaintDotNet.Effects.EffectConfigForm")
            {
                return true;
            }
        }

        return false;
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
        {
            PropertyInfo? property = current.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null)
            {
                return property;
            }
        }

        return null;
    }
}
