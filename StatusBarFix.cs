using HarmonyLib;
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

internal static class StatusBarFix
{
    private const string StatusStripRendererTypeName = "PaintDotNet.Controls.PdnStatusStripRenderer";
    private const string ThemeConfigTypeName = "PaintDotNet.VisualStyling.ThemeConfig";

    private static readonly object sync = new();

    private static Action<object, object> callBaseOnRenderItemText = null!;
    private static Func<int>? getEffectiveTheme;
    private static int aeroThemeValue;
    private static bool statusStripRendererPatched;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        lock (sync)
        {
            if (statusStripRendererPatched)
            {
                return;
            }

            Type? rendererType = assembly.GetType(StatusStripRendererTypeName, throwOnError: false, ignoreCase: false);
            if (rendererType == null)
            {
                return;
            }

            MethodInfo rendererMethod = GetDeclaredSingleParameterMethod(rendererType, "OnRenderItemText");
            Type eventArgsType = rendererMethod.GetParameters()[0].ParameterType;

            Type baseType = rendererType.BaseType
                ?? throw new MissingMethodException(rendererType.FullName, "BaseType");
            MethodInfo baseRendererMethod = GetDeclaredSingleParameterMethod(
                baseType,
                "OnRenderItemText",
                eventArgsType);

            callBaseOnRenderItemText = CreateBaseRendererInvoker(baseRendererMethod, eventArgsType);
            TryInitializeThemeGetter();

            MethodInfo prefix = typeof(StatusBarFix).GetMethod(
                nameof(StatusStripRendererOnRenderItemTextPrefix),
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(StatusBarFix).FullName, nameof(StatusStripRendererOnRenderItemTextPrefix));

            harmony.Patch(rendererMethod, prefix: new HarmonyMethod(prefix));
            statusStripRendererPatched = true;
        }
    }

    private static MethodInfo GetDeclaredSingleParameterMethod(
        Type type,
        string name,
        Type? parameterType = null)
    {
        MethodInfo? result = null;
        foreach (MethodInfo method in type.GetMethods(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (method.Name != name)
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 1 ||
                (parameterType != null && parameters[0].ParameterType != parameterType))
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

    private static Action<object, object> CreateBaseRendererInvoker(
        MethodInfo baseRendererMethod,
        Type eventArgsType)
    {
        DynamicMethod invoker = new(
            "PDNClassic_CallBaseOnRenderItemText",
            typeof(void),
            new[] { typeof(object), typeof(object) },
            typeof(StatusBarFix).Module,
            skipVisibility: true);

        ILGenerator il = invoker.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, baseRendererMethod.DeclaringType!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, eventArgsType);
        il.Emit(OpCodes.Call, baseRendererMethod);
        il.Emit(OpCodes.Ret);

        return (Action<object, object>)invoker.CreateDelegate(typeof(Action<object, object>));
    }

    private static void TryInitializeThemeGetter()
    {
        if (getEffectiveTheme != null)
        {
            return;
        }

        Type? themeConfigType = FindLoadedType(ThemeConfigTypeName);
        PropertyInfo? effectiveThemeProperty = themeConfigType?.GetProperty(
            "EffectiveTheme",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? effectiveThemeGetter = effectiveThemeProperty?.GetMethod;
        if (effectiveThemeGetter == null || !effectiveThemeProperty!.PropertyType.IsEnum)
        {
            return;
        }

        object aeroValue = Enum.Parse(effectiveThemeProperty.PropertyType, "Aero", ignoreCase: false);
        aeroThemeValue = Convert.ToInt32(aeroValue);
        getEffectiveTheme = CreateThemeGetter(effectiveThemeGetter);
    }

    private static Func<int> CreateThemeGetter(MethodInfo effectiveThemeGetter)
    {
        DynamicMethod getter = new(
            "PDNClassic_GetEffectiveTheme",
            typeof(int),
            Type.EmptyTypes,
            typeof(StatusBarFix).Module,
            skipVisibility: true);

        ILGenerator il = getter.GetILGenerator();
        il.Emit(OpCodes.Call, effectiveThemeGetter);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ret);

        return (Func<int>)getter.CreateDelegate(typeof(Func<int>));
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static bool StatusStripRendererOnRenderItemTextPrefix(
        object __instance,
        object __0)
    {
        if (IsAeroTheme() && !OldThemeColorsFix.IsOldPaletteActive())
        {
            return true;
        }

        callBaseOnRenderItemText(__instance, __0);
        return false;
    }

    internal static bool IsAeroTheme()
    {
        try
        {
            Func<int>? getter = Volatile.Read(ref getEffectiveTheme);
            if (getter == null)
            {
                lock (sync)
                {
                    TryInitializeThemeGetter();
                    getter = getEffectiveTheme;
                }
            }

            return getter != null && getter() == Volatile.Read(ref aeroThemeValue);
        }
        catch
        {
            // Falling back to the base renderer is safe for every non-Aero theme.
            return false;
        }
    }
}
