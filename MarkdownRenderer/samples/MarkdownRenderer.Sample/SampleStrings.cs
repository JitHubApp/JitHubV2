using System;
using System.IO;
using Microsoft.Windows.ApplicationModel.Resources;

namespace MarkdownRenderer.Sample;

/// <summary>
/// Resolves sample-shell strings from MRT while retaining an English fallback
/// for isolated development hosts that do not have an application resource map.
/// The window loads one instance and passes it to the parts it composes.
/// </summary>
internal sealed class SampleStrings
{
    private readonly ResourceAccessor? _accessor;

    private SampleStrings(ResourceAccessor? accessor) => _accessor = accessor;

    internal static SampleStrings Load()
    {
        try
        {
            return new SampleStrings(CreateAccessor());
        }
        catch
        {
            // Without a resource map every lookup uses its English fallback.
            return new SampleStrings(accessor: null);
        }
    }

    public string Get(string key, string fallback)
    {
        try
        {
            string? value = _accessor?.Get(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private static ResourceAccessor? CreateAccessor()
    {
        string priPath = Path.Combine(AppContext.BaseDirectory, "resources.pri");
        ResourceManager manager = File.Exists(priPath)
            ? new ResourceManager(priPath)
            : new ResourceManager();
        ResourceMap? map = manager.MainResourceMap.TryGetSubtree("Resources");
        return map is null ? null : new ResourceAccessor(manager, map);
    }

    private sealed class ResourceAccessor
    {
        private readonly ResourceManager _manager;
        private readonly ResourceMap _map;
        private readonly ResourceContext _context;

        public ResourceAccessor(ResourceManager manager, ResourceMap map)
        {
            _manager = manager;
            _map = map;
            _context = manager.CreateResourceContext();
        }

        public string? Get(string key)
        {
            ResourceCandidate? candidate = _map.TryGetValue(key, _context);
            GC.KeepAlive(_manager);
            return candidate?.ValueAsString;
        }
    }
}
