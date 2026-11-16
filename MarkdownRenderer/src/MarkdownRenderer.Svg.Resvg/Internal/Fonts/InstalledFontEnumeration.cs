namespace MarkdownRenderer.Svg.Resvg.Internal.Fonts;

/// <summary>
/// The running totals of one installed-font capture across every approved
/// root. It is passed by reference to each root and file step so the
/// candidate ceiling, the mapped-view ceiling and file deduplication apply to
/// the capture as a whole.
/// </summary>
internal struct InstalledFontEnumeration
{
    /// <summary>Creates the totals for a new capture.</summary>
    public InstalledFontEnumeration()
    {
    }

    /// <summary>The volume and file index of every file already mapped.</summary>
    internal readonly HashSet<FontFileIdentity> FileIdentities = [];

    /// <summary>The address space every mapped font view will need in the worker.</summary>
    internal long AggregateViewBytes;

    /// <summary>The number of directory entries considered so far.</summary>
    internal int CandidateCount;
}
