using System.Text.Json.Serialization;

namespace JitHub.Services;

[JsonSerializable(typeof(DashboardWidgetLayoutDto))]
internal sealed partial class DashboardWidgetLayoutJsonContext : JsonSerializerContext
{
}
