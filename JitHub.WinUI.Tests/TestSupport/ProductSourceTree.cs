using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JitHub.WinUI.Tests.TestSupport;

/// <summary>
/// The desktop app's product source files. The app project holds the WinUI
/// views, XAML and composition root; the class libraries it references hold
/// the rest of its code in the same folder layout. A contract that covers every
/// product file enumerates all of these projects and names each file by its
/// path inside its project, so a file keeps its name when it moves between them.
/// </summary>
internal static class ProductSourceTree
{
    /// <summary>The projects whose sources make up the desktop app, app first.</summary>
    public static IReadOnlyList<string> ProjectNames { get; } =
    [
        "JitHub.WinUI",
        "JitHub.Performance",
        "JitHub.Shared"
    ];

    /// <summary>
    /// Every file that matches <paramref name="searchPattern"/> under the
    /// product projects, ordered by project and then by path.
    /// </summary>
    public static IEnumerable<ProductSourceFile> EnumerateFiles(string searchPattern)
    {
        string repositoryRoot = FindRepositoryRoot();
        foreach (string projectName in ProjectNames)
        {
            string projectRoot = Path.Combine(repositoryRoot, projectName);
            foreach (string path in Directory.EnumerateFiles(projectRoot, searchPattern, SearchOption.AllDirectories)
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return new ProductSourceFile(
                    projectName,
                    path,
                    Path.GetRelativePath(projectRoot, path).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }
    }

    /// <summary>
    /// The full path of the product file at <paramref name="relativePath"/>
    /// inside whichever product project owns it.
    /// </summary>
    public static string GetFullPath(string relativePath)
    {
        string repositoryRoot = FindRepositoryRoot();
        string localPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string[] owners = ProjectNames
            .Select(projectName => Path.Combine(repositoryRoot, projectName, localPath))
            .Where(File.Exists)
            .ToArray();
        return owners.Length switch
        {
            1 => owners[0],
            0 => throw new FileNotFoundException($"No product project contains '{relativePath}'."),
            _ => throw new InvalidOperationException($"More than one product project contains '{relativePath}'.")
        };
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
