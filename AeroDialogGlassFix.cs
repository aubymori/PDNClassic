using HarmonyLib;
using System.Collections.Generic;
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

internal static class AeroDialogGlassFix
{
    private const string PdnBaseFormTypeName = "PaintDotNet.PdnBaseForm";
    private const string EffectConfigFormTypeName = "PaintDotNet.Effects.EffectConfigForm";
    private const string SettingsDialogTypeName = "PaintDotNet.Settings.UI.SettingsDialog";
    private const string ImageSizeDialogTypeName = "PaintDotNet.Dialogs.ImageSizeDialog";
    private const string SaveConfigDialogTypeName = "PaintDotNet.Dialogs.SaveConfigDialog";
    private const string ColorProfileDialogTypeName = "PaintDotNet.Dialogs.ColorProfileDialog";
    private const string IndirectUIDialogBaseTypeName = "PaintDotNet.Dialogs.IndirectUIDialogBase";
    private const string GdiBufferedAnimationControlTypeName =
        "PaintDotNet.Gdi.GdiBufferedAnimationControl";
    private sealed class ParentPaintInvoker : Control
    {
        internal void InvokeParentPaint(Control parent, PaintEventArgs e)
        {
            InvokePaintBackground(parent, e);
            InvokePaint(parent, e);
        }
    }


    private static bool patched;
    private static bool effectConfigFormPatched;
    private static bool settingsDialogPatched;
    private static bool imageSizeDialogPatched;
    private static bool saveConfigDialogPatched;
    private static bool colorProfileDialogPatched;
    private static bool indirectUIDialogBasePatched;
    private static bool gdiBufferedAnimationControlPatched;
    private static PropertyInfo? isGlassDesiredProperty;
    private static PropertyInfo? glassInsetProperty;
    private static PropertyInfo? autoHandleGlassRelatedOptimizationsProperty;
    private static readonly ConditionalWeakTable<Control, object> glassFooterControls = new();
    private static readonly object glassFooterControlMarker = new();
    private static ParentPaintInvoker? parentPaintInvoker;

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

