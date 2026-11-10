using System.Text;

namespace JitHub.Services;

public static class UserIdentityAutomationId
{
    public static string Create(string? navigationSource, string? instanceId, string? login)
    {
        bool isAvailable = UserIdentityNavigationPolicy.CanNavigate(login);
        StringBuilder value = new(isAvailable ? "UserProfile_" : "UserProfile_Unavailable_");
        AppendSegment(value, navigationSource);
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            value.Append('_');
            AppendSegment(value, instanceId);
        }

        if (isAvailable)
        {
            value.Append('_');
            AppendSegment(value, login);
        }

        return value.ToString();
    }

    private static void AppendSegment(StringBuilder value, string? segment)
    {
        string normalized = string.IsNullOrWhiteSpace(segment) ? "avatar" : segment.Trim();
        foreach (char character in normalized)
        {
            value.Append(char.IsLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_');
        }
    }
}
