using HarmonyLib;
using PaintDotNet;
using Microsoft.Win32;
using System;
using System.Drawing;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

internal static class PDNClassicSettingsFix
{
    private const string SettingsDialogTypeName = "PaintDotNet.Settings.UI.SettingsDialog";
    private const string RegistryPath = @"Software\paint.net\PDNClassic";
    private const string AeroGlassValueName = "EnableAeroGlass";
    private const string OldColorsValueName = "UseOldColors";
    private const string OldIconAccommodationsValueName = "UseOldIconAccommodations";
    private const string MetroCloseButtonsValueName = "UseMetroCloseButtons";
    private const string OldToolWindowPositioningValueName = "UseOldToolWindowPositioning";
    private const string GdiClassicFontRenderingValueName = "UseGdiClassicFontRendering";
    private const string RoundedMenuBarValueName = "UseRoundedMenuBar";
    private const string LegacyControlStylesValueName = "UseLegacyControlStyles";
    private const string RepositoryUrl = "https://github.com/aubymori/PDNClassic";

    private static readonly object sync = new();
    private static readonly ConditionalWeakTable<object, DialogState> dialogStates = new();
    private static readonly bool enabledAtStartup = ReadAeroGlassEnabled();
    private static readonly bool oldColorsEnabledAtStartup = ReadOldColorsEnabled();
    private static readonly bool oldIconAccommodationsEnabledAtStartup =
        ReadOldIconAccommodationsEnabled();
    private static readonly bool metroCloseButtonsEnabledAtStartup =
        ReadMetroCloseButtonsEnabled();
    private static readonly bool oldToolWindowPositioningEnabledAtStartup =
        ReadOldToolWindowPositioningEnabled();
    private static readonly bool gdiClassicFontRenderingEnabledAtStartup =
        ReadGdiClassicFontRenderingEnabled();
    private static readonly bool roundedMenuBarEnabledAtStartup =
        ReadRoundedMenuBarEnabled();
    private static readonly bool legacyControlStylesEnabledAtStartup =
        ReadLegacyControlStylesEnabled();
    private static bool patched;
    private static ConstructorInfo? runtimeSectionConstructor;
    private static FieldInfo? appSettingsField;
    private static FieldInfo? settingsSectionsField;
    private static FieldInfo? settingsPagesField;
    private static FieldInfo? sectionsListBoxField;

    internal static bool AeroGlassEnabledAtStartup => enabledAtStartup;
    internal static bool OldColorsEnabledAtStartup => oldColorsEnabledAtStartup;
    internal static bool OldIconAccommodationsEnabledAtStartup =>
        oldIconAccommodationsEnabledAtStartup;
    internal static bool MetroIconsEnabledAtStartup =>
        metroCloseButtonsEnabledAtStartup;
    internal static bool OldToolWindowPositioningEnabledAtStartup =>
        oldToolWindowPositioningEnabledAtStartup;
    internal static bool GdiClassicFontRenderingEnabledAtStartup =>
        gdiClassicFontRenderingEnabledAtStartup;
    internal static bool RoundedMenuBarEnabledAtStartup =>
        roundedMenuBarEnabledAtStartup;
    internal static bool LegacyControlStylesEnabledAtStartup =>
        legacyControlStylesEnabledAtStartup;

