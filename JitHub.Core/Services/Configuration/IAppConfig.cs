using JitHub.Core.Models.Authentication;

namespace JitHub.Core.Services.Configuration;

public interface IAppConfig
{
    Credential Credential { get; }
}
