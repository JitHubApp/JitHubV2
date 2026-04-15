using System.Text.Json.Serialization;

namespace JitHub.Auth;

[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(GitHubTokenResponse))]
internal sealed partial class JitHubAuthJsonSerializerContext : JsonSerializerContext
{
}
