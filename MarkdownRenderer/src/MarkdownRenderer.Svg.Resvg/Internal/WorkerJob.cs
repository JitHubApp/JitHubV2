using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using MarkdownRenderer.Images;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class WorkerJob : IDisposable
{
    private SafeJobHandle? _handle;

    internal WorkerJob(SafeJobHandle handle)
    {
        _handle = handle;
    }

    internal void Assign(SafeProcessHandle process)
    {
        SafeJobHandle handle = _handle ?? throw new ObjectDisposedException(nameof(WorkerJob));
        if (!WorkerNativeMethods.AssignProcessToJobObject(handle, process))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The resvg worker could not enter its aggregate kill-on-close job.");
    }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
}
