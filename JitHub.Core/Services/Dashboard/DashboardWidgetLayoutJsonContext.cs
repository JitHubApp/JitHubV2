using System.Text.Json.Serialization;

namespace JitHub.Core.Services.Dashboard;

[JsonSerializable(typeof(DashboardWidgetLayoutDto))]
internal sealed partial class DashboardWidgetLayoutJsonContext : JsonSerializerContext
{
}
