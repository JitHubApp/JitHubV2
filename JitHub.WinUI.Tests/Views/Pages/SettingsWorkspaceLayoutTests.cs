using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views.Pages;

public sealed class SettingsWorkspaceLayoutTests
{
    [Fact]
    public void VisualStateTargets_ResolveToNamedElements()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "SettingsPage.xaml");
        XDocument document = XDocument.Load(Path.GetFullPath(path));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> names = document
            .Descendants()
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        string[] unresolved = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Setter")
            .Select(element => (string?)element.Attribute("Target"))
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target!.Split(new[] { '.', '(' }, 2)[0])
            .Where(targetName => !names.Contains(targetName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unresolved);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
