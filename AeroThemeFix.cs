using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

internal static class AeroThemeFix
{
    private const string ToolBarTypeName = "PaintDotNet.Controls.PdnToolBar";
    private const string DrawingContextUtilTypeName = "PaintDotNet.Drawing.DrawingContextUtil";

    private const int BpbfTopDownDib = 2;
    private const int DtVCenter = 0x0004;
    private const int DtSingleLine = 0x0020;
    private const int DtNoPrefix = 0x0800;
    private const int DtEndEllipsis = 0x8000;
    private const uint DttShadowType = 0x0010;
    private const uint DttGlowSize = 0x0800;
    private const uint DttComposited = 0x2000;
    private const int TextShadowTypeSingle = 1;
    private const int WindowPartCaption = 1;
    private const int CaptionStateActive = 1;
    private const int CaptionStateInactive = 2;
    private const int SmCyCaption = 4;
    private const int SmCxSmallIcon = 49;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;

    private static readonly object sync = new();
    private static bool patched;
    private static FieldInfo? documentStripField;
    private static MethodInfo? findFormMethod;
    private static MethodInfo? glassCaptionDragInsetGetter;
    private static PropertyInfo? drawCaptionAreaProperty;
    private static PropertyInfo? documentStripLeftProperty;
    private static PropertyInfo? formHandleProperty;
    private static PropertyInfo? formTextProperty;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        if (!PDNClassicSettingsFix.AeroGlassEnabledAtStartup)
        {
            return;
        }

