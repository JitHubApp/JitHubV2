using JitHub.Models;

namespace JitHub.Services
{
    public interface IAppConfig
    {
        Credential Credential { get; }
    }
}
