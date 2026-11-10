namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Processes;

/// <summary>
/// The three steps that release a worker created suspended. The order is
/// owned by <see cref="WorkerProcessSandbox.ActivateSuspended"/>; each step
/// throws when it fails.
/// </summary>
internal interface ISuspendedWorkerActivation
{
    /// <summary>Places the process in its kill-on-close job.</summary>
    void AssignToJob();

    /// <summary>Attaches the x86-only restricted startup token to the initial thread.</summary>
    void AttachX86StartupToken();

    /// <summary>Resumes the initial thread.</summary>
    void Resume();
}
