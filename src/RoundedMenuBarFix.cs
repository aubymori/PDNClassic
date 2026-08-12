using HarmonyLib;
using PaintDotNet;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Reflection.Emit;

internal static class RoundedMenuBarFix
{
    private const string PdnToolBarTypeName = "PaintDotNet.Controls.PdnToolBar";
    private const int LegacyCornerRadiusDip = 8;
    private sealed record CornerGeometry(Rectangle LeftBounds, Rectangle RightBounds);


    private static readonly object sync = new();
    private static object? geometryPath;
    private static CornerGeometry? geometry;
    private static bool patched;
    private static MethodInfo? addLineMethod;
    private static MethodInfo? addArcMethod;
    private static FieldInfo? outlinePathField;
    private static FieldInfo? penBrushCacheField;
    private static PropertyInfo? toolBarOutlineColorProperty;
    private static MethodInfo? getSolidBrushMethod;
    private static MethodInfo? getPenMethod;
    private static MethodInfo? fillRegionMethod;
    private static MethodInfo? drawPathMethod;
    private static PropertyInfo? smoothingModeProperty;
    private static PropertyInfo? compositingModeProperty;
    private static object? sourceCopyCompositingMode;
    private static object? antiAliasSmoothingMode;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        if (!PDNClassicSettingsFix.RoundedMenuBarEnabledAtStartup)
        {
            return;
        }

        lock (sync)
        {
            if (patched)
            {
                return;
            }

            Type? toolBarType = assembly.GetType(
                PdnToolBarTypeName,
                throwOnError: false,
                ignoreCase: false);
            if (toolBarType == null)
            {
                return;
            }

            outlinePathField = toolBarType.GetField(
                "outlinePath",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(toolBarType.FullName, "outlinePath");
            penBrushCacheField = toolBarType.GetField(
                "penBrushCache",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(toolBarType.FullName, "penBrushCache");

            Type aeroColorsType = FindLoadedType("PaintDotNet.VisualStyling.AeroColors")
                ?? throw new TypeLoadException("Could not load PaintDotNet.VisualStyling.AeroColors.");
            toolBarOutlineColorProperty = aeroColorsType.GetProperty(
                "ToolBarOutlineColor",
                BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMemberException(aeroColorsType.FullName, "ToolBarOutlineColor");

            MethodInfo paintBackground = FindPaintBackground(toolBarType);
            MethodInfo transpiler = GetPatchMethod(nameof(PaintBackgroundTranspiler));
            MethodInfo postfix = GetPatchMethod(nameof(PaintBackgroundPostfix));

            harmony.Patch(
                paintBackground,
                postfix: new HarmonyMethod(postfix),
                transpiler: new HarmonyMethod(transpiler));
            patched = true;
        }
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (Assembly loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = loadedAssembly.GetType(
                fullName,
                throwOnError: false,
                ignoreCase: false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static MethodInfo FindPaintBackground(Type toolBarType)
    {
        foreach (MethodInfo method in toolBarType.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (method.Name == "PaintBackground" &&
                parameters.Length == 2 &&
                parameters[0].ParameterType.FullName == "System.Drawing.Graphics" &&
                parameters[1].ParameterType == typeof(Rectangle))
            {
                return method;
            }
        }

        throw new MissingMethodException(toolBarType.FullName, "PaintBackground(Graphics, Rectangle)");
    }

    private static IEnumerable<CodeInstruction> PaintBackgroundTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        const string graphicsPathTypeName = "System.Drawing.Drawing2D.GraphicsPath";
        MethodInfo addRoundedOutline = GetPatchMethod(nameof(AddRoundedOutline));

        int replacementCount = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo calledMethod &&
                calledMethod.Name == "AddLines" &&
                calledMethod.DeclaringType?.FullName == graphicsPathTypeName)
            {
                yield return new CodeInstruction(OpCodes.Call, addRoundedOutline)
                    .MoveLabelsFrom(instruction)
                    .MoveBlocksFrom(instruction);
                ++replacementCount;
            }
            else
            {
                yield return instruction;
            }
        }

        if (replacementCount != 1)
        {
            throw new InvalidOperationException(
                $"Expected one PdnToolBar outline call, found {replacementCount}.");
        }
    }

    private static MethodInfo GetPatchMethod(string name)
    {
        return typeof(RoundedMenuBarFix).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RoundedMenuBarFix).FullName, name);
    }

