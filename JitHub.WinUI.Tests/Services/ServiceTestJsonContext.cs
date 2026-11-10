using System.Text.Json.Serialization;

namespace JitHub.WinUI.Tests.Services;

/// <summary>AOT-safe JSON metadata for the payloads the service tests cache and persist.</summary>
[JsonSerializable(typeof(CachedQueryTestPayload))]
[JsonSerializable(typeof(JitHub.Core.Services.Repositories.RepositoryForkOwnershipState[]))]
internal sealed partial class ServiceTestJsonContext : JsonSerializerContext
{
}