    internal static void Apply(Harmony harmony, Assembly assembly)
    {
        lock (sync)
        {
            if (patched)
            {
                return;
            }

            Type? dialogType = assembly.GetType(SettingsDialogTypeName, throwOnError: false, ignoreCase: false);
            if (dialogType == null)
            {
                return;
            }

            appSettingsField = GetRequiredField(dialogType, "appSettings");
            settingsSectionsField = GetRequiredField(dialogType, "settingsSections");
            settingsPagesField = GetRequiredField(dialogType, "settingsPages");
            sectionsListBoxField = GetRequiredField(dialogType, "sectionsListBox");

            Type sectionType = settingsSectionsField.FieldType.GetElementType()
                ?? throw new InvalidOperationException("SettingsDialog.settingsSections is not an array.");
            Type pageType = settingsPagesField.FieldType.GetElementType()
                ?? throw new InvalidOperationException("SettingsDialog.settingsPages is not an array.");
            runtimeSectionConstructor = CreateRuntimeSectionTypes(sectionType, pageType);

            ConstructorInfo dialogConstructor = GetSingleInstanceConstructor(dialogType);
            MethodInfo onClosed = dialogType.GetMethod(
                "OnClosed",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null,
                types: new[] { typeof(EventArgs) },
                modifiers: null)
                ?? throw new MissingMethodException(dialogType.FullName, "OnClosed(EventArgs)");
            MethodInfo constructorPostfix = typeof(PDNClassicSettingsFix).GetMethod(
                nameof(SettingsDialogConstructorPostfix),
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(PDNClassicSettingsFix).FullName, nameof(SettingsDialogConstructorPostfix));
            MethodInfo onClosedPostfix = typeof(PDNClassicSettingsFix).GetMethod(
                nameof(SettingsDialogOnClosedPostfix),
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(PDNClassicSettingsFix).FullName, nameof(SettingsDialogOnClosedPostfix));

            harmony.Patch(dialogConstructor, postfix: new HarmonyMethod(constructorPostfix));
            harmony.Patch(onClosed, postfix: new HarmonyMethod(onClosedPostfix));
            patched = true;
        }
    }

    private static FieldInfo GetRequiredField(Type type, string name)
    {
        return type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            ?? throw new MissingFieldException(type.FullName, name);
    }

    private static ConstructorInfo GetSingleInstanceConstructor(Type type)
    {
        ConstructorInfo? result = null;
        foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (result != null)
            {
                throw new AmbiguousMatchException(type.FullName + ".ctor");
            }

            result = constructor;
        }