    private static void PaintBackgroundPostfix(object __instance, object __0)
    {
        object? path = outlinePathField!.GetValue(__instance);
        if (path == null || !ReferenceEquals(path, geometryPath) || geometry == null)
        {
            return;
        }

        object penBrushCache = penBrushCacheField!.GetValue(__instance)
            ?? throw new InvalidOperationException("PdnToolBar pen brush cache is unavailable.");
        Type penBrushCacheType = penBrushCache.GetType();
        getSolidBrushMethod ??= penBrushCacheType.GetMethod(
            "GetSolidBrush",
            new[] { typeof(Color) })
            ?? throw new MissingMethodException(penBrushCacheType.FullName, "GetSolidBrush(Color)");
        getPenMethod ??= penBrushCacheType.GetMethod(
            "GetPen",
            new[] { typeof(Color) })
            ?? throw new MissingMethodException(penBrushCacheType.FullName, "GetPen(Color)");
        Type graphicsType = __0.GetType();
        Type pathType = path.GetType();
        Type regionType = pathType.Assembly.GetType(
            "System.Drawing.Region",
            throwOnError: true,
            ignoreCase: false)!;
        object exteriorRegion = Activator.CreateInstance(regionType, new object[] { geometry.LeftBounds })
            ?? throw new InvalidOperationException("Could not create the rounded menu corner region.");
        try
        {
            regionType.GetMethod("Union", new[] { typeof(Rectangle) })!
                .Invoke(exteriorRegion, new object[] { geometry.RightBounds });
            regionType.GetMethod("Exclude", new[] { pathType })!
                .Invoke(exteriorRegion, new[] { path });


            fillRegionMethod ??= FindInstanceMethod(
                graphicsType,
                "FillRegion",
                "System.Drawing.Brush",
                "System.Drawing.Region");
            compositingModeProperty ??= graphicsType.GetProperty(
                "CompositingMode",
                BindingFlags.Instance | BindingFlags.Public)
                ?? throw new MissingMemberException(graphicsType.FullName, "CompositingMode");
            sourceCopyCompositingMode ??= Enum.Parse(
                compositingModeProperty.PropertyType,
                "SourceCopy");

            object transparentBrush = getSolidBrushMethod.Invoke(
                penBrushCache,
                new object[] { Color.Transparent })!;
            object? previousCompositingMode = compositingModeProperty.GetValue(__0);
            try
            {
                compositingModeProperty.SetValue(__0, sourceCopyCompositingMode);
                fillRegionMethod.Invoke(__0, new[] { transparentBrush, exteriorRegion });
            }
            finally
            {
                compositingModeProperty.SetValue(__0, previousCompositingMode);
            }
        }
        finally
        {
            ((IDisposable)exteriorRegion).Dispose();
        }


        object outlineColor = toolBarOutlineColorProperty!.GetValue(null)!;
        object pen = getPenMethod.Invoke(penBrushCache, new[] { outlineColor })!;

        drawPathMethod ??= FindInstanceMethod(
            graphicsType,
            "DrawPath",
            "System.Drawing.Pen",
            "System.Drawing.Drawing2D.GraphicsPath");
        smoothingModeProperty ??= graphicsType.GetProperty(
            "SmoothingMode",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(graphicsType.FullName, "SmoothingMode");
        antiAliasSmoothingMode ??= Enum.Parse(
            smoothingModeProperty.PropertyType,
            "AntiAlias");

        object? previousSmoothingMode = smoothingModeProperty.GetValue(__0);
        try
        {
            smoothingModeProperty.SetValue(__0, antiAliasSmoothingMode);
            drawPathMethod.Invoke(__0, new[] { pen, path });
        }
        finally
        {
            smoothingModeProperty.SetValue(__0, previousSmoothingMode);
        }
    }

    private static MethodInfo FindInstanceMethod(
        Type type,
        string name,
        string firstParameterTypeName,
        string secondParameterTypeName)
    {
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (method.Name == name &&
                parameters.Length == 2 &&
                parameters[0].ParameterType.FullName == firstParameterTypeName &&
                parameters[1].ParameterType.FullName == secondParameterTypeName)
            {
                return method;
            }
        }

        throw new MissingMethodException(
            type.FullName,
            $"{name}({firstParameterTypeName}, {secondParameterTypeName})");
    }


    private static void AddRoundedOutline(object path, Point[] outlinePoints)
    {
        if (outlinePoints.Length != 9)
        {
            throw new InvalidOperationException(
                $"Expected nine PdnToolBar outline points, found {outlinePoints.Length}.");
        }

        int menuTop = outlinePoints[3].Y;
        int folderTop = outlinePoints[2].Y;
        int folderLeft = outlinePoints[3].X;
        int menuRight = outlinePoints[4].X;
        int requestedRadius = UIScaleFactor.Current.ConvertDipsToPixelsInt(LegacyCornerRadiusDip);
        int radius = Math.Max(
            1,
            Math.Min(requestedRadius, Math.Min(folderTop - menuTop, (menuRight - folderLeft) / 2)));
        geometryPath = path;
        geometry = new CornerGeometry(
            new Rectangle(folderLeft, menuTop, radius + 1, radius + 2),
            new Rectangle(menuRight - radius, menuTop, radius + 1, radius + 2));



        Type pathType = path.GetType();
        addLineMethod ??= pathType.GetMethod(
            "AddLine",
            new[] { typeof(Point), typeof(Point) })
            ?? throw new MissingMethodException(pathType.FullName, "AddLine(Point, Point)");
        addArcMethod ??= pathType.GetMethod(
            "AddArc",
            new[] { typeof(Rectangle), typeof(float), typeof(float) })
            ?? throw new MissingMethodException(pathType.FullName, "AddArc(Rectangle, float, float)");

        AddLine(path, outlinePoints[0], outlinePoints[1]);
        AddLine(path, outlinePoints[1], outlinePoints[2]);
        AddLine(path, outlinePoints[2], new Point(folderLeft, folderTop - 1));
        AddArc(path, new Rectangle(folderLeft, menuTop, radius, radius), 180f, 90f);
        AddLine(
            path,
            new Point(folderLeft + radius, menuTop),
            new Point(menuRight - radius, menuTop));
        AddArc(
            path,
            new Rectangle(menuRight - radius, menuTop, radius, radius + 1),
            -90f,
            90f);
        AddLine(
            path,
            new Point(menuRight, menuTop + radius + 1),
            new Point(menuRight, folderTop - 1));
        AddLine(path, outlinePoints[5], outlinePoints[6]);
        AddLine(path, outlinePoints[6], outlinePoints[7]);
        AddLine(path, outlinePoints[7], outlinePoints[8]);
    }

    private static void AddLine(object path, Point start, Point end)
    {
        addLineMethod!.Invoke(path, new object[] { start, end });
    }

    private static void AddArc(object path, Rectangle bounds, float startAngle, float sweepAngle)
    {
        addArcMethod!.Invoke(path, new object[] { bounds, startAngle, sweepAngle });
    }

}
