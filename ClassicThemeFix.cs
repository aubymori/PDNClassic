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
    private const string GdiPaintTypeName = "PaintDotNet.Gdi.GdiPaint";
    private const string RectInt32TypeName = "PaintDotNet.Rendering.RectInt32";

    private static readonly object sync = new();
    private static bool controlsPatched;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        lock (sync)
        {
            if (controlsPatched)
            {
                return;
            }

            Type? checkBoxType = assembly.GetType(CheckBoxTypeName, throwOnError: false, ignoreCase: false);
            if (checkBoxType == null)
            {
                return;
            }

            Type radioButtonType = assembly.GetType(RadioButtonTypeName, throwOnError: true, ignoreCase: false)!;
            MethodInfo transpiler = typeof(ClassicThemeFix).GetMethod(
                nameof(MeasureAndDrawTranspiler),
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(ClassicThemeFix).FullName, nameof(MeasureAndDrawTranspiler));

            HarmonyMethod harmonyTranspiler = new(transpiler);
            harmony.Patch(FindMeasureAndDraw(checkBoxType), transpiler: harmonyTranspiler);
            harmony.Patch(FindMeasureAndDraw(radioButtonType), transpiler: harmonyTranspiler);
            controlsPatched = true;
        }
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

    private static ColorBgr24 CorrectColor(ColorBgr24 color)
    {
        // GdiPaint passes ColorBgr24.Bgr to CreateSolidBrush, but Win32 COLORREF
        // stores red in the low byte. Correct the argument at the non-inlineable
        // control paint callers so the JIT cannot bypass the fix by inlining ClearRect.
        (color.B, color.R) = (color.R, color.B);
        return color;
    }
}
