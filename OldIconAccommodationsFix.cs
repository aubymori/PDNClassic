using HarmonyLib;
using PaintDotNet;
using PaintDotNet.Imaging;
using PaintDotNet.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

internal static class OldIconAccommodationsFix
{
    private const string FontStyleButtonGroupTypeName =
        "PaintDotNet.Controls.ToolConfigUI.FontStyleFlagsButtonGroup";
    private const string ShapeTypeName = "PaintDotNet.Shapes.Shape";
    private const uint CurrentShapeColor = 4283071921u;
    private const uint LegacyShapeOutlineColor = 4283995329u;
    private const uint LegacyShapeGradientStartColor = 4290830835u;
    private const uint LegacyShapeGradientEndColor = 4292931576u;

    private static bool fontStylePatched;
    private static bool shapePatched;
    private static Type? linearGradientBrushType;
    private static Type? gradientStopType;
    private static PropertyInfo? gradientStopsProperty;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        if (!PDNClassicSettingsFix.OldIconAccommodationsEnabledAtStartup)
        {
            return;
        }

        lock (typeof(OldIconAccommodationsFix))
        {
            Type? buttonGroupType = assembly.GetType(
                FontStyleButtonGroupTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (!fontStylePatched && buttonGroupType is not null)
            {
                MethodInfo getButtonImage = buttonGroupType.GetMethod(
                    "GetButtonImage",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: new[] { typeof(Enum) },
                    modifiers: null)
                    ?? throw new MissingMethodException(buttonGroupType.FullName, "GetButtonImage(Enum)");

                harmony.Patch(
                    getButtonImage,
                    transpiler: new HarmonyMethod(GetPatchMethod(nameof(GetButtonImageTranspiler))));
                fontStylePatched = true;
            }

            Type? shapeType = assembly.GetType(
                ShapeTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (!shapePatched && shapeType is not null)
            {
                MethodInfo drawImageResource = shapeType.GetMethod(
                    "DrawImageResource",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    ?? throw new MissingMethodException(shapeType.FullName, "DrawImageResource");
                Type mediaAssemblyMarker = drawImageResource.GetMethodBody()?.LocalVariables
                    .Select(variable => variable.LocalType)
                    .FirstOrDefault(type => type.FullName == "PaintDotNet.UI.Media.Brush")
                    ?? throw new TypeLoadException("PaintDotNet.UI.Media.Brush");
                Assembly mediaAssembly = mediaAssemblyMarker.Assembly;
                Type gradientBrushType = mediaAssembly.GetType(
                    "PaintDotNet.UI.Media.LinearGradientBrush",
                    throwOnError: true,
                    ignoreCase: false)
                    ?? throw new TypeLoadException("PaintDotNet.UI.Media.LinearGradientBrush");
                linearGradientBrushType = gradientBrushType;
                gradientStopType = mediaAssembly.GetType(
                    "PaintDotNet.UI.Media.GradientStop",
                    throwOnError: true,
                    ignoreCase: false)
                    ?? throw new TypeLoadException("PaintDotNet.UI.Media.GradientStop");
                gradientStopsProperty = gradientBrushType.BaseType?.GetProperty(
                    "GradientStops",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingMemberException(
                        linearGradientBrushType.FullName,
                        "GradientStops");

                harmony.Patch(
                    drawImageResource,
                    transpiler: new HarmonyMethod(GetPatchMethod(nameof(ShapeDrawImageResourceTranspiler))));
                shapePatched = true;
            }
        }
    }

    private static IEnumerable<CodeInstruction> GetButtonImageTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        _ = instructions;
        Type baseType = original.DeclaringType?.BaseType
            ?? throw new MissingMemberException(original.DeclaringType?.FullName, "BaseType");
        MethodInfo baseGetButtonImage = baseType.GetMethod(
            "GetButtonImage",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            binder: null,
            types: new[] { typeof(Enum) },
            modifiers: null)
            ?? throw new MissingMethodException(baseType.FullName, "GetButtonImage(Enum)");

        // The base implementation resolves Icons.Enum.FontStyle.{value}, matching
        // the bitmap resource names used by Paint.NET 4.1.5. Bypass 5.1.12's
        // FontStyle-specific DirectWrite renderer.
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Ldarg_1);
        yield return new CodeInstruction(OpCodes.Call, baseGetButtonImage);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static IEnumerable<CodeInstruction> ShapeDrawImageResourceTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        List<CodeInstruction> source = instructions.ToList();
        Type brushType = original.GetMethodBody()?.LocalVariables
            .Select(variable => variable.LocalType)
            .FirstOrDefault(type => type.FullName == "PaintDotNet.UI.Media.Brush")
            ?? throw new TypeLoadException("PaintDotNet.UI.Media.Brush");
        MethodInfo createFillBrush = CreateFactoryWrapper(
            "PDNClassic_CreateLegacyShapeFillBrush",
            brushType,
            nameof(CreateLegacyShapeFillBrush),
            new[] { typeof(object), typeof(int), typeof(int), typeof(double) });
        MethodInfo createOutlineBrush = CreateFactoryWrapper(
            "PDNClassic_CreateLegacyShapeOutlineBrush",
            brushType,
            nameof(CreateLegacyShapeOutlineBrush),
            Type.EmptyTypes);
        MethodInfo getOutlineColor = GetPatchMethod(nameof(GetLegacyShapeOutlineColor));

        int fillStoreIndex = -1;
        int fillLocalIndex = -1;
        for (int index = 0; index < source.Count; ++index)
        {
            if (!LoadsUInt32(source[index], CurrentShapeColor))
            {
                continue;
            }

            for (int candidate = index + 1;
                candidate < Math.Min(source.Count, index + 8);
                ++candidate)
            {
                if (TryGetStoredLocalIndex(source[candidate], out fillLocalIndex))
                {
                    fillStoreIndex = candidate;
                    break;
                }
            }
            break;
        }

        if (fillStoreIndex < 0)
        {
            throw new MissingMethodException(
                original.DeclaringType?.FullName,
                "current shape fill brush initialization");
        }

        int outlineLoadIndex = -1;
        for (int index = fillStoreIndex + 1; index + 1 < source.Count; ++index)
        {
            if (TryGetLoadedLocalIndex(source[index], out int loadedLocalIndex) &&
                loadedLocalIndex == fillLocalIndex &&
                TryGetStoredLocalIndex(source[index + 1], out int storedLocalIndex) &&
                storedLocalIndex != fillLocalIndex)
            {
                outlineLoadIndex = index;
                break;
            }
        }

        if (outlineLoadIndex < 0)
        {
            throw new MissingMethodException(
                original.DeclaringType?.FullName,
                "shape outline brush initialization");
        }

        bool overlayColorPatched = false;
        for (int index = 0; index < source.Count; ++index)
        {
            CodeInstruction instruction = source[index];
            if (index == outlineLoadIndex)
            {
                CodeInstruction replacement = new(OpCodes.Call, createOutlineBrush);
                replacement.labels.AddRange(instruction.labels);
                replacement.blocks.AddRange(instruction.blocks);
                yield return replacement;
            }
            else if (instruction.operand is MethodInfo method &&
                method.Name == "get_White" &&
                method.DeclaringType == typeof(ColorBgra))
            {
                CodeInstruction replacement = new(OpCodes.Call, getOutlineColor);
                replacement.labels.AddRange(instruction.labels);
                replacement.blocks.AddRange(instruction.blocks);
                yield return replacement;
                overlayColorPatched = true;
            }
            else
            {
                yield return instruction;
            }

            if (index == fillStoreIndex)
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return new CodeInstruction(OpCodes.Ldarg_3);
                yield return new CodeInstruction(OpCodes.Ldarg_S, 4);
                yield return new CodeInstruction(OpCodes.Call, createFillBrush);
                yield return CreateStoreLocalInstruction(source[fillStoreIndex], fillLocalIndex);
            }
        }

        if (!overlayColorPatched)
        {
            throw new MissingMethodException(
                original.DeclaringType?.FullName,
                "shape image overlay color");
        }
    }

