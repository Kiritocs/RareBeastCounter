using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    #region Map device automation

    private const int MapDeviceFragmentSlotCount = 5;

    private async Task RunMapDeviceAutomationFromHotkeyAsync()
    {
        if (_isAutomationRunning)
        {
            RequestAutomationStop();
            return;
        }

        if (!TryGetEnabledStashAutomation(out var automation))
        {
            return;
        }

        BeginAutomationRun();
        try
        {
            UpdateAutomationStatus("Preparing map device...");
            if (GameController?.IngameState?.IngameUi?.Atlas?.IsVisible != true)
            {
                await CloseMapDeviceBlockingUiAsync();
            }

            if (!await EnsureMapDeviceWindowOpenAsync())
            {
                return;
            }

            var requestedItems = ResolveConfiguredMapDeviceItemsFromInventory(automation);
            var configuredInventoryTotals = ResolveConfiguredMapDeviceInventoryTotals(automation);
            var requestedMapCount = requestedItems.Count(x => x.IsMap);
            if (requestedMapCount != 1)
            {
                throw new InvalidOperationException(requestedMapCount <= 0
                    ? "Exactly one configured map item must be present in inventory before loading the Map Device."
                    : "More than one configured map item was found in inventory. Only one map can be placed in the Map Device.");
            }

            if (GetVisibleMapDeviceItemMetadata().Count > 0)
            {
                if (!DoesMapDeviceMatchRequestedItems(requestedItems, configuredInventoryTotals))
                {
                    throw new InvalidOperationException("Map Device already contains unexpected items. Clear it before running this hotkey.");
                }
            }
            else
            {
                ValidateConfiguredMapDeviceInventoryTotalsBeforeLoad(configuredInventoryTotals);
                var expectedMapDeviceQuantities = BuildExpectedMapDeviceQuantities(configuredInventoryTotals, requestedItems);
                var requestedCountsByMetadata = BuildRequestedMapDeviceCounts(requestedItems);
                var attemptedCountsByMetadata = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var requestedItem in requestedItems)
                {
                    attemptedCountsByMetadata.TryGetValue(requestedItem.Metadata, out var attemptedCount);
                    var attemptNumberForMetadata = attemptedCount + 1;
                    requestedCountsByMetadata.TryGetValue(requestedItem.Metadata, out var totalRequestedCountForMetadata);
                    expectedMapDeviceQuantities.TryGetValue(requestedItem.Metadata, out var expectedMapDeviceQuantity);

                    await CtrlClickInventoryItemIntoMapDeviceAsync(
                        requestedItem,
                        attemptNumberForMetadata,
                        totalRequestedCountForMetadata,
                        Math.Max(1, expectedMapDeviceQuantity));
                    attemptedCountsByMetadata[requestedItem.Metadata] = attemptNumberForMetadata;
                    await DelayAutomationAsync(automation.ClickDelayMs.Value);
                }
            }

            if (!await WaitForRequestedMapDeviceItemsAsync(requestedItems, configuredInventoryTotals))
            {
                throw new InvalidOperationException("Timed out verifying the Map Device contents.");
            }

            ValidateConfiguredMapDeviceInventoryTotalsAfterLoad(configuredInventoryTotals, requestedItems);

            MoveCursorToMapDeviceActivateButton();
            UpdateAutomationStatus("Map device loaded. Cursor moved to Activate.");
        }
        catch (OperationCanceledException)
        {
            UpdateAutomationStatus("Map device load cancelled.");
        }
        catch (Exception ex)
        {
            LogAutomationError("Map device load failed.", ex);
            UpdateAutomationStatus($"Map device load failed: {ex.Message}");
        }
        finally
        {
            EndAutomationRun();
        }
    }

    private async Task CloseMapDeviceBlockingUiAsync()
    {
        await CloseBlockingUiWithSpaceAsync(IsAutomationBlockingUiOpen, "map device automation", MapDeviceCloseUiMaxAttempts, throwOnFailure: true);
    }

    private async Task<bool> EnsureMapDeviceWindowOpenAsync()
    {
        var ui = GameController?.IngameState?.IngameUi;
        if (ui?.Atlas?.IsVisible == true && ui.MapDeviceWindow?.IsVisible == true)
        {
            return true;
        }

        var timing = AutomationTiming;
        var startedAt = DateTime.UtcNow;
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < GetAutomationTimeoutMs(MapDeviceOpenTimeoutMs))
        {
            ThrowIfAutomationStopRequested();

            ui = GameController?.IngameState?.IngameUi;
            if (ui?.Atlas?.IsVisible == true)
            {
                if (ui.MapDeviceWindow?.IsVisible == true)
                {
                    return true;
                }

                UpdateAutomationStatus("Atlas opened, but the Map Device window is not visible.");
                return false;
            }

            var mapDeviceEntity = FindNearestVisibleHideoutMapDeviceEntity();
            if (mapDeviceEntity == null)
            {
                UpdateAutomationStatus("No nearby visible Map Device found.");
                return false;
            }

            var distance = GetPlayerDistanceToEntity(mapDeviceEntity);
            var statusMessage = distance.HasValue && distance.Value <= timing.StashInteractionDistance
                ? "Opening Map Device..."
                : "Moving to Map Device...";
            if (!await ClickMapDeviceEntityAsync(mapDeviceEntity, statusMessage))
            {
                return false;
            }

            var atlasOpened = await WaitForBestiaryConditionAsync(
                () => GameController?.IngameState?.IngameUi?.Atlas?.IsVisible == true,
                MapDeviceOpenTimeoutMs,
                Math.Max(10, timing.FastPollDelayMs));
            if (atlasOpened)
            {
                ui = GameController?.IngameState?.IngameUi;
                if (ui?.MapDeviceWindow?.IsVisible == true)
                {
                    return true;
                }

                UpdateAutomationStatus("Atlas opened, but the Map Device window is not visible.");
                return false;
            }

            await DelayAutomationAsync(timing.StashOpenPollDelayMs);
        }

        UpdateAutomationStatus("Timed out opening the Map Device.");
        return false;
    }

    private async Task<bool> ClickMapDeviceEntityAsync(Entity mapDeviceEntity, string statusMessage)
    {
        if (mapDeviceEntity?.GetComponent<Render>() == null)
        {
            UpdateAutomationStatus("Could not find a clickable Map Device position.");
            return false;
        }

        UpdateAutomationStatus(statusMessage);
        if (!await HoverWorldEntityAsync(mapDeviceEntity, "Map Device"))
        {
            UpdateAutomationStatus("Could not hover the Map Device.");
            return false;
        }

        var timing = AutomationTiming;
        await ClickCurrentCursorAsync(
            holdCtrl: false,
            preClickDelayMs: timing.UiClickPreDelayMs,
            postClickDelayMs: timing.OpenStashPostClickDelayMs);
        return true;
    }

    private Entity FindNearestVisibleHideoutMapDeviceEntity()
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
        Entity nearestMapDevice = null;
        var nearestDistanceSquared = float.MaxValue;

        foreach (var entity in entities)
        {
            if (entity?.IsValid != true ||
                !string.Equals(entity.Metadata, HideoutMapDeviceMetadata, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var positioned = entity.GetComponent<Positioned>();
            var render = entity.GetComponent<Render>();
            if (positioned == null || render == null)
            {
                continue;
            }

            if (!IsScreenPositionVisible(camera.WorldToScreen(render.PosNum), windowRect.Width, windowRect.Height))
            {
                continue;
            }

            var distanceSquared = System.Numerics.Vector2.DistanceSquared(playerGridPos, positioned.GridPosNum);
            if (distanceSquared >= nearestDistanceSquared)
            {
                continue;
            }

            nearestDistanceSquared = distanceSquared;
            nearestMapDevice = entity;
        }

        return nearestMapDevice;
    }

    private List<(string Label, string Metadata, bool IsMap)> ResolveConfiguredMapDeviceItemsFromInventory(StashAutomationSettings automation)
    {
        var inventoryItems = GetVisiblePlayerInventoryItems();
        if (inventoryItems == null)
        {
            throw new InvalidOperationException("Player inventory is not visible.");
        }

        var requestedItems = new List<(string Label, string Metadata, bool IsMap)>();
        var missingTargets = new List<string>();
        var fragmentTargets = new List<(string Label, string Metadata)>();

        foreach (var (label, _, target) in GetAutomationTargets(automation))
        {
            if (target?.Enabled?.Value != true || target.Quantity.Value <= 0)
            {
                continue;
            }

            var inventoryItem = FindInventoryItemForMapDeviceTarget(inventoryItems, target);
            if (inventoryItem?.Item == null)
            {
                missingTargets.Add(label);
                continue;
            }

            var metadata = inventoryItem.Item.Metadata;
            var isMap = inventoryItem.Item.GetComponent<MapKey>() != null;
            if (isMap)
            {
                requestedItems.Add((label, metadata, true));
                continue;
            }

            var availableCount = CountMatchingItemQuantity(inventoryItems, metadata);
            if (availableCount > 0)
            {
                fragmentTargets.Add((label, metadata));
            }
        }

        if (missingTargets.Count > 0)
        {
            throw new InvalidOperationException($"Missing required inventory item(s): {string.Join(", ", missingTargets)}.");
        }

        if (requestedItems.Count(item => item.IsMap) > 1)
        {
            throw new InvalidOperationException("Only one configured map can be loaded into the Map Device.");
        }

        if (fragmentTargets.Count > 0)
        {
            foreach (var fragmentTarget in fragmentTargets.Take(MapDeviceFragmentSlotCount))
            {
                requestedItems.Add((fragmentTarget.Label, fragmentTarget.Metadata, false));
            }
        }

        if (requestedItems.Count <= 0)
        {
            throw new InvalidOperationException("No enabled map-device targets were found in player inventory.");
        }

        return requestedItems;
    }

    private Dictionary<string, (string Label, int ExpectedQuantity)> ResolveConfiguredMapDeviceInventoryTotals(StashAutomationSettings automation)
    {
        var inventoryItems = GetVisiblePlayerInventoryItems();
        if (inventoryItems == null)
        {
            throw new InvalidOperationException("Player inventory is not visible.");
        }

        var configuredTotals = new Dictionary<string, (string Label, int ExpectedQuantity)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, _, target) in GetAutomationTargets(automation))
        {
            if (target?.Enabled?.Value != true || target.Quantity.Value <= 0)
            {
                continue;
            }

            var inventoryItem = FindInventoryItemForMapDeviceTarget(inventoryItems, target);
            if (inventoryItem?.Item == null || string.IsNullOrWhiteSpace(inventoryItem.Item.Metadata))
            {
                continue;
            }

            var metadata = inventoryItem.Item.Metadata;
            if (configuredTotals.TryGetValue(metadata, out var existing))
            {
                configuredTotals[metadata] = ($"{existing.Label} / {label}", existing.ExpectedQuantity + target.Quantity.Value);
            }
            else
            {
                configuredTotals[metadata] = (label, target.Quantity.Value);
            }
        }

        return configuredTotals;
    }

    private void ValidateConfiguredMapDeviceInventoryTotalsBeforeLoad(IReadOnlyDictionary<string, (string Label, int ExpectedQuantity)> configuredInventoryTotals)
    {
        if (configuredInventoryTotals == null)
        {
            return;
        }

        foreach (var (metadata, configured) in configuredInventoryTotals)
        {
            var actualQuantity = TryGetVisiblePlayerInventoryMatchingQuantity(metadata) ?? 0;
            if (actualQuantity != configured.ExpectedQuantity)
            {
                throw new InvalidOperationException(
                    $"Inventory quantity mismatch for {configured.Label}. Expected total {configured.ExpectedQuantity} of {metadata}, found {actualQuantity}.");
            }
        }
    }

    private void ValidateConfiguredMapDeviceInventoryTotalsAfterLoad(
        IReadOnlyDictionary<string, (string Label, int ExpectedQuantity)> configuredInventoryTotals,
        IReadOnlyList<(string Label, string Metadata, bool IsMap)> requestedItems)
    {
        if (configuredInventoryTotals == null || requestedItems == null)
        {
            return;
        }

        var expectedMapDeviceQuantities = BuildExpectedMapDeviceQuantities(configuredInventoryTotals, requestedItems);
        foreach (var (metadata, expectedInMapDevice) in expectedMapDeviceQuantities)
        {
            var actualRemainingInventory = TryGetVisiblePlayerInventoryMatchingQuantity(metadata) ?? 0;
            var actualInMapDevice = GetVisibleMapDeviceMatchingQuantity(metadata);

            if (actualInMapDevice != expectedInMapDevice)
            {
                var configuredLabel = configuredInventoryTotals.TryGetValue(metadata, out var configured) ? configured.Label : metadata;
                var configuredTotalQuantity = configuredInventoryTotals.TryGetValue(metadata, out configured) ? configured.ExpectedQuantity : 0;
                throw new InvalidOperationException(
                    $"Post-load quantity mismatch for {configuredLabel} ({metadata}). " +
                    $"Expected Map Device x{expectedInMapDevice}, found x{actualInMapDevice}. " +
                    $"Visible inventory qty={actualRemainingInventory}. " +
                    $"Configured total qty={configuredTotalQuantity}.");
            }
        }
    }

    private static NormalInventoryItem FindInventoryItemForMapDeviceTarget(IList<NormalInventoryItem> inventoryItems, StashAutomationTargetSettings target)
    {
        if (inventoryItems == null || target == null)
        {
            return null;
        }

        var configuredMapTier = TryGetConfiguredMapTier(target);
        if (configuredMapTier.HasValue)
        {
            return inventoryItems
                .Where(item => item?.Item != null && item.Item.GetComponent<MapKey>()?.Tier == configuredMapTier.Value)
                .OrderBy(item => item.GetClientRect().Top)
                .ThenBy(item => item.GetClientRect().Left)
                .FirstOrDefault();
        }

        return FindStashItemByName(inventoryItems, target.ItemName.Value?.Trim());
    }

    private async Task CtrlClickInventoryItemIntoMapDeviceAsync((string Label, string Metadata, bool IsMap) requestedItem, int attemptNumberForMetadata, int totalRequestedCountForMetadata, int expectedMapDeviceQuantity)
    {
        while (GetVisibleMapDeviceMatchingQuantity(requestedItem.Metadata) < Math.Max(1, expectedMapDeviceQuantity))
        {
            var inventoryItems = GetVisiblePlayerInventoryItems();
            var inventoryItem = FindNextMatchingStashItem(inventoryItems, requestedItem.Metadata);
            if (inventoryItem?.Item == null)
            {
                var visibleInventoryQuantity = TryGetVisiblePlayerInventoryMatchingQuantity(requestedItem.Metadata) ?? 0;
                LogAutomationDebug(
                    $"Map device inventory lookup failed for {requestedItem.Label} ({requestedItem.Metadata}). " +
                    $"attempt={attemptNumberForMetadata}/{Math.Max(1, totalRequestedCountForMetadata)}, " +
                    $"expectedMapDeviceQty={Math.Max(1, expectedMapDeviceQuantity)}, " +
                    $"currentMapDeviceQty={GetVisibleMapDeviceMatchingQuantity(requestedItem.Metadata)}, " +
                    $"visibleQty={visibleInventoryQuantity}, " +
                    $"visibleMatches=[{DescribeVisibleInventoryMatches(inventoryItems, requestedItem.Metadata)}]");
                throw new InvalidOperationException($"Could not find {requestedItem.Label} in player inventory.");
            }

            var deviceQuantityBefore = GetVisibleMapDeviceMatchingQuantity(requestedItem.Metadata);
            var inventoryQuantityBefore = TryGetVisiblePlayerInventoryMatchingQuantity(requestedItem.Metadata);
            UpdateAutomationStatus($"Loading Map Device: {requestedItem.Label}");

            await ClickAtAsync(
                inventoryItem.GetClientRect().Center,
                holdCtrl: true,
                preClickDelayMs: AutomationTiming.CtrlClickPreDelayMs,
                postClickDelayMs: AutomationTiming.CtrlClickPostDelayMs);

            var inserted = await WaitForBestiaryConditionAsync(
                () =>
                {
                    var currentInventoryQuantity = TryGetVisiblePlayerInventoryMatchingQuantity(requestedItem.Metadata);
                    return GetVisibleMapDeviceMatchingQuantity(requestedItem.Metadata) > deviceQuantityBefore ||
                           (inventoryQuantityBefore.HasValue && currentInventoryQuantity.HasValue && currentInventoryQuantity.Value < inventoryQuantityBefore.Value);
                },
                MapDeviceTransferTimeoutMs,
                Math.Max(10, AutomationTiming.FastPollDelayMs));
            if (!inserted)
            {
                throw new InvalidOperationException($"Failed to move {requestedItem.Label} into the Map Device.");
            }
        }
    }

    private static string DescribeVisibleInventoryMatches(IList<NormalInventoryItem> inventoryItems, string metadata)
    {
        if (inventoryItems == null || string.IsNullOrWhiteSpace(metadata))
        {
            return "none";
        }

        var matches = inventoryItems
            .Where(item => item?.Item != null && string.Equals(item.Item.Metadata, metadata, StringComparison.OrdinalIgnoreCase))
            .Select(item =>
            {
                var rect = item.GetClientRect();
                var stackSize = Math.Max(1, item.Item.GetComponent<ExileCore.PoEMemory.Components.Stack>()?.Size ?? 1);
                return $"stack={stackSize}, rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})";
            })
            .ToList();

        return matches.Count > 0 ? string.Join(" | ", matches) : "none";
    }

    private async Task<bool> WaitForRequestedMapDeviceItemsAsync(
        IReadOnlyList<(string Label, string Metadata, bool IsMap)> requestedItems,
        IReadOnlyDictionary<string, (string Label, int ExpectedQuantity)> configuredInventoryTotals)
    {
        if (DoesMapDeviceMatchRequestedItems(requestedItems, configuredInventoryTotals))
        {
            return true;
        }

        return await WaitForBestiaryConditionAsync(
            () => DoesMapDeviceMatchRequestedItems(requestedItems, configuredInventoryTotals),
            MapDeviceTransferTimeoutMs,
            Math.Max(10, AutomationTiming.FastPollDelayMs));
    }

    private bool DoesMapDeviceMatchRequestedItems(
        IReadOnlyList<(string Label, string Metadata, bool IsMap)> requestedItems,
        IReadOnlyDictionary<string, (string Label, int ExpectedQuantity)> configuredInventoryTotals)
    {
        if (requestedItems == null)
        {
            return false;
        }

        var expectedQuantities = BuildExpectedMapDeviceQuantities(configuredInventoryTotals, requestedItems);
        var detectedQuantities = GetVisibleMapDeviceQuantities();
        if (detectedQuantities.Count != expectedQuantities.Count)
        {
            LogMapDeviceVerificationMismatch(requestedItems, detectedQuantities, configuredInventoryTotals, "count");
            return false;
        }

        foreach (var (metadata, expectedQuantity) in expectedQuantities)
        {
            if (!detectedQuantities.TryGetValue(metadata, out var detectedQuantity) || detectedQuantity != expectedQuantity)
            {
                LogMapDeviceVerificationMismatch(requestedItems, detectedQuantities, configuredInventoryTotals, $"metadata:{metadata}");
                return false;
            }
        }

        return true;
    }

    private void LogMapDeviceVerificationMismatch(
        IReadOnlyList<(string Label, string Metadata, bool IsMap)> requestedItems,
        IReadOnlyDictionary<string, int> detectedQuantities,
        IReadOnlyDictionary<string, (string Label, int ExpectedQuantity)> configuredInventoryTotals,
        string reason)
    {
        var requestedLabels = BuildRequestedMapDeviceLabels(requestedItems);
        var expectedQuantities = BuildExpectedMapDeviceQuantities(configuredInventoryTotals, requestedItems);
        var safeDetectedQuantities = detectedQuantities != null
            ? new Dictionary<string, int>(detectedQuantities, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missingCounts = BuildMissingMetadataCounts(expectedQuantities, safeDetectedQuantities);
        var extraCounts = BuildExtraMetadataCounts(expectedQuantities, safeDetectedQuantities);
        var inventoryQuantities = BuildVisibleInventoryQuantities(expectedQuantities.Keys.Concat(safeDetectedQuantities.Keys));

        LogAutomationDebug(
            "Map device verification mismatch." + Environment.NewLine +
            $"Reason: {reason}" + Environment.NewLine +
            $"Missing item(s): {DescribeMetadataCounts(missingCounts, requestedLabels, inventoryQuantities)}" + Environment.NewLine +
            $"Extra item(s): {DescribeMetadataCounts(extraCounts, requestedLabels, inventoryQuantities)}" + Environment.NewLine +
            $"Expected Map Device quantities: {DescribeMetadataCounts(expectedQuantities, requestedLabels, inventoryQuantities)}" + Environment.NewLine +
            $"Detected Map Device quantities: {DescribeMetadataCounts(safeDetectedQuantities, requestedLabels, inventoryQuantities)}" + Environment.NewLine +
            $"Requested items: {DescribeRequestedMapDeviceItems(requestedItems)}" + Environment.NewLine +
            $"Detected items: {DescribeMetadataList(GetVisibleMapDeviceItemMetadata())}");
    }

    private Dictionary<string, string> BuildRequestedMapDeviceLabels(IReadOnlyList<(string Label, string Metadata, bool IsMap)> requestedItems)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (requestedItems == null)
        {
            return labels;
        }

        foreach (var item in requestedItems)
        {
            if (string.IsNullOrWhiteSpace(item.Metadata) || string.IsNullOrWhiteSpace(item.Label) || labels.ContainsKey(item.Metadata))
            {
                continue;
            }

            labels[item.Metadata] = item.Label;
        }

        return labels;
    }

    private Dictionary<string, int> BuildVisibleInventoryQuantities(IEnumerable<string> metadataKeys)
    {
        var quantities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (metadataKeys == null)
        {
            return quantities;
        }

        foreach (var metadata in metadataKeys)
        {
            if (string.IsNullOrWhiteSpace(metadata) || quantities.ContainsKey(metadata))
            {
                continue;
            }

            quantities[metadata] = TryGetVisiblePlayerInventoryMatchingQuantity(metadata) ?? 0;
        }

        return quantities;
    }

    private static Dictionary<string, int> BuildExpectedMapDeviceQuantities(
        IReadOnlyDictionary<string, (string Label, int ExpectedQuantity)> configuredInventoryTotals,
        IReadOnlyList<(string Label, string Metadata, bool IsMap)> requestedItems)
    {
        var quantities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (requestedItems == null)
        {
            return quantities;
        }

        foreach (var requestedItem in requestedItems)
        {
            if (string.IsNullOrWhiteSpace(requestedItem.Metadata))
            {
                continue;
            }

            if (requestedItem.IsMap)
            {
                quantities[requestedItem.Metadata] = 1;
                continue;
            }

            if (configuredInventoryTotals != null && configuredInventoryTotals.TryGetValue(requestedItem.Metadata, out var configured))
            {
                quantities[requestedItem.Metadata] = configured.ExpectedQuantity;
                continue;
            }

            quantities[requestedItem.Metadata] = 1;
        }

        return quantities;
    }

    private static Dictionary<string, int> BuildRequestedMapDeviceCounts(IReadOnlyList<(string Label, string Metadata, bool IsMap)> requestedItems)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (requestedItems == null)
        {
            return counts;
        }

        foreach (var item in requestedItems)
        {
            if (string.IsNullOrWhiteSpace(item.Metadata))
            {
                continue;
            }

            counts[item.Metadata] = counts.TryGetValue(item.Metadata, out var count) ? count + 1 : 1;
        }

        return counts;
    }

    private static Dictionary<string, int> BuildMetadataCounts(IReadOnlyList<string> metadataItems)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (metadataItems == null)
        {
            return counts;
        }

        foreach (var metadata in metadataItems)
        {
            if (string.IsNullOrWhiteSpace(metadata))
            {
                continue;
            }

            counts[metadata] = counts.TryGetValue(metadata, out var count) ? count + 1 : 1;
        }

        return counts;
    }

    private static Dictionary<string, int> BuildMissingMetadataCounts(IReadOnlyDictionary<string, int> requestedCounts, IReadOnlyDictionary<string, int> detectedCounts)
    {
        var missingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (requestedCounts == null)
        {
            return missingCounts;
        }

        foreach (var (metadata, requestedCount) in requestedCounts)
        {
            var detectedCount = detectedCounts != null && detectedCounts.TryGetValue(metadata, out var foundCount) ? foundCount : 0;
            var missingCount = requestedCount - detectedCount;
            if (missingCount > 0)
            {
                missingCounts[metadata] = missingCount;
            }
        }

        return missingCounts;
    }

    private static Dictionary<string, int> BuildExtraMetadataCounts(IReadOnlyDictionary<string, int> requestedCounts, IReadOnlyDictionary<string, int> detectedCounts)
    {
        var extraCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (detectedCounts == null)
        {
            return extraCounts;
        }

        foreach (var (metadata, detectedCount) in detectedCounts)
        {
            var requestedCount = requestedCounts != null && requestedCounts.TryGetValue(metadata, out var expectedCount) ? expectedCount : 0;
            var extraCount = detectedCount - requestedCount;
            if (extraCount > 0)
            {
                extraCounts[metadata] = extraCount;
            }
        }

        return extraCounts;
    }

    private static string DescribeRequestedMapDeviceItems(IReadOnlyList<(string Label, string Metadata, bool IsMap)> requestedItems)
    {
        if (requestedItems == null || requestedItems.Count <= 0)
        {
            return string.Empty;
        }

        return string.Join(", ", requestedItems.Select(item => $"{item.Label}:{item.Metadata}"));
    }

    private static string DescribeMetadataList(IReadOnlyList<string> metadataItems)
    {
        if (metadataItems == null || metadataItems.Count <= 0)
        {
            return string.Empty;
        }

        return string.Join(", ", metadataItems);
    }

    private static string DescribeMetadataCounts(
        IReadOnlyDictionary<string, int> metadataCounts,
        IReadOnlyDictionary<string, string> metadataLabels = null,
        IReadOnlyDictionary<string, int> inventoryQuantities = null)
    {
        if (metadataCounts == null || metadataCounts.Count <= 0)
        {
            return "none";
        }

        return string.Join(", ", metadataCounts.Select(x =>
        {
            var labelText = metadataLabels != null && metadataLabels.TryGetValue(x.Key, out var label) && !string.IsNullOrWhiteSpace(label)
                ? $"{label} ({x.Key})"
                : x.Key;
            var quantityText = inventoryQuantities != null && inventoryQuantities.TryGetValue(x.Key, out var quantity)
                ? $", qty={quantity}"
                : string.Empty;
            return $"{labelText} x{x.Value}{quantityText}";
        }));
    }

    private List<string> GetVisibleMapDeviceItemMetadata()
    {
        var mapDeviceWindow = GameController?.IngameState?.IngameUi?.MapDeviceWindow;
        if (mapDeviceWindow?.IsVisible != true)
        {
            return [];
        }

        try
        {
            var itemsProperty = mapDeviceWindow.GetType().GetProperty("Items");
            if (itemsProperty?.GetValue(mapDeviceWindow) is IEnumerable reflectedItems)
            {
                var reflectedMetadata = new List<string>();
                var visitedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
                var visitedIdentityKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var reflectedItem in reflectedItems)
                {
                    CollectMapDeviceItemMetadata(reflectedItem, reflectedMetadata, visitedObjects, visitedIdentityKeys);
                }

                if (reflectedMetadata.Count > 0)
                {
                    return reflectedMetadata;
                }
            }
        }
        catch
        {
        }

        return GetVisibleMapDeviceItems()
            .Where(item => item?.Item != null && !string.IsNullOrWhiteSpace(item.Item.Metadata))
            .Select(item => item.Item.Metadata)
            .ToList();
    }

    private Dictionary<string, int> GetVisibleMapDeviceQuantities()
    {
        var mapDeviceWindow = GameController?.IngameState?.IngameUi?.MapDeviceWindow;
        if (mapDeviceWindow?.IsVisible != true)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var itemsProperty = mapDeviceWindow.GetType().GetProperty("Items");
            if (itemsProperty?.GetValue(mapDeviceWindow) is IEnumerable reflectedItems)
            {
                var reflectedQuantities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var visitedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
                var visitedIdentityKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var reflectedItem in reflectedItems)
                {
                    CollectMapDeviceItemQuantities(reflectedItem, reflectedQuantities, visitedObjects, visitedIdentityKeys);
                }

                if (reflectedQuantities.Count > 0)
                {
                    return reflectedQuantities;
                }
            }
        }
        catch
        {
        }

        return GetVisibleMapDeviceItems()
            .Where(item => item?.Item != null && !string.IsNullOrWhiteSpace(item.Item.Metadata))
            .GroupBy(item => item.Item.Metadata, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(item => Math.Max(1, item.Item.GetComponent<ExileCore.PoEMemory.Components.Stack>()?.Size ?? 1)),
                StringComparer.OrdinalIgnoreCase);
    }

    private static void CollectMapDeviceItemQuantities(
        object itemObject,
        IDictionary<string, int> quantityBuffer,
        ISet<object> visitedObjects,
        ISet<string> visitedIdentityKeys)
    {
        if (itemObject == null || quantityBuffer == null || visitedObjects == null || visitedIdentityKeys == null)
        {
            return;
        }

        if (itemObject is NormalInventoryItem inventoryItem)
        {
            var identityKey = GetMapDeviceItemIdentityKey(inventoryItem.Item) ?? GetMapDeviceItemIdentityKey(inventoryItem);
            if (!MarkMapDeviceItemVisited(identityKey, inventoryItem, visitedIdentityKeys, visitedObjects))
            {
                return;
            }

            AddMapDeviceItemQuantity(
                quantityBuffer,
                inventoryItem.Item?.Metadata,
                Math.Max(1, inventoryItem.Item?.GetComponent<ExileCore.PoEMemory.Components.Stack>()?.Size ?? 1));
            return;
        }

        if (itemObject is Entity entity)
        {
            if (!MarkMapDeviceItemVisited(GetMapDeviceItemIdentityKey(entity), entity, visitedIdentityKeys, visitedObjects))
            {
                return;
            }

            AddMapDeviceItemQuantity(
                quantityBuffer,
                entity.Metadata,
                Math.Max(1, entity.GetComponent<ExileCore.PoEMemory.Components.Stack>()?.Size ?? 1));
            return;
        }

        if (itemObject is string)
        {
            return;
        }

        if (itemObject is IEnumerable enumerable)
        {
            foreach (var child in enumerable)
            {
                CollectMapDeviceItemQuantities(child, quantityBuffer, visitedObjects, visitedIdentityKeys);
            }

            return;
        }

        var itemType = itemObject.GetType();
        var innerItem = itemType.GetProperty("item")?.GetValue(itemObject) ??
                        itemType.GetProperty("Item")?.GetValue(itemObject);
        if (!ReferenceEquals(innerItem, itemObject))
        {
            CollectMapDeviceItemQuantities(innerItem, quantityBuffer, visitedObjects, visitedIdentityKeys);
            return;
        }

        if (itemType.GetProperty("Metadata")?.GetValue(itemObject) is string metadata &&
            !string.IsNullOrWhiteSpace(metadata) &&
            MarkMapDeviceItemVisited(GetMapDeviceItemIdentityKey(itemObject), itemObject, visitedIdentityKeys, visitedObjects))
        {
            AddMapDeviceItemQuantity(quantityBuffer, metadata, 1);
        }
    }

    private static void AddMapDeviceItemQuantity(IDictionary<string, int> quantityBuffer, string metadata, int quantity)
    {
        if (quantityBuffer == null || string.IsNullOrWhiteSpace(metadata) || quantity <= 0)
        {
            return;
        }

        quantityBuffer[metadata] = quantityBuffer.TryGetValue(metadata, out var existingQuantity)
            ? existingQuantity + quantity
            : quantity;
    }

    private static void CollectMapDeviceItemMetadata(object itemObject, ICollection<string> metadataBuffer, ISet<object> visitedObjects, ISet<string> visitedIdentityKeys)
    {
        if (itemObject == null || metadataBuffer == null || visitedObjects == null || visitedIdentityKeys == null)
        {
            return;
        }

        if (itemObject is NormalInventoryItem inventoryItem)
        {
            var identityKey = GetMapDeviceItemIdentityKey(inventoryItem.Item) ?? GetMapDeviceItemIdentityKey(inventoryItem);
            if (!MarkMapDeviceItemVisited(identityKey, inventoryItem, visitedIdentityKeys, visitedObjects))
            {
                return;
            }

            var inventoryMetadata = inventoryItem.Item?.Metadata;
            if (!string.IsNullOrWhiteSpace(inventoryMetadata))
            {
                metadataBuffer.Add(inventoryMetadata);
            }

            return;
        }

        if (itemObject is Entity entity)
        {
            if (!MarkMapDeviceItemVisited(GetMapDeviceItemIdentityKey(entity), entity, visitedIdentityKeys, visitedObjects))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(entity.Metadata))
            {
                metadataBuffer.Add(entity.Metadata);
            }

            return;
        }

        if (itemObject is string)
        {
            return;
        }

        if (itemObject is IEnumerable enumerable)
        {
            foreach (var child in enumerable)
            {
                CollectMapDeviceItemMetadata(child, metadataBuffer, visitedObjects, visitedIdentityKeys);
            }

            return;
        }

        var itemType = itemObject.GetType();
        var innerItem = itemType.GetProperty("item")?.GetValue(itemObject) ??
                        itemType.GetProperty("Item")?.GetValue(itemObject);
        if (!ReferenceEquals(innerItem, itemObject))
        {
            CollectMapDeviceItemMetadata(innerItem, metadataBuffer, visitedObjects, visitedIdentityKeys);
            return;
        }

        if (itemType.GetProperty("Metadata")?.GetValue(itemObject) is string metadata &&
            !string.IsNullOrWhiteSpace(metadata) &&
            MarkMapDeviceItemVisited(GetMapDeviceItemIdentityKey(itemObject), itemObject, visitedIdentityKeys, visitedObjects))
        {
            metadataBuffer.Add(metadata);
        }
    }

    private static bool MarkMapDeviceItemVisited(string identityKey, object itemObject, ISet<string> visitedIdentityKeys, ISet<object> visitedObjects)
    {
        if (!string.IsNullOrWhiteSpace(identityKey))
        {
            return visitedIdentityKeys.Add(identityKey);
        }

        return visitedObjects.Add(itemObject);
    }

    private static string GetMapDeviceItemIdentityKey(object itemObject)
    {
        if (itemObject == null)
        {
            return null;
        }

        if (itemObject is Entity entity)
        {
            if (entity.Address != 0)
            {
                return $"entity:{entity.Address:X}";
            }

            if (entity.Id != 0)
            {
                return $"entity-id:{entity.Id}";
            }
        }

        var itemType = itemObject.GetType();
        if (itemType.GetProperty("Address")?.GetValue(itemObject) is long longAddress && longAddress != 0)
        {
            return $"addr:{longAddress:X}";
        }

        if (itemType.GetProperty("Address")?.GetValue(itemObject) is ulong ulongAddress && ulongAddress != 0)
        {
            return $"addr:{ulongAddress:X}";
        }

        if (itemType.GetProperty("Id")?.GetValue(itemObject) is int intId && intId != 0)
        {
            return $"id:{intId}";
        }

        if (itemType.GetProperty("Id")?.GetValue(itemObject) is long longId && longId != 0)
        {
            return $"id:{longId}";
        }

        return null;
    }

    private List<NormalInventoryItem> GetVisibleMapDeviceItems()
    {
        var mapDeviceWindow = GameController?.IngameState?.IngameUi?.MapDeviceWindow;
        if (mapDeviceWindow?.IsVisible != true)
        {
            return [];
        }

        var mapDeviceSlots = mapDeviceWindow.GetChildAtIndex(0)?.GetChildAtIndex(1);
        if (mapDeviceSlots == null)
        {
            return [];
        }

        var items = new List<NormalInventoryItem>();
        for (var i = 1; i < mapDeviceSlots.ChildCount; i++)
        {
            var slot = mapDeviceSlots.GetChildAtIndex(i);
            var item = slot.AsObject<NormalInventoryItem>();
            if (item?.Item != null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private int GetVisibleMapDeviceMatchingQuantity(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return 0;
        }

        return GetVisibleMapDeviceQuantities().TryGetValue(metadata, out var quantity) ? quantity : 0;
    }

    private int CountMatchingMapDeviceItems(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return 0;
        }

        return GetVisibleMapDeviceItemMetadata().Count(itemMetadata =>
            string.Equals(itemMetadata, metadata, StringComparison.OrdinalIgnoreCase));
    }

    private void MoveCursorToMapDeviceActivateButton()
    {
        var activateButton = GameController?.IngameState?.IngameUi?.MapDeviceWindow?.ActivateButton;
        if (activateButton?.IsVisible != true)
        {
            throw new InvalidOperationException("Map Device Activate button is not visible.");
        }

        Input.SetCursorPos(activateButton.GetClientRect().Center);
        Input.MouseMove();
    }

    #endregion
}
