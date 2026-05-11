namespace JitHub.Models
{
    [WinRT.GeneratedBindableCustomProperty]
    public partial class FailedRepo
    {
        public RepoModel Repo { get; set; }
        public string Reason { get; set; } = string.Empty;
        public FailedRepo(RepoModel repo, string reason)
        {
            Repo = repo;
            Reason = reason;
        }
    }
}
