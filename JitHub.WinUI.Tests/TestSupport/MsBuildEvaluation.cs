using System;
using System.Collections.Generic;

namespace JitHub.WinUI.Tests.TestSupport;

/// <summary>The properties and items one MSBuild evaluation of a project produced.</summary>
internal sealed class MsBuildEvaluation
{
    private readonly IReadOnlyDictionary<string, string> _properties;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<MsBuildItem>> _items;

    public MsBuildEvaluation(
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyDictionary<string, IReadOnlyList<MsBuildItem>> items)
    {
        _properties = properties;
        _items = items;
    }

    /// <summary>The evaluated value, or an empty string for an undefined property, as in MSBuild.</summary>
    public string GetProperty(string name) =>
        _properties.TryGetValue(name, out string? value) ? value : string.Empty;

    public IReadOnlyList<MsBuildItem> GetItems(string itemType) =>
        _items.TryGetValue(itemType, out IReadOnlyList<MsBuildItem>? items) ? items : [];
}
