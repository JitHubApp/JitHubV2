using Octokit;

namespace JitHub.Models.NavArgs
{
    // can also be used for commits
    public class CodeViewerNavArg : PageNavArg
    {
        private CodeRefType _type;
        private string _branch;
        private string _gitRef;
        public CodeRefType Type => _type;
        public string Branch => _branch;
        public string GitRef => _gitRef;
        public bool IsBranch => _type == CodeRefType.Branch;
        public bool IsGitRef => _type == CodeRefType.GitRef;

        public CodeViewerNavArg(Repository repo) : base(repo)
        {
        }

        public static CodeViewerNavArg CreateWithRepo(Repository repo)
        {
            return new CodeViewerNavArg(repo) { _branch = repo.DefaultBranch, _type = CodeRefType.Branch };
        }

        public static CodeViewerNavArg CreateWithBranch(Repository repo, string branch)
        {
            return new CodeViewerNavArg(repo) { _branch = branch, _type = CodeRefType.Branch };
        }

        public static CodeViewerNavArg CreateWithGitRef(Repository repo, string _ref)
        {
            return new CodeViewerNavArg(repo) { _gitRef = _ref, _type = CodeRefType.GitRef };
        }
    }

    public enum CodeRefType
    {
        Branch,
        GitRef
    }
}
