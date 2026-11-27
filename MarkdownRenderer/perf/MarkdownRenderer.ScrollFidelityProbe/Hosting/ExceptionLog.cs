using System.Runtime.ExceptionServices;

namespace MarkdownRenderer.ScrollFidelityProbe.Hosting;

/// <summary>
/// Appends every first-chance exception, with its stack, to a file next to
/// the report. A WinUI app can fail fast from an exception that escapes a
/// dispatcher callback without raising <c>Application.UnhandledException</c>;
/// this log is then the only record of what threw. A healthy run throws
/// rarely, so the log costs nothing measurable.
/// </summary>
internal sealed class ExceptionLog : IDisposable
{
    private readonly object _gate = new();
    // Writing can itself throw; never log the log's own exceptions.
    private readonly ThreadLocal<bool> _writing = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    private ExceptionLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    /// <summary>Starts logging to <paramref name="reportPath"/> plus <c>.exceptions.log</c>.</summary>
    internal static ExceptionLog StartFor(string reportPath) => new(reportPath + ".exceptions.log");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
            _writer.Dispose();
            // _writing is left to its finalizer: a handler already past the
            // _disposed check on another thread may still read it.
        }
    }

    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (_disposed || _writing.Value)
            return;
        _writing.Value = true;
        try
        {
            string entry =
                $"[{DateTime.UtcNow:O}] thread {Environment.CurrentManagedThreadId} " +
                $"{args.Exception.GetType().FullName} (0x{args.Exception.HResult:X8}): {args.Exception.Message}" +
                Environment.NewLine + Environment.StackTrace + Environment.NewLine;
            lock (_gate)
            {
                if (!_disposed)
                    _writer.Write(entry);
            }
        }
        catch (IOException)
        {
            // The log is diagnostic only; a full or locked disk must not
            // change what the probe measures.
        }
        finally
        {
            _writing.Value = false;
        }
    }
}
