using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace JitHub.Models.NavArgs
{
    public class ModalArg
    {
        public string Title { get; set; }
        public bool UseHeader { get; set; }
        public FrameworkElement Content { get; set; }
    }
}
