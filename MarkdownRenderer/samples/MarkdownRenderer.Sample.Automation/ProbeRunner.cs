namespace MarkdownRenderer.Sample.Automation;

/// <summary>
/// Runs named probes for one driver run, records which passed and which
/// failed, and prints the results summary that decides the exit code.
/// </summary>
internal sealed class ProbeRunner
{
    private readonly List<string> _passes = new();
    private readonly List<string> _failures = new();
    private readonly TextWriter _output;
    private readonly TextWriter _error;

    internal ProbeRunner(TextWriter output, TextWriter error)
    {
        _output = output;
        _error = error;
    }

    internal void Run(string name, Action probe)
    {
        try
        {
            probe();
            _passes.Add(name);
            _output.WriteLine($"[automation] PASS {name}");
        }
        catch (Exception ex)
        {
            _failures.Add($"{name}: {ex.Message}");
            _error.WriteLine($"[automation] FAIL {name}: {ex}");
        }
    }

    /// <summary>
    /// Prints every pass and failure and returns the process exit code: 0 when
    /// no probe failed, otherwise 1.
    /// </summary>
    internal int WriteSummary()
    {
        _output.WriteLine();
        _output.WriteLine("──────────────── results ────────────────");
        foreach (var p in _passes)   _output.WriteLine($"  PASS {p}");
        foreach (var f in _failures) _output.WriteLine($"  FAIL {f}");
        _output.WriteLine($"{_passes.Count} passed, {_failures.Count} failed");
        return _failures.Count == 0 ? 0 : 1;
    }
}
