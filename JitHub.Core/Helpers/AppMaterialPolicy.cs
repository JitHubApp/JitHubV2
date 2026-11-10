namespace JitHub.WinUI.Helpers;

public readonly record struct AppMaterialPolicyState(
    bool UseSystemBackdrop,
    bool UseTransientAcrylic,
    bool UseTransparentWindowSurface);

public static class AppMaterialPolicy
{
    public static AppMaterialPolicyState Evaluate(
        bool animationsEnabled,
        bool advancedEffectsEnabled,
        bool highContrastEnabled,
        bool systemBackdropSupported)
    {
        bool effectsAllowed = animationsEnabled && advancedEffectsEnabled && !highContrastEnabled;
        bool useSystemBackdrop = effectsAllowed && systemBackdropSupported;
        return new(
            useSystemBackdrop,
            effectsAllowed,
            useSystemBackdrop);
    }
}
