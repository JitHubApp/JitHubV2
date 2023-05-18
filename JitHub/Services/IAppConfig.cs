using JitHub.Models;

namespace JitHub.Services;

internal interface IAppConfig
{
    Credential Credential { get; }
    Features Features { get; }
}
