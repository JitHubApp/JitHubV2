using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class NotificationsWorkspaceContractTests
{
    [Fact]
    public void LoadedAndPartialScope_IsVisibleInTheWorkspaceHeader()
    {
        XDocument document = XDocument.Load(NotificationsXamlPath());
        XElement scope = Assert.Single(
            document.Descendants(),
            static element =>
                element.Name.LocalName == "TextBlock" &&
                element.Attribute("AutomationProperties.AutomationId")?.Value == "NotificationsResultScope");

        Assert.Contains(scope.Descendants(), static element =>
            element.Name.LocalName == "Run" &&
            element.Attribute("Text")?.Value.Contains("ResultCountText", StringComparison.Ordinal) == true);
        Assert.Equal("Notification result scope", scope.Attribute("AutomationProperties.Name")?.Value);
    }

    [Fact]
    public void HeaderTitleAndCountShareOneTextBaseline()
    {
        XDocument document = XDocument.Load(NotificationsXamlPath());
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement identity = Assert.Single(document.Descendants(), element =>
            element.Attribute(xaml + "Name")?.Value == "NotificationsHeaderIdentity");
        XElement scope = Assert.Single(identity.Descendants(), element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "NotificationsResultScope");

        Assert.Equal("Grid", identity.Name.LocalName);
        Assert.Equal("Center", identity.Attribute("VerticalAlignment")?.Value);
        Assert.Equal("1", scope.Attribute("Grid.Column")?.Value);
        Assert.Null(scope.Attribute("Margin"));
        Assert.Null(scope.Attribute("Translation"));
        Assert.Equal("Left", scope.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Center", scope.Attribute("VerticalAlignment")?.Value);
        Assert.Contains(scope.Descendants(), static element =>
            element.Name.LocalName == "Run" &&
            string.Equals(element.Attribute("Text")?.Value, "Notifications", StringComparison.Ordinal));
        Assert.Contains(scope.Descendants(), static element =>
            element.Name.LocalName == "Run" &&
            element.Attribute("Text")?.Value.Contains("ResultCountText", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Filter_UsesOneStableNativeSelectorBar()
    {
        XDocument document = XDocument.Load(NotificationsXamlPath());
        XElement filter = Assert.Single(
            document.Descendants(),
            static element =>
                element.Name.LocalName == "SelectorBar" &&
                element.Attribute("AutomationProperties.AutomationId")?.Value == "NotificationsFilter");

        Assert.Equal("{ThemeResource AppDimension286}", filter.Attribute("MinWidth")?.Value);
        Assert.Equal("Right", filter.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Notification view", filter.Attribute("AutomationProperties.Name")?.Value);

        (string Id, string Label)[] expected =
        [
            ("NotificationsFilter_Unread", "Unread"),
            ("NotificationsFilter_All", "All"),
            ("NotificationsFilter_Participating", "Participating")
        ];

        XElement[] items = filter.Elements().Where(static element => element.Name.LocalName == "SelectorBarItem").ToArray();
        Assert.Equal(expected.Length, items.Length);

        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Id, items[index].Attribute("AutomationProperties.AutomationId")?.Value);
            Assert.Equal(expected[index].Label, items[index].Attribute("AutomationProperties.Name")?.Value);
            Assert.Equal(expected[index].Label, items[index].Attribute("Text")?.Value);
        }
    }

    [Fact]
    public void ThreadActions_ExposeReadFollowAndMuteWithAccessibleNames()
    {
        XDocument document = XDocument.Load(NotificationsXamlPath());
        XElement template = Assert.Single(
            document.Descendants(),
            static element => element.Name.LocalName == "DataTemplate" &&
                element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "NotificationRowTemplate");

        AssertAction(template, "MarkReadCommand", "ReadAutomationId", "ReadActionLabel");
        Assert.DoesNotContain("Mark as unread", template.ToString(), StringComparison.Ordinal);
        AssertAction(template, "ToggleSubscriptionCommand", "SubscriptionAutomationId", "SubscriptionActionLabel");
        AssertAction(template, "ToggleMuteCommand", "MuteAutomationId", "MuteActionLabel");

        Assert.Contains(
            template.Descendants().Where(static element => element.Name.LocalName == "MenuFlyoutItem"),
            static element => element.Attribute("Command")?.Value.Contains("ToggleSubscriptionCommand", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void MarkReadActions_UseTheSemanticNativeAcceptGlyph()
    {
        XDocument document = XDocument.Load(NotificationsXamlPath());
        XElement rowTemplate = Assert.Single(document.Descendants(), element =>
            element.Name.LocalName == "DataTemplate" &&
            element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "NotificationRowTemplate");
        XElement rowButton = Assert.Single(rowTemplate.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Command")?.Value.Contains("MarkReadCommand", StringComparison.Ordinal) == true);
        XElement rowIcon = Assert.Single(rowButton.Descendants(), element => element.Name.LocalName == "SymbolIcon");
        Assert.Equal("Accept", rowIcon.Attribute("Symbol")?.Value);

        XElement markAll = Assert.Single(document.Descendants(), element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "NotificationsMarkAllRead");
        XElement markAllIcon = Assert.Single(markAll.Descendants(), element => element.Name.LocalName == "SymbolIcon");
        Assert.Equal("Accept", markAllIcon.Attribute("Symbol")?.Value);
        Assert.DoesNotContain("E91B", document.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RowHover_PrefetchesDestinationWithoutLoadingSubscriptionState()
    {
        string code = File.ReadAllText(Path.ChangeExtension(NotificationsXamlPath(), ".xaml.cs"));

        int handlerStart = code.IndexOf("private void NotificationRow_PointerEntered", StringComparison.Ordinal);
        int handlerEnd = code.IndexOf("private void NotificationRow_PointerExited", handlerStart, StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        string handler = code[handlerStart..handlerEnd];
        Assert.Contains("ViewModel.PrefetchDestinationAsync(item)", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureSubscriptionStateAsync", handler, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CancelDestinationPrefetch()", code, StringComparison.Ordinal);
        Assert.Contains("PointerExited", code, StringComparison.Ordinal);
    }

    private static void AssertAction(
        XElement template,
        string command,
        string automationId,
        string accessibleName)
    {
        XElement button = Assert.Single(
            template.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                element.Attribute("Command")?.Value.Contains(command, StringComparison.Ordinal) == true);

        Assert.Contains(automationId, button.Attribute("AutomationProperties.AutomationId")?.Value, StringComparison.Ordinal);
        Assert.Contains(accessibleName, button.Attribute("AutomationProperties.Name")?.Value, StringComparison.Ordinal);
        Assert.Contains(accessibleName, button.Attribute("ToolTipService.ToolTip")?.Value, StringComparison.Ordinal);
    }

    private static string NotificationsXamlPath() => Path.Combine(
        FindRepositoryRoot(),
        "JitHub.WinUI",
        "Views",
        "Pages",
        "NotificationsPage.xaml");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
