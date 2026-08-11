using HarmonyLib;
using PaintDotNet;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Reflection.Emit;

internal static class LegacyControlStylesFix
{
    private const string GdiHighlightTypeName = "PaintDotNet.VisualStyling.SelectionHighlight";
    private const string D2dHighlightRendererTypeName = "PaintDotNet.VisualStyling.SelectionHighlightRenderer";
    private const string ToolStripRendererTypeName = "PaintDotNet.VisualStyling.PdnToolStripRenderer";
    private const string ToleranceSliderTypeName = "PaintDotNet.Controls.ToleranceSliderControl";
    private const string SliderHostTypeName = "PaintDotNet.Controls.ToolConfigUI.SliderControl";

    private static readonly object sync = new();
    private static bool gdiPatched;
    private static bool d2dPatched;
    private static bool imageListControlsPatched;
    private static bool toolStripRendererPatched;
    private static bool toleranceSliderPatched;
    private static Func<object, Color, Pen>? getPen;
    private static Action<object, double, double, double, double, Color>? fillD2dRectangle;
    private static MethodInfo? drawRoundedInsetMethod;

    [ThreadStatic]
    private static Point[]? outlinePoints;
    [ThreadStatic]
    private static int imageListRenderDepth;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        if (!PDNClassicSettingsFix.LegacyControlStylesEnabledAtStartup)
        {
            return;
        }

