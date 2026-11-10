namespace JitHub.WinUI.Helpers;

public static class RepositoryLanguageColorPalette
{
    public const string DefaultHex = "#74BEA7";

    public static string GetHex(string? language) => (language ?? string.Empty).Trim() switch
    {
        "C#" => "#178600",
        "C++" => "#F34B7D",
        "C" => "#555555",
        "CSS" => "#563D7C",
        "Dart" => "#00B4AB",
        "Go" => "#00ADD8",
        "HTML" => "#E34C26",
        "Java" => "#B07219",
        "JavaScript" => "#F1E05A",
        "Kotlin" => "#A97BFF",
        "PHP" => "#4F5D95",
        "PowerShell" => "#012456",
        "Python" => "#3572A5",
        "Ruby" => "#701516",
        "Rust" => "#DEA584",
        "Shell" => "#89E051",
        "Swift" => "#F05138",
        "TypeScript" => "#3178C6",
        _ => DefaultHex
    };
}
