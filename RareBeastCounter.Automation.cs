using System;
using System.Collections.Generic;
using System.Linq;
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
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    private static readonly int[] FragmentStashScarabTabPath = [2, 0, 0, 1, 1, 24, 0, 5, 0, 1];
    private static readonly int[] MapStashTierOneToNineTabPath = [2, 0, 0, 1, 1, 2, 0, 0];
    private static readonly int[] MapStashTierTenToSixteenTabPath = [2, 0, 0, 1, 1, 2, 0, 1];
    private static readonly int[] MapStashPageTabPath = [2, 0, 0, 1, 1, 2, 0, 3, 0];
    private static readonly int[] MapStashPageNumberPath = [0, 1];
    private static readonly int[] MapStashPageContentPath = [2, 0, 0, 1, 1, 2, 0, 4];
    private string _lastAutomationStatusMessage;
    private bool _isAutomationRunning;
    private bool _isAutomationStopRequested;
    private int _lastAutomationFragmentScarabTabIndex = -1;
    private int _lastAutomationMapStashTierSelection = -1;
    private int _lastAutomationMapStashPageNumber = -1;

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

        _isAutomationRunning = true;
        _isAutomationStopRequested = false;
        ResetAutomationState();
        try
        {
            var automationTargets = GetAutomationTargets(automation);
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
        if (!target.Enabled.Value || requestedQuantity <= 0)
        {
            return 0;
        }

        UpdateAutomationStatus($"Loading {label}...");

        var tabIndex = ResolveConfiguredTabIndex(target);
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
            var movedThisAttempt = false;
            while (transferred < transferGoal)
            {
                var movedAmount = await TryTransferNextMatchingItemAsync(target, sourceMetadata, useMapStashPageItems);
                if (movedAmount <= 0)
                {
                    break;
                }

                movedThisAttempt = true;
                transferred += movedAmount;
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
        await SelectStashTabAsync(tabIndex);
        await EnsureSpecialStashSubTabSelectedAsync(target);
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
            _lastAutomationMapStashTierSelection = -1;
            return;
        }

        var mapTier = configuredMapTier.Value;
        var childIndex = mapTier <= 9 ? mapTier - 1 : mapTier - 10;
        var tierPath = mapTier <= 9 ? MapStashTierOneToNineTabPath : MapStashTierTenToSixteenTabPath;
        var tierTab = GameController?.IngameState?.IngameUi?.OpenLeftPanel?.GetChildFromIndices(tierPath)?.Children.ElementAtOrDefault(childIndex);
        if (tierTab == null)
        {
            _lastAutomationMapStashTierSelection = -1;
            return;
        }

        var selectionKey = stash.IndexVisibleStash * 100 + mapTier;
        if (_lastAutomationMapStashTierSelection == selectionKey)
        {
            return;
        }

        var tierRect = tierTab.GetClientRect();
        await ClickAtAsync(
            tierRect.Center,
            holdCtrl: false,
            preClickDelayMs: timing.UiClickPreDelayMs.Value,
            postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs.Value, automation.TabSwitchDelayMs.Value));
        _lastAutomationMapStashTierSelection = selectionKey;
    }

    private async Task<bool> EnsureMapStashPageWithItemSelectedAsync(StashAutomationTargetSettings target, string metadata = null)
    {
        var automation = Settings.StashAutomation;
        if (!IsMapStashTarget(target))
        {
            return false;
        }

        var itemName = target.ItemName.Value?.Trim();
        if (MapStashVisiblePageContainsMatch(itemName, metadata))
        {
            return true;
        }

        var pageTabsByNumber = GetMapStashPageTabsByNumber();
        if (pageTabsByNumber == null || pageTabsByNumber.Count == 0)
        {
            return false;
        }

        var orderedPageNumbers = GetMapStashSearchPageNumbers(pageTabsByNumber);

        foreach (var pageNumber in orderedPageNumbers)
        {
            if (!await EnsureMapStashPageSelectedAsync(target, pageNumber))
            {
                continue;
            }

            if (MapStashVisiblePageContainsMatch(itemName, metadata))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> EnsureMapStashPageSelectedAsync(StashAutomationTargetSettings target, int pageNumber)
    {
        var automation = Settings.StashAutomation;
        if (!IsMapStashTarget(target))
        {
            return false;
        }

        var pageTabsByNumber = GetMapStashPageTabsByNumber();
        if (pageTabsByNumber == null || pageTabsByNumber.Count == 0)
        {
            return false;
        }

        if (!pageTabsByNumber.TryGetValue(pageNumber, out var pageTab))
        {
            return false;
        }

        var sourceIndex = GetMapStashPageSourceIndex(pageTab);
        _lastAutomationMapStashPageNumber = pageNumber;

        await SelectMapStashPageAsync(pageTab, sourceIndex, pageNumber, automation);
        return true;
    }

    private async Task SelectMapStashPageAsync(Element pageTab, int sourceIndex, int pageNumber, StashAutomationSettings automation)
    {
        ThrowIfAutomationStopRequested();

        var timing = automation.Timing;
        var rect = pageTab.GetClientRect();
        var center = rect.Center;

        await ClickAtAsync(
            center,
            holdCtrl: false,
            preClickDelayMs: timing.UiClickPreDelayMs.Value,
            postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs.Value, automation.TabSwitchDelayMs.Value));
        await DelayAutomationAsync(automation.TabSwitchDelayMs.Value);
    }

    private IList<Element> GetMapStashPageTabs()
    {
        var pageTabs = GameController?.IngameState?.IngameUi?.OpenLeftPanel?.GetChildFromIndices(MapStashPageTabPath)?.Children;
        if (pageTabs == null)
        {
            return null;
        }

        return pageTabs;
    }

    private Dictionary<int, Element> GetMapStashPageTabsByNumber()
    {
        var pageTabs = GetMapStashPageTabs();
        if (pageTabs == null)
        {
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

        return pageTabsByNumber;
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
        var pageNumberText = pageTab?.GetChildFromIndices(MapStashPageNumberPath)?.GetText(16)?.Trim();
        return int.TryParse(pageNumberText, out var pageNumber) && pageNumber is >= 1 and <= 6
            ? pageNumber
            : null;
    }

    private IList<Element> GetVisibleMapStashPageItems()
    {
        return GameController?.IngameState?.IngameUi?.OpenLeftPanel?
            .GetChildFromIndices(MapStashPageContentPath)?
            .Children?
            .FirstOrDefault(child => child?.IsVisible == true)?
            .Children?
            .Where(child => child?.Entity != null)
            .ToList();
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
                return nextPageItem;
            }

            await DelayAutomationAsync(timing.FastPollDelayMs.Value);
        }

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
            return -1;

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
            throw new InvalidOperationException("Select a valid stash tab name before running restock.");
        }

        if (stash.IndexVisibleStash == tabIndex)
        {
            return;
        }

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
                return;
            }

            var key = tabIndex < currentIndex ? Keys.Left : Keys.Right;
            Input.KeyDown(key);
            await DelayAutomationAsync(timing.KeyTapDelayMs.Value);
            Input.KeyUp(key);

            var changedIndex = await WaitForVisibleTabIndexChangeAsync(currentIndex, Math.Max(timing.TabChangeTimeoutMs.Value, automation.TabSwitchDelayMs.Value));
            if (changedIndex == currentIndex)
            {
                await DelayAutomationAsync(Math.Max(timing.TabRetryDelayMs.Value, automation.TabSwitchDelayMs.Value / 2));
            }
        }

        await WaitForVisibleTabAsync(tabIndex);
    }

    private async Task EnsureFragmentStashScarabTabSelectedAsync()
    {
        var automation = Settings.StashAutomation;
        var timing = automation.Timing;
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        if (stash?.IsVisible != true || stash.VisibleStash?.InvType != InventoryType.FragmentStash)
        {
            _lastAutomationFragmentScarabTabIndex = -1;
            return;
        }

        if (stash.IndexVisibleStash == _lastAutomationFragmentScarabTabIndex)
        {
            return;
        }

        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(timing.FragmentTabBaseTimeoutMs.Value, automation.TabSwitchDelayMs.Value + timing.FragmentTabBaseTimeoutMs.Value));
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();
            stash = GameController?.IngameState?.IngameUi?.StashElement;
            if (stash?.IsVisible != true || stash.VisibleStash?.InvType != InventoryType.FragmentStash)
            {
                _lastAutomationFragmentScarabTabIndex = -1;
                return;
            }

            var scarabTab = stash.GetChildFromIndices(FragmentStashScarabTabPath);
            if (scarabTab != null)
            {
                await ClickAtAsync(
                    scarabTab.GetClientRect().Center,
                    holdCtrl: false,
                    preClickDelayMs: timing.UiClickPreDelayMs.Value,
                    postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs.Value, automation.TabSwitchDelayMs.Value));
                _lastAutomationFragmentScarabTabIndex = stash.IndexVisibleStash;
                return;
            }

            await DelayAutomationAsync(timing.FastPollDelayMs.Value);
        }
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
    }

}
