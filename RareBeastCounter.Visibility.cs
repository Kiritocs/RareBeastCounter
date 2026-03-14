using System.Linq;
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
        return ingameUi.FullscreenPanels.Any(x => x.IsVisible);
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
}
