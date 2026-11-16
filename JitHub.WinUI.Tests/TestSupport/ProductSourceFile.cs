namespace JitHub.WinUI.Tests.TestSupport;

/// <summary>
/// A product source file: the project that owns it, its full path, and its
/// path inside that project with forward slashes.
/// </summary>
internal sealed record ProductSourceFile(string ProjectName, string FullPath, string RelativePath);
