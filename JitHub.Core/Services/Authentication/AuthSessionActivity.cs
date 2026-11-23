namespace JitHub.Core.Services.Authentication;

/// <summary>Work the auth service is doing that needs no input from the user.</summary>
public enum AuthSessionActivity
{
    Idle,

    /// <summary>A saved session is being validated or its token refreshed.</summary>
    RestoringSession,

    /// <summary>GitHub approved the device and the account is being loaded.</summary>
    CompletingSignIn
}
