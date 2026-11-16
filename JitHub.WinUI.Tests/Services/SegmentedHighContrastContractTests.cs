using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class SegmentedHighContrastContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] Themes = ["Light", "Dark", "HighContrast"];
    private static readonly string[] BrushProperties = ["Background", "Foreground", "BorderBrush", "Fill", "Stroke"];
    private static readonly string[] SelectedStates = ["Selected", "PointerOverSelected", "SelectedPointerOver"];
    private static readonly string[] PressedStates = ["Pressed", "PressedSelected", "SelectedPressed"];
    private static readonly string[] DisabledStates = ["Disabled", "SelectedDisabled"];
    private static readonly Regex ThemeResourceReference = new(
        @"^\{ThemeResource\s+(?<key>[A-Za-z0-9_]+)\s*\}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SystemColorBrush = new(
        "^SystemColor(Window|WindowText|ButtonFace|ButtonText|Highlight|HighlightText|GrayText|Hotlight)ColorBrush$",
        RegexOptions.CultureInvariant);

    [Fact]
    public void SegmentedStyles_ResolveEveryBrushThroughThemeDictionariesThatUseSystemColorsInHighContrast()
    {
        XDocument bridge = LoadBridge();
        Dictionary<string, Dictionary<string, string>> themes = ReadThemeAliases(bridge);
        string[] brushKeys = FindSegmentedStyles(bridge)
            .SelectMany(ReadBrushReferences)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        List<string> violations = [];

        Assert.NotEmpty(brushKeys);
        foreach (string key in brushKeys)
        {
            foreach (string theme in Themes)
            {
                if (!themes.TryGetValue(theme, out Dictionary<string, string>? aliases) ||
                    !aliases.ContainsKey(key))
                {
                    violations.Add($"{key} is not defined in the {theme} theme dictionary.");
                }
            }

            if (themes.TryGetValue("HighContrast", out Dictionary<string, string>? highContrast) &&
                highContrast.TryGetValue(key, out string? systemKey) &&
                !SystemColorBrush.IsMatch(systemKey))
            {
                violations.Add($"{key} resolves to {systemKey} in HighContrast; expected a SystemColor*ColorBrush.");
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Segmented brushes must be theme-scoped and system-aware in High Contrast:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void SegmentedItem_PaintsSystemHighlightPairsWithoutTheHighContrastTextBackplate()
    {
        XDocument bridge = LoadBridge();
        Dictionary<string, string> highContrast = ReadThemeAliases(bridge).GetValueOrDefault("HighContrast") ?? [];
        XElement itemStyle = FindStyle(bridge, "AppSegmentedItemBaseStyle");
        XElement template = itemStyle.Descendants().Single(element => element.Name.LocalName == "ControlTemplate");
        XElement contentPresenter = FindNamed(template, "ContentPresenter");
        XElement focusVisual = FindNamed(template, "FocusVisual");

        // WinUI draws a WindowText-on-Window backplate behind every TextBlock whose HighContrastAdjustment
        // is Auto, replacing the selected HighlightText foreground. The item paints its own system pairs,
        // so the presenter must opt out exactly like WinUI's TabViewItem header presenter does.
        Assert.Equal("None", (string?)contentPresenter.Attribute("HighContrastAdjustment"));

        foreach (string state in SelectedStates)
        {
            Assert.Equal("SystemColorHighlightColorBrush", ResolveHighContrast(template, state, "Root.Background", highContrast));
            Assert.Equal("SystemColorHighlightTextColorBrush", ResolveHighContrast(template, state, "ContentPresenter.Foreground", highContrast));
        }

        foreach (string state in PressedStates)
        {
            Assert.Equal("SystemColorHighlightColorBrush", ResolveHighContrast(template, state, "Root.Background", highContrast));
            Assert.Equal("SystemColorHighlightTextColorBrush", ResolveHighContrast(template, state, "ContentPresenter.Foreground", highContrast));
        }

        Assert.Equal("SystemColorButtonFaceColorBrush", ResolveHighContrast(template, "PointerOver", "Root.Background", highContrast));
        Assert.Equal("SystemColorHighlightColorBrush", ResolveHighContrast(template, "PointerOver", "Outline.BorderBrush", highContrast));
        Assert.Equal("SystemColorButtonTextColorBrush", ResolveHighContrast(template, "PointerOver", "ContentPresenter.Foreground", highContrast));

        foreach (string state in DisabledStates)
        {
            Assert.Equal("SystemColorGrayTextColorBrush", ResolveHighContrast(template, state, "ContentPresenter.Foreground", highContrast));
            Assert.Equal("SystemColorGrayTextColorBrush", ResolveHighContrast(template, state, "Outline.BorderBrush", highContrast));
            Assert.Equal("AppOpacityVisible", ResolveHighContrast(template, state, "Root.Opacity", highContrast));
        }

        // Unselected items are outlined and selected items are filled, so selection is a shape change and
        // not only a hue change. The outline thickness is theme-scoped and identical in every state.
        Assert.Equal("SystemColorButtonFaceColorBrush", ResolveSetter(itemStyle, "Background", highContrast));
        Assert.Equal("SystemColorButtonTextColorBrush", ResolveSetter(itemStyle, "Foreground", highContrast));
        Assert.Equal("SystemColorButtonTextColorBrush", ResolveSetter(itemStyle, "BorderBrush", highContrast));
        Assert.Equal("AppHairlineBorderThickness", ResolveSetter(itemStyle, "BorderThickness", highContrast));
        Assert.DoesNotContain(
            template.Descendants().Where(element => element.Name.LocalName == "Setter"),
            setter => ((string?)setter.Attribute("Target"))?.EndsWith(".BorderThickness", StringComparison.Ordinal) == true);

        // The High Contrast-only outline is an overlay, so it never shrinks the label's layout slot.
        XElement outline = FindNamed(template, "Outline");
        Assert.Equal("{TemplateBinding BorderThickness}", (string?)outline.Attribute("BorderThickness"));
        Assert.Equal("False", (string?)outline.Attribute("IsHitTestVisible"));
        Assert.Empty(outline.Elements());
        Assert.All(
            contentPresenter.Ancestors().TakeWhile(ancestor => ancestor != template),
            ancestor => Assert.Null(ancestor.Attribute("BorderThickness")));

        Assert.Equal("SystemColorWindowTextColorBrush", ResolveAttribute(focusVisual, "BorderBrush", highContrast));
        Assert.Equal("AppStrongBorderThickness", ResolveAttribute(focusVisual, "BorderThickness", highContrast));
    }

    [Fact]
    public void SegmentedItemTextLabels_InheritTheItemStateForeground()
    {
        // The implicit TextBlock style sets Foreground, and an implicit style value beats the foreground a
        // TextBlock would otherwise inherit from the item's ContentPresenter. A label that keeps the implicit
        // style therefore ignores every state brush; in High Contrast the selected label stays WindowText on the
        // Highlight fill. Labels must use the keyed label style, which sets no Foreground, and set none locally.
        const string labelStyleReference = "{StaticResource AppSegmentedItemLabelTextBlockStyle}";
        XElement labelStyle = FindStyle(LoadBridge(), "AppSegmentedItemLabelTextBlockStyle");
        Assert.Equal("TextBlock", (string?)labelStyle.Attribute("TargetType"));
        Assert.Null(labelStyle.Attribute("BasedOn"));
        Assert.DoesNotContain(
            labelStyle.Elements(),
            setter => string.Equals((string?)setter.Attribute("Property"), "Foreground", StringComparison.Ordinal));

        string appRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        List<string> violations = [];
        int labelCount = 0;
        foreach (string path in Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
                     .Where(path => !IsBuildOutput(appRoot, path)))
        {
            XDocument document = XDocument.Load(path);
            foreach (XElement label in document.Descendants()
                         .Where(element => element.Name.LocalName is "AppSegmentedItem" or "SegmentedItem")
                         .SelectMany(item => item.Descendants())
                         .Where(element => element.Name.LocalName == "TextBlock"))
            {
                labelCount++;
                string where = $"{Path.GetRelativePath(appRoot, path)}: TextBlock '{(string?)label.Attribute("Text")}'";
                if (!string.Equals((string?)label.Attribute("Style"), labelStyleReference, StringComparison.Ordinal))
                {
                    violations.Add($"{where} must use {labelStyleReference}.");
                }

                if (label.Attribute("Foreground") is not null)
                {
                    violations.Add($"{where} must not set Foreground.");
                }
            }
        }

        Assert.True(labelCount > 0, "Expected at least one segmented item with a TextBlock label.");
        Assert.True(
            violations.Count == 0,
            $"Segmented item labels must follow the item's state foreground:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    private static bool IsBuildOutput(string appRoot, string path)
    {
        string first = Path.GetRelativePath(appRoot, path).Split(Path.DirectorySeparatorChar)[0];
        return first is "obj" or "bin";
    }

    private static IEnumerable<string> ReadBrushReferences(XElement style)
    {
        foreach (XElement element in style.Descendants())
        {
            foreach (XAttribute attribute in element.Attributes()
                         .Where(attribute => BrushProperties.Contains(attribute.Name.LocalName, StringComparer.Ordinal)))
            {
                if (TryReadThemeResourceKey(attribute.Value, out string key))
                {
                    yield return key;
                }
            }

            if (element.Name.LocalName != "Setter" || element.Attribute("Value") is not { } value)
            {
                continue;
            }

            string? property = (string?)element.Attribute("Property") ??
                ((string?)element.Attribute("Target"))?.Split('.').LastOrDefault();
            if (property is not null &&
                BrushProperties.Contains(property, StringComparer.Ordinal) &&
                TryReadThemeResourceKey(value.Value, out string setterKey))
            {
                yield return setterKey;
            }
        }
    }

    private static string? ResolveHighContrast(
        XElement template,
        string stateName,
        string target,
        IReadOnlyDictionary<string, string> highContrast)
    {
        XElement state = template.Descendants()
            .Single(element =>
                element.Name.LocalName == "VisualState" &&
                string.Equals((string?)element.Attribute(Xaml + "Name"), stateName, StringComparison.Ordinal));
        XElement? setter = state.Descendants()
            .SingleOrDefault(element =>
                element.Name.LocalName == "Setter" &&
                string.Equals((string?)element.Attribute("Target"), target, StringComparison.Ordinal));

        return Resolve((string?)setter?.Attribute("Value"), highContrast);
    }

    private static string? ResolveSetter(XElement style, string property, IReadOnlyDictionary<string, string> highContrast)
    {
        XElement setter = style.Elements()
            .Single(element =>
                element.Name.LocalName == "Setter" &&
                string.Equals((string?)element.Attribute("Property"), property, StringComparison.Ordinal));
        return Resolve((string?)setter.Attribute("Value"), highContrast);
    }

    private static string? ResolveAttribute(XElement element, string attribute, IReadOnlyDictionary<string, string> highContrast) =>
        Resolve((string?)element.Attribute(attribute), highContrast);

    private static string? Resolve(string? value, IReadOnlyDictionary<string, string> highContrast) =>
        value is not null &&
        TryReadThemeResourceKey(value, out string key) &&
        highContrast.TryGetValue(key, out string? resolved)
            ? resolved
            : null;

    private static bool TryReadThemeResourceKey(string value, out string key)
    {
        Match match = ThemeResourceReference.Match(value.Trim());
        key = match.Success ? match.Groups["key"].Value : string.Empty;
        return match.Success;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadThemeAliases(XDocument bridge) =>
        bridge.Root!
            .Elements()
            .Where(element => element.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .Elements()
            .Where(element => element.Name.LocalName == "ResourceDictionary")
            .ToDictionary(
                element => (string)element.Attribute(Xaml + "Key")!,
                element => element.Elements()
                    .Where(resource => resource.Name.LocalName == "StaticResource")
                    .ToDictionary(
                        resource => (string)resource.Attribute(Xaml + "Key")!,
                        resource => (string)resource.Attribute("ResourceKey")!,
                        StringComparer.Ordinal),
                StringComparer.Ordinal);

    private static IEnumerable<XElement> FindSegmentedStyles(XDocument bridge) =>
        bridge.Root!
            .Elements()
            .Where(element => element.Name.LocalName == "Style")
            .Where(element => ((string?)element.Attribute("TargetType"))?.Contains("Segmented", StringComparison.Ordinal) == true);

    private static XElement FindStyle(XDocument bridge, string key) =>
        bridge.Root!
            .Elements()
            .Single(element =>
                element.Name.LocalName == "Style" &&
                string.Equals((string?)element.Attribute(Xaml + "Key"), key, StringComparison.Ordinal));

    private static XElement FindNamed(XElement scope, string name) =>
        scope.Descendants()
            .Single(element => string.Equals((string?)element.Attribute(Xaml + "Name"), name, StringComparison.Ordinal));

    private static XDocument LoadBridge() =>
        XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "WinUIResourceBridge.xaml"));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
