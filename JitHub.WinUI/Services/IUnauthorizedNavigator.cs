namespace JitHub.Services;

/// <summary>
/// Shows the signed-out experience after the session ends, for example when
/// the user signs out. The app's navigation service implements it with its
/// root frame.
/// </summary>
public interface IUnauthorizedNavigator
{
    void Unauthorized();
}
