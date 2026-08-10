using HarmonyLib;
using System.Collections.Generic;
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class AeroDialogGlassFix
{
    private const string PdnBaseFormTypeName = "PaintDotNet.PdnBaseForm";
    private const string EffectConfigFormTypeName = "PaintDotNet.Effects.EffectConfigForm";
    private const string SettingsDialogTypeName = "PaintDotNet.Settings.UI.SettingsDialog";
    private const string GdiBufferedAnimationControlTypeName =
        "PaintDotNet.Gdi.GdiBufferedAnimationControl";
    private const uint BlacknessRasterOperation = 0x00000042;

    private static bool patched;
    private static bool effectConfigFormPatched;
    private static bool settingsDialogPatched;
    private static bool gdiBufferedAnimationControlPatched;
    private static PropertyInfo? isGlassDesiredProperty;
    private static PropertyInfo? glassInsetProperty;
    private static PropertyInfo? autoHandleGlassRelatedOptimizationsProperty;
    private static readonly ConditionalWeakTable<Control, object> glassFooterControls = new();
    private static readonly object glassFooterControlMarker = new();

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
                autoHandleGlassRelatedOptimizationsProperty = baseFormType.GetProperty(
                    "AutoHandleGlassRelatedOptimizations",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingMemberException(
                        baseFormType.FullName,
                        "AutoHandleGlassRelatedOptimizations");

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

            Type? animationControlType = assembly.GetType(
                GdiBufferedAnimationControlTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (animationControlType != null && !gdiBufferedAnimationControlPatched)
            {
                MethodInfo paintCachedFrame = animationControlType.GetMethod(
                    "PaintCachedFrame",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(
                        animationControlType.FullName,
                        "PaintCachedFrame");
                harmony.Patch(
                    paintCachedFrame,
                    transpiler: new HarmonyMethod(
                        GetPatchMethod(nameof(PaintCachedFrameTranspiler))));
                gdiBufferedAnimationControlPatched = true;
            }
        }
    }


    private static MethodInfo GetPatchMethod(string name)
    {
        return typeof(AeroDialogGlassFix).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(AeroDialogGlassFix).FullName, name);
    }


    private static IEnumerable<CodeInstruction> PaintCachedFrameTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        List<CodeInstruction> instructionList = instructions.ToList();
        LocalVariableInfo hdcBitmapLocal = __originalMethod.GetMethodBody()?.LocalVariables
            .SingleOrDefault(local =>
                local.LocalType.FullName == "TerraFX.Interop.Windows.HDC")
            ?? throw new InvalidOperationException(
                $"{__originalMethod.FullDescription()} has no unique HDC local.");
        MethodInfo clearCachedFrame = GetPatchMethod(nameof(ClearCachedFrame))
            .MakeGenericMethod(hdcBitmapLocal.LocalType);
        bool inserted = false;

        foreach (CodeInstruction instruction in instructionList)
        {
            if (!inserted &&
                instruction.opcode == OpCodes.Newobj &&
                instruction.operand is ConstructorInfo constructor &&
                constructor.DeclaringType?.FullName ==
                    "PaintDotNet.Gdi.GdiPaintContext")
            {
                CodeInstruction loadHdc =
                    CodeInstruction.LoadLocal(hdcBitmapLocal.LocalIndex);
                loadHdc.labels.AddRange(instruction.labels);
                loadHdc.blocks.AddRange(instruction.blocks);
                instruction.labels.Clear();
                instruction.blocks.Clear();

                yield return loadHdc;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, clearCachedFrame);
                inserted = true;
            }

            yield return instruction;
        }

        if (!inserted)
        {
            throw new InvalidOperationException(
                $"Could not locate GdiPaintContext construction in " +
                $"{__originalMethod.FullDescription()}.");
        }
    }

    private static void ClearCachedFrame<THdc>(THdc hdc, object instance)
        where THdc : unmanaged
    {
        if (instance is not Control control ||
            !glassFooterControls.TryGetValue(control, out _) ||
            control.Parent == null ||
            control.BackColor.A == byte.MaxValue)
        {
            return;
        }

        nint hdcValue = Unsafe.As<THdc, nint>(ref hdc);
        _ = PatBlt(
            hdcValue,
            0,
            0,
            control.ClientSize.Width,
            control.ClientSize.Height,
            BlacknessRasterOperation);
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PatBlt(
        nint hdc,
        int x,
        int y,
        int width,
        int height,
        uint rasterOperation);

    private static void OnShownPrefix(object __instance)
    {
        if (!ShouldEnableFor(__instance) || __instance is not Control control)
        {
            return;
        }

        isGlassDesiredProperty?.SetValue(__instance, true);
        autoHandleGlassRelatedOptimizationsProperty?.SetValue(__instance, true);
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
        NotifyFooterControlsOnGlass(form, footerTop);
        form.SizeGripStyle = SizeGripStyle.Hide;
    }

    internal static void NotifyFooterControlsOnGlass(Form form, int footerTop)
    {
        Rectangle footerBounds = Rectangle.FromLTRB(
            0,
            footerTop,
            form.ClientSize.Width,
            form.ClientSize.Height);
        foreach (Control control in form.Controls)
        {
            glassFooterControls.Remove(control);
            if (!control.Visible)
            {
                continue;
            }

            Rectangle controlBoundsScreen = control.RectangleToScreen(
                new Rectangle(Point.Empty, control.Size));
            Rectangle controlBounds = form.RectangleToClient(controlBoundsScreen);
            if (!controlBounds.IntersectsWith(footerBounds))
            {
                continue;
            }

            Type? glassNotifyType = control.GetType().GetInterface(
                "PaintDotNet.Controls.IGlassNotify",
                ignoreCase: false);
            MethodInfo? notifyGlassSetting = glassNotifyType?.GetMethod(
                "NotifyGlassSetting",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (notifyGlassSetting != null)
            {
                notifyGlassSetting.Invoke(control, new object[] { true });
                glassFooterControls.Add(control, glassFooterControlMarker);
            }
        }
    }

    private static int FindFooterTop(Form form)
    {
        Control? separator = form.Controls
            .Cast<Control>()
            .Where(control => control.GetType().Name.Contains("Separator", StringComparison.Ordinal))
            .OrderByDescending(control => control.Top)
            .FirstOrDefault();
        int separatorTop = -1;
        if (separator != null && separator.Height > 0)
        {
            separator.Visible = false;
            separatorTop = separator.Top;
        }

        int buttonTop = FindButtonTop(form);
        int buttonGlassTop = buttonTop < 0
            ? -1
            : Math.Max(
                0,
                buttonTop - Math.Max(1, (8 * form.DeviceDpi + 48) / 96));

        if (separatorTop < 0)
        {
            return buttonGlassTop;
        }

        if (buttonGlassTop < 0)
        {
            return separatorTop;
        }

        // Some Paint.NET 5 dialogs position their legacy separator through
        // the upper half of the footer buttons. The entire button must be
        // inside the glass inset, as it was in Paint.NET 4.1.
        return Math.Min(separatorTop, buttonGlassTop);
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
