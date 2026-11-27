namespace MarkdownRenderer.ScrollFidelityProbe.Hosting;

/// <summary>
/// Command-line options: <c>--out &lt;file&gt;</c> for the JSON report,
/// <c>--label &lt;text&gt;</c> naming the build under test, <c>--kib &lt;n&gt;</c>
/// for the corpus size and <c>--scenarios a,b</c> to run a subset.
/// </summary>
internal sealed record ProbeOptions(
    string OutputPath,
    string Label,
    int CorpusKib,
    IReadOnlySet<string> Scenarios,
    bool Progressive)
{
    internal static readonly string[] AllScenarios = ["wheel", "fling", "drag", "jump"];

    internal static ProbeOptions Parse(string[] arguments)
    {
        string output = Path.Combine(
            Environment.GetEnvironmentVariable("WINAPP_WORK_ROOT") is { Length: > 0 } work
                ? work
                : @"C:\WinApp\work",
            "scroll-probe",
            "report.json");
        string label = "unlabeled";
        int kib = 768;
        bool progressive = true;
        var scenarios = new HashSet<string>(AllScenarios, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            string Next() => index + 1 < arguments.Length
                ? arguments[++index]
                : throw new ArgumentException($"{argument} requires a value.");
            switch (argument)
            {
                case "--out":
                    output = Next();
                    break;
                case "--label":
                    label = Next();
                    break;
                case "--kib":
                    kib = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--no-session":
                    progressive = false;
                    break;
                case "--scenarios":
                    scenarios = new HashSet<string>(
                        Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        StringComparer.OrdinalIgnoreCase);
                    break;
            }
        }

        return new ProbeOptions(Path.GetFullPath(output), label, Math.Clamp(kib, 64, 16 * 1024), scenarios, progressive);
    }
}
