using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using MarkdownRenderer.Images;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeTokenHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => WorkerNativeMethods.CloseHandle(handle);
}
