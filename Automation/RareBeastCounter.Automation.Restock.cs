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
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    #region Restock automation flow

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
        var inventoryQuantityBeforeTransfer = GetVisiblePlayerInventoryMatchingQuantity(sourceMetadata);
        var remainingRequestedQuantity = Math.Max(0, requestedQuantity - inventoryQuantityBeforeTransfer);

        LogAutomationDebug($"Target '{label}' source resolved. useMapStashPageItems={useMapStashPageItems}, configuredName='{configuredItemName}', metadata='{sourceMetadata}', available={availableInStash}, visibleItems={visibleItems.Count}, inventoryBefore={inventoryQuantityBeforeTransfer}, remainingRequested={remainingRequestedQuantity}");

        if (string.IsNullOrWhiteSpace(sourceMetadata))
        {
            throw new InvalidOperationException($"Source item metadata is unavailable for {label}.");
        }

        if (availableInStash <= 0)
        {
            throw new InvalidOperationException($"No {label} were found in the visible stash tab.");
        }

        if (remainingRequestedQuantity <= 0)
        {
            UpdateAutomationStatus($"{label} already stocked: {inventoryQuantityBeforeTransfer}/{requestedQuantity}");
            LogAutomationDebug($"Target '{label}' skipped because inventory already satisfies the requested total. inventoryBefore={inventoryQuantityBeforeTransfer}, requested={requestedQuantity}");
            return 0;
        }

        UpdateAutomationStatus($"Loading {label}: {inventoryQuantityBeforeTransfer}/{requestedQuantity}");

        var transferred = 0;
        var transferGoal = useMapStashPageItems
            ? remainingRequestedQuantity
            : Math.Min(remainingRequestedQuantity, availableInStash);
        var observedTransfer = false;

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
                observedTransfer = true;
                transferred += movedAmount;
                LogAutomationDebug($"Target '{label}' transferred {movedAmount}. totalTransferred={transferred}, requested={requestedQuantity}");
                UpdateAutomationStatus($"Loading {label}: {Math.Min(inventoryQuantityBeforeTransfer + transferred, requestedQuantity)}/{requestedQuantity}");
                await DelayAutomationAsync(automation.ClickDelayMs.Value);
            }

            var remainingAvailable = useMapStashPageItems
                ? GetVisibleMapStashPageMatchingQuantity(sourceMetadata)
                : GetVisibleMatchingItemQuantity(sourceMetadata);
            if (!useMapStashPageItems && (movedThisAttempt || transferred > 0))
            {
                transferred = Math.Max(transferred, Math.Max(0, availableInStash - remainingAvailable));
            }

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
        var inventoryTransferred = Math.Max(0, GetVisiblePlayerInventoryMatchingQuantity(sourceMetadata) - inventoryQuantityBeforeTransfer);
        if (useMapStashPageItems && observedTransfer && inventoryTransferred > transferred)
        {
            LogAutomationDebug($"Target '{label}' reconciled transferred count from inventory delta. previousTransferred={transferred}, inventoryDelta={inventoryTransferred}");
            transferred = inventoryTransferred;
        }

        if (transferred > 0 && finalRemainingAvailable > 0 && transferred < transferGoal)
        {
            throw new InvalidOperationException(
                $"{label} transfer stalled after {inventoryQuantityBeforeTransfer + transferred}/{requestedQuantity}. {finalRemainingAvailable} still remain in stash.");
        }

        if (transferred > 0 && inventoryQuantityBeforeTransfer + transferred < requestedQuantity && finalRemainingAvailable <= 0)
        {
            UpdateAutomationStatus($"Loaded {label}: {inventoryQuantityBeforeTransfer + transferred}/{requestedQuantity}. No more matching items found.");
        }

        if (transferred <= 0)
        {
            throw new InvalidOperationException($"No {label} were transferred.");
        }

        return transferred;
    }

    #endregion
    #region Restock preparation and source resolution

    private async Task PrepareConfiguredTargetAsync(StashAutomationSettings automation, StashAutomationTargetSettings target, int tabIndex)
    {
        LogAutomationDebug($"Preparing target. {DescribeTarget(target)}, requestedTabIndex={tabIndex}");
        await SelectStashTabAsync(tabIndex);
        await WaitForTargetStashReadyAsync(target, tabIndex);
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
                var currentVisibleQuantity = CountMatchingMapStashPageItems(visiblePageItems, sourceMetadata);
                if (nextPageItem?.Entity == null)
                {
                    if (currentVisibleQuantity > 0)
                    {
                        nextPageItem = await WaitForNextMatchingMapStashPageItemAsync(sourceMetadata);
                    }
                    else
                    {
                        LogAutomationDebug($"Current map stash page has no remaining matches for metadata='{sourceMetadata}'. Searching other pages immediately.");
                    }

                    visiblePageItems = GetVisibleMapStashPageItems();
                    currentVisibleQuantity = CountMatchingMapStashPageItems(visiblePageItems, sourceMetadata);
                    nextPageItem = FindNextMatchingMapStashPageItem(visiblePageItems, sourceMetadata);
                }

                if (nextPageItem?.Entity == null && currentVisibleQuantity <= 0)
                {
                    if (await EnsureMapStashPageWithItemSelectedAsync(target, sourceMetadata))
                    {
                        nextPageItem = await WaitForNextMatchingMapStashPageItemAsync(sourceMetadata);
                    }
                }

                if (nextPageItem?.Entity == null && currentVisibleQuantity > 0)
                {
                    LogAutomationDebug($"Visible map stash page still reports {currentVisibleQuantity} matching item(s) for metadata='{sourceMetadata}'. Deferring page scan until current page is empty.");
                    await DelayAutomationAsync(AutomationTiming.FastPollDelayMs);
                    continue;
                }

                if (nextPageItem?.Entity == null)
                {
                    return 0;
                }

                visiblePageItems = GetVisibleMapStashPageItems();
                nextPageItem = FindNextMatchingMapStashPageItem(visiblePageItems, sourceMetadata) ?? nextPageItem;
                var availableBeforeTransfer = GetVisibleMapStashPageMatchingQuantity(sourceMetadata);
                var inventoryBeforeTransfer = TryGetVisiblePlayerInventoryMatchingQuantity(sourceMetadata);
                var remainingRequestedQuantity = Math.Max(0, target.Quantity.Value - (inventoryBeforeTransfer ?? 0));
                if (remainingRequestedQuantity <= 0)
                {
                    return 0;
                }

                var timing = AutomationTiming;
                var batchTransferTargets = (visiblePageItems ?? (nextPageItem?.Entity != null ? [nextPageItem] : []))
                    .Where(item => string.Equals(item?.Entity?.Metadata, sourceMetadata, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.GetClientRect().Top)
                    .ThenBy(item => item.GetClientRect().Left)
                    .Take(remainingRequestedQuantity)
                    .Select(item => new
                    {
                        Position = item.GetClientRect().Center,
                        StackSize = Math.Max(1, item.Entity?.GetComponent<Stack>()?.Size ?? 1)
                    })
                    .ToList();
                var attemptedTransferQuantity = batchTransferTargets.Sum(x => x.StackSize);
                if (attemptedTransferQuantity <= 0)
                {
                    return 0;
                }

                var expectedLandingSlots = GetPlayerInventoryNextFreeCells(batchTransferTargets.Count);
                var expectedSlotFillBeforeTransfer = CountOccupiedPlayerInventoryCells(expectedLandingSlots);
                LogAutomationDebug($"Predicted inventory landing slots for map batch. metadata='{sourceMetadata}', slots={DescribePlayerInventoryCells(expectedLandingSlots)}, occupiedBefore={expectedSlotFillBeforeTransfer}/{expectedLandingSlots.Count}");

                LogAutomationDebug($"Batch transferring visible map stash page items. metadata='{sourceMetadata}', remainingRequested={remainingRequestedQuantity}, targetCount={batchTransferTargets.Count}, attemptedQuantity={attemptedTransferQuantity}, previousQuantity={availableBeforeTransfer}");
                foreach (var batchTarget in batchTransferTargets)
                {
                    ThrowIfAutomationStopRequested();
                    await ClickAtAsync(
                        batchTarget.Position,
                        holdCtrl: true,
                        preClickDelayMs: timing.CtrlClickPreDelayMs,
                        postClickDelayMs: timing.CtrlClickPostDelayMs);
                }

                if (expectedLandingSlots.Count > 0)
                {
                    LogAutomationDebug($"Waiting for predicted inventory slot fill confirmation. metadata='{sourceMetadata}', expectedSlots={expectedLandingSlots.Count}, previousFilled={expectedSlotFillBeforeTransfer}, slots={DescribePlayerInventoryCells(expectedLandingSlots)}");
                    var expectedSlotFillAfterTransfer = await WaitForPlayerInventorySlotFillToSettleAsync(expectedLandingSlots, expectedSlotFillBeforeTransfer, MapTransferExtraConfirmationDelayMs);
                    LogAutomationDebug($"Predicted inventory slot fill confirmation complete. metadata='{sourceMetadata}', expectedSlots={expectedLandingSlots.Count}, previousFilled={expectedSlotFillBeforeTransfer}, currentFilled={expectedSlotFillAfterTransfer}, landed={(expectedSlotFillAfterTransfer > expectedSlotFillBeforeTransfer)}, slots={DescribePlayerInventoryCells(expectedLandingSlots)}");
                }

                LogAutomationDebug($"Waiting for map stash batch quantity confirmation. metadata='{sourceMetadata}', previousQuantity={availableBeforeTransfer}");
                var availableAfterTransfer = await WaitForMapStashPageQuantityToSettleAsync(sourceMetadata, availableBeforeTransfer);
                LogAutomationDebug($"Map stash batch quantity confirmation complete. metadata='{sourceMetadata}', previousQuantity={availableBeforeTransfer}, currentQuantity={availableAfterTransfer}");
                LogAutomationDebug($"Waiting for inventory batch quantity confirmation. metadata='{sourceMetadata}', previousQuantity={(inventoryBeforeTransfer.HasValue ? inventoryBeforeTransfer.Value.ToString() : "null")}");
                var inventoryAfterTransfer = await WaitForPlayerInventoryQuantityToSettleAsync(sourceMetadata, inventoryBeforeTransfer, MapTransferExtraConfirmationDelayMs);
                LogAutomationDebug($"Inventory batch quantity confirmation complete. metadata='{sourceMetadata}', previousQuantity={(inventoryBeforeTransfer.HasValue ? inventoryBeforeTransfer.Value.ToString() : "null")}, currentQuantity={(inventoryAfterTransfer.HasValue ? inventoryAfterTransfer.Value.ToString() : "null")}");
                var stashMovedAmount = Math.Max(0, availableBeforeTransfer - availableAfterTransfer);
                var inventoryMovedAmount = inventoryBeforeTransfer.HasValue && inventoryAfterTransfer.HasValue
                    ? Math.Max(0, inventoryAfterTransfer.Value - inventoryBeforeTransfer.Value)
                    : 0;
                if (inventoryBeforeTransfer.HasValue && inventoryMovedAmount <= 0 && stashMovedAmount > 0)
                {
                    LogAutomationDebug($"Inventory confirmation fallback delay triggered. metadata='{sourceMetadata}', stashMoved={stashMovedAmount}, inventoryBefore={inventoryBeforeTransfer.Value}, inventoryAfter={(inventoryAfterTransfer.HasValue ? inventoryAfterTransfer.Value.ToString() : "null")}");
                    await DelayForUiCheckAsync();
                    inventoryAfterTransfer = TryGetVisiblePlayerInventoryMatchingQuantity(sourceMetadata);
                    inventoryMovedAmount = inventoryBeforeTransfer.HasValue && inventoryAfterTransfer.HasValue
                        ? Math.Max(0, inventoryAfterTransfer.Value - inventoryBeforeTransfer.Value)
                        : 0;
                    LogAutomationDebug($"Inventory confirmation fallback recheck complete. metadata='{sourceMetadata}', inventoryBefore={inventoryBeforeTransfer.Value}, inventoryAfter={(inventoryAfterTransfer.HasValue ? inventoryAfterTransfer.Value.ToString() : "null")}");
                }

                if (stashMovedAmount > 0 && inventoryMovedAmount > 0 && stashMovedAmount != inventoryMovedAmount)
                {
                    LogAutomationDebug($"Map stash transfer quantity mismatch. metadata='{sourceMetadata}', stashMoved={stashMovedAmount}, inventoryMoved={inventoryMovedAmount}. Using inventory delta.");
                }

                if (attemptedTransferQuantity > 0 && (stashMovedAmount > 0 || inventoryMovedAmount > 0) && attemptedTransferQuantity != Math.Max(stashMovedAmount, inventoryMovedAmount))
                {
                    LogAutomationDebug($"Map stash batch transfer shortfall. metadata='{sourceMetadata}', attempted={attemptedTransferQuantity}, stashMoved={stashMovedAmount}, inventoryMoved={inventoryMovedAmount}.");
                }

                var movedAmount = inventoryBeforeTransfer.HasValue
                    ? inventoryMovedAmount > 0
                        ? inventoryMovedAmount
                        : attemptedTransferQuantity <= 1
                            ? stashMovedAmount
                            : 0
                    : stashMovedAmount;
                if (movedAmount > 0)
                {
                    return movedAmount;
                }

                await DelayAutomationAsync(AutomationTiming.FastPollDelayMs);
            }

            return 0;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            ThrowIfAutomationStopRequested();

            var visibleItems = GetVisibleStashItems();
            var matchingVisibleItems = visibleItems?
                .Where(item => item?.Item != null && string.Equals(item.Item.Metadata, sourceMetadata, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.GetClientRect().Top)
                .ThenBy(item => item.GetClientRect().Left)
                .ToList();
            var nextItem = matchingVisibleItems?.FirstOrDefault();
            if (nextItem?.Item == null)
            {
                return 0;
            }

            var availableBeforeItemTransfer = GetVisibleMatchingItemQuantity(sourceMetadata);
            var inventoryBeforeItemTransfer = TryGetVisiblePlayerInventoryMatchingQuantity(sourceMetadata);
            var remainingRequestedQuantity = Math.Max(0, target.Quantity.Value - (inventoryBeforeItemTransfer ?? 0));
            var stackSizeBeforeItemTransfer = Math.Max(1, nextItem.Item?.GetComponent<Stack>()?.Size ?? 1);
            var matchingStackCount = matchingVisibleItems?.Count ?? 0;
            var knownFullStackSize = GetKnownFullStackSize(sourceMetadata);
            var hasStackableQuantity = stackSizeBeforeItemTransfer > 1 ||
                                       matchingVisibleItems?.Any(item => Math.Max(1, item.Item?.GetComponent<Stack>()?.Size ?? 1) > 1) == true ||
                                       availableBeforeItemTransfer > matchingStackCount;
            var usePartialShiftClick = knownFullStackSize.HasValue &&
                                       remainingRequestedQuantity > 0 &&
                                       remainingRequestedQuantity < knownFullStackSize.Value &&
                                       availableBeforeItemTransfer > remainingRequestedQuantity &&
                                       hasStackableQuantity;
            var useBulkCtrlRightClick = availableBeforeItemTransfer > 0 &&
                                        availableBeforeItemTransfer <= remainingRequestedQuantity &&
                                        matchingStackCount > 0 &&
                                        hasStackableQuantity;
            var expectedLandingSlots = GetPlayerInventoryNextFreeCells(useBulkCtrlRightClick ? matchingStackCount : 1);
            var expectedSlotFillBeforeItemTransfer = CountOccupiedPlayerInventoryCells(expectedLandingSlots);
            var transferMode = useBulkCtrlRightClick ? "ctrl-right-click" : usePartialShiftClick ? "shift-click-partial" : "ctrl-click";
            LogAutomationDebug($"Predicted inventory landing slot{(expectedLandingSlots.Count == 1 ? string.Empty : "s")} for stash transfer. metadata='{sourceMetadata}', mode='{transferMode}', matchingStacks={matchingStackCount}, hasStackableQuantity={hasStackableQuantity}, fullStackSize={(knownFullStackSize.HasValue ? knownFullStackSize.Value.ToString() : "null")}, slots={DescribePlayerInventoryCells(expectedLandingSlots)}, occupiedBefore={expectedSlotFillBeforeItemTransfer}/{expectedLandingSlots.Count}");
            if (useBulkCtrlRightClick)
            {
                LogAutomationDebug($"Using ctrl-right-click bulk transfer for metadata='{sourceMetadata}'. available={availableBeforeItemTransfer}, remainingRequested={remainingRequestedQuantity}, matchingStacks={matchingStackCount}");
                await CtrlRightClickInventoryItemAsync(nextItem);
            }
            else if (usePartialShiftClick)
            {
                LogAutomationDebug($"Using shift-click partial transfer for metadata='{sourceMetadata}'. available={availableBeforeItemTransfer}, remainingRequested={remainingRequestedQuantity}, fullStackSize={knownFullStackSize.Value}, targetSlot={DescribePlayerInventoryCells(expectedLandingSlots)}");
                await ShiftClickInventoryItemAsync(nextItem);
                await InputCurrencyShiftClickQuantityAsync(remainingRequestedQuantity);
                await DelayForUiCheckAsync(100);

                var targetInventoryCell = expectedLandingSlots.FirstOrDefault();
                if (expectedLandingSlots.Count <= 0)
                {
                    throw new InvalidOperationException($"No free inventory slot found for partial transfer of '{sourceMetadata}'.");
                }

                LogAutomationDebug($"Placing partial transfer into inventory slot ({targetInventoryCell.X},{targetInventoryCell.Y}). metadata='{sourceMetadata}'");
                await PlaceItemIntoPlayerInventoryCellAsync(targetInventoryCell.X, targetInventoryCell.Y);
            }
            else
            {
                await CtrlClickInventoryItemAsync(nextItem);
            }

            if (expectedLandingSlots.Count > 0)
            {
                LogAutomationDebug($"Waiting for predicted inventory slot fill confirmation. metadata='{sourceMetadata}', expectedSlots={expectedLandingSlots.Count}, previousFilled={expectedSlotFillBeforeItemTransfer}, slots={DescribePlayerInventoryCells(expectedLandingSlots)}");
                var expectedSlotFillAfterItemTransfer = await WaitForPlayerInventorySlotFillToSettleAsync(expectedLandingSlots, expectedSlotFillBeforeItemTransfer);
                LogAutomationDebug($"Predicted inventory slot fill confirmation complete. metadata='{sourceMetadata}', expectedSlots={expectedLandingSlots.Count}, previousFilled={expectedSlotFillBeforeItemTransfer}, currentFilled={expectedSlotFillAfterItemTransfer}, landed={(expectedSlotFillAfterItemTransfer > expectedSlotFillBeforeItemTransfer)}, slots={DescribePlayerInventoryCells(expectedLandingSlots)}");
            }
            LogAutomationDebug($"Waiting for stash quantity confirmation. metadata='{sourceMetadata}', previousQuantity={availableBeforeItemTransfer}");
            var availableAfterItemTransfer = await WaitForMatchingItemQuantityToChangeAsync(sourceMetadata, availableBeforeItemTransfer);
            LogAutomationDebug($"Stash quantity confirmation complete. metadata='{sourceMetadata}', previousQuantity={availableBeforeItemTransfer}, currentQuantity={availableAfterItemTransfer}");
            LogAutomationDebug($"Waiting for inventory quantity confirmation. metadata='{sourceMetadata}', previousQuantity={(inventoryBeforeItemTransfer.HasValue ? inventoryBeforeItemTransfer.Value.ToString() : "null")}");
            var inventoryAfterItemTransfer = await WaitForPlayerInventoryQuantityToChangeAsync(sourceMetadata, inventoryBeforeItemTransfer);
            LogAutomationDebug($"Inventory quantity confirmation complete. metadata='{sourceMetadata}', previousQuantity={(inventoryBeforeItemTransfer.HasValue ? inventoryBeforeItemTransfer.Value.ToString() : "null")}, currentQuantity={(inventoryAfterItemTransfer.HasValue ? inventoryAfterItemTransfer.Value.ToString() : "null")}");
            var stashMovedAmount = Math.Max(0, availableBeforeItemTransfer - availableAfterItemTransfer);
            var inventoryMovedAmount = inventoryBeforeItemTransfer.HasValue && inventoryAfterItemTransfer.HasValue
                ? Math.Max(0, inventoryAfterItemTransfer.Value - inventoryBeforeItemTransfer.Value)
                : 0;
            if (inventoryBeforeItemTransfer.HasValue && inventoryMovedAmount <= 0 && stashMovedAmount > 0)
            {
                LogAutomationDebug($"Inventory confirmation fallback delay triggered. metadata='{sourceMetadata}', stashMoved={stashMovedAmount}, inventoryBefore={inventoryBeforeItemTransfer.Value}, inventoryAfter={(inventoryAfterItemTransfer.HasValue ? inventoryAfterItemTransfer.Value.ToString() : "null")}");
                await DelayForUiCheckAsync();
                inventoryAfterItemTransfer = TryGetVisiblePlayerInventoryMatchingQuantity(sourceMetadata);
                inventoryMovedAmount = inventoryBeforeItemTransfer.HasValue && inventoryAfterItemTransfer.HasValue
                    ? Math.Max(0, inventoryAfterItemTransfer.Value - inventoryBeforeItemTransfer.Value)
                    : 0;
                LogAutomationDebug($"Inventory confirmation fallback recheck complete. metadata='{sourceMetadata}', inventoryBefore={inventoryBeforeItemTransfer.Value}, inventoryAfter={(inventoryAfterItemTransfer.HasValue ? inventoryAfterItemTransfer.Value.ToString() : "null")}");
            }

            if (stashMovedAmount > 0 && inventoryMovedAmount > 0 && stashMovedAmount != inventoryMovedAmount)
            {
                LogAutomationDebug($"Stash transfer quantity mismatch. metadata='{sourceMetadata}', stashMoved={stashMovedAmount}, inventoryMoved={inventoryMovedAmount}. Using inventory delta.");
            }

            var movedAmount = inventoryBeforeItemTransfer.HasValue
                ? inventoryMovedAmount > 0
                    ? inventoryMovedAmount
                    : useBulkCtrlRightClick || usePartialShiftClick || stackSizeBeforeItemTransfer <= 1
                        ? stashMovedAmount
                        : 0
                : stashMovedAmount;
            if (movedAmount > 0)
            {
                return movedAmount;
            }

            await DelayAutomationAsync(AutomationTiming.FastPollDelayMs);
        }

        return 0;
    }

    #endregion
}
