using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

/// <summary>The native activation steps for one suspended worker process.</summary>
internal sealed class SuspendedWorkerActivation(
    WorkerJob job,
    SafeProcessHandle process,
    SafeWaitHandle thread,
    SafeTokenHandle sourceToken,
    WorkerObjectSecurity.WorkerObjectSecurityContext objectSecurity) : ISuspendedWorkerActivation
{
    public void AssignToJob() => job.Assign(process);

    public void AttachX86StartupToken() =>
        WorkerProcessSandbox.AttachX86StartupToken(thread, sourceToken, objectSecurity);

    public void Resume()
    {
        if (WorkerNativeMethods.ResumeThread(thread) == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The resvg worker could not leave its suspended launch state.");
    }
}