        lock (sync)
        {
            if (!gdiPatched)
            {
                Type? highlightType = assembly.GetType(
                    GdiHighlightTypeName,
                    throwOnError: false,
                    ignoreCase: false);
                if (highlightType != null)
                {
                    PatchGdiHighlight(harmony, highlightType);
                    gdiPatched = true;
                }
            }

            if (!d2dPatched)
            {
                Type? rendererType = assembly.GetType(
                    D2dHighlightRendererTypeName,
                    throwOnError: false,
                    ignoreCase: false);
                if (rendererType != null)
                {
                    PatchD2dHighlightRenderer(harmony, rendererType);
                    d2dPatched = true;
                }
            }

            if (!toolStripRendererPatched)
            {
                Type? toolStripRendererType = assembly.GetType(
                    ToolStripRendererTypeName,
                    throwOnError: false,
                    ignoreCase: false);
                if (toolStripRendererType != null)
                {
                    PatchToolStripRenderer(harmony, toolStripRendererType);
                    toolStripRendererPatched = true;
                }
            }

            if (!toleranceSliderPatched)
            {
                Type? toleranceSliderType = assembly.GetType(
                    ToleranceSliderTypeName,
                    throwOnError: false,
                    ignoreCase: false);
                if (toleranceSliderType != null)
                {
                    Type sliderHostType = assembly.GetType(
                        SliderHostTypeName,
                        throwOnError: true,
                        ignoreCase: false)!;
                    PatchToleranceSlider(harmony, toleranceSliderType, sliderHostType);
                    toleranceSliderPatched = true;
                }
            }

            if (!imageListControlsPatched)
            {
                Type? imageListMenuType = assembly.GetType(
                    "PaintDotNet.Controls.ImageListMenu",
                    throwOnError: false,
                    ignoreCase: false);
                if (imageListMenuType != null)
                {
                    PatchImageListControls(
                        harmony,
                        assembly.GetType(
                            "PaintDotNet.Controls.ImageStrip",
                            throwOnError: true,
                            ignoreCase: false)!);
                    imageListControlsPatched = true;
                }
            }
        }
    }

    private static void PatchToleranceSlider(
        Harmony harmony,
        Type sliderType,
        Type sliderHostType)
    {
        MethodInfo onRender = GetRequiredDeclaredMethod(
            sliderType,
            "OnRender",
            parameterCount: 2);
        MethodInfo onMouseMove = GetRequiredDeclaredMethod(
            sliderType,
            "OnMouseMove",
            parameterCount: 1);
        MethodInfo renderTranspiler = typeof(LegacyControlStylesFix).GetMethod(
            nameof(ToleranceSliderOnRenderTranspiler),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(LegacyControlStylesFix).FullName,
                nameof(ToleranceSliderOnRenderTranspiler));
        MethodInfo renderPostfix = typeof(LegacyControlStylesFix).GetMethod(
            nameof(ToleranceSliderOnRenderPostfix),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(LegacyControlStylesFix).FullName,
                nameof(ToleranceSliderOnRenderPostfix));
        MethodInfo mouseMoveTranspiler = typeof(LegacyControlStylesFix).GetMethod(
            nameof(ToleranceSliderOnMouseMoveTranspiler),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(LegacyControlStylesFix).FullName,
                nameof(ToleranceSliderOnMouseMoveTranspiler));

        fillD2dRectangle = CreateD2dFillRectangleAdapter(
            onRender.GetParameters()[0].ParameterType);
        harmony.Patch(
            onRender,
            postfix: new HarmonyMethod(renderPostfix),
            transpiler: new HarmonyMethod(renderTranspiler));
        harmony.Patch(
            onMouseMove,
            transpiler: new HarmonyMethod(mouseMoveTranspiler));

        ConstructorInfo[] hostConstructors = sliderHostType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (hostConstructors.Length != 1)
        {
            throw new AmbiguousMatchException(
                $"Expected one {sliderHostType.FullName} constructor, found {hostConstructors.Length}.");
        }

        MethodInfo hostConstructorPostfix = typeof(LegacyControlStylesFix).GetMethod(
            nameof(SliderHostConstructorPostfix),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(LegacyControlStylesFix).FullName,
                nameof(SliderHostConstructorPostfix));
        harmony.Patch(
            hostConstructors[0],
            postfix: new HarmonyMethod(hostConstructorPostfix));
    }

    private static void SliderHostConstructorPostfix(object __instance)
    {
        PropertyInfo controlProperty = __instance.GetType().GetProperty(
            "Control",
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly)
            ?? throw new MissingMemberException(__instance.GetType().FullName, "Control");
        System.Windows.Forms.Control control =
            (System.Windows.Forms.Control)(controlProperty.GetValue(__instance)
                ?? throw new InvalidOperationException("Slider host has no control."));
        control.Width = ((control.Width * 6) + 3) / 7;
        System.Windows.Forms.ToolStripItem host =
            (System.Windows.Forms.ToolStripItem)__instance;
        host.AutoSize = false;
        host.Size = control.Size;
    }

    private static Action<object, double, double, double, double, Color>
        CreateD2dFillRectangleAdapter(Type drawingContextType)
    {
        Type rectDoubleType = FindLoadedType("PaintDotNet.Rendering.RectDouble");
        ConstructorInfo rectConstructor = rectDoubleType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(double),
                typeof(double),
                typeof(double),
                typeof(double)
            },
            modifiers: null)
            ?? throw new MissingMethodException(
                rectDoubleType.FullName,
                ".ctor(double, double, double, double)");
        Type colorType = FindLoadedType("PaintDotNet.Imaging.ColorRgba128Float");
        MethodInfo colorConversion = colorType.GetMethod(
            "op_Implicit",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(Color) },
            modifiers: null)
            ?? throw new MissingMethodException(
                colorType.FullName,
                "op_Implicit(Color)");
        Type brushCacheType = FindLoadedType("PaintDotNet.UI.Media.SolidColorBrushCache");
        MethodInfo getBrush = brushCacheType.GetMethod(
            "Get",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { colorType },
            modifiers: null)
            ?? throw new MissingMethodException(
                brushCacheType.FullName,
                "Get(ColorRgba128Float)");
        Type drawingExtensionsType =
            FindLoadedType("PaintDotNet.Direct2D1.DrawingContextExtensions");
        MethodInfo fillRectangle = Array.Find(
            drawingExtensionsType.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return method.Name == "FillRectangle" &&
                    parameters.Length == 3 &&
                    parameters[0].ParameterType == drawingContextType &&
                    parameters[1].ParameterType == rectDoubleType;
            }) ?? throw new MissingMethodException(
                drawingExtensionsType.FullName,
                "FillRectangle(IDrawingContext, RectDouble, Brush)");

        DynamicMethod adapter = new(
            "PDNClassic_FillD2dRectangle",
            typeof(void),
            new[]
            {
                typeof(object),
                typeof(double),
                typeof(double),
                typeof(double),
                typeof(double),
                typeof(Color)
            },
            typeof(LegacyControlStylesFix),
            skipVisibility: true);
        ILGenerator il = adapter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, drawingContextType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldarg_S, 4);
        il.Emit(OpCodes.Newobj, rectConstructor);
        il.Emit(OpCodes.Ldarg_S, 5);
        il.Emit(OpCodes.Call, colorConversion);
        il.Emit(OpCodes.Call, getBrush);
        il.Emit(OpCodes.Call, fillRectangle);
        il.Emit(OpCodes.Ret);
        return (Action<object, double, double, double, double, Color>)
            adapter.CreateDelegate(
                typeof(Action<object, double, double, double, double, Color>));
    }

    private static void ToleranceSliderOnRenderPostfix(
        object __instance,
        object __0)
    {
        System.Windows.Forms.Control control =
            (System.Windows.Forms.Control)__instance;
        int width = control.ClientSize.Width;
        int height = control.ClientSize.Height;
        if (width < 3 || height < 3)
        {
            return;
        }

        Action<object, double, double, double, double, Color> fill =
            fillD2dRectangle
            ?? throw new InvalidOperationException(
                "The Direct2D rectangle adapter has not been initialized.");
        Color window = SystemColors.Window;
        Color outline = SystemColors.WindowText;
        fill(__0, 1.0, height - 2.0, width - 2.0, 1.0, window);


        fill(__0, 0.0, 0.0, 1.0, 1.0, window);
        fill(__0, width - 1.0, 0.0, 1.0, 1.0, window);
        fill(__0, 0.0, height - 1.0, 1.0, 1.0, window);
        fill(__0, width - 1.0, height - 1.0, 1.0, 1.0, window);

        fill(__0, 1.0, 0.0, width - 2.0, 1.0, outline);
        fill(__0, 1.0, height - 1.0, width - 2.0, 1.0, outline);
        fill(__0, 0.0, 1.0, 1.0, height - 2.0, outline);
        fill(__0, width - 1.0, 1.0, 1.0, height - 2.0, outline);
    }


    private static MethodInfo GetRequiredDeclaredMethod(
        Type type,
        string name,
        int parameterCount)
    {
        MethodInfo? result = null;
        foreach (MethodInfo method in type.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            if (method.Name != name ||
                method.GetParameters().Length != parameterCount)
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

    private static IEnumerable<CodeInstruction> ToleranceSliderOnRenderTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        Type sliderType = original.DeclaringType
            ?? throw new InvalidOperationException("Tolerance slider render method has no declaring type.");
        FieldInfo fillInsetField = sliderType.GetField(
            "fillInsetPx",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(sliderType.FullName, "fillInsetPx");
        FieldInfo textInsetField = sliderType.GetField(
            "textInsetPx",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(sliderType.FullName, "textInsetPx");
        MethodInfo windowGetter = typeof(SystemColors)
            .GetProperty(nameof(SystemColors.Window))!.GetMethod!;
        MethodInfo truncatePercentage = typeof(LegacyControlStylesFix).GetMethod(
            nameof(TruncateSliderPercentage),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(LegacyControlStylesFix).FullName,
                nameof(TruncateSliderPercentage));
        int[] fillInsetValues = { 1, 2, 2, 2, 2 };
        int[] textInsetValues = { 2, 1, 4, 1 };
        int fillInsetIndex = 0;
        int textInsetIndex = 0;
        int backColorReplacementCount = 0;
        int outlineColorReplacementCount = 0;
        int percentageReplacementCount = 0;
        int outlineDrawReplacementCount = 0;
        List<CodeInstruction> code = new(instructions);
        foreach (CodeInstruction instruction in code)
        {
            if (instruction.opcode == OpCodes.Ldsfld &&
                instruction.operand is FieldInfo field)
            {
                if (field == fillInsetField &&
                    fillInsetIndex < fillInsetValues.Length)
                {
                    instruction.opcode = OpCodes.Ldc_I4;
                    instruction.operand = fillInsetValues[fillInsetIndex++];
                    continue;
                }

                if (field == textInsetField &&
                    textInsetIndex < textInsetValues.Length)
                {
                    instruction.opcode = OpCodes.Ldc_I4;
                    instruction.operand = textInsetValues[textInsetIndex++];
                    continue;
                }
            }

            if (instruction.operand is not MethodInfo calledMethod)
            {
                continue;
            }

            if (calledMethod.DeclaringType?.FullName ==
                    "PaintDotNet.VisualStyling.AeroColors" &&
                calledMethod.Name == "get_SliderBackColor")
            {
                instruction.operand = windowGetter;
                ++backColorReplacementCount;
            }
            else if (calledMethod.DeclaringType?.FullName ==
                        "PaintDotNet.Imaging.SystemColors" &&
                calledMethod.Name == "get_ControlDark")
            {
                instruction.operand = calledMethod.DeclaringType
                    .GetProperty("WindowText")!.GetMethod!;
                ++outlineColorReplacementCount;
            }
            else if (calledMethod.DeclaringType == typeof(Math) &&
                calledMethod.Name == nameof(Math.Round) &&
                calledMethod.GetParameters() is ParameterInfo[] parameters &&
                parameters.Length == 2 &&
                parameters[0].ParameterType == typeof(double) &&
                parameters[1].ParameterType == typeof(MidpointRounding))
            {
                instruction.operand = truncatePercentage;
                ++percentageReplacementCount;
            }
            else if (calledMethod.DeclaringType?.FullName ==
                        "PaintDotNet.Direct2D1.DrawingContextExtensions" &&
                calledMethod.Name == "DrawRectangle")
            {
                instruction.operand = CreateNoOpAdapter(calledMethod);
                ++outlineDrawReplacementCount;
            }
        }

        if (fillInsetIndex != fillInsetValues.Length ||
            textInsetIndex != textInsetValues.Length ||
            backColorReplacementCount != 1 ||
            outlineColorReplacementCount != 1 ||
            percentageReplacementCount != 1 ||
            outlineDrawReplacementCount != 1)
        {
            throw new InvalidOperationException(
                "Could not locate the complete ToleranceSliderControl render sequence.");
        }

        return code;
    }

    private static MethodInfo CreateNoOpAdapter(MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        Type[] parameterTypes = new Type[parameters.Length];
        for (int i = 0; i < parameters.Length; ++i)
        {
            parameterTypes[i] = parameters[i].ParameterType;
        }

        DynamicMethod adapter = new(
            "PDNClassic_NoOp_" + method.Name,
            method.ReturnType,
            parameterTypes,
            typeof(LegacyControlStylesFix),
            skipVisibility: true);
        ILGenerator il = adapter.GetILGenerator();
        if (method.ReturnType != typeof(void))
        {
            throw new InvalidOperationException(
                $"Cannot create a no-op adapter for non-void method {method}.");
        }

        il.Emit(OpCodes.Ret);
        return adapter;
    }

    private static IEnumerable<CodeInstruction> ToleranceSliderOnMouseMoveTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        Type sliderType = original.DeclaringType
            ?? throw new InvalidOperationException("Tolerance slider mouse method has no declaring type.");
        FieldInfo fillInsetField = sliderType.GetField(
            "fillInsetPx",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(sliderType.FullName, "fillInsetPx");
        int replacementCount = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldsfld &&
                instruction.operand is FieldInfo field &&
                field == fillInsetField)
            {
                instruction.opcode = OpCodes.Ldc_I4_2;
                instruction.operand = null;
                ++replacementCount;
            }

            yield return instruction;
        }

        if (replacementCount != 1)
        {
            throw new InvalidOperationException(
                $"Expected one tolerance slider mouse inset, found {replacementCount}.");
        }
    }

    private static double TruncateSliderPercentage(
        double value,
        MidpointRounding _)
    {
        return Math.Truncate(value);
    }

    private static void PatchToolStripRenderer(Harmony harmony, Type rendererType)
    {
        MethodInfo drawAeroSeparator = rendererType.GetMethod(
            "DrawAeroSeparator",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(Graphics), typeof(Rectangle) },
            modifiers: null)
            ?? throw new MissingMethodException(
                rendererType.FullName,
                "DrawAeroSeparator(Graphics, Rectangle)");
        MethodInfo prefix = typeof(LegacyControlStylesFix).GetMethod(
            nameof(DrawLegacyAeroSeparatorPrefix),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(LegacyControlStylesFix).FullName,
                nameof(DrawLegacyAeroSeparatorPrefix));
        harmony.Patch(drawAeroSeparator, prefix: new HarmonyMethod(prefix));
    }

    private static bool DrawLegacyAeroSeparatorPrefix(
        Graphics __0,
        Rectangle __1)
    {
        int x = __1.Left + (__1.Width / 2);
        int top = __1.Top + 5;
        int bottom = __1.Bottom - 6;
        if (bottom - top < 1)
        {
            return false;
        }

        Point topPoint = new(x, top);
        Point bottomPoint = new(x, bottom);
        using (LinearGradientBrush brush = new(
            topPoint,
            bottomPoint,
            Color.FromArgb(255, 174, 191, 211),
            Color.FromArgb(255, 165, 184, 208)))
        {
            __0.FillRectangle(brush, new Rectangle(x, top, 1, bottom - top));
        }

        using Pen outlinePen = new(Color.FromArgb(128, 255, 255, 255));
        __0.DrawRectangle(outlinePen, x - 1, top - 1, 2, bottom - top + 1);
        return false;
    }

    private static void PatchImageListControls(
        Harmony harmony,
        Type imageStripType)
    {
        MethodInfo prefix = typeof(LegacyControlStylesFix).GetMethod(
            nameof(BeginImageListItemRenderPrefix),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(LegacyControlStylesFix).FullName,
                nameof(BeginImageListItemRenderPrefix));
        MethodInfo postfix = typeof(LegacyControlStylesFix).GetMethod(
            nameof(EndImageListItemRenderPostfix),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(LegacyControlStylesFix).FullName,
                nameof(EndImageListItemRenderPostfix));
        MethodInfo finalizer = typeof(LegacyControlStylesFix).GetMethod(
            nameof(EndImageListItemRenderFinalizer),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(LegacyControlStylesFix).FullName,
                nameof(EndImageListItemRenderFinalizer));
        MethodInfo[] imageStripRenderMethods =
        {
            GetRequiredInstanceMethod(imageStripType, "DrawSelectionHighlight"),
            GetRequiredInstanceMethod(imageStripType, "DrawImageItemBackground")
        };
        foreach (MethodInfo renderMethod in imageStripRenderMethods)
        {
            harmony.Patch(
                renderMethod,
                prefix: new HarmonyMethod(prefix),
                postfix: new HarmonyMethod(postfix),
                finalizer: new HarmonyMethod(finalizer));
        }
    }

    private static MethodInfo GetRequiredInstanceMethod(Type type, string name)
    {
        return type.GetMethod(
            name,
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, name);
    }

    private static void BeginImageListItemRenderPrefix(out int __state)
    {
        __state = imageListRenderDepth;
        imageListRenderDepth = __state + 1;
    }

    private static void EndImageListItemRenderPostfix(int __state)
    {
        imageListRenderDepth = __state;
    }

    private static Exception? EndImageListItemRenderFinalizer(
        Exception? __exception,
        int __state)
    {
        imageListRenderDepth = __state;
        return __exception;
    }

    private static void PatchGdiHighlight(Harmony harmony, Type highlightType)
    {
        MethodInfo drawBackground = Array.Find(
            highlightType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return method.Name == "DrawBackground" &&
                    parameters.Length == 4 &&
                    parameters[0].ParameterType.FullName == "System.Drawing.Graphics" &&
                    parameters[2].ParameterType == typeof(Rectangle);
            }) ?? throw new MissingMethodException(highlightType.FullName, "DrawBackground(Graphics, PenBrushCache, Rectangle, HighlightState)");

        Type cacheType = drawBackground.GetParameters()[1].ParameterType;
        getPen = CreateCacheGetter<Pen>(cacheType, "GetPen");

        MethodInfo prefix = typeof(LegacyControlStylesFix).GetMethod(
            nameof(DrawGdiBackgroundPrefix),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(LegacyControlStylesFix).FullName, nameof(DrawGdiBackgroundPrefix));
        harmony.Patch(drawBackground, prefix: new HarmonyMethod(prefix));
    }

    private static Func<object, Color, TResult> CreateCacheGetter<TResult>(Type cacheType, string methodName)
        where TResult : class
    {
        MethodInfo method = cacheType.GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(Color) },
            modifiers: null)
            ?? throw new MissingMethodException(cacheType.FullName, $"{methodName}(Color)");
        DynamicMethod adapter = new(
            $"PDNClassic_{methodName}",
            typeof(TResult),
            new[] { typeof(object), typeof(Color) },
            typeof(LegacyControlStylesFix),
            skipVisibility: true);
        ILGenerator il = adapter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, cacheType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, method);
        il.Emit(OpCodes.Ret);
        return (Func<object, Color, TResult>)adapter.CreateDelegate(typeof(Func<object, Color, TResult>));
    }

    private static bool DrawGdiBackgroundPrefix(object __0, object __1, Rectangle __2, object __3)
    {
        if (!StatusBarFix.IsAeroTheme())
        {
            return true;
        }

        int state = Convert.ToInt32(__3);
        if (state == 0)
        {
            return false;
        }

        Graphics graphics = (Graphics)__0;
        Color highlight = SystemColors.Highlight;
        Color hotTrack = SystemColors.HotTrack;
        Color backFillTop;
        Color backFillBottom;
        Color insetOutline;
        Color outline;
        if (state == 1)
        {
            backFillTop = Color.FromArgb(32, highlight);
            backFillBottom = Color.FromArgb(128, highlight);
            insetOutline = Color.FromArgb(64, SystemColors.Window);
            outline = Color.FromArgb(255, highlight);
        }
        else
        {
            backFillTop = Color.FromArgb(4, hotTrack);
            backFillBottom = Color.FromArgb(48, hotTrack);
            insetOutline = Color.FromArgb(32, SystemColors.Window);
            outline = Color.FromArgb(128, hotTrack);
        }

        if (state == 3)
        {
            backFillTop = ToGray(backFillTop);
            backFillBottom = ToGray(backFillBottom);
            insetOutline = ToGray(insetOutline);
            outline = ToGray(outline);
        }

        PixelOffsetMode oldPixelOffsetMode = graphics.PixelOffsetMode;
        SmoothingMode oldSmoothingMode = graphics.SmoothingMode;
        try
        {
            graphics.PixelOffsetMode = PixelOffsetMode.None;
            graphics.SmoothingMode = SmoothingMode.None;
            Rectangle fillRect = __2;
            fillRect.Inflate(-2, -2);
            using (LinearGradientBrush fillBrush = new(__2, backFillTop, backFillBottom, LinearGradientMode.Vertical))
            {
                graphics.FillRectangle(fillBrush, fillRect);
            }

            Rectangle insetRect = __2;
            insetRect.Inflate(-1, -1);
            --insetRect.Width;
            --insetRect.Height;
            graphics.DrawRectangle(getPen!(__1, insetOutline), insetRect);

            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Point[] points = outlinePoints ??= new Point[9];
            points[0] = new Point(__2.Left + 1, __2.Top);
            points[1] = new Point(__2.Right - 2, __2.Top);
            points[2] = new Point(__2.Right - 1, __2.Top + 1);
            points[3] = new Point(__2.Right - 1, __2.Bottom - 2);
            points[4] = new Point(__2.Right - 2, __2.Bottom - 1);
            points[5] = new Point(__2.Left + 1, __2.Bottom - 1);
            points[6] = new Point(__2.Left, __2.Bottom - 2);
            points[7] = new Point(__2.Left, __2.Top + 1);
            points[8] = points[0];
            graphics.DrawLines(getPen!(__1, outline), points);
        }
        finally
        {
            graphics.PixelOffsetMode = oldPixelOffsetMode;
            graphics.SmoothingMode = oldSmoothingMode;
        }

        return false;
    }

    private static Color ToGray(Color color)
    {
        int intensity = (19595 * color.R + 38470 * color.G + 7471 * color.B + 32768) >> 16;
        return Color.FromArgb(color.A, intensity, intensity, intensity);
    }

    private static Color CreateLegacyFillColor(int alpha, Color color)
    {
        if (imageListRenderDepth != 0)
        {
            return Color.FromArgb(alpha, color);
        }

        Color window = SystemColors.Window;
        int inverseAlpha = 255 - alpha;
        return Color.FromArgb(
            255,
            ((color.R * alpha) + (window.R * inverseAlpha) + 127) / 255,
            ((color.G * alpha) + (window.G * inverseAlpha) + 127) / 255,
            ((color.B * alpha) + (window.B * inverseAlpha) + 127) / 255);
    }

    private static void PatchD2dHighlightRenderer(Harmony harmony, Type rendererType)
    {
        MethodInfo renderBackground = Array.Find(
            rendererType.GetMethods(BindingFlags.Instance | BindingFlags.Public),
            method => method.Name == "RenderBackground" &&
                method.GetParameters().Length == 2)
            ?? throw new MissingMethodException(rendererType.FullName, "RenderBackground");
        drawRoundedInsetMethod = CreateRoundedInsetAdapter(rendererType);
        MethodInfo transpiler = typeof(LegacyControlStylesFix).GetMethod(
            nameof(RenderBackgroundTranspiler),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(LegacyControlStylesFix).FullName, nameof(RenderBackgroundTranspiler));
        harmony.Patch(renderBackground, transpiler: new HarmonyMethod(transpiler));
    }

    private static MethodInfo CreateRoundedInsetAdapter(Type rendererType)
    {
        MethodInfo drawInsetRectangle = rendererType.GetMethod(
            "DrawInsetRectangle",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(rendererType.FullName, "DrawInsetRectangle");
        ParameterInfo[] adapterParameters = drawInsetRectangle.GetParameters();
        Type rectDoubleType = adapterParameters[1].ParameterType;
        Type roundedRectType = FindLoadedType("PaintDotNet.Rendering.RoundedRectDouble");
        ConstructorInfo roundedRectConstructor = roundedRectType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { rectDoubleType, typeof(double) },
            modifiers: null)
            ?? throw new MissingMethodException(roundedRectType.FullName, ".ctor(RectDouble, double)");
        MethodInfo inflateRect = rectDoubleType.GetMethod(
            "Inflate",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: new[] { rectDoubleType, typeof(double), typeof(double) },
            modifiers: null)
            ?? throw new MissingMethodException(rectDoubleType.FullName, "Inflate(RectDouble, double, double)");
        Type drawingExtensionsType = FindLoadedType("PaintDotNet.Direct2D1.DrawingContextExtensions");
        MethodInfo drawRoundedRectangle = Array.Find(
            drawingExtensionsType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name == "DrawRoundedRectangle" && method.GetParameters().Length == 5)
            ?? throw new MissingMethodException(drawingExtensionsType.FullName, "DrawRoundedRectangle");

        Type[] parameterTypes = Array.ConvertAll(adapterParameters, parameter => parameter.ParameterType);
        DynamicMethod adapter = new(
            "PDNClassic_DrawRoundedInsetRectangle",
            typeof(void),
            parameterTypes,
            typeof(LegacyControlStylesFix),
            skipVisibility: true);
        ILGenerator il = adapter.GetILGenerator();
        LocalBuilder insetRect = il.DeclareLocal(rectDoubleType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldc_R8, -0.5);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldc_R8, -0.5);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Call, inflateRect);
        il.Emit(OpCodes.Stloc, insetRect);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, insetRect);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Newobj, roundedRectConstructor);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, drawRoundedRectangle);
        il.Emit(OpCodes.Ret);
        return adapter;
    }

    private static Type FindLoadedType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (type != null)
            {
                return type;
            }
        }

        throw new TypeLoadException(fullName);
    }

    private static IEnumerable<CodeInstruction> RenderBackgroundTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = new(instructions);
        MethodInfo systemWindowGetter = typeof(SystemColors).GetProperty(nameof(SystemColors.Window))!.GetMethod!;
        MethodInfo createLegacyFillColor = typeof(LegacyControlStylesFix).GetMethod(
            nameof(CreateLegacyFillColor),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(LegacyControlStylesFix).FullName, nameof(CreateLegacyFillColor));
        List<int> fromArgbCalls = new();
        int contentBackColorReplacementCount = 0;
        int drawInsetCallCount = 0;
        int antialiasModeReplacementCount = 0;
        int interpolationSpaceReplacementCount = 0;

        for (int index = 0; index < code.Count; ++index)
        {
            if (code[index].operand is not MethodInfo calledMethod)
            {
                continue;
            }

            if (calledMethod.DeclaringType == typeof(Color) &&
                calledMethod.Name == nameof(Color.FromArgb) &&
                calledMethod.GetParameters().Length == 2)
            {
                fromArgbCalls.Add(index);
            }
            else if (calledMethod.DeclaringType?.FullName == "PaintDotNet.VisualStyling.AeroColors" &&
                calledMethod.Name == "get_ContentBackColor")
            {
                code[index].operand = systemWindowGetter;
                ++contentBackColorReplacementCount;
            }
            else if (calledMethod.Name == "DrawInsetRectangle" &&
                calledMethod.DeclaringType?.FullName == D2dHighlightRendererTypeName)
            {
                ++drawInsetCallCount;
                if (drawInsetCallCount == 2)
                {
                    code[index].operand = drawRoundedInsetMethod;
                }
            }
            else if (calledMethod.Name == "set_ColorInterpolationSpace" &&
                calledMethod.GetParameters() is [ParameterInfo interpolationParameter] &&
                interpolationParameter.ParameterType.FullName == "PaintDotNet.UI.Media.ColorInterpolationSpace" &&
                index > 0)
            {
                if (code[index - 1].opcode != OpCodes.Ldc_I4_1)
                {
                    throw new InvalidOperationException("Expected linear SelectionHighlightRenderer gradient interpolation.");
                }

                code[index - 1].opcode = OpCodes.Ldc_I4_0;
                code[index - 1].operand = null;
                ++interpolationSpaceReplacementCount;
            }
            else if (calledMethod.Name == "UseAntialiasMode" && index > 0)
            {
                if (code[index - 1].opcode != OpCodes.Ldc_I4_1)
                {
                    throw new InvalidOperationException("Expected aliased SelectionHighlightRenderer primitives.");
                }

                code[index - 1].opcode = OpCodes.Ldc_I4_0;
                code[index - 1].operand = null;
                ++antialiasModeReplacementCount;
            }
        }

        if (fromArgbCalls.Count != 6)
        {
            throw new InvalidOperationException($"Expected six SelectionHighlightRenderer colors, found {fromArgbCalls.Count}.");
        }

        code[fromArgbCalls[0]].operand = createLegacyFillColor;
        code[fromArgbCalls[3]].operand = createLegacyFillColor;

        SetNearbyConstants(code, fromArgbCalls[0], 32, 64, 128);
        SetNearbyConstants(code, fromArgbCalls[3], 4, 32, 64);
        SetNearbyConstants(code, fromArgbCalls[5], 128, 128, 192);
        ReplaceCopiedBottomColor(code, fromArgbCalls[3], 48);
        ReplaceCopiedBottomColor(code, fromArgbCalls[0], 128);

        if (contentBackColorReplacementCount != 2 ||
            drawInsetCallCount != 2 ||
            antialiasModeReplacementCount != 1 ||
            interpolationSpaceReplacementCount != 1)
        {
            throw new InvalidOperationException("Could not locate the complete SelectionHighlightRenderer style sequence.");
        }

        return code;
    }

    private static void SetNearbyConstants(
        List<CodeInstruction> code,
        int callIndex,
        int replacement,
        params int[] expectedValues)
    {
        int replacementCount = 0;
        for (int index = Math.Max(0, callIndex - 8); index < callIndex; ++index)
        {
            for (int expectedIndex = 0; expectedIndex < expectedValues.Length; ++expectedIndex)
            {
                if (code[index].LoadsConstant(expectedValues[expectedIndex]))
                {
                    code[index].opcode = OpCodes.Ldc_I4;
                    code[index].operand = replacement;
                    ++replacementCount;
                    break;
                }
            }
        }

        if (replacementCount != 2)
        {
            throw new InvalidOperationException($"Expected two alpha constants before SelectionHighlightRenderer color {callIndex}.");
        }
    }

    private static void ReplaceCopiedBottomColor(
        List<CodeInstruction> code,
        int fromArgbCallIndex,
        int bottomAlpha)
    {
        MethodInfo srgbToColor = (MethodInfo)code[fromArgbCallIndex - 1].operand;
        MethodInfo colorFromArgb = (MethodInfo)code[fromArgbCallIndex].operand;
        MethodInfo colorToSrgb = (MethodInfo)code[fromArgbCallIndex + 1].operand;
        CodeInstruction highlightColorLoad = code[fromArgbCallIndex - 2].Clone();
        int copiedColorIndex = fromArgbCallIndex + 3;
        CodeInstruction copiedColorLoad = code[copiedColorIndex];
        List<CodeInstruction> replacement = new()
        {
            new CodeInstruction(OpCodes.Ldc_I4, bottomAlpha)
                .MoveLabelsFrom(copiedColorLoad)
                .MoveBlocksFrom(copiedColorLoad),
            highlightColorLoad,
            new CodeInstruction(OpCodes.Call, srgbToColor),
            new CodeInstruction(OpCodes.Call, colorFromArgb),
            new CodeInstruction(OpCodes.Call, colorToSrgb)
        };
        code.RemoveAt(copiedColorIndex);
        code.InsertRange(copiedColorIndex, replacement);
    }
}