    private static MethodInfo CreateFactoryWrapper(
        string name,
        Type resultType,
        string factoryName,
        Type[] parameterTypes)
    {
        DynamicMethod wrapper = new(
            name,
            resultType,
            parameterTypes,
            typeof(OldIconAccommodationsFix).Module,
            skipVisibility: true);
        MethodInfo factory = GetPatchMethod(factoryName);
        ILGenerator il = wrapper.GetILGenerator();
        for (short index = 0; index < parameterTypes.Length; ++index)
        {
            il.Emit(OpCodes.Ldarg, index);
        }
        il.Emit(OpCodes.Call, factory);
        il.Emit(OpCodes.Castclass, resultType);
        il.Emit(OpCodes.Ret);
        return wrapper;
    }

    private static object CreateLegacyShapeFillBrush(
        object shape,
        int width,
        int height,
        double borderSize)
    {
        if (linearGradientBrushType is null ||
            gradientStopType is null ||
            gradientStopsProperty is null)
        {
            throw new InvalidOperationException("Legacy shape brush reflection state is unavailable.");
        }

        double insetAmount = borderSize / 2.0;
        RectDouble insetBounds = new(
            insetAmount,
            insetAmount,
            width - borderSize,
            height - borderSize);
        double aspectRatio = (double)(shape.GetType().GetProperty(
            "AspectRatio",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shape)
            ?? throw new MissingMemberException(shape.GetType().FullName, "AspectRatio"));
        RectDouble bounds;
        if (aspectRatio > 1.0)
        {
            double newHeight = insetBounds.Height / aspectRatio;
            bounds = new RectDouble(
                insetBounds.X,
                insetBounds.Y + ((insetBounds.Height - newHeight) / 2.0),
                insetBounds.Width,
                newHeight);
        }
        else if (aspectRatio < 1.0)
        {
            double newWidth = insetBounds.Width * aspectRatio;
            bounds = new RectDouble(
                insetBounds.X + ((insetBounds.Width - newWidth) / 2.0),
                insetBounds.Y,
                newWidth,
                insetBounds.Height);
        }
        else
        {
            bounds = insetBounds;
        }

        object brush = Activator.CreateInstance(linearGradientBrushType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create LinearGradientBrush.");
        linearGradientBrushType.GetProperty("StartPoint")?.SetValue(brush, bounds.TopLeft);
        linearGradientBrushType.GetProperty("EndPoint")?.SetValue(brush, bounds.BottomRight);
        object stops = gradientStopsProperty.GetValue(brush)
            ?? throw new InvalidOperationException("LinearGradientBrush.GradientStops is null.");
        MethodInfo addStop = stops.GetType().GetMethod(
            "Add",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { gradientStopType },
            modifiers: null)
            ?? throw new MissingMethodException(stops.GetType().FullName, "Add(GradientStop)");
        addStop.Invoke(stops, new[] { CreateGradientStop(LegacyShapeGradientStartColor, 0.0) });
        addStop.Invoke(stops, new[] { CreateGradientStop(LegacyShapeGradientEndColor, 1.0) });
        return brush;
    }

    private static object CreateGradientStop(uint color, double offset)
    {
        if (gradientStopType is null)
        {
            throw new InvalidOperationException("GradientStop type is unavailable.");
        }

        ColorRgba128Float convertedColor = ColorBgra32.FromUInt32(color);
        return Activator.CreateInstance(
            gradientStopType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { convertedColor, offset },
            culture: null)
            ?? throw new InvalidOperationException("Could not create GradientStop.");
    }

    private static object CreateLegacyShapeOutlineBrush()
    {
        Type cacheType = linearGradientBrushType?.Assembly.GetType(
            "PaintDotNet.UI.Media.SolidColorBrushCache",
            throwOnError: true,
            ignoreCase: false)
            ?? throw new TypeLoadException("PaintDotNet.UI.Media.SolidColorBrushCache");
        MethodInfo getBrush = cacheType.GetMethod(
            "Get",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(ColorRgba128Float) },
            modifiers: null)
            ?? throw new MissingMethodException(cacheType.FullName, "Get(ColorRgba128Float)");
        ColorRgba128Float color = ColorBgra32.FromUInt32(LegacyShapeOutlineColor);
        return getBrush.Invoke(null, new object[] { color })
            ?? throw new InvalidOperationException("Could not obtain the legacy shape outline brush.");
    }

