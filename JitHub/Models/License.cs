using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.Models
{
    public class License
    {
        public string Name { get; set; }
        public string DiaplayName { get; set; }
        public string Url { get; set; }

        public static ICollection<License> GetLicenses()
        {
            return new List<License>()
            {
                new License()
                {
                  Name = "agpl-3.0",
                  DiaplayName = "GNU Affero General Public License v3.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/agpl-3.0.txt"
                },
                new License()
                {
                  Name = "afl-3.0",
                  DiaplayName = "Academic Free License v3.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/afl-3.0.txt"
                },
                new License()
                {
                  Name = "bsd-4-clause",
                  DiaplayName = "BSD 4-Clause \"Original\" or \"Old\" License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/bsd-4-clause.txt"
                },
                new License()
                {
                  Name = "bsd-2-clause",
                  DiaplayName = "BSD 2-Clause \"Simplified\" License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/bsd-2-clause.txt"
                },
                new License()
                {
                  Name = "artistic-2.0",
                  DiaplayName = "Artistic License 2.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/artistic-2.0.txt"
                },
                new License()
                {
                  Name = "0bsd",
                  DiaplayName = "BSD Zero Clause License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/0bsd.txt"
                },
                new License()
                {
                  Name = "bsd-3-clause-clear",
                  DiaplayName = "BSD 3-Clause Clear License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/bsd-3-clause-clear.txt"
                },
                new License()
                {
                  Name = "apache-2.0",
                  DiaplayName = "Apache License 2.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/apache-2.0.txt"
                },
                new License()
                {
                  Name = "bsl-1.0",
                  DiaplayName = "Boost Software License 1.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/bsl-1.0.txt"
                },
                new License()
                {
                  Name = "bsd-3-clause",
                  DiaplayName = "BSD 3-Clause \"New\" or \"Revised\" License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/bsd-3-clause.txt"
                },
                new License()
                {
                  Name = "ecl-2.0",
                  DiaplayName = "Educational Community License v2.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/ecl-2.0.txt"
                },
                new License()
                {
                  Name = "cc0-1.0",
                  DiaplayName = "Creative Commons Zero v1.0 Universal",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/cc0-1.0.txt"
                },
                new License()
                {
                  Name = "cc-by-sa-4.0",
                  DiaplayName = "Creative Commons Attribution Share Alike 4.0 International",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/cc-by-sa-4.0.txt"
                },
                new License()
                {
                  Name = "eupl-1.2",
                  DiaplayName = "European Union Public License 1.2",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/eupl-1.2.txt"
                },
                new License()
                {
                  Name = "epl-1.0",
                  DiaplayName = "Eclipse Public License 1.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/epl-1.0.txt"
                },
                new License()
                {
                  Name = "cc-by-4.0",
                  DiaplayName = "Creative Commons Attribution 4.0 International",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/cc-by-4.0.txt"
                },
                new License()
                {
                  Name = "cecill-2.1",
                  DiaplayName = "CeCILL Free Software License Agreement v2.1",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/cecill-2.1.txt"
                },
                new License()
                {
                  Name = "epl-2.0",
                  DiaplayName = "Eclipse Public License 2.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/epl-2.0.txt"
                },
                new License()
                {
                  Name = "gpl-2.0",
                  DiaplayName = "GNU General Public License v2.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/gpl-2.0.txt"
                },
                new License()
                {
                  Name = "lgpl-3.0",
                  DiaplayName = "GNU Lesser General Public License v3.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/lgpl-3.0.txt"
                },
                new License()
                {
                  Name = "isc",
                  DiaplayName = "ISC License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/isc.txt"
                },
                new License()
                {
                  Name = "gpl-3.0",
                  DiaplayName = "GNU General Public License v3.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/gpl-3.0.txt"
                },
                new License()
                {
                  Name = "eupl-1.1",
                  DiaplayName = "European Union Public License 1.1",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/eupl-1.1.txt"
                },
                new License()
                {
                  Name = "lgpl-2.1",
                  DiaplayName = "GNU Lesser General Public License v2.1",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/lgpl-2.1.txt"
                },
                new License()
                {
                  Name = "lppl-1.3c",
                  DiaplayName = "LaTeX Project Public License v1.3c",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/lppl-1.3c.txt"
                },
                new License()
                {
                  Name = "mit-0",
                  DiaplayName = "MIT No Attribution",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/mit-0.txt"
                },
                new License()
                {
                  Name = "mpl-2.0",
                  DiaplayName = "Mozilla Public License 2.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/mpl-2.0.txt"
                },
                new License()
                {
                  Name = "ms-pl",
                  DiaplayName = "Microsoft Public License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/ms-pl.txt"
                },
                new License()
                {
                  Name = "mit",
                  DiaplayName = "MIT License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/mit.txt"
                },
                new License()
                {
                  Name = "ms-rl",
                  DiaplayName = "Microsoft Reciprocal License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/ms-rl.txt"
                },
                new License()
                {
                  Name = "odbl-1.0",
                  DiaplayName = "Open Data Commons Open Database License v1.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/odbl-1.0.txt"
                },
                new License()
                {
                  Name = "ofl-1.1",
                  DiaplayName = "SIL Open Font License 1.1",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/ofl-1.1.txt"
                },
                new License()
                {
                  Name = "mulanpsl-2.0",
                  DiaplayName = "Mulan Permissive Software License, Version 2",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/mulanpsl-2.0.txt"
                },
                new License()
                {
                  Name = "osl-3.0",
                  DiaplayName = "Open Software License 3.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/osl-3.0.txt"
                },
                new License()
                {
                  Name = "ncsa",
                  DiaplayName = "University of Illinois/NCSA Open Source License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/ncsa.txt"
                },
                new License()
                {
                  Name = "postgresql",
                  DiaplayName = "PostgreSQL License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/postgresql.txt"
                },
                new License()
                {
                  Name = "unlicense",
                  DiaplayName = "The Unlicense",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/unlicense.txt"
                },
                new License()
                {
                  Name = "upl-1.0",
                  DiaplayName = "Universal Permissive License v1.0",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/upl-1.0.txt"
                },
                new License()
                {
                  Name = "vim",
                  DiaplayName = "Vim License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/vim.txt"
                },
                new License()
                {
                  Name = "wtfpl",
                  DiaplayName = "Do What The F*ck You Want To Public License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/wtfpl.txt"
                },
                new License()
                {
                  Name = "zlib",
                  DiaplayName = "zlib License",
                  Url = "https://github.com/github/choosealicense.com/tree/gh-pages/_licenses/zlib.txt"
                }
            };
        }
    }
}