            Type? imageSizeDialogType = assembly.GetType(
                ImageSizeDialogTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (imageSizeDialogType != null && !imageSizeDialogPatched)
            {
                MethodInfo doLayout = imageSizeDialogType.GetMethod(
                    "DoLayout",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: new[] { typeof(int), typeof(bool) },
                    modifiers: null)
                    ?? throw new MissingMethodException(
                        imageSizeDialogType.FullName,
                        "DoLayout(Int32, Boolean)");
                harmony.Patch(
                    doLayout,
                    postfix: new HarmonyMethod(
                        GetPatchMethod(nameof(ImageSizeDoLayoutPostfix))));
                imageSizeDialogPatched = true;
            }

            Type? saveConfigDialogType = assembly.GetType(
                SaveConfigDialogTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (saveConfigDialogType != null && !saveConfigDialogPatched)
            {
                ConstructorInfo constructor = saveConfigDialogType
                    .GetConstructors(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    .SingleOrDefault()
                    ?? throw new MissingMethodException(
                        saveConfigDialogType.FullName,
                        ".ctor(Document, Surface)");
                harmony.Patch(
                    constructor,
                    postfix: new HarmonyMethod(
                        GetPatchMethod(nameof(EnableGlassConstructorPostfix))));
                saveConfigDialogPatched = true;
            }

            Type? colorProfileDialogType = assembly.GetType(
                ColorProfileDialogTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (colorProfileDialogType != null && !colorProfileDialogPatched)
            {
                ConstructorInfo constructor = colorProfileDialogType
                    .GetConstructors(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    .SingleOrDefault()
                    ?? throw new MissingMethodException(
                        colorProfileDialogType.FullName,
                        ".ctor()");
                MethodInfo onLayout = colorProfileDialogType.GetMethod(
                    "OnLayout",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: new[] { typeof(LayoutEventArgs) },
                    modifiers: null)
                    ?? throw new MissingMethodException(
                        colorProfileDialogType.FullName,
                        "OnLayout(LayoutEventArgs)");
                harmony.Patch(
                    constructor,
                    postfix: new HarmonyMethod(
                        GetPatchMethod(nameof(EnableGlassConstructorPostfix))));
                harmony.Patch(
                    onLayout,
                    postfix: new HarmonyMethod(
                        GetPatchMethod(nameof(ColorProfileOnLayoutPostfix))));
                colorProfileDialogPatched = true;
            }

            Type? indirectUIDialogBaseType = assembly.GetType(
                IndirectUIDialogBaseTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (indirectUIDialogBaseType != null && !indirectUIDialogBasePatched)
            {
                ConstructorInfo constructor = indirectUIDialogBaseType
                    .GetConstructors(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic)
                    .SingleOrDefault()
                    ?? throw new MissingMethodException(
                        indirectUIDialogBaseType.FullName,
                        ".ctor(Object)");
                harmony.Patch(
                    constructor,
                    postfix: new HarmonyMethod(
                        GetPatchMethod(nameof(EnableGlassConstructorPostfix))));
                indirectUIDialogBasePatched = true;
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
        using Graphics graphics = Graphics.FromHdc(hdcValue);
        using PaintEventArgs paintEventArgs = new(graphics, control.Bounds);
        graphics.Clear(Color.Transparent);
        graphics.TranslateTransform(-control.Left, -control.Top);
        (parentPaintInvoker ??= new ParentPaintInvoker()).InvokeParentPaint(control.Parent, paintEventArgs);
    }

    private static void EnableGlassConstructorPostfix(object __instance)
    {
        isGlassDesiredProperty?.SetValue(__instance, true);
        autoHandleGlassRelatedOptimizationsProperty?.SetValue(__instance, true);
    }

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

    private static void ColorProfileOnLayoutPostfix(object __instance)
    {
        if (__instance is not Form form ||
            FindProperty(__instance.GetType(), "IsGlassEffectivelyEnabled")?.GetValue(__instance) is not true)
        {
            return;
        }

        Control[] buttons = form.Controls
            .Cast<Control>()
            .Where(control =>
                control.Visible &&
                control.GetType().Name == "PdnPushButton")
            .ToArray();
        if (buttons.Length == 0)
        {
            return;
        }

        int footerButtonTop = buttons.Max(button => button.Top);
        Control[] footerButtons = buttons
            .Where(button => button.Top == footerButtonTop)
            .ToArray();
        int dx = form.ClientSize.Width - footerButtons.Max(button => button.Right);
        int dy = form.ClientSize.Height - footerButtons.Max(button => button.Bottom);
        foreach (Control button in footerButtons)
        {
            button.Location = new Point(button.Left + dx, button.Top + dy);
        }

        int footerTop = FindFooterTop(form);
        if (footerTop >= 0 && footerTop < form.ClientSize.Height)
        {
            glassInsetProperty?.SetValue(
                __instance,
                new Padding(0, 0, 0, form.ClientSize.Height - footerTop));
            NotifyFooterControlsOnGlass(form, footerTop);
            form.SizeGripStyle = SizeGripStyle.Hide;
        }
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

    private static void ImageSizeDoLayoutPostfix(
        object __instance,
        bool applyLayout,
        ref Size __result)
    {
        if (!applyLayout ||
            __instance is not Form form ||
            !ShouldEnableFor(__instance))
        {
            return;
        }

        PropertyInfo? effectivelyEnabledProperty =
            FindProperty(__instance.GetType(), "IsGlassEffectivelyEnabled");
        if (effectivelyEnabledProperty?.GetValue(__instance) is not true)
        {
            return;
        }

        int buttonBottom = form.Controls
            .Cast<Control>()
            .Where(control =>
                control.Visible &&
                control.GetType().Name == "PdnPushButton")
            .Select(control => control.Bottom)
            .DefaultIfEmpty(__result.Height)
            .Max();
        __result = new Size(__result.Width, Math.Min(__result.Height, buttonBottom));
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
        if (IsInstanceOfType(form, ImageSizeDialogTypeName))
        {
            footerTop = FindImageSizeFooterTop(form);
        }
        else if (IsInstanceOfType(form, SettingsDialogTypeName))
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

    internal static int FindImageSizeFooterTop(Form form)
    {
        Control? whiteSpaceControl = form.Controls
            .Cast<Control>()
            .FirstOrDefault(control => control.Name == "whiteSpaceControl");
        if (whiteSpaceControl == null)
        {
            return FindFooterTop(form);
        }

        int footerTop = whiteSpaceControl.Top;
        whiteSpaceControl.Visible = false;
        return footerTop;
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
                name == ColorProfileDialogTypeName ||
                name == ImageSizeDialogTypeName ||
                name == IndirectUIDialogBaseTypeName ||
                name == SaveConfigDialogTypeName ||
                name == "PaintDotNet.Dialogs.TaskProgressDialog" ||
                name == SettingsDialogTypeName ||
                name == "PaintDotNet.Updates.UpdatesDialog" ||
                name == "PaintDotNet.Effects.EffectConfigDialog" ||
                name == EffectConfigFormTypeName)
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
