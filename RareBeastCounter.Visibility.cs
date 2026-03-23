using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    private void GetOverlayVisibility(out bool shouldRenderCounterAndMessage, out bool shouldRenderAnalytics)
    {
        shouldRenderCounterAndMessage = false;
        shouldRenderAnalytics = false;

        if (!TryGetIngameUi(out var ingameUi))
        {
            return;
        }

        var visibility = Settings.Visibility;
        var fullscreenHidden = visibility.HideOnFullscreenPanels.Value && HasVisibleFullscreenPanels(ingameUi);
        if (fullscreenHidden)
        {
            return;
        }

        shouldRenderAnalytics = !IsConfiguredSidePanelOpen(
            ingameUi,
            visibility.HideAnalyticsOnOpenLeftPanel.Value,
            visibility.HideAnalyticsOnOpenRightPanel.Value);

        if (visibility.HideInHideout.Value && GameController.Area?.CurrentArea?.IsHideout == true)
        {
            return;
        }

        shouldRenderCounterAndMessage = !IsConfiguredSidePanelOpen(
            ingameUi,
            visibility.HideOnOpenLeftPanel.Value,
            visibility.HideOnOpenRightPanel.Value);
    }

    private bool ShouldRenderCounterAndMessageOverlays()
    {
        GetOverlayVisibility(out var shouldRenderCounterAndMessage, out _);
        return shouldRenderCounterAndMessage;
    }

    private bool ShouldRenderAnalyticsOverlay()
    {
        GetOverlayVisibility(out _, out var shouldRenderAnalytics);
        return shouldRenderAnalytics;
    }

    private bool TryGetIngameUi(out IngameUIElements ingameUi)
    {
        ingameUi = GameController?.IngameState?.IngameUi;
        return ingameUi != null;
    }

    private static bool HasVisibleFullscreenPanels(IngameUIElements ingameUi)
    {
        foreach (var panel in ingameUi.FullscreenPanels)
        {
            if (panel.IsVisible)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsConfiguredCounterSidePanelOpen(IngameUIElements ingameUi)
    {
        return IsConfiguredSidePanelOpen(
            ingameUi,
            Settings.Visibility.HideOnOpenLeftPanel.Value,
            Settings.Visibility.HideOnOpenRightPanel.Value);
    }

    private bool IsConfiguredAnalyticsSidePanelOpen(IngameUIElements ingameUi)
    {
        return IsConfiguredSidePanelOpen(
            ingameUi,
            Settings.Visibility.HideAnalyticsOnOpenLeftPanel.Value,
            Settings.Visibility.HideAnalyticsOnOpenRightPanel.Value);
    }

    private static bool IsConfiguredSidePanelOpen(IngameUIElements ingameUi, bool checkLeft, bool checkRight)
    {
        return checkLeft && ingameUi.OpenLeftPanel?.IsVisible == true ||
               checkRight && ingameUi.OpenRightPanel?.IsVisible == true;
    }

    private static bool IsConfiguredSidePanelOpen(bool hideSetting, Element panel)
    {
        return hideSetting && panel?.IsVisible == true;
    }

    private bool IsBestiaryTabVisible()
    {
        return IsBestiaryCapturedBeastsTabVisible();
    }
}
