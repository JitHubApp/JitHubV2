using System.Collections.Generic;
using JitHub.Services;

namespace JitHub.WinUI.Tests.Services;

internal sealed class MemorySettingService : ISettingService
{
    private readonly Dictionary<string, object?> _values = new();

    public bool Contains(string key) => _values.ContainsKey(key);

    public void Save<T>(string key, T value)
    {
        if (value is null)
        {
            _values.Remove(key);
            return;
        }

        _values[key] = value;
    }

    public T Get<T>(string key)
    {
        return _values.TryGetValue(key, out object? value) && value is T typed
            ? typed
            : default!;
    }
}