        return result ?? throw new MissingMethodException(type.FullName, ".ctor");
    }

    private static ConstructorInfo CreateRuntimeSectionTypes(Type sectionType, Type pageType)
    {
        AssemblyName assemblyName = new("PDNClassic.SettingsUI.Runtime");
        AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name!);
        Type ignoresAccessChecksAttribute = DefineIgnoresAccessChecksAttribute(moduleBuilder);
        ConstructorInfo ignoresAccessChecksConstructor = ignoresAccessChecksAttribute.GetConstructor(new[] { typeof(string) })
            ?? throw new MissingMethodException(ignoresAccessChecksAttribute.FullName, ".ctor(string)");
        assemblyBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            ignoresAccessChecksConstructor,
            new object[] { sectionType.Assembly.GetName().Name! }));
        assemblyBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            ignoresAccessChecksConstructor,
            new object[] { typeof(PDNClassicSettingsFix).Assembly.GetName().Name! }));

        ConstructorInfo pageBaseConstructor = pageType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { sectionType },
            modifiers: null)
            ?? throw new MissingMethodException(pageType.FullName, ".ctor(SettingsDialogSection)");
        MethodInfo configurePage = typeof(PDNClassicSettingsFix).GetMethod(
            nameof(ConfigurePage),
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(PDNClassicSettingsFix).FullName, nameof(ConfigurePage));

        TypeBuilder pageBuilder = moduleBuilder.DefineType(
            "PDNClassic.Settings.UI.PDNClassicSettingsPage",
            TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.NotPublic,
            pageType);
        ConstructorBuilder pageConstructor = pageBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { sectionType });
        ILGenerator pageConstructorIl = pageConstructor.GetILGenerator();
        pageConstructorIl.Emit(OpCodes.Ldarg_0);
        pageConstructorIl.Emit(OpCodes.Ldarg_1);
        pageConstructorIl.Emit(OpCodes.Call, pageBaseConstructor);
        pageConstructorIl.Emit(OpCodes.Ldarg_0);
        pageConstructorIl.Emit(OpCodes.Call, configurePage);
        pageConstructorIl.Emit(OpCodes.Ret);
        Type runtimePageType = pageBuilder.CreateType()
            ?? throw new InvalidOperationException("Could not create the PDNClassic settings page type.");
        ConstructorInfo runtimePageConstructor = runtimePageType.GetConstructor(new[] { sectionType })
            ?? throw new MissingMethodException(runtimePageType.FullName, ".ctor(SettingsDialogSection)");

        ConstructorInfo sectionBaseConstructor = FindSectionBaseConstructor(sectionType, out Type appSettingsType, out Type iconType);
        MethodInfo onCreateUi = sectionType.GetMethod(
            "OnCreateUI",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(sectionType.FullName, "OnCreateUI");

        TypeBuilder sectionBuilder = moduleBuilder.DefineType(
            "PDNClassic.Settings.UI.PDNClassicSettingsSection",
            TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.NotPublic,
            sectionType);
        ConstructorBuilder sectionConstructor = sectionBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { appSettingsType, iconType });
        ILGenerator sectionConstructorIl = sectionConstructor.GetILGenerator();
        sectionConstructorIl.Emit(OpCodes.Ldarg_0);
        sectionConstructorIl.Emit(OpCodes.Ldarg_1);
        sectionConstructorIl.Emit(OpCodes.Ldstr, "PDNClassic");
        sectionConstructorIl.Emit(OpCodes.Ldarg_2);
        sectionConstructorIl.Emit(OpCodes.Call, sectionBaseConstructor);
        sectionConstructorIl.Emit(OpCodes.Ret);

        MethodBuilder createUi = sectionBuilder.DefineMethod(
            onCreateUi.Name,
            MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            pageType,
            Type.EmptyTypes);
        ILGenerator createUiIl = createUi.GetILGenerator();
        createUiIl.Emit(OpCodes.Ldarg_0);
        createUiIl.Emit(OpCodes.Newobj, runtimePageConstructor);
        createUiIl.Emit(OpCodes.Ret);
        sectionBuilder.DefineMethodOverride(createUi, onCreateUi);

        Type runtimeSectionType = sectionBuilder.CreateType()
            ?? throw new InvalidOperationException("Could not create the PDNClassic settings section type.");
        return runtimeSectionType.GetConstructor(new[] { appSettingsType, iconType })
            ?? throw new MissingMethodException(runtimeSectionType.FullName, ".ctor(AppSettings, UIImageResource)");
    }

    private static Type DefineIgnoresAccessChecksAttribute(ModuleBuilder moduleBuilder)
    {
        TypeBuilder attributeBuilder = moduleBuilder.DefineType(
            "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",
            TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.NotPublic,
            typeof(Attribute));
        ConstructorInfo attributeBaseConstructor = typeof(Attribute).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)
            ?? throw new MissingMethodException(typeof(Attribute).FullName, ".ctor");
        ConstructorBuilder constructor = attributeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { typeof(string) });
        ILGenerator il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, attributeBaseConstructor);
        il.Emit(OpCodes.Ret);
        return attributeBuilder.CreateType()
            ?? throw new InvalidOperationException("Could not create IgnoresAccessChecksToAttribute.");
    }

    private static ConstructorInfo FindSectionBaseConstructor(Type sectionType, out Type appSettingsType, out Type iconType)
    {
        foreach (ConstructorInfo constructor in sectionType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            ParameterInfo[] parameters = constructor.GetParameters();
            if (parameters.Length == 3 && parameters[1].ParameterType == typeof(string))
            {
                appSettingsType = parameters[0].ParameterType;
                iconType = parameters[2].ParameterType;
                return constructor;
            }
        }

        throw new MissingMethodException(sectionType.FullName, ".ctor(AppSettings, string, UIImageResource)");
    }

    private static void SettingsDialogConstructorPostfix(object __instance)
    {
        if (runtimeSectionConstructor == null ||
            appSettingsField == null ||
            settingsSectionsField == null ||
            settingsPagesField == null ||
            sectionsListBoxField == null)
        {
            throw new InvalidOperationException("PDNClassic settings reflection state was not initialized.");
        }

        Array sections = (Array)(settingsSectionsField.GetValue(__instance)
            ?? throw new InvalidOperationException("SettingsDialog.settingsSections is null."));
        Array pages = (Array)(settingsPagesField.GetValue(__instance)
            ?? throw new InvalidOperationException("SettingsDialog.settingsPages is null."));
        object appSettings = appSettingsField.GetValue(__instance)
            ?? throw new InvalidOperationException("SettingsDialog.appSettings is null.");
        object icon = sections.GetValue(0)?.GetType().BaseType?.GetProperty(
            "IconResource",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(sections.GetValue(0)!)
            ?? throw new InvalidOperationException("Could not obtain a settings section icon.");
        object section = runtimeSectionConstructor.Invoke(new[] { appSettings, icon });

        Array newSections = Array.CreateInstance(sections.GetType().GetElementType()!, sections.Length + 1);
        Array.Copy(sections, newSections, sections.Length);
        newSections.SetValue(section, sections.Length);
        settingsSectionsField.SetValue(__instance, newSections);

        Array newPages = Array.CreateInstance(pages.GetType().GetElementType()!, pages.Length + 1);
        Array.Copy(pages, newPages, pages.Length);
        settingsPagesField.SetValue(__instance, newPages);

        if (sectionsListBoxField.GetValue(__instance) is not ListBox sectionsListBox)
        {
            throw new InvalidOperationException("SettingsDialog.sectionsListBox is not a ListBox.");
        }
        sectionsListBox.Items.Add(section);
        dialogStates.Add(
            __instance,
            new DialogState(
                ReadAeroGlassEnabled(),
                ReadOldColorsEnabled(),
                ReadGdiClassicFontRenderingEnabled(),
                ReadRoundedMenuBarEnabled(),
                ReadLegacyControlStylesEnabled(),
                ReadOldIconAccommodationsEnabled(),
                ReadMetroCloseButtonsEnabled(),
                ReadOldToolWindowPositioningEnabled()));
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

    private static void ConfigurePage(object page)
    {
        if (page is not Control pageControl)
        {
            throw new InvalidOperationException("The PDNClassic settings page is not a Control.");
        }

        Type checkBoxType = FindLoadedType("PaintDotNet.Controls.PdnCheckBox");
        Control aeroCheckBox = CreateCheckBox(
            checkBoxType,
            "enableAeroGlassCheckBox",
            "Enable Aero glass effect",
            ReadAeroGlassEnabled(),
            OnAeroGlassCheckBoxChanged);
        aeroCheckBox.Location = new Point(0, UIScaleFactor.Current.ConvertDipsToPixelsInt(4));
        pageControl.Controls.Add(aeroCheckBox);
        aeroCheckBox.PerformLayout();

        Control oldColorsCheckBox = CreateCheckBox(
            checkBoxType,
            "useOldColorsCheckBox",
            "Use old colors",
            ReadOldColorsEnabled(),
            OnOldColorsCheckBoxChanged);
        int oldColorsTop = aeroCheckBox.Bottom + UIScaleFactor.Current.ConvertDipsToPixelsInt(10);
        oldColorsCheckBox.Location = new Point(0, oldColorsTop);
        pageControl.Controls.Add(oldColorsCheckBox);
        oldColorsCheckBox.PerformLayout();

        Control gdiClassicFontRenderingCheckBox = CreateCheckBox(
            checkBoxType,
            "useGdiClassicFontRenderingCheckBox",
            "Use GDI classic font rendering",
            ReadGdiClassicFontRenderingEnabled(),
            OnGdiClassicFontRenderingCheckBoxChanged);
        int gdiClassicFontRenderingTop =
            oldColorsCheckBox.Bottom + UIScaleFactor.Current.ConvertDipsToPixelsInt(10);
        gdiClassicFontRenderingCheckBox.Location = new Point(0, gdiClassicFontRenderingTop);
        pageControl.Controls.Add(gdiClassicFontRenderingCheckBox);
        gdiClassicFontRenderingCheckBox.PerformLayout();

        Control roundedMenuBarCheckBox = CreateCheckBox(
            checkBoxType,
            "useRoundedMenuBarCheckBox",
            "Use rounded menu bar (Paint.NET v3.5)",
            ReadRoundedMenuBarEnabled(),
            OnRoundedMenuBarCheckBoxChanged);
        int roundedMenuBarTop =
            gdiClassicFontRenderingCheckBox.Bottom +
            UIScaleFactor.Current.ConvertDipsToPixelsInt(10);
        roundedMenuBarCheckBox.Location = new Point(0, roundedMenuBarTop);
        pageControl.Controls.Add(roundedMenuBarCheckBox);
        roundedMenuBarCheckBox.PerformLayout();
        Control legacyControlStylesCheckBox = CreateCheckBox(
            checkBoxType,
            "useLegacyControlStylesCheckBox",
            "Use Paint.NET v3.5 control styles",
            ReadLegacyControlStylesEnabled(),
            OnLegacyControlStylesCheckBoxChanged);
        int legacyControlStylesTop =
            roundedMenuBarCheckBox.Bottom +
            UIScaleFactor.Current.ConvertDipsToPixelsInt(10);
        legacyControlStylesCheckBox.Location = new Point(0, legacyControlStylesTop);
        pageControl.Controls.Add(legacyControlStylesCheckBox);
        legacyControlStylesCheckBox.PerformLayout();


        Control oldIconAccommodationsCheckBox = CreateCheckBox(
            checkBoxType,
            "useOldIconAccommodationsCheckBox",
            "Use accommodations for old icons",
            ReadOldIconAccommodationsEnabled(),
            OnOldIconAccommodationsCheckBoxChanged);
        int oldIconAccommodationsTop =
            legacyControlStylesCheckBox.Bottom +
            UIScaleFactor.Current.ConvertDipsToPixelsInt(10);
        oldIconAccommodationsCheckBox.Location = new Point(0, oldIconAccommodationsTop);
        pageControl.Controls.Add(oldIconAccommodationsCheckBox);
        oldIconAccommodationsCheckBox.PerformLayout();

        Control metroCloseButtonsCheckBox = CreateCheckBox(
            checkBoxType,
            "useMetroCloseButtonsCheckBox",
            "Use Metro icons",
            ReadMetroCloseButtonsEnabled(),
            OnMetroCloseButtonsCheckBoxChanged);
        int metroCloseButtonsTop =
            oldIconAccommodationsCheckBox.Bottom +
            UIScaleFactor.Current.ConvertDipsToPixelsInt(10);
        metroCloseButtonsCheckBox.Location = new Point(0, metroCloseButtonsTop);
        pageControl.Controls.Add(metroCloseButtonsCheckBox);

        Control oldToolWindowPositioningCheckBox = CreateCheckBox(
            checkBoxType,
            "useOldToolWindowPositioningCheckBox",
            "Use old tool window positioning",
            ReadOldToolWindowPositioningEnabled(),
            OnOldToolWindowPositioningCheckBoxChanged);
        int oldToolWindowPositioningTop =
            metroCloseButtonsCheckBox.Bottom +
            UIScaleFactor.Current.ConvertDipsToPixelsInt(10);
        oldToolWindowPositioningCheckBox.Location = new Point(0, oldToolWindowPositioningTop);
        pageControl.Controls.Add(oldToolWindowPositioningCheckBox);
        AddVersionFooter(pageControl, oldToolWindowPositioningCheckBox);
    }


    private static void AddVersionFooter(
        Control pageControl,
        Control precedingControl)
    {
        Type labelType = FindLoadedType("PaintDotNet.Controls.PdnLabel");
        Type linkLabelType = FindLoadedType("PaintDotNet.Controls.PdnLinkLabel");
        if (Activator.CreateInstance(labelType, nonPublic: true) is not System.Windows.Forms.Label versionLabel)
        {
            throw new InvalidOperationException("Could not create PdnLabel.");
        }

        if (Activator.CreateInstance(linkLabelType, nonPublic: true) is not LinkLabel repositoryLink)
        {
            throw new InvalidOperationException("Could not create PdnLinkLabel.");
        }

        Version version = typeof(PDNClassicSettingsFix).Assembly.GetName().Version
            ?? throw new InvalidOperationException("PDNClassic has no assembly version.");
        versionLabel.Name = "pdnClassicVersionLabel";
        versionLabel.Text =
            $"PDNClassic v{version.Major}.{version.Minor}.{version.Build}";
        versionLabel.AutoSize = true;
        versionLabel.Location = new Point(
            0,
            precedingControl.Bottom +
                UIScaleFactor.Current.ConvertDipsToPixelsInt(20));
        pageControl.Controls.Add(versionLabel);
        versionLabel.PerformLayout();

        repositoryLink.Name = "pdnClassicRepositoryLink";
        repositoryLink.Text = RepositoryUrl;
        repositoryLink.LinkArea = new LinkArea(0, RepositoryUrl.Length);
        repositoryLink.ForeColor = SystemColors.HotTrack;
        repositoryLink.LinkColor = SystemColors.HotTrack;
        repositoryLink.ActiveLinkColor = SystemColors.Highlight;
        repositoryLink.VisitedLinkColor = SystemColors.HotTrack;
        repositoryLink.AutoSize = true;
        repositoryLink.Location = new Point(
            0,
            versionLabel.Bottom +
                UIScaleFactor.Current.ConvertDipsToPixelsInt(2));
        repositoryLink.LinkClicked += OnRepositoryLinkClicked;
        pageControl.Controls.Add(repositoryLink);
        repositoryLink.PerformLayout();
    }

    private static void OnRepositoryLinkClicked(
        object? sender,
        LinkLabelLinkClickedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(RepositoryUrl)
        {
            UseShellExecute = true
        });
    }

    private static Control CreateCheckBox(
        Type checkBoxType,
        string name,
        string text,
        bool isChecked,
        EventHandler changedHandler)
    {
        object checkBoxObject = Activator.CreateInstance(checkBoxType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create PdnCheckBox.");
        if (checkBoxObject is not Control checkBox)
        {
            throw new InvalidOperationException("PdnCheckBox is not a Control.");
        }

        PropertyInfo isCheckedProperty = checkBoxType.GetProperty(
            "IsChecked",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(checkBoxType.FullName, "IsChecked");
        EventInfo isCheckedChangedEvent = checkBoxType.GetEvent(
            "IsCheckedChanged",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(checkBoxType.FullName, "IsCheckedChanged");

        checkBox.Name = name;
        checkBox.Text = text;
        checkBox.AutoSize = true;
        isCheckedProperty.SetValue(checkBoxObject, isChecked);
        isCheckedChangedEvent.AddEventHandler(checkBoxObject, changedHandler);
        return checkBox;
    }

    private static void OnAeroGlassCheckBoxChanged(object? sender, EventArgs e)
    {
        if (sender == null)
        {
            return;
        }

        PropertyInfo isCheckedProperty = sender.GetType().GetProperty("IsChecked", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(sender.GetType().FullName, "IsChecked");
        bool enabled = (bool)(isCheckedProperty.GetValue(sender) ?? true);
        WriteAeroGlassEnabled(enabled);
    }

    private static void OnOldColorsCheckBoxChanged(object? sender, EventArgs e)
    {
        if (sender == null)
        {
            return;
        }

        PropertyInfo isCheckedProperty = sender.GetType().GetProperty(
            "IsChecked",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(sender.GetType().FullName, "IsChecked");
        bool enabled = (bool)(isCheckedProperty.GetValue(sender) ?? false);
        WriteOldColorsEnabled(enabled);
    }

    private static void OnGdiClassicFontRenderingCheckBoxChanged(object? sender, EventArgs e)
    {
        if (sender == null)
        {
            return;
        }

        PropertyInfo isCheckedProperty = sender.GetType().GetProperty(
            "IsChecked",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(sender.GetType().FullName, "IsChecked");
        bool enabled = (bool)(isCheckedProperty.GetValue(sender) ?? false);
        WriteGdiClassicFontRenderingEnabled(enabled);
    }

    private static void OnRoundedMenuBarCheckBoxChanged(object? sender, EventArgs e)
    {
        if (sender == null)
        {
            return;
        }

        PropertyInfo isCheckedProperty = sender.GetType().GetProperty(
            "IsChecked",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(sender.GetType().FullName, "IsChecked");
        bool enabled = (bool)(isCheckedProperty.GetValue(sender) ?? false);
        WriteRoundedMenuBarEnabled(enabled);
    }
    private static void OnLegacyControlStylesCheckBoxChanged(object? sender, EventArgs e)
    {
        if (sender == null)
        {
            return;
        }

        PropertyInfo isCheckedProperty = sender.GetType().GetProperty(
            "IsChecked",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(sender.GetType().FullName, "IsChecked");
        bool enabled = (bool)(isCheckedProperty.GetValue(sender) ?? false);
        WriteLegacyControlStylesEnabled(enabled);
    }

    private static void OnOldIconAccommodationsCheckBoxChanged(object? sender, EventArgs e)
    {
        if (sender == null)
        {
            return;
        }

        PropertyInfo isCheckedProperty = sender.GetType().GetProperty(
            "IsChecked",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(sender.GetType().FullName, "IsChecked");
        bool enabled = (bool)(isCheckedProperty.GetValue(sender) ?? false);
        WriteOldIconAccommodationsEnabled(enabled);
    }
    private static void OnMetroCloseButtonsCheckBoxChanged(object? sender, EventArgs e)
    {
        if (sender == null)
        {
            return;
        }

        PropertyInfo isCheckedProperty = sender.GetType().GetProperty(
            "IsChecked",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(sender.GetType().FullName, "IsChecked");
        bool enabled = (bool)(isCheckedProperty.GetValue(sender) ?? false);
        WriteMetroCloseButtonsEnabled(enabled);
    }

    private static void OnOldToolWindowPositioningCheckBoxChanged(object? sender, EventArgs e)
    {
        if (sender == null)
        {
            return;
        }

        PropertyInfo isCheckedProperty = sender.GetType().GetProperty(
            "IsChecked",
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(sender.GetType().FullName, "IsChecked");
        bool enabled = (bool)(isCheckedProperty.GetValue(sender) ?? false);
        WriteOldToolWindowPositioningEnabled(enabled);
    }



    private static void SettingsDialogOnClosedPostfix(object __instance)
    {
        if (!dialogStates.TryGetValue(__instance, out DialogState? state))
        {
            return;
        }

        bool currentAeroValue = ReadAeroGlassEnabled();
        bool currentOldColorsValue = ReadOldColorsEnabled();
        bool currentGdiClassicFontRenderingValue = ReadGdiClassicFontRenderingEnabled();
        bool currentRoundedMenuBarValue = ReadRoundedMenuBarEnabled();
        bool currentLegacyControlStylesValue = ReadLegacyControlStylesEnabled();
        bool currentOldIconAccommodationsValue = ReadOldIconAccommodationsEnabled();
        bool currentMetroCloseButtonsValue = ReadMetroCloseButtonsEnabled();
        bool currentOldToolWindowPositioningValue = ReadOldToolWindowPositioningEnabled();
        bool aeroRequiresRestart =
            currentAeroValue != state.InitialAeroValue &&
            currentAeroValue != enabledAtStartup;
        bool oldColorsRequireRestart =
            currentOldColorsValue != state.InitialOldColorsValue &&
            currentOldColorsValue != oldColorsEnabledAtStartup;
        bool gdiClassicFontRenderingRequiresRestart =
            currentGdiClassicFontRenderingValue != state.InitialGdiClassicFontRenderingValue &&
            currentGdiClassicFontRenderingValue != gdiClassicFontRenderingEnabledAtStartup;
        bool roundedMenuBarRequiresRestart =
            currentRoundedMenuBarValue != state.InitialRoundedMenuBarValue &&
            currentRoundedMenuBarValue != roundedMenuBarEnabledAtStartup;
        bool legacyControlStylesRequireRestart =
            currentLegacyControlStylesValue != state.InitialLegacyControlStylesValue &&
            currentLegacyControlStylesValue != legacyControlStylesEnabledAtStartup;
        bool oldIconAccommodationsRequireRestart =
            currentOldIconAccommodationsValue != state.InitialOldIconAccommodationsValue &&
            currentOldIconAccommodationsValue != oldIconAccommodationsEnabledAtStartup;
        bool metroCloseButtonsRequireRestart =
            currentMetroCloseButtonsValue != state.InitialMetroCloseButtonsValue &&
            currentMetroCloseButtonsValue != metroCloseButtonsEnabledAtStartup;
        bool oldToolWindowPositioningRequiresRestart =
            currentOldToolWindowPositioningValue != state.InitialOldToolWindowPositioningValue &&
            currentOldToolWindowPositioningValue != oldToolWindowPositioningEnabledAtStartup;
        if (!aeroRequiresRestart &&
            !oldColorsRequireRestart &&
            !gdiClassicFontRenderingRequiresRestart &&
            !roundedMenuBarRequiresRestart &&
            !legacyControlStylesRequireRestart &&
            !oldIconAccommodationsRequireRestart &&
            !metroCloseButtonsRequireRestart &&
            !oldToolWindowPositioningRequiresRestart)
        {
            return;
        }

        MessageBox.Show(
            "Restart Paint.NET for the new settings to take effect.",
            "PDNClassic",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static bool ReadAeroGlassEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(AeroGlassValueName) is int value ? value != 0 : true;
        }
        catch
        {
            return true;
        }
    }

    private static bool ReadOldColorsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(OldColorsValueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadGdiClassicFontRenderingEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(GdiClassicFontRenderingValueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadRoundedMenuBarEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(RoundedMenuBarValueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }
    private static bool ReadLegacyControlStylesEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(LegacyControlStylesValueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadOldIconAccommodationsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(OldIconAccommodationsValueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }
    private static bool ReadMetroCloseButtonsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(MetroCloseButtonsValueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadOldToolWindowPositioningEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(OldToolWindowPositioningValueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }



    private static void WriteAeroGlassEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the PDNClassic settings registry key.");
        key.SetValue(AeroGlassValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    private static void WriteOldColorsEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the PDNClassic settings registry key.");
        key.SetValue(OldColorsValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    private static void WriteGdiClassicFontRenderingEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the PDNClassic settings registry key.");
        key.SetValue(
            GdiClassicFontRenderingValueName,
            enabled ? 1 : 0,
            RegistryValueKind.DWord);
    }

    private static void WriteRoundedMenuBarEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the PDNClassic settings registry key.");
        key.SetValue(RoundedMenuBarValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
    }
    private static void WriteLegacyControlStylesEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the PDNClassic settings registry key.");
        key.SetValue(
            LegacyControlStylesValueName,
            enabled ? 1 : 0,
            RegistryValueKind.DWord);
    }

    private static void WriteOldIconAccommodationsEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the PDNClassic settings registry key.");
        key.SetValue(
            OldIconAccommodationsValueName,
            enabled ? 1 : 0,
            RegistryValueKind.DWord);
    }
    private static void WriteMetroCloseButtonsEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the PDNClassic settings registry key.");
        key.SetValue(MetroCloseButtonsValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    private static void WriteOldToolWindowPositioningEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the PDNClassic settings registry key.");
        key.SetValue(
            OldToolWindowPositioningValueName,
            enabled ? 1 : 0,
            RegistryValueKind.DWord);
    }



    private sealed class DialogState
    {
        internal DialogState(
            bool initialAeroValue,
            bool initialOldColorsValue,
            bool initialGdiClassicFontRenderingValue,
            bool initialRoundedMenuBarValue,
            bool initialLegacyControlStylesValue,
            bool initialOldIconAccommodationsValue,
            bool initialMetroCloseButtonsValue,
            bool initialOldToolWindowPositioningValue)
        {
            InitialAeroValue = initialAeroValue;
            InitialOldColorsValue = initialOldColorsValue;
            InitialGdiClassicFontRenderingValue = initialGdiClassicFontRenderingValue;
            InitialRoundedMenuBarValue = initialRoundedMenuBarValue;
            InitialLegacyControlStylesValue = initialLegacyControlStylesValue;
            InitialOldIconAccommodationsValue = initialOldIconAccommodationsValue;
            InitialMetroCloseButtonsValue = initialMetroCloseButtonsValue;
            InitialOldToolWindowPositioningValue = initialOldToolWindowPositioningValue;
        }

        internal bool InitialAeroValue { get; }

        internal bool InitialOldColorsValue { get; }

        internal bool InitialGdiClassicFontRenderingValue { get; }

        internal bool InitialRoundedMenuBarValue { get; }
        internal bool InitialLegacyControlStylesValue { get; }


        internal bool InitialOldIconAccommodationsValue { get; }

        internal bool InitialMetroCloseButtonsValue { get; }

        internal bool InitialOldToolWindowPositioningValue { get; }
    }
}
