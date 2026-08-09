using HarmonyLib;
using System;
using System.Reflection;
using System.Windows;

internal static class StartupHook
{
    private const string HarmonyId = "aubymori.pdnclassic";

    private static readonly object sync = new();
    private static Harmony harmony = new(HarmonyId);
    private static bool initialized;

    public static void Initialize()
    {
        try
        {
            lock (sync)
            {
                if (initialized)
                {
                    return;
                }

                initialized = true;
                harmony = new Harmony(HarmonyId);
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    ApplyFixes(assembly);
                }
            }
        }
        catch (Exception e)
        {
            ShowInitializationError(e);
        }
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs e)
    {
        try
        {
            lock (sync)
            {
                ApplyFixes(e.LoadedAssembly);
            }
        }
        catch (Exception ex)
        {
            ShowInitializationError(ex);
        }
    }

    private static void ApplyFixes(Assembly assembly)
    {
        StatusBarFix.Apply(harmony, assembly);
    }

    private static void ShowInitializationError(Exception e)
    {
        System.Windows.MessageBox.Show(
            $"{e.Message}\n{e.StackTrace}",
            "PDNClassic",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Environment.Exit(0);
    }
}

