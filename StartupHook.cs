using HarmonyLib;
using PaintDotNet.DirectWrite;
using PaintDotNet.Rendering;
using System;
using System.Windows;

internal class StartupHook
{
    private static Harmony harmony = new Harmony("aubymori.pdnclassic");

    public static void Initialize()
    {
        try
        {
            harmony = new Harmony("aubymori.pdnclassic");


        }
        catch (Exception e)
        {
            System.Windows.MessageBox.Show($"{e.Message}\n{e.StackTrace}", "PDNClassic", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(0);
        }
    }
}