    private static ColorBgra GetLegacyShapeOutlineColor()
    {
        return ColorBgra.FromUInt32(LegacyShapeOutlineColor);
    }

    private static bool LoadsUInt32(CodeInstruction instruction, uint value)
    {
        return instruction.opcode == OpCodes.Ldc_I4 &&
            instruction.operand is int operand &&
            operand == unchecked((int)value);
    }

    private static bool TryGetLoadedLocalIndex(CodeInstruction instruction, out int index)
    {
        if (instruction.opcode == OpCodes.Ldloc_0) { index = 0; return true; }
        if (instruction.opcode == OpCodes.Ldloc_1) { index = 1; return true; }
        if (instruction.opcode == OpCodes.Ldloc_2) { index = 2; return true; }
        if (instruction.opcode == OpCodes.Ldloc_3) { index = 3; return true; }
        if (instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S)
        {
            return TryGetLocalOperandIndex(instruction.operand, out index);
        }
        index = -1;
        return false;
    }

    private static bool TryGetStoredLocalIndex(CodeInstruction instruction, out int index)
    {
        if (instruction.opcode == OpCodes.Stloc_0) { index = 0; return true; }
        if (instruction.opcode == OpCodes.Stloc_1) { index = 1; return true; }
        if (instruction.opcode == OpCodes.Stloc_2) { index = 2; return true; }
        if (instruction.opcode == OpCodes.Stloc_3) { index = 3; return true; }
        if (instruction.opcode == OpCodes.Stloc || instruction.opcode == OpCodes.Stloc_S)
        {
            return TryGetLocalOperandIndex(instruction.operand, out index);
        }
        index = -1;
        return false;
    }

    private static bool TryGetLocalOperandIndex(object? operand, out int index)
    {
        switch (operand)
        {
            case LocalBuilder local:
                index = local.LocalIndex;
                return true;
            case byte byteIndex:
                index = byteIndex;
                return true;
            case int intIndex:
                index = intIndex;
                return true;
            default:
                index = -1;
                return false;
        }
    }

    private static CodeInstruction CreateStoreLocalInstruction(
        CodeInstruction template,
        int localIndex)
    {
        return localIndex switch
        {
            0 => new CodeInstruction(OpCodes.Stloc_0),
            1 => new CodeInstruction(OpCodes.Stloc_1),
            2 => new CodeInstruction(OpCodes.Stloc_2),
            3 => new CodeInstruction(OpCodes.Stloc_3),
            _ => new CodeInstruction(template.opcode, template.operand)
        };
    }

    private static MethodInfo GetPatchMethod(string name)
    {
        return typeof(OldIconAccommodationsFix).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(OldIconAccommodationsFix).FullName, name);
    }
}
