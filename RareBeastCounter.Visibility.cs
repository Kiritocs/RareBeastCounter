using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    private bool ShouldRenderCounterAndMessageOverlays()
    {
        if (!TryGetIngameUi(out var ingameUi))
        {
            return false;
        }

        if (Settings.Visibility.HideInHideout.Value && GameController.Area?.CurrentArea?.IsHideout == true)
        {
            return false;
        }

        if (Settings.Visibility.HideOnFullscreenPanels.Value && HasVisibleFullscreenPanels(ingameUi))
        {
            return false;
        }

        if (IsConfiguredCounterSidePanelOpen(ingameUi))
        {
            return false;
        }

        return true;
    }

    private bool ShouldRenderAnalyticsOverlay()
    {
        if (!TryGetIngameUi(out var ingameUi))
        {
            return false;
        }

        if (Settings.Visibility.HideOnFullscreenPanels.Value && HasVisibleFullscreenPanels(ingameUi))
        {
            return false;
        }

        if (IsConfiguredAnalyticsSidePanelOpen(ingameUi))
        {
            return false;
        }

        return true;
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
        try
        {
            return GameController?.IngameState?.IngameUi
                ?.GetChildAtIndex(50)
                ?.GetChildAtIndex(2)
                ?.GetChildAtIndex(0)
                ?.GetChildAtIndex(1)
                ?.GetChildAtIndex(1)
                ?.GetChildAtIndex(15)
                ?.GetChildAtIndex(0)
                ?.IsVisible == true;
        }
        catch
        {
            return false;
        }
    }
}
