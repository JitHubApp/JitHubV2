using System;
using System.Collections.Generic;

namespace JitHub.WinUI.Tests.TestSupport;

/// <summary>An item as MSBuild evaluated it: its identity and its metadata.</summary>
internal sealed class MsBuildItem
{
    private readonly IReadOnlyDictionary<string, string> _metadata;

    public MsBuildItem(string identity, IReadOnlyDictionary<string, string> metadata)
    {
        Identity = identity;
        _metadata = metadata;
    }

    public string Identity { get; }

    /// <summary>The evaluated metadata value, or an empty string when the item has none, as in MSBuild.</summary>
    public string GetMetadata(string name) =>
        _metadata.TryGetValue(name, out string? value) ? value : string.Empty;

    public override string ToString() => Identity;
}
