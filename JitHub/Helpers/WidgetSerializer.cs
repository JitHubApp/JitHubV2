using Microsoft.Toolkit.Helpers;
using System.Text.Json;

namespace JitHub.Helpers;

internal class WidgetSerializer : IObjectSerializer
{
    public string Serialize<T>(T value) => JsonSerializer.Serialize(value);
    public T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>((string)value);
}
