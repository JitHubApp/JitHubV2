using Octokit;

namespace JitHub.Models
{
    public class CodeBranchRef
    {
        private string _name;
        private string _ref;
        public string Name => _name;
        public string Ref => _ref;

        public CodeBranchRef(Branch branch)
        {
            _name = branch.Name;
            _ref = branch.Name;
        }

        public CodeBranchRef(string @ref)
        {
            _name = @ref;
            _ref = @ref;
        }
    }
}
