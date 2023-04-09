using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.Models.CommandArgs
{
    public class IssueFormArgs
    {
        public long RepoId { get; set; }
        public Issue Issue { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
    }
}
