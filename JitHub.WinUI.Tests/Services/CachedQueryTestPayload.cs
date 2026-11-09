namespace JitHub.WinUI.Tests.Services;

/// <summary>A small payload the query and cache tests store and read back.</summary>
public sealed class CachedQueryTestPayload
{
    public string Name { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
}
