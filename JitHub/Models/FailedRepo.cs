namespace JitHub.Models
{
    public class FailedRepo
    {
        public RepoModel Repo { get; set; }
        public string Reason { get; set; }
        public FailedRepo(RepoModel repo, string reason)
        {
            Repo = repo;
            Reason = reason;
        }
    }
}
