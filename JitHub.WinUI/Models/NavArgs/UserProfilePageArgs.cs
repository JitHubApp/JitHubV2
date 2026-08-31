namespace JitHub.Models.NavArgs;

public sealed record UserProfilePageArgs(string? Login = null, long? UserId = null, string? Source = null);
