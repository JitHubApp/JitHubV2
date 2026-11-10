namespace JitHub.WinUI.Automation.Logging;

internal static class AutomationLifecycleLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Configure(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        lock (Gate)
        {
            _path = fullPath;
            File.WriteAllText(fullPath, string.Empty);
        }
    }

    public static void Write(string eventName, string detail)
    {
        lock (Gate)
        {
            if (_path is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(
                    _path,
                    $"{DateTimeOffset.Now:O}\t{eventName}\t{detail}{Environment.NewLine}");
            }
            catch
            {
            }
        }
    }
}
