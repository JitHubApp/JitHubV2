using System;
using System.Runtime.InteropServices;

namespace MarkdownRenderer.Layout.Internal.Text;

/// <summary>
/// Converts a XAML font family to the form Win2D draws with.
/// </summary>
/// <remarks>
/// A XAML family such as <c>ms-appx:///Assets/Fonts/Mono.ttf#Mono Face</c> names a
/// font file and the face inside it. Win2D reads the same <c>uri#family</c> form,
/// and without the fragment it falls back to its default proportional face. Win2D
/// can resolve <c>ms-appx</c> and <c>ms-appdata</c> files only in a process with
/// package identity, so an unpackaged host gets the face name instead, which an
/// installed copy of the font can still satisfy.
/// </remarks>
// Partial for the LibraryImport source generator.
internal static partial class CanvasFontFamilyReference
{
    private const int AppModelErrorNoPackage = 15700;

    private static readonly bool ProcessHasPackageIdentity = ReadProcessHasPackageIdentity();

    /// <summary>Converts <paramref name="fontFamily"/> for this process.</summary>
    internal static string ForCanvas(string fontFamily) =>
        ForCanvas(fontFamily, ProcessHasPackageIdentity);

    /// <summary>Converts <paramref name="fontFamily"/> for a process with or without package identity.</summary>
    internal static string ForCanvas(string fontFamily, bool processHasPackageIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        string candidate = fontFamily.Trim();
        if (processHasPackageIdentity)
            return candidate;

        int fragmentIndex = candidate.LastIndexOf('#');
        if (fragmentIndex <= 0 ||
            !Uri.TryCreate(candidate[..fragmentIndex], UriKind.Absolute, out Uri? fontFileUri) ||
            !IsAppPackageScheme(fontFileUri.Scheme))
            return candidate;

        string faceName = candidate[(fragmentIndex + 1)..].Trim();
        return faceName.Length == 0 ? candidate : faceName;
    }

    private static bool IsAppPackageScheme(string scheme) =>
        string.Equals(scheme, "ms-appx", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scheme, "ms-appdata", StringComparison.OrdinalIgnoreCase);

    private static bool ReadProcessHasPackageIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, 0) != AppModelErrorNoPackage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);
}
