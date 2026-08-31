using System;

namespace JitHub.WinUI.Helpers;

internal enum UserFacingErrorKind
{
    Action,
    Activation,
    Loading,
    Refresh,
    SignIn
}

internal static class UserFacingError
{
    public static string For(Exception exception, UserFacingErrorKind kind, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        HandledFailureReporter.Report(exception, NormalizeContext(context));
        return GetLocalizedMessage(kind);
    }

    public static string ForInternalMessage(
        string? internalMessage,
        UserFacingErrorKind kind,
        string? context = null)
    {
        if (!string.IsNullOrWhiteSpace(internalMessage))
        {
            HandledFailureReporter.Report(internalMessage, NormalizeContext(context));
        }

        return GetLocalizedMessage(kind);
    }

    private static string GetLocalizedMessage(UserFacingErrorKind kind) => kind switch
    {
        UserFacingErrorKind.Activation => LocalizedResourceText.GetString(
            "Errors.Activation",
            "JitHub could not open this request. Try again."),
        UserFacingErrorKind.Loading => LocalizedResourceText.GetString(
            "Errors.Loading",
            "JitHub could not load this content. Try again."),
        UserFacingErrorKind.Refresh => LocalizedResourceText.GetString(
            "Errors.Refresh",
            "JitHub could not refresh this content. Existing data is still available."),
        UserFacingErrorKind.SignIn => LocalizedResourceText.GetString(
            "Errors.SignIn",
            "JitHub could not sign you in. Try again."),
        _ => LocalizedResourceText.GetString(
            "Errors.Action",
            "JitHub could not complete this action. Try again.")
    };

    private static string NormalizeContext(string? context) =>
        string.IsNullOrWhiteSpace(context) ? "ui" : context.Trim();
}
