using System.Text;

namespace JitHub.WinUI.Automation.Screenshots;

/// <summary>
/// Names and removes screenshot files.
/// </summary>
internal static class CaptureFiles
{
    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }
}