        lock (sync)
        {
            if (patched)
            {
                return;
            }

            Type? toolBarType = assembly.GetType(ToolBarTypeName, throwOnError: false, ignoreCase: false);
            if (toolBarType == null)
            {
                return;
            }

            PropertyInfo glassInsetProperty = toolBarType.GetProperty(
                "GlassInset",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? throw new MissingMemberException(toolBarType.FullName, "GlassInset");
            PropertyInfo glassCaptionDragInsetProperty = toolBarType.GetProperty(
                "GlassCaptionDragInset",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? throw new MissingMemberException(toolBarType.FullName, "GlassCaptionDragInset");
            MethodInfo glassInsetGetter = glassInsetProperty.GetMethod
                ?? throw new MissingMethodException(toolBarType.FullName, "get_GlassInset");
            MethodInfo glassCaptionDragInsetGetter = glassCaptionDragInsetProperty.GetMethod
                ?? throw new MissingMethodException(toolBarType.FullName, "get_GlassCaptionDragInset");
            MethodInfo paintBackground = FindPaintBackground(toolBarType);

            documentStripField = toolBarType.GetField(
                "documentStrip",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(toolBarType.FullName, "documentStrip");
            findFormMethod = toolBarType.GetMethod(
                "FindForm",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(toolBarType.FullName, "FindForm");
            drawCaptionAreaProperty = toolBarType.GetProperty(
                "DrawCaptionArea",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(toolBarType.FullName, "DrawCaptionArea");
            documentStripLeftProperty = documentStripField.FieldType.GetProperty(
                "Left",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new MissingMemberException(documentStripField.FieldType.FullName, "Left");
            Type formType = findFormMethod.ReturnType;
            formHandleProperty = formType.GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(formType.FullName, "Handle");
            formTextProperty = formType.GetProperty("Text", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(formType.FullName, "Text");

            MethodInfo transpiler = typeof(AeroThemeFix).GetMethod(
                nameof(PaintBackgroundTranspiler),
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(AeroThemeFix).FullName, nameof(PaintBackgroundTranspiler));
            MethodInfo postfix = typeof(AeroThemeFix).GetMethod(
                nameof(PaintBackgroundPostfix),
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(AeroThemeFix).FullName, nameof(PaintBackgroundPostfix));
            MethodInfo glassInsetPrefix = typeof(AeroThemeFix).GetMethod(
                nameof(GlassInsetPrefix),
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(AeroThemeFix).FullName, nameof(GlassInsetPrefix));


            AeroThemeFix.glassCaptionDragInsetGetter = glassCaptionDragInsetGetter;
            harmony.Patch(
                glassInsetGetter,
                prefix: new HarmonyMethod(glassInsetPrefix));
            harmony.Patch(
                paintBackground,
                postfix: new HarmonyMethod(postfix),
                transpiler: new HarmonyMethod(transpiler));
            patched = true;
        }
    }

    private static MethodInfo FindPaintBackground(Type toolBarType)
    {
        MethodInfo? result = null;
        foreach (MethodInfo method in toolBarType.GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.Name != "PaintBackground")
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 2 ||
                parameters[0].ParameterType != typeof(Graphics) ||
                parameters[1].ParameterType != typeof(Rectangle))
            {
                continue;
            }

            if (result != null)
            {
                throw new AmbiguousMatchException($"{toolBarType.FullName}.PaintBackground");
            }

            result = method;
        }

        return result ?? throw new MissingMethodException(toolBarType.FullName, "PaintBackground(Graphics, Rectangle)");
    }

    private static bool GlassInsetPrefix(
        object __instance,
        ref System.Windows.Forms.Padding __result)
    {
        if (!ShouldUseLegacyAero())
        {
            return true;
        }

        object? value = glassCaptionDragInsetGetter?.Invoke(__instance, null);
        if (value is not System.Windows.Forms.Padding padding)
        {
            throw new InvalidOperationException("PdnToolBar.GlassCaptionDragInset did not return Padding.");
        }

        __result = padding;
        return false;
    }

    private static IEnumerable<CodeInstruction> PaintBackgroundTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        bool patchedCall = false;
        foreach (CodeInstruction instruction in instructions)
        {
            if (!patchedCall &&
                instruction.operand is MethodInfo drawMethod &&
                IsCaptionDrawingContextCall(drawMethod))
            {
                MethodInfo conditionalDraw = CreateConditionalCaptionDrawMethod(drawMethod);
                CodeInstruction replacement = new(OpCodes.Call, conditionalDraw);
                replacement.labels.AddRange(instruction.labels);
                replacement.blocks.AddRange(instruction.blocks);
                instruction.labels.Clear();
                instruction.blocks.Clear();
                yield return replacement;
                patchedCall = true;
                continue;
            }

            yield return instruction;
        }

        if (!patchedCall)
        {
            throw new MissingMethodException(original.DeclaringType?.FullName, "DrawingContextUtil.Draw caption call");
        }
    }

    private static bool IsCaptionDrawingContextCall(MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        return method.IsStatic &&
            method.Name == "Draw" &&
            method.DeclaringType?.FullName == DrawingContextUtilTypeName &&
            parameters.Length == 4 &&
            parameters[0].ParameterType == typeof(Graphics) &&
            parameters[1].ParameterType.FullName == "PaintDotNet.Rendering.RectInt32" &&
            parameters[2].ParameterType == typeof(bool) &&
            typeof(Delegate).IsAssignableFrom(parameters[3].ParameterType);
    }

    private static DynamicMethod CreateConditionalCaptionDrawMethod(MethodInfo drawMethod)
    {
        ParameterInfo[] parameters = drawMethod.GetParameters();
        Type[] parameterTypes = Array.ConvertAll(parameters, parameter => parameter.ParameterType);
        DynamicMethod conditionalDraw = new(
            "PDNClassic_DrawLegacyAeroCaptionConditionally",
            typeof(void),
            parameterTypes,
            typeof(AeroThemeFix).Module,
            skipVisibility: true);
        MethodInfo shouldUseLegacyAero = typeof(AeroThemeFix).GetMethod(
            nameof(ShouldUseLegacyAero),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(AeroThemeFix).FullName, nameof(ShouldUseLegacyAero));

        ILGenerator il = conditionalDraw.GetILGenerator();
        Label runOriginal = il.DefineLabel();
        il.Emit(OpCodes.Call, shouldUseLegacyAero);
        il.Emit(OpCodes.Brfalse, runOriginal);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(runOriginal);
        for (short index = 0; index < parameterTypes.Length; ++index)
        {
            il.Emit(OpCodes.Ldarg, index);
        }
        il.Emit(OpCodes.Call, drawMethod);
        il.Emit(OpCodes.Ret);
        return conditionalDraw;
    }

    private static bool ShouldUseLegacyAero()
    {
        if (!StatusBarFix.IsAeroTheme())
        {
            return false;
        }

        return DwmIsCompositionEnabled(out bool enabled) >= 0 && enabled;
    }

    private static void PaintBackgroundPostfix(object __instance, Graphics g, Rectangle clipRect)
    {
        if (!ShouldUseLegacyAero() ||
            drawCaptionAreaProperty?.GetValue(__instance) is not true ||
            findFormMethod?.Invoke(__instance, null) is not object form ||
            documentStripField?.GetValue(__instance) is not object documentStrip)
        {
            return;
        }

        nint hwnd = (nint)(formHandleProperty?.GetValue(form) ?? nint.Zero);
        string title = formTextProperty?.GetValue(form) as string ?? string.Empty;
        bool isActive = GetActiveWindow() == hwnd;
        int documentStripLeft = (int)(documentStripLeftProperty?.GetValue(documentStrip) ?? 0);
        int paddedSizeFrameHeight = GetSystemMetrics(SmCySizeFrame) + GetSystemMetrics(SmCxPaddedBorder);
        int captionTop = IsZoomed(hwnd) ? paddedSizeFrameHeight : 0;
        int captionBottom = paddedSizeFrameHeight + GetSystemMetrics(SmCyCaption);
        int textLeft = 2 + GetSystemMetrics(SmCxSmallIcon);
        Rectangle textBounds = Rectangle.FromLTRB(textLeft, captionTop, documentStripLeft - 1, captionBottom);
        Rectangle clippedTextBounds = Rectangle.Intersect(clipRect, textBounds);
        if (textBounds.Width <= 0 || textBounds.Height <= 0 ||
            clippedTextBounds.Width <= 0 || clippedTextBounds.Height <= 0)
        {
            return;
        }

        _ = DrawThemedCaptionText(hwnd, g, "  " + title + "  ", textBounds, isActive);
    }

    private static bool DrawThemedCaptionText(
        nint hwnd,
        Graphics graphics,
        string text,
        Rectangle bounds,
        bool isActive)
    {
        nint theme = OpenThemeData(hwnd, "WINDOW");
        if (theme == nint.Zero)
        {
            return false;
        }

        bool drawSucceeded = false;
        using Font captionFont = CreateFittedCaptionFont(graphics, text, bounds.Width);
        nint targetHdc = graphics.GetHdc();
        try
        {
            if (BufferedPaintInit() < 0)
            {
                return false;
            }

            try
            {
                NativeRect rect = new(bounds);
                nint paintBuffer = BeginBufferedPaint(targetHdc, ref rect, BpbfTopDownDib, nint.Zero, out nint bufferHdc);
                if (paintBuffer == nint.Zero)
                {
                    return false;
                }

                nint hfont = captionFont.ToHfont();
                nint oldFont = SelectObject(bufferHdc, hfont);
                try
                {
                    DrawThemeTextOptions options = new()
                    {
                        Size = (uint)Marshal.SizeOf<DrawThemeTextOptions>(),
                        Flags = DttShadowType | DttGlowSize | DttComposited,
                        TextShadowType = TextShadowTypeSingle,
                        ApplyOverlay = 1,
                        GlowSize = 10
                    };
                    drawSucceeded = DrawThemeTextEx(
                        theme,
                        bufferHdc,
                        WindowPartCaption,
                        isActive ? CaptionStateActive : CaptionStateInactive,
                        text,
                        text.Length,
                        DtEndEllipsis | DtNoPrefix | DtSingleLine | DtVCenter,
                        ref rect,
                        ref options) >= 0;
                }
                finally
                {
                    _ = SelectObject(bufferHdc, oldFont);
                    _ = DeleteObject(hfont);
                    _ = EndBufferedPaint(paintBuffer, drawSucceeded);
                }
            }
            finally
            {
                _ = BufferedPaintUnInit();
            }
        }
        finally
        {
            graphics.ReleaseHdc(targetHdc);
            _ = CloseThemeData(theme);
        }

        return drawSucceeded;
    }

    private static Font CreateFittedCaptionFont(Graphics graphics, string text, int availableWidth)
    {
        Font systemFont = SystemFonts.CaptionFont
            ?? throw new InvalidOperationException("The system caption font is unavailable.");
        float minimumSize = systemFont.Size * 0.75f;
        float size = systemFont.Size;
        Font? fittedFont = null;
        while (size > minimumSize)
        {
            fittedFont?.Dispose();
            fittedFont = new Font(systemFont.FontFamily, size, systemFont.Style, GraphicsUnit.Point);
            SizeF measured = graphics.MeasureString(text, fittedFont, int.MaxValue, StringFormat.GenericTypographic);
            if (measured.Width < availableWidth)
            {
                systemFont.Dispose();
                return fittedFont;
            }
            size = Math.Max(minimumSize, size - 0.25f);
        }

        fittedFont?.Dispose();
        Font result = new(systemFont.FontFamily, minimumSize, systemFont.Style, GraphicsUnit.Point);
        systemFont.Dispose();
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public NativeRect(Rectangle value)
        {
            Left = value.Left;
            Top = value.Top;
            Right = value.Right;
            Bottom = value.Bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawThemeTextOptions
    {
        public uint Size;
        public uint Flags;
        public uint TextColor;
        public uint BorderColor;
        public uint ShadowColor;
        public int TextShadowType;
        public NativePoint ShadowOffset;
        public int BorderSize;
        public int FontPropertyId;
        public int ColorPropertyId;
        public int StateId;
        public int ApplyOverlay;
        public int GlowSize;
        public nint DrawTextCallback;
        public nint CallbackData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern nint OpenThemeData(nint hwnd, string classList);

    [DllImport("uxtheme.dll")]
    private static extern int CloseThemeData(nint theme);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawThemeTextEx(
        nint theme,
        nint hdc,
        int partId,
        int stateId,
        string text,
        int textLength,
        int textFlags,
        ref NativeRect rect,
        ref DrawThemeTextOptions options);

    [DllImport("uxtheme.dll")]
    private static extern int BufferedPaintInit();

    [DllImport("uxtheme.dll")]
    private static extern int BufferedPaintUnInit();

    [DllImport("uxtheme.dll")]
    private static extern nint BeginBufferedPaint(
        nint targetHdc,
        ref NativeRect targetRect,
        int format,
        nint paintParams,
        out nint bufferHdc);

    [DllImport("uxtheme.dll")]
    private static extern int EndBufferedPaint(nint paintBuffer, [MarshalAs(UnmanagedType.Bool)] bool updateTarget);

    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint gdiObject);
}
