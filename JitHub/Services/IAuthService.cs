using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.Services
{
    public interface IAuthService
    {
        bool Authenticated { get; set; }
        User AuthenticatedUser { get; set; }
        Task Authenticate();
        Task<bool> Authorize(string response);
        string GetToken(int userId);
        bool CheckAuth(int userId);
        void SignOut();
    }
}
