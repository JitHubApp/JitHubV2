using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class CanonicalPageAccessibilityContractTests
{
    private sealed record CustomInteractiveControlContract(
        string TypeName,
        string DefinitionPath,
        bool HasDirectIdentityProperties,
        bool HasInstanceIdentityProperty,
        bool SuppliesInnerAccessibleNames,
        string? DerivedInstanceIdentityProperty);

    private static readonly HashSet<string> InteractiveTypes = new(StringComparer.Ordinal)
    {
        "AutoSuggestBox",
        "Button",
        "CalendarDatePicker",
        "CheckBox",
        "ComboBox",
        "ComboBoxItem",
        "DataGrid",
        "DatePicker",
        "DropDownButton",
        "Expander",
        "HyperlinkButton",
        "ListView",
        "MenuFlyoutItem",
        "NavigationView",
        "RadioButton",
        "Segmented",
        "SegmentedItem",
        "SelectorBar",
        "SelectorBarItem",
        "TabView",
        "TabViewItem",
        "TextBox",
        "ToggleSwitch",
        "TreeView"
    };

    private static readonly HashSet<string> InteractiveAttributes = new(StringComparer.Ordinal)
    {
        "Click",
        "Command",
        "DoubleTapped",
        "ItemClick",
        "KeyDown",
        "KeyUp",
        "PointerCanceled",
        "PointerCaptureLost",
        "PointerEntered",
        "PointerExited",
        "PointerMoved",
        "PointerPressed",
        "PointerReleased",
        "PointerWheelChanged",
        "RightTapped",
        "Tapped"
    };

    public static IEnumerable<object[]> AccessibleSurfaces()
    {
        string viewsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views");
        foreach (string path in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return [Path.GetRelativePath(viewsRoot, path).Replace(Path.DirectorySeparatorChar, '/')];
        }
    }

    [Theory]
    [MemberData(nameof(AccessibleSurfaces))]
    public void InteractiveControlsExposeStableIdsAndExplicitNames(string relativePath)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
        IReadOnlyDictionary<string, CustomInteractiveControlContract> customControls = DiscoverCustomInteractiveControls();
        string codeBehindPath = path + ".cs";
        string codeBehind = File.Exists(codeBehindPath) ? File.ReadAllText(codeBehindPath) : string.Empty;
        List<string> missing = FindMissingAccessibilityContracts(document, customControls, codeBehind);

        Assert.True(missing.Count == 0, $"{relativePath}{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }

    [Fact]
    public void CustomInteractiveControlsAreDiscoveredFromIdentityContractsAndInteractivePeers()
    {
        IReadOnlyDictionary<string, CustomInteractiveControlContract> controls = DiscoverCustomInteractiveControls();

        Assert.Contains("AppStatButton", controls.Keys);
        Assert.Contains("Avatar", controls.Keys);
        Assert.Contains("EmojiButton", controls.Keys);
        Assert.Contains("EmojiPanelButton", controls.Keys);
        Assert.Contains("MarkdownForm", controls.Keys);
        Assert.Contains("MarkdownViewer", controls.Keys);
        Assert.Contains("AdaptiveWorkspace", controls.Keys);
        Assert.Contains("CreditPersonaleButton", controls.Keys);
        Assert.Contains("ReviewCommentBlock", controls.Keys);
        Assert.True(controls["AppStatButton"].HasDirectIdentityProperties);
        Assert.True(controls["MarkdownForm"].HasInstanceIdentityProperty);
        Assert.False(controls["AdaptiveWorkspace"].HasDirectIdentityProperties);
        Assert.False(controls["ReviewCommentBlock"].HasInstanceIdentityProperty);
    }

    [Fact]
    public void NewCustomInteractiveControlInstanceWithoutIdentityFailsDiscovery()
    {
        XDocument document = XDocument.Parse("""
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:app="using:JitHub.WinUI.Views.Controls.App">
                <app:AppStatButton ValueText="12" />
            </Page>
            """);

        List<string> missing = FindMissingAccessibilityContracts(document, DiscoverCustomInteractiveControls());

        string failure = Assert.Single(missing);
        Assert.Contains("AppStatButton", failure, StringComparison.Ordinal);
        Assert.Contains("id=missing", failure, StringComparison.Ordinal);
        Assert.Contains("name=missing", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomInteractiveControlDefaultsCannotForwardEmptyIdentity()
    {
        string controlsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views", "Controls");
        string source = File.ReadAllText(Path.Combine(controlsRoot, "App", "AppStatButton.xaml.cs"));
        string xaml = File.ReadAllText(Path.Combine(controlsRoot, "App", "AppStatButton.xaml"));

        Assert.DoesNotMatch(
            @"Automation(?:Id|Name)Property\s*=.*?new\s+PropertyMetadata\(string\.Empty",
            source.Replace("\r", string.Empty));
        Assert.Contains("EffectiveAutomationId", source, StringComparison.Ordinal);
        Assert.Contains("EffectiveAutomationName", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"{x:Bind EffectiveAutomationId", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{x:Bind EffectiveAutomationName", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReachableDesignLabStatButtonHasAUniqueMeaningfulIdentity()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "Design",
            "DesignLabPage.xaml");
        XDocument document = XDocument.Load(path);
        XElement[] statButtons = document.Descendants()
            .Where(static element => element.Name.LocalName == "AppStatButton")
            .ToArray();

        Assert.Equal(7, statButtons.Length);
        Assert.All(statButtons, element =>
        {
            Assert.True(IsMeaningfulAutomationValue(element.Attribute("AutomationId")?.Value));
            Assert.True(IsMeaningfulAutomationValue(element.Attribute("AutomationName")?.Value));
        });
        Assert.Equal(
            statButtons.Length,
            statButtons.Select(static element => element.Attribute("AutomationId")!.Value).Distinct(StringComparer.Ordinal).Count());
    }

    private static List<string> FindMissingAccessibilityContracts(
        XDocument document,
        IReadOnlyDictionary<string, CustomInteractiveControlContract> customControls,
        string consumerCodeBehind = "")
    {
        List<string> missing = [];

        foreach (XElement element in document.Descendants().Where(element => IsInteractiveElement(element, customControls)))
        {
            if (string.Equals(
                    element.Attribute("AutomationProperties.AccessibilityView")?.Value,
                    "Raw",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            customControls.TryGetValue(element.Name.LocalName, out CustomInteractiveControlContract? customControl);
            if (customControl is not null &&
                !IsCustomControlInstanceInteractive(element, customControl, consumerCodeBehind))
            {
                continue;
            }

            string? automationId = customControl?.HasInstanceIdentityProperty == true
                ? element.Attribute("AutomationInstanceId")?.Value
                    ?? (customControl.DerivedInstanceIdentityProperty is string derivedProperty
                        ? element.Attribute(derivedProperty)?.Value
                        : null)
                    ?? GetCodeAssignedIdentity(element, consumerCodeBehind)
                : element.Attribute("AutomationProperties.AutomationId")?.Value
                    ?? element.Attribute("AutomationId")?.Value
                    ?? GetCodeAssignedIdentity(element, consumerCodeBehind);
            string? accessibleName = element.Attribute("AutomationProperties.Name")?.Value
                ?? element.Attribute("AutomationName")?.Value
                ?? (customControl?.HasInstanceIdentityProperty == true && customControl.SuppliesInnerAccessibleNames
                    ? $"provided by {customControl.TypeName}"
                    : null);
            if (IsMeaningfulAutomationValue(automationId) && IsMeaningfulAutomationValue(accessibleName))
            {
                continue;
            }

            IXmlLineInfo line = (IXmlLineInfo)element;
            missing.Add(
                $"{element.Name.LocalName} at line {line.LineNumber}: " +
                $"id={(IsMeaningfulAutomationValue(automationId) ? automationId : "missing")}, " +
                $"name={(IsMeaningfulAutomationValue(accessibleName) ? accessibleName : "missing")}");
        }

        return missing;
    }

    [Fact]
    public void ContractTracksCustomInteractiveControlTypes()
    {
        Assert.Contains("Segmented", InteractiveTypes);
        Assert.Contains("SegmentedItem", InteractiveTypes);
        Assert.Contains("DataGrid", InteractiveTypes);
        Assert.Contains("Expander", InteractiveTypes);
        Assert.Contains("NavigationView", InteractiveTypes);
        Assert.Contains("ComboBoxItem", InteractiveTypes);
        Assert.Contains("SelectorBarItem", InteractiveTypes);
        Assert.Contains("TabViewItem", InteractiveTypes);
        Assert.Contains("AppStatButton", DiscoverCustomInteractiveControls().Keys);
        Assert.Contains("MarkdownForm", DiscoverCustomInteractiveControls().Keys);
    }

    [Fact]
    public void ContractTracksGenericAndCustomInteractiveSurfaces()
    {
        Assert.Contains("Command", InteractiveAttributes);
        Assert.Contains("KeyDown", InteractiveAttributes);
        Assert.Contains("PointerPressed", InteractiveAttributes);
        Assert.Contains("PointerReleased", InteractiveAttributes);
        Assert.Contains("Tapped", InteractiveAttributes);
    }

    [Fact]
    public void EveryCodePreviewRendererIsCovered()
    {
        string renderersDirectory = Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "Renderers");
        HashSet<string> covered = AccessibleSurfaces()
            .Select(static data => (string)data[0])
            .Where(static path => path.StartsWith("Controls/CodeViewer/Renderers/", StringComparison.Ordinal))
            .Select(static path => Path.GetFileName(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] missing = Directory.EnumerateFiles(renderersDirectory, "*.xaml")
            .Select(static path => Path.GetFileName(path)!)
            .Where(fileName => !covered.Contains(fileName))
            .OrderBy(static fileName => fileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(missing.Length == 0, $"Preview accessibility coverage is missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void ContractAutomaticallyCoversEveryProductViewXamlFile()
    {
        string viewsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views");
        int productFileCount = Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories).Count();
        Assert.Equal(productFileCount, AccessibleSurfaces().Count());
    }

    private static bool IsInteractiveElement(
        XElement element,
        IReadOnlyDictionary<string, CustomInteractiveControlContract> customControls)
    {
        if (InteractiveTypes.Contains(element.Name.LocalName) || customControls.ContainsKey(element.Name.LocalName))
        {
            return true;
        }

        if (string.Equals(
                element.Attribute("AutomationProperties.AccessibilityView")?.Value,
                "Control",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(element.Attribute("IsTabStop")?.Value, "True", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return element.Attributes().Any(attribute => InteractiveAttributes.Contains(attribute.Name.LocalName));
    }

    private static IReadOnlyDictionary<string, CustomInteractiveControlContract> DiscoverCustomInteractiveControls()
    {
        string controlsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views", "Controls");
        Dictionary<string, CustomInteractiveControlContract> controls = new(StringComparer.Ordinal);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string definitionPath in Directory.EnumerateFiles(controlsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            string codeBehindPath = definitionPath + ".cs";
            if (!File.Exists(codeBehindPath))
            {
                continue;
            }

            XDocument definition = XDocument.Load(definitionPath);
            string? className = definition.Root?.Attribute(xaml + "Class")?.Value;
            if (string.IsNullOrWhiteSpace(className))
            {
                continue;
            }

            string source = File.ReadAllText(codeBehindPath);
            bool hasAutomationId = DeclaresIdentityDependencyProperty(source, "AutomationId");
            bool hasAutomationName = DeclaresIdentityDependencyProperty(source, "AutomationName");
            bool hasAutomationInstanceId = DeclaresIdentityDependencyProperty(source, "AutomationInstanceId");
            bool hasInteractivePeer = definition.Descendants().Any(IsBuiltInInteractiveElement);
            bool suppliesInnerNames = definition.Descendants()
                .Where(IsBuiltInInteractiveElement)
                .All(element => IsMeaningfulAutomationValue(element.Attribute("AutomationProperties.Name")?.Value)) ||
                source.Contains("AutomationProperties.SetName(", StringComparison.Ordinal) ||
                source.Contains("MarkdownHostContract.GetAutomationName", StringComparison.Ordinal);
            // A custom control is interactive because of the peers it exposes, even when it
            // has not yet added an identity dependency property. Discovering peers first keeps
            // a newly-authored UserControl from silently escaping the canonical XAML audit.
            if (!hasInteractivePeer)
            {
                continue;
            }

            string typeName = className[(className.LastIndexOf('.') + 1)..];
            controls[typeName] = new CustomInteractiveControlContract(
                typeName,
                Path.GetRelativePath(controlsRoot, definitionPath),
                hasAutomationId && hasAutomationName,
                hasAutomationInstanceId,
                suppliesInnerNames,
                source.Contains("UserIdentityAutomationId.Create(", StringComparison.Ordinal) ? "Login" : null);
        }

        return controls;
    }

    private static bool DeclaresIdentityDependencyProperty(string source, string propertyName) =>
        source.Contains($"{propertyName}Property", StringComparison.Ordinal) &&
        Regex.IsMatch(
            source,
            $@"DependencyProperty\.Register\s*\(\s*nameof\s*\(\s*{Regex.Escape(propertyName)}\s*\)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static bool IsBuiltInInteractiveElement(XElement element) =>
        InteractiveTypes.Contains(element.Name.LocalName) ||
        string.Equals(
            element.Attribute("AutomationProperties.AccessibilityView")?.Value,
            "Control",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(element.Attribute("IsTabStop")?.Value, "True", StringComparison.OrdinalIgnoreCase) ||
        element.Attributes().Any(attribute => InteractiveAttributes.Contains(attribute.Name.LocalName));

    private static bool IsMeaningfulAutomationValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        return !string.Equals(normalized, "{Binding}", StringComparison.Ordinal) &&
               !string.Equals(normalized, "{x:Bind}", StringComparison.Ordinal) &&
               !string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCustomControlInstanceInteractive(
        XElement element,
        CustomInteractiveControlContract contract,
        string consumerCodeBehind)
    {
        // Composition controls such as AdaptiveWorkspace expose independently named buttons,
        // lists, and text inputs but do not expose an invokable peer of their own. Their inner
        // peers are audited from the control definition; only controls with an explicit public
        // identity contract require identity again at every consuming instance.
        if (!contract.HasDirectIdentityProperties && !contract.HasInstanceIdentityProperty)
        {
            return false;
        }

        if (contract.DerivedInstanceIdentityProperty is null)
        {
            return true;
        }

        if (string.Equals(
                element.Attribute("IsProfileNavigationEnabled")?.Value,
                "False",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsMeaningfulAutomationValue(element.Attribute(contract.DerivedInstanceIdentityProperty)?.Value) ||
               IsMeaningfulAutomationValue(element.Attribute("AutomationInstanceId")?.Value) ||
               IsMeaningfulAutomationValue(GetCodeAssignedIdentity(element, consumerCodeBehind));
    }

    private static string? GetCodeAssignedIdentity(XElement element, string consumerCodeBehind)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        string? elementName = element.Attribute(xaml + "Name")?.Value;
        if (string.IsNullOrWhiteSpace(consumerCodeBehind))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(elementName) &&
            (consumerCodeBehind.Contains($"{elementName}.AutomationInstanceId =", StringComparison.Ordinal) ||
             consumerCodeBehind.Contains($"AutomationProperties.SetAutomationId({elementName},", StringComparison.Ordinal)))
        {
            return $"assigned in code to {elementName}";
        }

        string? loadedHandler = element.Attribute("Loaded")?.Value;
        return !string.IsNullOrWhiteSpace(loadedHandler) &&
               MethodContainsAutomationIdAssignment(consumerCodeBehind, loadedHandler)
            ? $"assigned in {loadedHandler}"
            : null;
    }

    private static bool MethodContainsAutomationIdAssignment(string source, string methodName)
    {
        int methodStart = source.IndexOf($"{methodName}(", StringComparison.Ordinal);
        if (methodStart < 0)
        {
            return false;
        }

        int bodyStart = source.IndexOf('{', methodStart);
        if (bodyStart < 0)
        {
            return false;
        }

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return source.AsSpan(bodyStart, index - bodyStart + 1)
                    .Contains("AutomationProperties.SetAutomationId(", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
