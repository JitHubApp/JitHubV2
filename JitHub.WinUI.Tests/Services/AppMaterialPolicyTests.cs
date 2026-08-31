using JitHub.WinUI.Helpers;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class AppMaterialPolicyTests
{
    [Fact]
    public void FullEffects_UseMicaAndTransientAcrylic()
    {
        AppMaterialPolicyState state = AppMaterialPolicy.Evaluate(
            animationsEnabled: true,
            advancedEffectsEnabled: true,
            highContrastEnabled: false,
            systemBackdropSupported: true);

        Assert.True(state.UseSystemBackdrop);
        Assert.True(state.UseTransientAcrylic);
        Assert.True(state.UseTransparentWindowSurface);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void AccessibilityOrEffectsOptOut_UsesOpaqueFallbacks(
        bool animationsEnabled,
        bool advancedEffectsEnabled,
        bool highContrastEnabled)
    {
        AppMaterialPolicyState state = AppMaterialPolicy.Evaluate(
            animationsEnabled,
            advancedEffectsEnabled,
            highContrastEnabled,
            systemBackdropSupported: true);

        Assert.False(state.UseSystemBackdrop);
        Assert.False(state.UseTransientAcrylic);
        Assert.False(state.UseTransparentWindowSurface);
    }

    [Fact]
    public void UnsupportedBackdrop_KeepsAcrylicButUsesAnOpaqueWindowSurface()
    {
        AppMaterialPolicyState state = AppMaterialPolicy.Evaluate(
            animationsEnabled: true,
            advancedEffectsEnabled: true,
            highContrastEnabled: false,
            systemBackdropSupported: false);

        Assert.False(state.UseSystemBackdrop);
        Assert.True(state.UseTransientAcrylic);
        Assert.False(state.UseTransparentWindowSurface);
    }
}
