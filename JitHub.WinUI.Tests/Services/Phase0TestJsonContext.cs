using System.Text.Json.Serialization;

namespace JitHub.WinUI.Tests.Services;

[JsonSerializable(typeof(Phase0TestPayload))]
internal sealed partial class Phase0TestJsonContext : JsonSerializerContext
{
}
public sealed class Phase0TestPayload
{
    public string Name { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
}
