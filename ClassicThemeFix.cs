using HarmonyLib;
using PaintDotNet.Imaging;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

internal static class ClassicThemeFix
{
    private const string CheckBoxTypeName = "PaintDotNet.Controls.PdnCheckBox";
    private const string RadioButtonTypeName = "PaintDotNet.Controls.PdnRadioButton";
    private const string ZoomSliderTypeName = "PaintDotNet.Controls.ZoomSliderControl";
    private const string GdiPaintTypeName = "PaintDotNet.Gdi.GdiPaint";
    private const string RectInt32TypeName = "PaintDotNet.Rendering.RectInt32";
    private const string RectFloatTypeName = "PaintDotNet.Rendering.RectFloat";
    private const string DrawingContextExtensionsTypeName = "PaintDotNet.Direct2D1.DrawingContextExtensions";

    private static readonly object sync = new();
    private static bool controlsPatched;
    private static bool zoomSliderPatched;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        lock (sync)
        {
            if (!controlsPatched)
            {
                TryPatchCheckboxesAndRadioButtons(harmony, assembly);
            }

            if (!zoomSliderPatched)
            {
                TryPatchZoomSlider(harmony, assembly);
            }
        }
    }

    private static void TryPatchCheckboxesAndRadioButtons(Harmony harmony, Assembly assembly)
    {
        Type? checkBoxType = assembly.GetType(CheckBoxTypeName, throwOnError: false, ignoreCase: false);
        Type? radioButtonType = assembly.GetType(RadioButtonTypeName, throwOnError: false, ignoreCase: false);
        if (checkBoxType == null || radioButtonType == null)
        {
            return;
        }

        MethodInfo transpiler = typeof(ClassicThemeFix).GetMethod(
            nameof(MeasureAndDrawTranspiler),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ClassicThemeFix).FullName, nameof(MeasureAndDrawTranspiler));

        HarmonyMethod harmonyTranspiler = new(transpiler);
        harmony.Patch(FindMeasureAndDraw(checkBoxType), transpiler: harmonyTranspiler);
        harmony.Patch(FindMeasureAndDraw(radioButtonType), transpiler: harmonyTranspiler);
        controlsPatched = true;
    }

    private static void TryPatchZoomSlider(Harmony harmony, Assembly assembly)
    {
        Type? zoomSliderType = assembly.GetType(ZoomSliderTypeName, throwOnError: false, ignoreCase: false);
        if (zoomSliderType == null)
        {
            return;
        }

        Type? sliderImplType = zoomSliderType.GetNestedType("SliderImpl", BindingFlags.NonPublic);
        if (sliderImplType == null)
        {
            return;
        }

        MethodInfo constructorPostfix = typeof(ClassicThemeFix).GetMethod(
            nameof(ZoomSliderConstructorPostfix),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ClassicThemeFix).FullName, nameof(ZoomSliderConstructorPostfix));
        MethodInfo transpiler = typeof(ClassicThemeFix).GetMethod(
            nameof(ZoomSliderOnRenderTranspiler),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ClassicThemeFix).FullName, nameof(ZoomSliderOnRenderTranspiler));

        harmony.Patch(
            FindParameterlessConstructor(sliderImplType),
            postfix: new HarmonyMethod(constructorPostfix));
        harmony.Patch(
            FindSliderOnRender(sliderImplType),
            transpiler: new HarmonyMethod(transpiler));
        zoomSliderPatched = true;
    }

    private static MethodInfo FindMeasureAndDraw(Type controlType)
    {
        MethodInfo? result = null;
        foreach (MethodInfo method in controlType.GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.Name != "MeasureAndDraw")
            {
                continue;
            }

            if (result != null)
            {
                throw new AmbiguousMatchException($"{controlType.FullName}.MeasureAndDraw");
            }

            result = method;
        }

        return result ?? throw new MissingMethodException(controlType.FullName, "MeasureAndDraw");
    }
    private static ConstructorInfo FindParameterlessConstructor(Type type)
    {
        return type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)
            ?? throw new MissingMethodException(type.FullName, ".ctor()");
    }


    private static MethodInfo FindSliderOnRender(Type sliderImplType)
    {
        MethodInfo? result = null;
        foreach (MethodInfo method in sliderImplType.GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.Name != "OnRender")
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 2 || parameters[1].ParameterType.FullName != RectFloatTypeName)
            {
                continue;
            }

            if (result != null)
            {
                throw new AmbiguousMatchException($"{sliderImplType.FullName}.OnRender");
            }

            result = method;
        }

        return result ?? throw new MissingMethodException(sliderImplType.FullName, "OnRender(IDrawingContext, RectFloat)");
    }

    private static void ZoomSliderConstructorPostfix(object __instance)
    {
        Type instanceType = __instance.GetType();
        MethodInfo? setStyleMethod = null;
        foreach (MethodInfo method in instanceType.GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic))
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (method.Name == "SetStyle" &&
                parameters.Length == 2 &&
                parameters[0].ParameterType.IsEnum &&
                parameters[1].ParameterType == typeof(bool))
            {
                setStyleMethod = method;
                break;
            }
        }

        if (setStyleMethod == null)
        {
            throw new MissingMethodException(instanceType.FullName, "SetStyle(ControlStyles, bool)");
        }

        Type controlStylesType = setStyleMethod.GetParameters()[0].ParameterType;
        object supportsTransparentBackColor = Enum.Parse(
            controlStylesType,
            "SupportsTransparentBackColor");
        PropertyInfo useBackColorProperty = instanceType.GetProperty(
            "UseBackColor",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(instanceType.FullName, "UseBackColor");
        PropertyInfo backColorProperty = instanceType.GetProperty(
            "BackColor",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(instanceType.FullName, "BackColor");

        setStyleMethod.Invoke(__instance, new[] { supportsTransparentBackColor, (object)true });
        backColorProperty.SetValue(__instance, System.Drawing.Color.Transparent);
        useBackColorProperty.SetValue(__instance, true);
    }

    private static IEnumerable<CodeInstruction> MeasureAndDrawTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        MethodInfo correctColorMethod = typeof(ClassicThemeFix).GetMethod(
            nameof(CorrectColor),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ClassicThemeFix).FullName, nameof(CorrectColor));

        bool patched = false;
        foreach (CodeInstruction instruction in instructions)
        {
            if (!patched && IsColorClearRectCall(instruction))
            {
                CodeInstruction correction = new(OpCodes.Call, correctColorMethod);
                correction.labels.AddRange(instruction.labels);
                correction.blocks.AddRange(instruction.blocks);
                instruction.labels.Clear();
                instruction.blocks.Clear();
                yield return correction;
                patched = true;
            }

            yield return instruction;
        }

        if (!patched)
        {
            throw new MissingMethodException(original.DeclaringType?.FullName, "GdiPaint.ClearRect color call");
        }
    }

    private static IEnumerable<CodeInstruction> ZoomSliderOnRenderTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        bool patched = false;
        foreach (CodeInstruction instruction in instructions)
        {
            if (!patched &&
                instruction.operand is MethodInfo fillRectangleMethod &&
                IsBackgroundFillCall(fillRectangleMethod))
            {
                MethodInfo conditionalFillMethod = CreateConditionalFillMethod(fillRectangleMethod);
                CodeInstruction replacement = new(OpCodes.Call, conditionalFillMethod);
                replacement.labels.AddRange(instruction.labels);
                replacement.blocks.AddRange(instruction.blocks);
                instruction.labels.Clear();
                instruction.blocks.Clear();
                yield return replacement;
                patched = true;
                continue;
            }

            yield return instruction;
        }

        if (!patched)
        {
            throw new MissingMethodException(original.DeclaringType?.FullName, "IDrawingContext.FillRectangle background call");
        }
    }

    private static bool IsColorClearRectCall(CodeInstruction instruction)
    {
        if (instruction.opcode != OpCodes.Call || instruction.operand is not MethodInfo method ||
            method.Name != "ClearRect" || method.DeclaringType?.FullName != GdiPaintTypeName)
        {
            return false;
        }

        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length == 3 &&
            parameters[0].ParameterType == typeof(nint) &&
            parameters[1].ParameterType.FullName == RectInt32TypeName &&
            parameters[2].ParameterType == typeof(ColorBgr24);
    }

    private static bool IsBackgroundFillCall(MethodInfo method)
    {
        if (method.Name != "FillRectangle")
        {
            return false;
        }
        ParameterInfo[] parameters = method.GetParameters();

        return method.IsStatic &&
            method.DeclaringType?.FullName == DrawingContextExtensionsTypeName &&
            parameters.Length == 3 &&
            parameters[0].ParameterType.FullName == "PaintDotNet.Direct2D1.IDrawingContext";
    }

    private static MethodInfo CreateConditionalFillMethod(MethodInfo fillRectangleMethod)
    {
        ParameterInfo[] parameters = fillRectangleMethod.GetParameters();
        Type[] parameterTypes = Array.ConvertAll(parameters, parameter => parameter.ParameterType);

        DynamicMethod conditionalFill = new(
            "PDNClassic_FillZoomSliderBackground",
            typeof(void),
            parameterTypes,
            typeof(ClassicThemeFix).Module,
            skipVisibility: true);

        MethodInfo shouldDrawBackground = typeof(ClassicThemeFix).GetMethod(
            nameof(ShouldDrawZoomSliderBackground),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(ClassicThemeFix).FullName,
                nameof(ShouldDrawZoomSliderBackground));

        ILGenerator il = conditionalFill.GetILGenerator();
        Label drawBackground = il.DefineLabel();
        il.Emit(OpCodes.Call, shouldDrawBackground);
        il.Emit(OpCodes.Brtrue, drawBackground);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(drawBackground);
        for (short index = 0; index < parameterTypes.Length; ++index)
        {
            il.Emit(OpCodes.Ldarg, index);
        }
        il.Emit(OpCodes.Call, fillRectangleMethod);
        il.Emit(OpCodes.Ret);
        return conditionalFill;
    }

    private static bool ShouldDrawZoomSliderBackground()
    {
        return StatusBarFix.IsAeroTheme() && !OldThemeColorsFix.IsOldPaletteActive();
    }

    private static ColorBgr24 CorrectColor(ColorBgr24 color)
    {
        // GdiPaint passes ColorBgr24.Bgr to CreateSolidBrush, but Win32 COLORREF
        // stores red in the low byte. Correct the argument at the non-inlineable
        // control paint callers so the JIT cannot bypass the fix by inlining ClearRect.
        (color.B, color.R) = (color.R, color.B);
        return color;
    }
}
