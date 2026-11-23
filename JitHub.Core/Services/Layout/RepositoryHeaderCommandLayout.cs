using System;

namespace JitHub.Core.Services.Layout;

/// <summary>
/// Decides whether the repository header has room for its inline watch, star
/// and fork buttons, or whether they belong in the overflow menu.
/// </summary>
public static class RepositoryHeaderCommandLayout
{
    /// <summary>
    /// Width reserved for the buttons before they have been loaded and measured.
    /// </summary>
    public const double EstimatedStatButtonsWidth = 264;

    // Buttons that were moved to the overflow menu need a little spare room
    // before they return, so a width near the limit cannot make them flicker.
    private const double RevealHysteresis = 12;

    public static bool CanShowStatButtons(
        double availableWidth,
        double occupiedWidth,
        double statButtonsWidth,
        bool areStatButtonsShown)
    {
        if (!double.IsFinite(availableWidth) ||
            !double.IsFinite(occupiedWidth) ||
            !double.IsFinite(statButtonsWidth))
        {
            return false;
        }

        double requiredWidth = Math.Max(0, occupiedWidth) + Math.Max(0, statButtonsWidth);
        return availableWidth >= (areStatButtonsShown ? requiredWidth : requiredWidth + RevealHysteresis);
    }
}
