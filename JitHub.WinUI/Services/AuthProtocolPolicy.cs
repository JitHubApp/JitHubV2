using System;

namespace JitHub.Services;

internal static class AuthProtocolPolicy
{
    internal const string ProductionScheme = "jithub";
    internal const string DevelopmentScheme = "jithub-dev";

    internal static bool IsExpectedScheme(Uri uri)
    {
#if DEBUG
        return IsExpectedScheme(uri, useDevelopmentScheme: true);
#else
        return IsExpectedScheme(uri, useDevelopmentScheme: false);
#endif
    }

    internal static bool IsExpectedScheme(Uri uri, bool useDevelopmentScheme)
    {
        ArgumentNullException.ThrowIfNull(uri);
        string expected = useDevelopmentScheme ? DevelopmentScheme : ProductionScheme;
        return string.Equals(uri.Scheme, expected, StringComparison.OrdinalIgnoreCase);
    }
}
