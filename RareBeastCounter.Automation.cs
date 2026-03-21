using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    private const string SettingsFileName = "RareBeastCounter_settings.json";
    private static readonly int[] FragmentStashScarabTabPath = [2, 0, 0, 1, 1, 1, 0, 5, 0, 1];
    private static readonly int[] MapStashTierOneToNineTabPath = [99];
    private static readonly int[] MapStashTierTenToSixteenTabPath = [99];
    private static readonly int[] MapStashPageTabPath = [99];
    private static readonly int[] MapStashPageNumberPath = [0, 1];
    private static readonly int[] MapStashPageContentPath = [99];
    private string _lastAutomationStatusMessage;
    private bool _isAutomationRunning;
    private bool _isAutomationStopRequested;
    private int _lastAutomationFragmentScarabTabIndex = -1;
    private int _lastAutomationMapStashTierSelection = -1;
    private int _lastAutomationMapStashPageNumber = -1;
    private int _lastAutomationMapStashUiCacheKey = -1;
    private Element _lastAutomationMapStashTierGroupRoot;
    private Element _lastAutomationMapStashPageTabContainer;
    private Dictionary<int, Element> _lastAutomationMapStashPageTabsByNumber;
    private Element _lastAutomationMapStashPageContentRoot;

    private void DrawTargetTabSelectorPanel(string label, string idSuffix, StashAutomationTargetSettings target)
    {
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        if (stash?.IsVisible != true)
        {
            ImGui.TextDisabled("Open stash to choose a stash tab.");
            return;
        }

        var stashTabNames = GetAvailableStashTabNames(stash);
        if (stashTabNames.Count <= 0)
        {
            ImGui.TextDisabled("No stash tabs available.");
            return;
        }

        DrawTargetTabSelector(label, idSuffix, target, stashTabNames);
    }

    private void InitializeAutomationSettingsUi(StashAutomationSettings automation)
    {
        foreach (var (label, idSuffix, target) in GetAutomationTargets(automation))
        {
            target.TabSelector.DrawDelegate = () => DrawTargetTabSelectorPanel(label, idSuffix, target);
        }
    }

    private async Task RunStashAutomationAsync()
    {
        if (_isAutomationRunning)
        {
            RequestAutomationStop();
            return;
        }

        var automation = Settings.StashAutomation;
        if (!automation.Enabled.Value)
        {
            UpdateAutomationStatus("Stash automation is disabled.");
            return;
        }

        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        if (stash?.IsVisible != true)
        {
            UpdateAutomationStatus("Open the stash before running restock.");
            return;
        }

        LogAutomationDebug($"Run started. {DescribeStash(stash)}");

        _isAutomationRunning = true;
        _isAutomationStopRequested = false;
        ResetAutomationState();
        try
        {
            var automationTargets = GetAutomationTargets(automation);
            LogAutomationDebug($"Configured targets: {string.Join(" | ", automationTargets.Select(x => $"{x.Label} [{DescribeTarget(x.Target)}]"))}");
            UpdateAutomationStatus("Restocking inventory...");

            var totalTransferred = 0;
            foreach (var (label, _, target) in automationTargets)
            {
                totalTransferred += await RestockConfiguredTargetAsync(label, target);
            }

            UpdateAutomationStatus($"Restock complete. Transferred {totalTransferred} total items.");
        }
        catch (OperationCanceledException)
        {
            UpdateAutomationStatus("Restock cancelled.");
        }
        catch (Exception ex)
        {
            UpdateAutomationStatus($"Restock failed: {ex.Message}");
        }
        finally
        {
            _isAutomationRunning = false;
            _isAutomationStopRequested = false;
            ResetAutomationState();
            Input.KeyUp(Keys.ControlKey);
            Input.KeyUp(Keys.LControlKey);
        }
    }

    private async Task RunStashAutomationFromHotkeyAsync()
    {
        if (_isAutomationRunning)
        {
            RequestAutomationStop();
            return;
        }

        var automation = Settings.StashAutomation;
        if (!automation.Enabled.Value)
        {
            UpdateAutomationStatus("Stash automation is disabled.");
            return;
        }

        if (!await EnsureStashOpenForAutomationAsync())
        {
            return;
        }

        await RunStashAutomationAsync();
    }

    private async Task EnsureSpecialStashSubTabSelectedAsync(StashAutomationTargetSettings target)
    {
        await EnsureMapStashTierTabSelectedAsync(target);
        await EnsureFragmentStashScarabTabSelectedAsync();
    }

    private async Task<int> RestockConfiguredTargetAsync(string label, StashAutomationTargetSettings target)
    {
        var automation = Settings.StashAutomation;
        var requestedQuantity = target.Quantity.Value;
        LogAutomationDebug($"Restock target '{label}' starting. {DescribeTarget(target)}");
        if (!target.Enabled.Value || requestedQuantity <= 0)
        {
            LogAutomationDebug($"Restock target '{label}' skipped. enabled={target.Enabled.Value}, requestedQuantity={requestedQuantity}");
            return 0;
        }

        UpdateAutomationStatus($"Loading {label}...");

        var tabIndex = ResolveConfiguredTabIndex(target);
        LogAutomationDebug($"Target '{label}' resolved stash tab index {tabIndex} for tab '{target.SelectedTabName.Value}'.");
        await PrepareConfiguredTargetAsync(automation, target, tabIndex);

        var useMapStashPageItems = IsMapStashTarget(target);
        var visibleItems = GetVisibleStashItems();
        if (visibleItems == null)
        {
            throw new InvalidOperationException($"No visible stash items found for {label}.");
        }

        var configuredItemName = target.ItemName.Value?.Trim();
        var (sourceMetadata, availableInStash) = ResolveSourceMetadataAndAvailability(
            label,
            configuredItemName,
            visibleItems,
            useMapStashPageItems);

        LogAutomationDebug($"Target '{label}' source resolved. useMapStashPageItems={useMapStashPageItems}, configuredName='{configuredItemName}', metadata='{sourceMetadata}', available={availableInStash}, visibleItems={visibleItems.Count}");

        if (string.IsNullOrWhiteSpace(sourceMetadata))
        {
            throw new InvalidOperationException($"Source item metadata is unavailable for {label}.");
        }

        if (availableInStash <= 0)
        {
            throw new InvalidOperationException($"No {label} were found in the visible stash tab.");
        }

        UpdateAutomationStatus($"Loading {label}: 0/{requestedQuantity} (available {availableInStash})");

        var transferred = 0;
        var transferGoal = useMapStashPageItems ? requestedQuantity : Math.Min(requestedQuantity, availableInStash);

        for (var retryAttempt = 0; retryAttempt < 3 && transferred < transferGoal; retryAttempt++)
        {
            LogAutomationDebug($"Target '{label}' transfer attempt {retryAttempt + 1}/3. transferred={transferred}, goal={transferGoal}");
            var movedThisAttempt = false;
            while (transferred < transferGoal)
            {
                var movedAmount = await TryTransferNextMatchingItemAsync(target, sourceMetadata, useMapStashPageItems);
                if (movedAmount <= 0)
                {
                    LogAutomationDebug($"Target '{label}' found no transferable item on attempt {retryAttempt + 1}. transferred={transferred}, goal={transferGoal}");
                    break;
                }

                movedThisAttempt = true;
                transferred += movedAmount;
                LogAutomationDebug($"Target '{label}' transferred {movedAmount}. totalTransferred={transferred}, requested={requestedQuantity}");
                UpdateAutomationStatus($"Loading {label}: {Math.Min(transferred, requestedQuantity)}/{requestedQuantity} (available {availableInStash})");
                await DelayAutomationAsync(automation.ClickDelayMs.Value);
            }

            var remainingAvailable = useMapStashPageItems
                ? GetVisibleMapStashPageMatchingQuantity(sourceMetadata)
                : GetVisibleMatchingItemQuantity(sourceMetadata);
            transferred = Math.Max(transferred, Math.Max(0, availableInStash - remainingAvailable));
            if (transferred >= transferGoal || remainingAvailable <= 0)
            {
                break;
            }

            if (!movedThisAttempt)
            {
                LogAutomationDebug($"Target '{label}' retrying special stash sub-tab selection. remainingAvailable={remainingAvailable}");
                await EnsureSpecialStashSubTabSelectedAsync(target);
                await DelayAutomationAsync(automation.TabSwitchDelayMs.Value);
            }
        }

        var finalRemainingAvailable = useMapStashPageItems
            ? GetVisibleMapStashPageMatchingQuantity(sourceMetadata)
            : GetVisibleMatchingItemQuantity(sourceMetadata);

        if (transferred > 0 && finalRemainingAvailable > 0 && transferred < transferGoal)
        {
            throw new InvalidOperationException(
                $"{label} transfer stalled after {transferred}/{requestedQuantity}. {finalRemainingAvailable} still remain in stash.");
        }

        if (transferred <= 0)
        {
            throw new InvalidOperationException($"No {label} were transferred.");
        }

        return transferred;
    }

    private void ResetAutomationState()
    {
        _lastAutomationFragmentScarabTabIndex = -1;
        _lastAutomationMapStashTierSelection = -1;
        _lastAutomationMapStashPageNumber = -1;
    }

    private void RequestAutomationStop()
    {
        if (!_isAutomationRunning || _isAutomationStopRequested)
        {
            return;
        }

        _isAutomationStopRequested = true;
        UpdateAutomationStatus("Stopping restock...");
    }

    private void ThrowIfAutomationStopRequested()
    {
        if (_isAutomationStopRequested)
        {
            throw new OperationCanceledException("Automation stop requested.");
        }
    }

    private bool IsMapStashTarget(StashAutomationTargetSettings target)
    {
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        return stash?.VisibleStash?.InvType == InventoryType.MapStash && TryGetConfiguredMapTier(target).HasValue;
    }

    private async Task PrepareConfiguredTargetAsync(StashAutomationSettings automation, StashAutomationTargetSettings target, int tabIndex)
    {
        LogAutomationDebug($"Preparing target. {DescribeTarget(target)}, requestedTabIndex={tabIndex}");
        await SelectStashTabAsync(tabIndex);
        LogAutomationDebug($"After SelectStashTabAsync: {DescribeStash(GameController?.IngameState?.IngameUi?.StashElement)}");
        await EnsureSpecialStashSubTabSelectedAsync(target);
        LogAutomationDebug($"After EnsureSpecialStashSubTabSelectedAsync: {DescribeStash(GameController?.IngameState?.IngameUi?.StashElement)}");
        await DelayAutomationAsync(automation.TabSwitchDelayMs.Value);
        await EnsureMapStashPageSelectedAsync(target, 1);
        await EnsureMapStashPageWithItemSelectedAsync(target);
    }

    private (string SourceMetadata, int AvailableInStash) ResolveSourceMetadataAndAvailability(
        string label,
        string configuredItemName,
        IList<NormalInventoryItem> visibleItems,
        bool useMapStashPageItems)
    {
        if (useMapStashPageItems)
        {
            var visiblePageItems = GetVisibleMapStashPageItems();
            var sourcePageItem = FindMapStashPageItemByName(visiblePageItems, configuredItemName);
            if (sourcePageItem?.Entity == null)
            {
                throw new InvalidOperationException($"No source item found for {label}.");
            }

            return (sourcePageItem.Entity.Metadata, CountMatchingMapStashPageItems(visiblePageItems, sourcePageItem.Entity.Metadata));
        }

        var sourceItem = FindStashItemByName(visibleItems, configuredItemName);
        if (sourceItem?.Item == null)
        {
            throw new InvalidOperationException($"No source item found for {label}.");
        }

        return (sourceItem.Item.Metadata, CountMatchingItemQuantity(visibleItems, sourceItem.Item.Metadata));
    }

    private async Task<int> TryTransferNextMatchingItemAsync(StashAutomationTargetSettings target, string sourceMetadata, bool useMapStashPageItems)
    {
        ThrowIfAutomationStopRequested();

        if (useMapStashPageItems)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ThrowIfAutomationStopRequested();

                var visiblePageItems = GetVisibleMapStashPageItems();
                var nextPageItem = FindNextMatchingMapStashPageItem(visiblePageItems, sourceMetadata);
                if (nextPageItem?.Entity == null)
                {
                    nextPageItem = await WaitForNextMatchingMapStashPageItemAsync(sourceMetadata);
                }

                if (nextPageItem?.Entity == null)
                {
                    if (await EnsureMapStashPageWithItemSelectedAsync(target, sourceMetadata))
                    {
                        nextPageItem = await WaitForNextMatchingMapStashPageItemAsync(sourceMetadata);
                    }
                }

                if (nextPageItem?.Entity == null)
                {
                    return 0;
                }

                var availableBeforeTransfer = GetVisibleMapStashPageMatchingQuantity(sourceMetadata);
                await CtrlClickElementAsync(nextPageItem);
                var availableAfterTransfer = await WaitForMapStashPageQuantityToChangeAsync(sourceMetadata, availableBeforeTransfer);
                var movedAmount = Math.Max(0, availableBeforeTransfer - availableAfterTransfer);
                if (movedAmount > 0)
                {
                    return movedAmount;
                }

                await DelayAutomationAsync(Settings.StashAutomation.Timing.FastPollDelayMs.Value);
            }

            return 0;
        }

        var visibleItems = GetVisibleStashItems();
        var nextItem = FindNextMatchingStashItem(visibleItems, sourceMetadata);
        if (nextItem?.Item == null)
        {
            return 0;
        }

        var availableBeforeItemTransfer = GetVisibleMatchingItemQuantity(sourceMetadata);
        await CtrlClickInventoryItemAsync(nextItem);
        var availableAfterItemTransfer = await WaitForMatchingItemQuantityToChangeAsync(sourceMetadata, availableBeforeItemTransfer);
        return Math.Max(0, availableBeforeItemTransfer - availableAfterItemTransfer);
    }

    private async Task<bool> EnsureStashOpenForAutomationAsync()
    {
        ThrowIfAutomationStopRequested();

        var timing = Settings.StashAutomation.Timing;
        if (GameController?.IngameState?.IngameUi?.StashElement?.IsVisible == true)
        {
            return true;
        }

        while (GameController?.IngameState?.IngameUi?.StashElement?.IsVisible != true)
        {
            ThrowIfAutomationStopRequested();
            var stashEntity = FindNearestVisibleStashEntity();
            if (stashEntity == null)
            {
                UpdateAutomationStatus("No nearby visible stash found.");
                return false;
            }

            var distance = GetPlayerDistanceToEntity(stashEntity);
            var statusMessage = distance.HasValue && distance.Value <= timing.StashInteractionDistance.Value
                ? "Opening stash..."
                : "Moving to stash...";

            if (!await ClickStashEntityAsync(stashEntity, statusMessage))
            {
                return false;
            }

            if (GameController?.IngameState?.IngameUi?.StashElement?.IsVisible == true)
            {
                return true;
            }

            await DelayAutomationAsync(timing.StashOpenPollDelayMs.Value);
        }

        return true;
    }

    private async Task<bool> ClickStashEntityAsync(Entity stashEntity, string statusMessage)
    {
        var render = stashEntity?.GetComponent<Render>();
        if (render == null)
        {
            UpdateAutomationStatus("Could not find a clickable stash position.");
            return false;
        }

        UpdateAutomationStatus(statusMessage);
        var timing = Settings.StashAutomation.Timing;
        var screenPosition = GameController.IngameState.Camera.WorldToScreen(render.PosNum);
        await ClickAtAsync(
            new SharpDX.Vector2(screenPosition.X, screenPosition.Y),
            holdCtrl: false,
            preClickDelayMs: timing.UiClickPreDelayMs.Value,
            postClickDelayMs: timing.OpenStashPostClickDelayMs.Value);
        return true;
    }

    private float? GetPlayerDistanceToEntity(Entity entity)
    {
        var playerPositioned = GameController?.Game?.IngameState?.Data?.LocalPlayer?.GetComponent<Positioned>();
        var entityPositioned = entity?.GetComponent<Positioned>();
        if (playerPositioned == null || entityPositioned == null)
        {
            return null;
        }

        return Vector2.Distance(playerPositioned.GridPosNum, entityPositioned.GridPosNum);
    }

    private Entity FindNearestVisibleStashEntity()
    {
        var entities = GameController?.EntityListWrapper?.Entities;
        var playerPositioned = GameController?.Game?.IngameState?.Data?.LocalPlayer?.GetComponent<Positioned>();
        var camera = GameController?.Game?.IngameState?.Camera;
        var window = GameController?.Window;
        if (entities == null || playerPositioned == null || camera == null || window == null)
        {
            return null;
        }

        var windowRect = window.GetWindowRectangle();
        var playerGridPos = playerPositioned.GridPosNum;

        return entities
            .Where(entity => entity?.IsValid == true && entity.Type == EntityType.Stash)
            .Select(entity => new
            {
                Entity = entity,
                Positioned = entity.GetComponent<Positioned>(),
                Render = entity.GetComponent<Render>()
            })
            .Where(x => x.Positioned != null && x.Render != null)
            .Where(x => IsScreenPositionVisible(camera.WorldToScreen(x.Render.PosNum), windowRect.Width, windowRect.Height))
            .OrderBy(x => Vector2.DistanceSquared(playerGridPos, x.Positioned.GridPosNum))
            .Select(x => x.Entity)
            .FirstOrDefault();
    }

    private static bool IsScreenPositionVisible(Vector2 position, float width, float height)
    {
        return !float.IsNaN(position.X) && !float.IsNaN(position.Y) &&
               !float.IsInfinity(position.X) && !float.IsInfinity(position.Y) &&
               position.X >= 0 && position.Y >= 0 && position.X <= width && position.Y <= height;
    }

    private static (string Label, string IdSuffix, StashAutomationTargetSettings Target)[] GetAutomationTargets(StashAutomationSettings automation)
    {
        return
        [
            (GetAutomationTargetLabel(automation.Target1, "Target 1"), "target1", automation.Target1),
            (GetAutomationTargetLabel(automation.Target2, "Target 2"), "target2", automation.Target2),
            (GetAutomationTargetLabel(automation.Target3, "Target 3"), "target3", automation.Target3),
            (GetAutomationTargetLabel(automation.Target4, "Target 4"), "target4", automation.Target4),
            (GetAutomationTargetLabel(automation.Target5, "Target 5"), "target5", automation.Target5),
            (GetAutomationTargetLabel(automation.Target6, "Target 6"), "target6", automation.Target6)
        ];
    }

    private static string GetAutomationTargetLabel(StashAutomationTargetSettings target, string fallbackLabel)
    {
        var configuredItemName = target?.ItemName.Value?.Trim();
        return string.IsNullOrWhiteSpace(configuredItemName) ? fallbackLabel : configuredItemName;
    }

    private async Task EnsureMapStashTierTabSelectedAsync(StashAutomationTargetSettings target)
    {
        var automation = Settings.StashAutomation;
        var timing = automation.Timing;
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        var configuredMapTier = TryGetConfiguredMapTier(target);
        if (stash?.IsVisible != true || stash.VisibleStash?.InvType != InventoryType.MapStash || !configuredMapTier.HasValue)
        {
            LogAutomationDebug($"EnsureMapStashTierTabSelectedAsync skipped. {DescribeStash(stash)}, configuredTier={(configuredMapTier.HasValue ? configuredMapTier.Value : -1)}, item='{target.ItemName.Value}'");
            _lastAutomationMapStashTierSelection = -1;
            return;
        }

        var mapTier = configuredMapTier.Value;
        var tierTab = TryResolveMapStashTierTab(mapTier);
        if (tierTab == null)
        {
            LogAutomationDebug($"Map stash tier tab not found. tier={mapTier}, openLeftPanel={DescribeElement(GameController?.IngameState?.IngameUi?.OpenLeftPanel)}");
            _lastAutomationMapStashTierSelection = -1;
            return;
        }

        var selectionKey = stash.IndexVisibleStash * 100 + mapTier;
        if (_lastAutomationMapStashTierSelection == selectionKey)
        {
            LogAutomationDebug($"Map stash tier tab {mapTier} already selected for stash index {stash.IndexVisibleStash}. selectionKey={selectionKey}");
            return;
        }

        var tierRect = tierTab.GetClientRect();
        LogAutomationDebug($"Clicking map stash tier tab. tier={mapTier}, selectionKey={selectionKey}, tab={DescribeElement(tierTab)}");
        await ClickAtAsync(
            tierRect.Center,
            holdCtrl: false,
            preClickDelayMs: timing.UiClickPreDelayMs.Value,
            postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs.Value, automation.TabSwitchDelayMs.Value));
        _lastAutomationMapStashTierSelection = selectionKey;
        LogAutomationDebug($"Map stash tier tab click complete. rememberedSelectionKey={_lastAutomationMapStashTierSelection}");
    }

    private async Task<bool> EnsureMapStashPageWithItemSelectedAsync(StashAutomationTargetSettings target, string metadata = null)
    {
        var automation = Settings.StashAutomation;
        if (!IsMapStashTarget(target))
        {
            LogAutomationDebug($"EnsureMapStashPageWithItemSelectedAsync skipped because target is not a map stash target. {DescribeTarget(target)}");
            return false;
        }

        var itemName = target.ItemName.Value?.Trim();
        if (MapStashVisiblePageContainsMatch(itemName, metadata))
        {
            LogAutomationDebug($"Visible map stash page already contains requested match. item='{itemName}', metadata='{metadata}', currentPage={_lastAutomationMapStashPageNumber}");
            return true;
        }

        var pageTabsByNumber = GetMapStashPageTabsByNumber();
        if (pageTabsByNumber == null || pageTabsByNumber.Count == 0)
        {
            LogAutomationDebug($"No map stash page tabs found while looking for item='{itemName}', metadata='{metadata}'.");
            return false;
        }

        var orderedPageNumbers = GetMapStashSearchPageNumbers(pageTabsByNumber);
        var describedPages = orderedPageNumbers.Count > 0 ? string.Join(", ", orderedPageNumbers) : "<none>";
        LogAutomationDebug($"Searching map stash pages for item='{itemName}', metadata='{metadata}'. Pages={describedPages}, tabs={DescribePageTabs(pageTabsByNumber)}");

        foreach (var pageNumber in orderedPageNumbers)
        {
            if (!await EnsureMapStashPageSelectedAsync(target, pageNumber))
            {
                LogAutomationDebug($"Failed to select map stash page {pageNumber} while searching for item='{itemName}', metadata='{metadata}'.");
                continue;
            }

            if (MapStashVisiblePageContainsMatch(itemName, metadata))
            {
                LogAutomationDebug($"Found requested map stash item on page {pageNumber}. item='{itemName}', metadata='{metadata}'");
                return true;
            }
        }

        LogAutomationDebug($"Requested map stash item was not found on searchable pages. item='{itemName}', metadata='{metadata}'");
        return false;
    }

    private async Task<bool> EnsureMapStashPageSelectedAsync(StashAutomationTargetSettings target, int pageNumber)
    {
        var automation = Settings.StashAutomation;
        if (!IsMapStashTarget(target))
        {
            LogAutomationDebug($"EnsureMapStashPageSelectedAsync skipped because target is not a map stash target. pageNumber={pageNumber}, {DescribeTarget(target)}");
            return false;
        }

        LogAutomationDebug($"Map stash page tabs path trace: {DescribePathLookup(GameController?.IngameState?.IngameUi?.OpenLeftPanel, MapStashPageTabPath)}");
        var pageTabsByNumber = GetMapStashPageTabsByNumber();
        if (pageTabsByNumber == null || pageTabsByNumber.Count == 0)
        {
            LogAutomationDebug($"EnsureMapStashPageSelectedAsync found no page tabs. requestedPage={pageNumber}");
            return false;
        }

        if (!pageTabsByNumber.TryGetValue(pageNumber, out var pageTab))
        {
            LogAutomationDebug($"Requested map stash page {pageNumber} was not found. Available pages: {string.Join(", ", pageTabsByNumber.Keys.OrderBy(x => x))}");
            return false;
        }

        var sourceIndex = GetMapStashPageSourceIndex(pageTab);
        _lastAutomationMapStashPageNumber = pageNumber;
        LogAutomationDebug($"Selecting map stash page {pageNumber}. sourceIndex={sourceIndex}, tab={DescribeElement(pageTab)}");

        await SelectMapStashPageAsync(pageTab, sourceIndex, pageNumber, automation);
        return true;
    }

    private async Task SelectMapStashPageAsync(Element pageTab, int sourceIndex, int pageNumber, StashAutomationSettings automation)
    {
        ThrowIfAutomationStopRequested();

        var timing = automation.Timing;
        var rect = pageTab.GetClientRect();
        var center = rect.Center;
        LogAutomationDebug($"Clicking map stash page {pageNumber}. sourceIndex={sourceIndex}, rect={DescribeRect(rect)}");

        await ClickAtAsync(
            center,
            holdCtrl: false,
            preClickDelayMs: timing.UiClickPreDelayMs.Value,
            postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs.Value, automation.TabSwitchDelayMs.Value));
        await DelayAutomationAsync(automation.TabSwitchDelayMs.Value);
    }

    private IList<Element> GetMapStashPageTabs()
    {
        var pageTabContainer = ResolveMapStashPageTabContainer();
        var pageTabs = pageTabContainer?.Children;
        if (pageTabs == null)
        {
            LogAutomationDebug($"GetMapStashPageTabs could not resolve page tab container. pathTrace={DescribePathLookup(GameController?.IngameState?.IngameUi?.OpenLeftPanel, MapStashPageTabPath)}");
            return null;
        }

        LogAutomationDebug($"GetMapStashPageTabs resolved {pageTabs.Count} page tab children. container={DescribeElement(pageTabContainer)}, children={DescribeChildren(pageTabContainer)}");

        return pageTabs;
    }

    private Dictionary<int, Element> GetMapStashPageTabsByNumber()
    {
        var pageTabContainer = ResolveMapStashPageTabContainer();
        if (pageTabContainer != null && ReferenceEquals(pageTabContainer, _lastAutomationMapStashPageTabContainer) && _lastAutomationMapStashPageTabsByNumber?.Count > 0)
        {
            return _lastAutomationMapStashPageTabsByNumber;
        }

        var pageTabs = pageTabContainer?.Children;
        if (pageTabs == null)
        {
            _lastAutomationMapStashPageTabsByNumber = null;
            return null;
        }

        var pageTabsByNumber = new Dictionary<int, Element>();
        for (var index = 0; index < pageTabs.Count; index++)
        {
            var pageNumber = GetMapStashPageNumber(pageTabs[index]);
            if (pageNumber.HasValue && !pageTabsByNumber.ContainsKey(pageNumber.Value))
            {
                pageTabsByNumber[pageNumber.Value] = pageTabs[index];
            }
        }

        _lastAutomationMapStashPageTabContainer = pageTabContainer;
        _lastAutomationMapStashPageTabsByNumber = pageTabsByNumber;
        return _lastAutomationMapStashPageTabsByNumber;
    }

    private IReadOnlyList<int> GetMapStashSearchPageNumbers(IReadOnlyDictionary<int, Element> pageTabsByNumber)
    {
        if (pageTabsByNumber == null || pageTabsByNumber.Count == 0)
        {
            return Array.Empty<int>();
        }

        var orderedPageNumbers = pageTabsByNumber.Keys.OrderBy(x => x).ToList();
        if (!pageTabsByNumber.ContainsKey(_lastAutomationMapStashPageNumber))
        {
            return orderedPageNumbers;
        }

        var currentIndex = orderedPageNumbers.IndexOf(_lastAutomationMapStashPageNumber);
        if (currentIndex < 0)
        {
            return orderedPageNumbers;
        }

        return orderedPageNumbers
            .Skip(currentIndex + 1)
            .ToList();
    }

    private static int GetMapStashPageSourceIndex(Element pageTab)
    {
        return pageTab?.Parent?.Children?.IndexOf(pageTab) ?? -1;
    }

    private static int? GetMapStashPageNumber(Element pageTab)
    {
        var pageNumberElement = TryGetChildFromIndicesQuietly(pageTab, MapStashPageNumberPath);
        var pageNumberText = pageNumberElement?.GetText(16)?.Trim();
        return int.TryParse(pageNumberText, out var pageNumber) && pageNumber is >= 1 and <= 6
            ? pageNumber
            : null;
    }

    private static Element TryGetChildFromIndicesQuietly(Element root, IReadOnlyList<int> path)
    {
        var current = root;
        if (current == null || path == null)
        {
            return null;
        }

        for (var i = 0; i < path.Count; i++)
        {
            var children = current.Children;
            var childIndex = path[i];
            if (children == null || childIndex < 0 || childIndex >= children.Count)
            {
                return null;
            }

            current = children[childIndex];
        }

        return current;
    }

    private static Element TryGetElementByPathQuietly(Element root, IReadOnlyList<int> path)
    {
        return TryGetChildFromIndicesQuietly(root, path);
    }

    private Element TryResolveMapStashTierTab(int mapTier)
    {
        var childIndex = mapTier <= 9 ? mapTier - 1 : mapTier - 10;
        var tierPath = mapTier <= 9 ? MapStashTierOneToNineTabPath : MapStashTierTenToSixteenTabPath;
        var openLeftPanel = GameController?.IngameState?.IngameUi?.OpenLeftPanel;
        InvalidateCachedMapStashUiStateIfNeeded();
        LogAutomationDebug($"Map stash tier path trace: {DescribePathLookup(openLeftPanel, tierPath)}");

        var tierContainer = TryGetElementByPathQuietly(openLeftPanel, tierPath);
        var tierTab = tierContainer?.Children.ElementAtOrDefault(childIndex);
        if (tierTab != null)
        {
            return tierTab;
        }

        LogAutomationDebug($"Map stash tier fixed path failed. tier={mapTier}, childIndex={childIndex}, path={DescribePath(tierPath)}, container={DescribeElement(tierContainer)}, children={DescribeChildren(tierContainer)}");

        var dynamicTierGroup = ResolveMapStashTierGroupRoot(openLeftPanel);
        var dynamicTierContainer = dynamicTierGroup?.Children.ElementAtOrDefault(mapTier <= 9 ? 0 : 1);
        var dynamicTierTabFromGroup = dynamicTierContainer?.Children.ElementAtOrDefault(childIndex);
        if (dynamicTierTabFromGroup != null)
        {
            LogAutomationDebug($"Map stash tier dynamically resolved from tier group. tier={mapTier}, group={DescribeElement(dynamicTierGroup)}, container={DescribeElement(dynamicTierContainer)}, tab={DescribeElement(dynamicTierTabFromGroup)}");
            return dynamicTierTabFromGroup;
        }

        var tierText = mapTier.ToString();
        var dynamicTierTab = EnumerateDescendants(openLeftPanel)
            .Where(element => element?.IsVisible == true)
            .FirstOrDefault(element => string.Equals(GetElementTextRecursive(element), tierText, StringComparison.OrdinalIgnoreCase));

        if (dynamicTierTab != null)
        {
            LogAutomationDebug($"Map stash tier dynamically resolved. tier={mapTier}, tab={DescribeElement(dynamicTierTab)}");
        }

        return dynamicTierTab;
    }

    private Element ResolveMapStashPageTabContainer()
    {
        var openLeftPanel = GameController?.IngameState?.IngameUi?.OpenLeftPanel;
        InvalidateCachedMapStashUiStateIfNeeded();
        var pageTabContainer = TryGetElementByPathQuietly(openLeftPanel, MapStashPageTabPath);
        if (CountValidMapStashPageTabs(pageTabContainer) >= 6)
        {
            _lastAutomationMapStashPageTabContainer = pageTabContainer;
            TryPersistMapStashElementPath(
                openLeftPanel,
                pageTabContainer,
                hints => hints.MapStashPageTabContainerPath,
                (hints, path) => hints.MapStashPageTabContainerPath = path,
                "map stash page tab container");
            return pageTabContainer;
        }

        if (CountValidMapStashPageTabs(_lastAutomationMapStashPageTabContainer) >= 6)
        {
            return _lastAutomationMapStashPageTabContainer;
        }

        var persistedContainer = TryResolvePersistedMapStashElementPath(
            openLeftPanel,
            GetAutomationDynamicHints()?.MapStashPageTabContainerPath,
            element => CountValidMapStashPageTabs(element) >= 6,
            "map stash page tab container");
        if (persistedContainer != null)
        {
            _lastAutomationMapStashPageTabContainer = persistedContainer;
            return persistedContainer;
        }

        Element dynamicContainer = null;
        var bestPageCount = 0;
        var bestArea = float.MinValue;
        foreach (var element in EnumerateDescendants(openLeftPanel))
        {
            var pageCount = CountValidMapStashPageTabs(element);
            if (pageCount < 6)
            {
                continue;
            }

            var area = GetRectangleArea(element.GetClientRect());
            if (dynamicContainer == null || pageCount > bestPageCount || pageCount == bestPageCount && area > bestArea)
            {
                dynamicContainer = element;
                bestPageCount = pageCount;
                bestArea = area;
            }
        }

        _lastAutomationMapStashPageTabContainer = dynamicContainer ?? pageTabContainer;
        TryPersistMapStashElementPath(
            openLeftPanel,
            _lastAutomationMapStashPageTabContainer,
            hints => hints.MapStashPageTabContainerPath,
            (hints, path) => hints.MapStashPageTabContainerPath = path,
            "map stash page tab container");

        return _lastAutomationMapStashPageTabContainer;
    }

    private Element ResolveMapStashPageContentRoot()
    {
        var openLeftPanel = GameController?.IngameState?.IngameUi?.OpenLeftPanel;
        InvalidateCachedMapStashUiStateIfNeeded();
        var pageContent = TryGetElementByPathQuietly(openLeftPanel, MapStashPageContentPath);
        if (TryRememberMapStashPageContentRoot(pageContent, "fixed path"))
        {
            return pageContent;
        }

        if (IsReusableMapStashPageContentRoot(_lastAutomationMapStashPageContentRoot))
        {
            LogAutomationDebug($"Reusing cached map stash page content root. content={DescribeElement(_lastAutomationMapStashPageContentRoot)}");
            return _lastAutomationMapStashPageContentRoot;
        }

        var persistedContentRoot = TryResolvePersistedMapStashElementPath(
            openLeftPanel,
            GetAutomationDynamicHints()?.MapStashPageContentRootPath,
            IsReusableMapStashPageContentRoot,
            "map stash page content root");
        if (persistedContentRoot != null)
        {
            _lastAutomationMapStashPageContentRoot = persistedContentRoot;
            return persistedContentRoot;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            openLeftPanel = GameController?.IngameState?.IngameUi?.OpenLeftPanel;
            Element dynamicContent = null;
            var bestMapDescendants = 0;
            var bestArea = float.MaxValue;
            foreach (var element in EnumerateDescendants(openLeftPanel))
            {
                if (!TryGetMapStashPageContentCandidateScore(element, out var mapDescendants, out var area))
                {
                    continue;
                }

                if (dynamicContent == null || mapDescendants > bestMapDescendants || mapDescendants == bestMapDescendants && area < bestArea)
                {
                    dynamicContent = element;
                    bestMapDescendants = mapDescendants;
                    bestArea = area;
                }
            }

            if (TryRememberMapStashPageContentRoot(dynamicContent, $"dynamic attempt {attempt + 1}"))
            {
                return dynamicContent;
            }

            if (attempt < 2)
            {
                System.Threading.Thread.Sleep(15);
            }
        }

        return IsReusableMapStashPageContentRoot(_lastAutomationMapStashPageContentRoot)
            ? _lastAutomationMapStashPageContentRoot
            : null;
    }

    private void InvalidateCachedMapStashUiStateIfNeeded()
    {
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        var currentCacheKey = stash?.IsVisible == true && stash.VisibleStash?.InvType == InventoryType.MapStash
            ? stash.IndexVisibleStash
            : -1;

        if (_lastAutomationMapStashUiCacheKey == currentCacheKey)
        {
            return;
        }

        _lastAutomationMapStashUiCacheKey = currentCacheKey;
        _lastAutomationMapStashTierGroupRoot = null;
        _lastAutomationMapStashPageTabContainer = null;
        _lastAutomationMapStashPageTabsByNumber = null;
        _lastAutomationMapStashPageContentRoot = null;
    }

    private Element ResolveMapStashTierGroupRoot(Element openLeftPanel)
    {
        if (IsMapStashTierGroupContainer(_lastAutomationMapStashTierGroupRoot))
        {
            return _lastAutomationMapStashTierGroupRoot;
        }

        var persistedTierGroup = TryResolvePersistedMapStashElementPath(
            openLeftPanel,
            GetAutomationDynamicHints()?.MapStashTierGroupPath,
            IsMapStashTierGroupContainer,
            "map stash tier group");
        if (persistedTierGroup != null)
        {
            _lastAutomationMapStashTierGroupRoot = persistedTierGroup;
            return persistedTierGroup;
        }

        Element bestTierGroup = null;
        var bestArea = float.MinValue;
        foreach (var element in EnumerateDescendants(openLeftPanel))
        {
            if (!IsMapStashTierGroupContainer(element))
            {
                continue;
            }

            var area = GetRectangleArea(element.GetClientRect());
            if (bestTierGroup == null || area > bestArea)
            {
                bestTierGroup = element;
                bestArea = area;
            }
        }

        _lastAutomationMapStashTierGroupRoot = bestTierGroup;
        TryPersistMapStashElementPath(
            openLeftPanel,
            _lastAutomationMapStashTierGroupRoot,
            hints => hints.MapStashTierGroupPath,
            (hints, path) => hints.MapStashTierGroupPath = path,
            "map stash tier group");
        return _lastAutomationMapStashTierGroupRoot;
    }

    private StashAutomationDynamicHintSettings GetAutomationDynamicHints()
    {
        return Settings?.StashAutomation?.DynamicHints;
    }

    private Element TryResolvePersistedMapStashElementPath(
        Element root,
        IReadOnlyList<int> path,
        Func<Element, bool> validator,
        string label)
    {
        if (root == null || path == null || path.Count <= 0 || validator == null)
        {
            return null;
        }

        var resolvedElement = TryGetElementByPathQuietly(root, path);
        if (!validator(resolvedElement))
        {
            return null;
        }

        LogAutomationDebug($"Resolved {label} from persisted path {DescribePath(path)}. element={DescribeElement(resolvedElement)}");
        return resolvedElement;
    }

    private void TryPersistMapStashElementPath(
        Element root,
        Element target,
        Func<StashAutomationDynamicHintSettings, List<int>> getter,
        Action<StashAutomationDynamicHintSettings, List<int>> setter,
        string label)
    {
        var hints = GetAutomationDynamicHints();
        if (root == null || target == null || hints == null || getter == null || setter == null)
        {
            return;
        }

        var resolvedPath = TryFindPathFromRoot(root, target);
        if (resolvedPath == null || resolvedPath.Count <= 0)
        {
            return;
        }

        var existingPath = getter(hints);
        if (existingPath != null && existingPath.SequenceEqual(resolvedPath))
        {
            LogAutomationDebug($"Persisted {label} path unchanged ({DescribePath(resolvedPath)}); skipping settings snapshot save.");
            return;
        }

        setter(hints, resolvedPath);
        LogAutomationDebug($"Persisted {label} path {DescribePath(resolvedPath)}");
        TrySaveSettingsSnapshot();
    }

    private void TrySaveSettingsSnapshot()
    {
        try
        {
            var settings = Settings;
            if (settings == null)
            {
                return;
            }

            var configDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "global");
            Directory.CreateDirectory(configDirectory);
            var settingsPath = Path.Combine(configDirectory, SettingsFileName);
            var settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(settingsPath, settingsJson);
            LogAutomationDebug($"Saved settings snapshot to '{settingsPath}'.");
        }
        catch (Exception ex)
        {
            LogAutomationDebug($"Failed to save settings snapshot: {ex.Message}");
        }
    }

    private static List<int> TryFindPathFromRoot(Element root, Element target)
    {
        if (root == null || target == null)
        {
            return null;
        }

        if (ReferenceEquals(root, target))
        {
            return [];
        }

        var stack = new Stack<(Element Element, List<int> Path)>();
        stack.Push((root, []));

        while (stack.Count > 0)
        {
            var (current, path) = stack.Pop();
            var children = current?.Children;
            if (children == null)
            {
                continue;
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                var child = children[i];
                if (child == null)
                {
                    continue;
                }

                var childPath = new List<int>(path.Count + 1);
                childPath.AddRange(path);
                childPath.Add(i);
                if (ReferenceEquals(child, target))
                {
                    return childPath;
                }

                stack.Push((child, childPath));
            }
        }

        return null;
    }

    private Element FindFragmentScarabTabDynamically(Element root)
    {
        return EnumerateDescendants(root)
            .Where(element => element?.IsVisible == true)
            .FirstOrDefault(element => GetElementTextRecursive(element)?.IndexOf("Scarab", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static int CountValidMapStashPageTabs(Element element)
    {
        var children = element?.Children;
        if (children == null)
        {
            return 0;
        }

        var pageNumbers = new HashSet<int>();
        foreach (var child in children)
        {
            var pageNumber = GetMapStashPageNumber(child);
            if (pageNumber.HasValue)
            {
                pageNumbers.Add(pageNumber.Value);
            }
        }

        return pageNumbers.Count;
    }

    private static bool IsMapStashTierGroupContainer(Element element)
    {
        var children = element?.Children;
        if (children == null || children.Count < 2)
        {
            return false;
        }

        return IsMapStashTierContainer(children[0]) && IsMapStashTierContainer(children[1]);
    }

    private static bool IsMapStashTierContainer(Element element)
    {
        var children = element?.Children;
        if (children == null || children.Count < 7)
        {
            return false;
        }

        var visibleChildren = children.Count(child => child?.IsVisible == true);
        return visibleChildren >= 7;
    }

    private static bool IsMapStashPageContentCandidate(Element element)
    {
        return TryGetMapStashPageContentCandidateScore(element, out _, out _);
    }

    private static bool TryGetMapStashPageContentCandidateScore(Element element, out int mapDescendants, out float area)
    {
        mapDescendants = 0;
        area = 0;
        if (element?.IsVisible != true || (element.Children?.Count ?? 0) <= 0)
        {
            return false;
        }

        mapDescendants = CountVisibleMapEntityDescendants(element);
        if (mapDescendants <= 0 || mapDescendants > 96)
        {
            return false;
        }

        if (CountValidMapStashPageTabs(element) >= 6 || IsMapStashTierGroupContainer(element))
        {
            return false;
        }

        foreach (var descendant in EnumerateDescendants(element))
        {
            if (CountValidMapStashPageTabs(descendant) >= 6 || IsMapStashTierGroupContainer(descendant))
            {
                return false;
            }
        }

        area = GetRectangleArea(element.GetClientRect());
        return true;
    }

    private static bool IsReusableMapStashPageContentRoot(Element element)
    {
        if (element?.IsVisible != true)
        {
            return false;
        }

        var childCount = element.Children?.Count ?? 0;
        if (childCount <= 0 || childCount > 32)
        {
            return false;
        }

        return CountValidMapStashPageTabs(element) < 6 && !IsMapStashTierGroupContainer(element);
    }

    private bool TryRememberMapStashPageContentRoot(Element element, string source)
    {
        if (!IsMapStashPageContentCandidate(element))
        {
            return false;
        }

        _lastAutomationMapStashPageContentRoot = element;
        TryPersistMapStashElementPath(
            GameController?.IngameState?.IngameUi?.OpenLeftPanel,
            element,
            hints => hints.MapStashPageContentRootPath,
            (hints, path) => hints.MapStashPageContentRootPath = path,
            "map stash page content root");
        LogAutomationDebug($"Map stash page content dynamically resolved via {source}. content={DescribeElement(element)}, mapDescendants={CountVisibleMapEntityDescendants(element)}, children={DescribeChildren(element)}");
        return true;
    }

    private static int CountVisibleMapEntityDescendants(Element root)
    {
        if (root == null)
        {
            return 0;
        }

        var count = 0;
        foreach (var element in EnumerateDescendants(root, includeSelf: true))
        {
            if (element?.IsVisible == true && element.Entity?.Metadata?.IndexOf("Metadata/Items/Maps", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                count++;
            }
        }

        return count;
    }

    private static string GetElementTextRecursive(Element element, int maxDepth = 3)
    {
        if (element == null)
        {
            return null;
        }

        var directText = TryGetElementText(element);
        if (!string.IsNullOrWhiteSpace(directText))
        {
            return directText;
        }

        if (maxDepth <= 0 || element.Children == null)
        {
            return null;
        }

        foreach (var child in element.Children)
        {
            var text = GetElementTextRecursive(child, maxDepth - 1);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string TryGetElementText(Element element)
    {
        try
        {
            return element?.GetText(16)?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<Element> EnumerateDescendants(Element root, bool includeSelf = false)
    {
        if (root == null)
        {
            yield break;
        }

        if (includeSelf)
        {
            yield return root;
        }

        var children = root.Children;
        if (children == null)
        {
            yield break;
        }

        var stack = new Stack<Element>();
        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (children[i] != null)
            {
                stack.Push(children[i]);
            }
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            var currentChildren = current.Children;
            if (currentChildren == null)
            {
                continue;
            }

            for (var i = currentChildren.Count - 1; i >= 0; i--)
            {
                if (currentChildren[i] != null)
                {
                    stack.Push(currentChildren[i]);
                }
            }
        }
    }

    private static float GetRectangleArea(RectangleF rect)
    {
        return Math.Max(0, rect.Width) * Math.Max(0, rect.Height);
    }

    private IList<Element> GetVisibleMapStashPageItems()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var pageContent = ResolveMapStashPageContentRoot();
            if (pageContent == null)
            {
                if (attempt < 2)
                {
                    System.Threading.Thread.Sleep(15);
                    continue;
                }

                LogAutomationDebug($"GetVisibleMapStashPageItems could not resolve page content. pathTrace={DescribePathLookup(GameController?.IngameState?.IngameUi?.OpenLeftPanel, MapStashPageContentPath)}");
                return null;
            }

            var items = new List<Element>();
            CollectVisibleEntityDescendants(pageContent, items);
            if (items.Count > 0)
            {
                LogAutomationDebug($"GetVisibleMapStashPageItems resolved content={DescribeElement(pageContent)}, itemCount={items.Count}");
                return items;
            }

            if (attempt < 2)
            {
                System.Threading.Thread.Sleep(15);
                continue;
            }

            LogAutomationDebug($"GetVisibleMapStashPageItems found no visible entity descendants in page content. content={DescribeElement(pageContent)}, children={DescribeChildren(pageContent)}");
            return null;
        }

        return null;
    }

    private static void CollectVisibleEntityDescendants(Element root, ICollection<Element> results)
    {
        if (root == null || results == null)
        {
            return;
        }

        if (root.IsVisible && root.Entity != null)
        {
            results.Add(root);
        }

        var children = root.Children;
        if (children == null)
        {
            return;
        }

        foreach (var child in children)
        {
            CollectVisibleEntityDescendants(child, results);
        }
    }

    private async Task<Element> WaitForNextMatchingMapStashPageItemAsync(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        var automation = Settings.StashAutomation;
        var timing = automation.Timing;
        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(
            timing.QuantityChangeBaseDelayMs.Value,
            automation.ClickDelayMs.Value + timing.QuantityChangeBaseDelayMs.Value + GetServerLatencyMs()));

        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();

            var visiblePageItems = GetVisibleMapStashPageItems();
            var nextPageItem = FindNextMatchingMapStashPageItem(visiblePageItems, metadata);
            if (nextPageItem?.Entity != null)
            {
                LogAutomationDebug($"WaitForNextMatchingMapStashPageItemAsync found metadata='{metadata}'. item={DescribeElement(nextPageItem)}");
                return nextPageItem;
            }

            await DelayAutomationAsync(timing.FastPollDelayMs.Value);
        }

        LogAutomationDebug($"WaitForNextMatchingMapStashPageItemAsync timed out for metadata='{metadata}'. pathTrace={DescribePathLookup(GameController?.IngameState?.IngameUi?.OpenLeftPanel, MapStashPageContentPath)}");

        return null;
    }

    private static Element FindMapStashPageItemByName(IList<Element> items, string itemName)
    {
        if (items == null || string.IsNullOrWhiteSpace(itemName))
        {
            return null;
        }

        return items.FirstOrDefault(item =>
            string.Equals(item?.Entity?.GetComponent<Base>()?.Name, itemName, StringComparison.OrdinalIgnoreCase));
    }

    private static Element FindNextMatchingMapStashPageItem(IList<Element> items, string metadata)
    {
        if (items == null || string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        return items
            .Where(item => string.Equals(item?.Entity?.Metadata, metadata, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.GetClientRect().Top)
            .ThenBy(item => item.GetClientRect().Left)
            .FirstOrDefault();
    }

    private static int CountMatchingMapStashPageItems(IList<Element> items, string metadata)
    {
        if (items == null || string.IsNullOrWhiteSpace(metadata))
        {
            return 0;
        }

        return items.Count(item => string.Equals(item?.Entity?.Metadata, metadata, StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeEntity(Entity entity)
    {
        if (entity == null)
        {
            return "entity=null";
        }

        return $"name='{entity.GetComponent<Base>()?.Name}', metadata='{entity.Metadata}'";
    }

    private bool MapStashVisiblePageContainsMatch(string itemName, string metadata)
    {
        var visiblePageItems = GetVisibleMapStashPageItems();
        if (visiblePageItems == null)
        {
            return false;
        }

        foreach (var child in visiblePageItems)
        {
            var entity = child?.Entity;
            if (entity == null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(metadata))
            {
                if (string.Equals(entity.Metadata, metadata, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(entity.GetComponent<Base>()?.Name, itemName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private int GetVisibleMapStashPageMatchingQuantity(string metadata) => CountMatchingMapStashPageItems(GetVisibleMapStashPageItems(), metadata);

    private static int? TryGetConfiguredMapTier(StashAutomationTargetSettings target)
    {
        var configuredItemName = target?.ItemName.Value?.Trim();
        if (string.IsNullOrWhiteSpace(configuredItemName))
        {
            return null;
        }

        const string tierPrefix = "(Tier ";
        var tierStartIndex = configuredItemName.IndexOf(tierPrefix, StringComparison.OrdinalIgnoreCase);
        if (tierStartIndex < 0)
        {
            return null;
        }

        tierStartIndex += tierPrefix.Length;
        var tierEndIndex = configuredItemName.IndexOf(')', tierStartIndex);
        if (tierEndIndex <= tierStartIndex)
        {
            return null;
        }

        return int.TryParse(configuredItemName.Substring(tierStartIndex, tierEndIndex - tierStartIndex), out var tier) && tier is >= 1 and <= 16
            ? tier
            : null;
    }

    private async Task DelayAutomationAsync(int baseDelayMs)
    {
        ThrowIfAutomationStopRequested();

        var adjustedDelayMs = GetAutomationDelayMs(baseDelayMs);
        var remainingDelayMs = adjustedDelayMs;
        while (remainingDelayMs > 0)
        {
            var sliceDelayMs = Math.Min(remainingDelayMs, 50);
            await Task.Delay(sliceDelayMs);
            remainingDelayMs -= sliceDelayMs;
            ThrowIfAutomationStopRequested();
        }
    }

    private int GetAutomationDelayMs(int baseDelayMs)
    {
        var normalizedBaseDelayMs = Math.Max(0, baseDelayMs);

        var automation = Settings?.StashAutomation;
        if (automation == null)
        {
            return normalizedBaseDelayMs;
        }

        return Math.Max(0, normalizedBaseDelayMs + automation.FlatExtraDelayMs.Value);
    }

    private int GetAutomationTimeoutMs(int baseDelayMs)
    {
        return Math.Max(0, GetAutomationDelayMs(baseDelayMs) + GetServerLatencyMs());
    }

    private int GetServerLatencyMs()
    {
        return Math.Max(0, GameController?.Game?.IngameState?.ServerData?.Latency ?? 0);
    }

    private void DrawTargetTabSelector(string label, string idSuffix, StashAutomationTargetSettings target, IReadOnlyList<string> stashTabNames)
    {
        var previewText = string.IsNullOrWhiteSpace(target.SelectedTabName.Value) ? "Select tab" : target.SelectedTabName.Value;
        ImGui.Text($"{label} tab");
        ImGui.SameLine();

        if (ImGui.BeginCombo($"##RareBeastCounterStashTab{idSuffix}", previewText))
        {
            for (var i = 0; i < stashTabNames.Count; i++)
            {
                var tabName = stashTabNames[i];
                var isSelected = string.Equals(target.SelectedTabName.Value, tabName, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{i}: {tabName}", isSelected))
                {
                    target.SelectedTabName.Value = tabName;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }
    }

    private static NormalInventoryItem FindStashItemByName(IList<NormalInventoryItem> items, string itemName)
    {
        if (items == null || string.IsNullOrWhiteSpace(itemName))
        {
            return null;
        }

        return items.FirstOrDefault(item =>
            item?.Item != null &&
            string.Equals(item.Item.GetComponent<Base>()?.Name, itemName, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountMatchingItemQuantity(IList<NormalInventoryItem> items, string metadata)
    {
        if (items == null || string.IsNullOrWhiteSpace(metadata))
        {
            return 0;
        }

        return items
            .Where(item => item?.Item != null &&
                           string.Equals(item.Item.Metadata, metadata, StringComparison.OrdinalIgnoreCase))
            .Sum(item => Math.Max(1, item.Item.GetComponent<Stack>()?.Size ?? 1));
    }

    private IList<NormalInventoryItem> GetVisibleStashItems() => GameController?.IngameState?.IngameUi?.StashElement?.VisibleStash?.VisibleInventoryItems;

    private int GetVisibleMatchingItemQuantity(string metadata) => CountMatchingItemQuantity(GetVisibleStashItems(), metadata);

    private static NormalInventoryItem FindNextMatchingStashItem(
        IList<NormalInventoryItem> items,
        string metadata)
    {
        if (items == null || string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        return items
            .Where(item => item?.Item != null &&
                           string.Equals(item.Item.Metadata, metadata, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.GetClientRect().Top)
            .ThenBy(item => item.GetClientRect().Left)
            .FirstOrDefault();
    }

    private async Task<int> WaitForMatchingItemQuantityToChangeAsync(string metadata, int previousQuantity)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return previousQuantity;
        }

        var automation = Settings.StashAutomation;
        var timing = automation.Timing;
        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(timing.QuantityChangeBaseDelayMs.Value, automation.ClickDelayMs.Value + timing.QuantityChangeBaseDelayMs.Value));
        int? pendingQuantity = null;
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();
            var visibleItems = GetVisibleStashItems();
            if (visibleItems == null)
            {
                pendingQuantity = null;
                await DelayAutomationAsync(timing.FastPollDelayMs.Value);
                continue;
            }

            var currentQuantity = CountMatchingItemQuantity(visibleItems, metadata);
            if (currentQuantity < previousQuantity)
            {
                return currentQuantity;
            }

            if (currentQuantity == previousQuantity)
            {
                pendingQuantity = null;
                await DelayAutomationAsync(timing.FastPollDelayMs.Value);
                continue;
            }

            if (pendingQuantity == currentQuantity)
            {
                return currentQuantity;
            }

            pendingQuantity = currentQuantity;

            await DelayAutomationAsync(timing.FastPollDelayMs.Value);
        }

        return previousQuantity;
    }

    private int ResolveConfiguredTabIndex(StashAutomationTargetSettings target)
    {
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        if (stash?.IsVisible != true)
        {
            LogAutomationDebug("ResolveConfiguredTabIndex aborted because stash is not visible.");
            return -1;
        }

        var stashTabNames = GetAvailableStashTabNames(stash);
        var configuredTabName = target.SelectedTabName.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredTabName))
        {
            for (var i = 0; i < stashTabNames.Count; i++)
            {
                if (!string.Equals(stashTabNames[i], configuredTabName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return i;
            }

            LogAutomationDebug($"Configured tab '{configuredTabName}' was not found. Available tabs: {string.Join(", ", stashTabNames.Select((name, index) => $"{index}:{name}"))}");
        }

        if (string.IsNullOrWhiteSpace(configuredTabName))
        {
            LogAutomationDebug($"No configured stash tab name for target '{target.ItemName.Value}'. Available tabs: {string.Join(", ", stashTabNames.Select((name, index) => $"{index}:{name}"))}");
        }

        return -1;
    }

    private static List<string> GetAvailableStashTabNames(StashElement stash)
    {
        var totalStashes = (int)stash.TotalStashes;
        var names = new List<string>(totalStashes);
        for (var i = 0; i < totalStashes; i++)
        {
            var name = stash.GetStashName(i);
            names.Add(string.IsNullOrWhiteSpace(name) ? $"Tab {i}" : name);
        }

        return names;
    }

    private async Task SelectStashTabAsync(int tabIndex)
    {
        var automation = Settings.StashAutomation;
        var timing = automation.Timing;
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        if (stash?.IsVisible != true)
        {
            throw new InvalidOperationException("Stash is not open.");
        }

        if (tabIndex < 0 || tabIndex >= stash.TotalStashes)
        {
            LogAutomationDebug($"SelectStashTabAsync received invalid tab index {tabIndex}. {DescribeStash(stash)}");
            throw new InvalidOperationException("Select a valid stash tab name before running restock.");
        }

        if (stash.IndexVisibleStash == tabIndex)
        {
            LogAutomationDebug($"SelectStashTabAsync skipping because stash tab {tabIndex} is already visible.");
            return;
        }

        LogAutomationDebug($"Selecting stash tab {tabIndex}. Starting state: {DescribeStash(stash)}");

        var maxSteps = Math.Max(3, (int)stash.TotalStashes * 2);
        for (var step = 0; step < maxSteps; step++)
        {
            ThrowIfAutomationStopRequested();
            stash = GameController?.IngameState?.IngameUi?.StashElement;
            if (stash?.IsVisible != true)
            {
                throw new InvalidOperationException("Stash closed while switching tabs.");
            }

            var currentIndex = stash.IndexVisibleStash;
            if (currentIndex == tabIndex)
            {
                LogAutomationDebug($"SelectStashTabAsync reached target tab {tabIndex} after {step} steps.");
                return;
            }

            var key = tabIndex < currentIndex ? Keys.Left : Keys.Right;
            LogAutomationDebug($"SelectStashTabAsync step {step + 1}/{maxSteps}. currentIndex={currentIndex}, targetIndex={tabIndex}, key={key}");
            Input.KeyDown(key);
            await DelayAutomationAsync(timing.KeyTapDelayMs.Value);
            Input.KeyUp(key);

            var changedIndex = await WaitForVisibleTabIndexChangeAsync(currentIndex, Math.Max(timing.TabChangeTimeoutMs.Value, automation.TabSwitchDelayMs.Value));
            LogAutomationDebug($"SelectStashTabAsync step {step + 1} result. previousIndex={currentIndex}, changedIndex={changedIndex}");
            if (changedIndex == currentIndex)
            {
                await DelayAutomationAsync(Math.Max(timing.TabRetryDelayMs.Value, automation.TabSwitchDelayMs.Value / 2));
            }
        }

        LogAutomationDebug($"SelectStashTabAsync exhausted step loop for targetIndex={tabIndex}. Waiting for visible tab. {DescribeStash(GameController?.IngameState?.IngameUi?.StashElement)}");
        await WaitForVisibleTabAsync(tabIndex);
    }

    private async Task EnsureFragmentStashScarabTabSelectedAsync()
    {
        var automation = Settings.StashAutomation;
        var timing = automation.Timing;
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        if (stash?.IsVisible != true || stash.VisibleStash?.InvType != InventoryType.FragmentStash)
        {
            LogAutomationDebug($"EnsureFragmentStashScarabTabSelectedAsync skipped. {DescribeStash(stash)}");
            _lastAutomationFragmentScarabTabIndex = -1;
            return;
        }

        if (stash.IndexVisibleStash == _lastAutomationFragmentScarabTabIndex)
        {
            LogAutomationDebug($"EnsureFragmentStashScarabTabSelectedAsync skipping because stash tab {stash.IndexVisibleStash} already selected scarab tab previously.");
            return;
        }

        LogAutomationDebug($"Ensuring fragment scarab tab using path {DescribePath(FragmentStashScarabTabPath)}. {DescribeStash(stash)}");
        LogAutomationDebug($"Fragment stash path trace: {DescribePathLookup(stash, FragmentStashScarabTabPath)}");
        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(timing.FragmentTabBaseTimeoutMs.Value, automation.TabSwitchDelayMs.Value + timing.FragmentTabBaseTimeoutMs.Value));
        var attempts = 0;
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            attempts++;
            ThrowIfAutomationStopRequested();
            stash = GameController?.IngameState?.IngameUi?.StashElement;
            if (stash?.IsVisible != true || stash.VisibleStash?.InvType != InventoryType.FragmentStash)
            {
                LogAutomationDebug($"EnsureFragmentStashScarabTabSelectedAsync aborted during polling. {DescribeStash(stash)}");
                _lastAutomationFragmentScarabTabIndex = -1;
                return;
            }

            var scarabTab = TryGetElementByPathQuietly(stash, FragmentStashScarabTabPath) ?? FindFragmentScarabTabDynamically(stash);
            if (scarabTab != null)
            {
                LogAutomationDebug($"Fragment scarab tab found on attempt {attempts}. {DescribeElement(scarabTab)}");
                await ClickAtAsync(
                    scarabTab.GetClientRect().Center,
                    holdCtrl: false,
                    preClickDelayMs: timing.UiClickPreDelayMs.Value,
                    postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs.Value, automation.TabSwitchDelayMs.Value));
                _lastAutomationFragmentScarabTabIndex = stash.IndexVisibleStash;
                LogAutomationDebug($"Fragment scarab tab clicked. rememberedStashIndex={_lastAutomationFragmentScarabTabIndex}");
                return;
            }

            if (attempts == 1 || attempts % 5 == 0)
            {
                LogAutomationDebug($"Fragment scarab tab not found on attempt {attempts}. path={DescribePath(FragmentStashScarabTabPath)}, stash={DescribeStash(stash)}");
                LogAutomationDebug($"Fragment scarab path trace attempt {attempts}: {DescribePathLookup(stash, FragmentStashScarabTabPath)}");
            }

            await DelayAutomationAsync(timing.FastPollDelayMs.Value);
        }

        LogAutomationDebug($"EnsureFragmentStashScarabTabSelectedAsync timed out after {attempts} attempts. path={DescribePath(FragmentStashScarabTabPath)}, stash={DescribeStash(GameController?.IngameState?.IngameUi?.StashElement)}");
    }

    private async Task WaitForVisibleTabAsync(int tabIndex)
    {
        var timing = Settings.StashAutomation.Timing;
        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(timing.VisibleTabTimeoutMs.Value);
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();
            var stash = GameController?.IngameState?.IngameUi?.StashElement;
            if (stash?.IsVisible == true && stash.IndexVisibleStash == tabIndex && stash.VisibleStash != null)
            {
                return;
            }

            await DelayAutomationAsync(timing.FastPollDelayMs.Value);
        }

        LogAutomationDebug($"WaitForVisibleTabAsync timed out. targetTab={tabIndex}, stash={DescribeStash(GameController?.IngameState?.IngameUi?.StashElement)}");
        throw new InvalidOperationException($"Timed out switching to stash tab {tabIndex}.");
    }

    private async Task<int> WaitForVisibleTabIndexChangeAsync(int previousTabIndex, int timeoutMs)
    {
        var timing = Settings.StashAutomation.Timing;
        var startedAt = DateTime.UtcNow;
        var adjustedTimeoutMs = GetAutomationTimeoutMs(timeoutMs);
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < adjustedTimeoutMs)
        {
            ThrowIfAutomationStopRequested();
            var stash = GameController?.IngameState?.IngameUi?.StashElement;
            if (stash?.IsVisible == true && stash.VisibleStash != null)
            {
                if (stash.IndexVisibleStash != previousTabIndex)
                {
                    return stash.IndexVisibleStash;
                }
            }

            await DelayAutomationAsync(timing.FastPollDelayMs.Value);
        }

        LogAutomationDebug($"WaitForVisibleTabIndexChangeAsync timed out. previousTabIndex={previousTabIndex}, timeoutMs={timeoutMs}, stash={DescribeStash(GameController?.IngameState?.IngameUi?.StashElement)}");
        return previousTabIndex;
    }

    private async Task CtrlClickInventoryItemAsync(NormalInventoryItem item)
    {
        var timing = Settings.StashAutomation.Timing;
        await ClickAtAsync(
            item.GetClientRect().Center,
            holdCtrl: true,
            preClickDelayMs: timing.CtrlClickPreDelayMs.Value,
            postClickDelayMs: timing.CtrlClickPostDelayMs.Value);
    }

    private async Task CtrlClickElementAsync(Element element)
    {
        var timing = Settings.StashAutomation.Timing;
        await ClickAtAsync(
            element.GetClientRect().Center,
            holdCtrl: true,
            preClickDelayMs: timing.CtrlClickPreDelayMs.Value,
            postClickDelayMs: timing.CtrlClickPostDelayMs.Value);
    }

    private async Task ClickAtAsync(SharpDX.Vector2 position, bool holdCtrl, int preClickDelayMs, int postClickDelayMs)
    {
        Input.SetCursorPos(position);
        Input.MouseMove();

        await DelayAutomationAsync(preClickDelayMs);

        if (holdCtrl)
        {
            Input.KeyDown(Keys.LControlKey);
        }

        Input.Click(MouseButtons.Left);

        if (holdCtrl)
        {
            Input.KeyUp(Keys.LControlKey);
        }

        await DelayAutomationAsync(postClickDelayMs);
    }

    private async Task<int> WaitForMapStashPageQuantityToChangeAsync(string metadata, int previousQuantity)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return previousQuantity;
        }

        var automation = Settings.StashAutomation;
        var timing = automation.Timing;
        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(timing.QuantityChangeBaseDelayMs.Value, automation.ClickDelayMs.Value + timing.QuantityChangeBaseDelayMs.Value));
        int? pendingQuantity = null;
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();
            var visibleItems = GetVisibleMapStashPageItems();
            if (visibleItems == null)
            {
                pendingQuantity = null;
                await DelayAutomationAsync(timing.FastPollDelayMs.Value);
                continue;
            }

            var currentQuantity = CountMatchingMapStashPageItems(visibleItems, metadata);
            if (currentQuantity < previousQuantity)
            {
                return currentQuantity;
            }

            if (currentQuantity == previousQuantity)
            {
                pendingQuantity = null;
                await DelayAutomationAsync(timing.FastPollDelayMs.Value);
                continue;
            }

            if (pendingQuantity == currentQuantity)
            {
                return currentQuantity;
            }

            pendingQuantity = currentQuantity;

            await DelayAutomationAsync(timing.FastPollDelayMs.Value);
        }

        return previousQuantity;
    }

    private void UpdateAutomationStatus(string message)
    {
        if (string.Equals(_lastAutomationStatusMessage, message, StringComparison.Ordinal))
        {
            return;
        }

        _lastAutomationStatusMessage = message;
        LogAutomationDebug($"STATUS: {message}");
    }

    private void LogAutomationDebug(string message)
    {
        if (Settings?.StashAutomation?.DebugLogging?.Value != true)
        {
            return;
        }

        try
        {
            DebugWindow.LogMsg($"[RareBeastCounter.Automation] {message}");
        }
        catch
        {
        }
    }

    private static string DescribeTarget(StashAutomationTargetSettings target)
    {
        if (target == null)
        {
            return "target=null";
        }

        return $"enabled={target.Enabled.Value}, item='{target.ItemName.Value}', quantity={target.Quantity.Value}, selectedTab='{target.SelectedTabName.Value}'";
    }

    private static string DescribeStash(StashElement stash)
    {
        if (stash == null)
        {
            return "stash=null";
        }

        return $"stashVisible={stash.IsVisible}, visibleTabIndex={stash.IndexVisibleStash}, totalTabs={stash.TotalStashes}, visibleType={stash.VisibleStash?.InvType.ToString() ?? "null"}";
    }

    private static string DescribeElement(Element element)
    {
        if (element == null)
        {
            return "element=null";
        }

        var rect = element.GetClientRect();
        return $"visible={element.IsVisible}, children={element.Children?.Count ?? 0}, rect={DescribeRect(rect)}";
    }

    private static string DescribeRect(RectangleF rect)
    {
        return $"[{rect.Left:0.#},{rect.Top:0.#}] -> [{rect.Right:0.#},{rect.Bottom:0.#}]";
    }

    private static string DescribePath(IReadOnlyList<int> path)
    {
        return path == null ? "null" : string.Join("->", path);
    }

    private static string DescribePageTabs(IReadOnlyDictionary<int, Element> pageTabsByNumber)
    {
        if (pageTabsByNumber == null || pageTabsByNumber.Count == 0)
        {
            return "none";
        }

        return string.Join(" | ", pageTabsByNumber.OrderBy(x => x.Key).Select(x => $"{x.Key}:{DescribeElement(x.Value)}"));
    }

    private static string DescribeChildren(Element parent, int maxChildren = 12)
    {
        if (parent?.Children == null)
        {
            return "children=null";
        }

        return string.Join(" | ", parent.Children.Take(maxChildren).Select((child, index) => $"{index}:{DescribeElement(child)}"));
    }

    private static string DescribePathLookup(Element root, IReadOnlyList<int> path)
    {
        if (root == null)
        {
            return $"root=null, path={DescribePath(path)}";
        }

        if (path == null || path.Count == 0)
        {
            return $"path empty, root={DescribeElement(root)}";
        }

        var builder = new StringBuilder();
        var current = root;
        builder.Append($"root={DescribeElement(root)}");

        for (var i = 0; i < path.Count; i++)
        {
            var childIndex = path[i];
            var children = current?.Children;
            builder.Append($" -> [{childIndex}] children={children?.Count ?? 0}");

            if (children == null || childIndex < 0 || childIndex >= children.Count)
            {
                builder.Append(" (missing)");
                if (current != null)
                {
                    builder.Append($", siblings={DescribeChildren(current)}");
                }

                return builder.ToString();
            }

            current = children[childIndex];
            builder.Append($" => {DescribeElement(current)}");
        }

        if (current != null)
        {
            builder.Append($", finalChildren={DescribeChildren(current)}");
        }

        return builder.ToString();
    }

}
