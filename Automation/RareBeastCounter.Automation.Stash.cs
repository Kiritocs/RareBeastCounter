using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared.Enums;
using Vector2 = System.Numerics.Vector2;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    #region Stash interaction and target metadata

    private bool IsAutomationStashVisible()
    {
        return GameController?.IngameState?.IngameUi?.StashElement?.IsVisible == true;
    }

    private async Task<bool> EnsureStashOpenForAutomationAsync()
    {
        ThrowIfAutomationStopRequested();

        var timing = AutomationTiming;
        if (IsAutomationStashVisible())
        {
            return true;
        }

        while (!IsAutomationStashVisible())
        {
            ThrowIfAutomationStopRequested();
            var stashEntity = FindNearestVisibleStashEntity();
            if (stashEntity == null)
            {
                UpdateAutomationStatus("No nearby visible stash found.");
                return false;
            }

            var distance = GetPlayerDistanceToEntity(stashEntity);
            var statusMessage = distance.HasValue && distance.Value <= timing.StashInteractionDistance
                ? "Opening stash..."
                : "Moving to stash...";

            if (!await ClickStashEntityAsync(stashEntity, statusMessage))
            {
                return false;
            }

            if (IsAutomationStashVisible())
            {
                return true;
            }

            await DelayAutomationAsync(timing.StashOpenPollDelayMs);
        }

        return true;
    }

    private async Task<bool> ClickStashEntityAsync(Entity stashEntity, string statusMessage)
    {
        if (stashEntity?.GetComponent<Render>() == null)
        {
            UpdateAutomationStatus("Could not find a clickable stash position.");
            return false;
        }

        UpdateAutomationStatus(statusMessage);
        var timing = AutomationTiming;
        if (!await HoverWorldEntityAsync(stashEntity, "stash"))
        {
            UpdateAutomationStatus("Could not hover the stash.");
            return false;
        }

        await ClickCurrentCursorAsync(
            holdCtrl: false,
            preClickDelayMs: timing.UiClickPreDelayMs,
            postClickDelayMs: timing.OpenStashPostClickDelayMs);
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
        Entity nearestStash = null;
        var nearestDistanceSquared = float.MaxValue;

        foreach (var entity in entities)
        {
            if (entity?.IsValid != true || entity.Type != EntityType.Stash)
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

            var distanceSquared = Vector2.DistanceSquared(playerGridPos, positioned.GridPosNum);
            if (distanceSquared >= nearestDistanceSquared)
            {
                continue;
            }

            nearestDistanceSquared = distanceSquared;
            nearestStash = entity;
        }

        return nearestStash;
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

    #endregion
    #region Quantity wait helpers

    private int GetQuantitySettleTimeoutMs(int extraBaseDelayMs)
    {
        var automation = Settings.StashAutomation;
        var timing = AutomationTiming;
        var normalizedExtraBaseDelayMs = Math.Max(0, extraBaseDelayMs);

        return GetAutomationTimeoutMs(Math.Max(
            timing.QuantityChangeBaseDelayMs + normalizedExtraBaseDelayMs,
            automation.ClickDelayMs.Value + timing.QuantityChangeBaseDelayMs + normalizedExtraBaseDelayMs));
    }

    private async Task<int> WaitForMapStashPageQuantityToSettleAsync(string metadata, int previousQuantity)
    {
        return await WaitForQuantityToSettleAsync(metadata, previousQuantity, () =>
        {
            var visibleItems = GetVisibleMapStashPageItems();
            return visibleItems == null ? (int?)null : CountMatchingMapStashPageItems(visibleItems, metadata);
        }, MapTransferExtraConfirmationDelayMs);
    }

    private async Task<int> WaitForQuantityToSettleAsync(string metadata, int previousQuantity, Func<int?> quantityProvider, int extraBaseDelayMs = 0)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return previousQuantity;
        }

        var timing = AutomationTiming;
        var startedAt = DateTime.UtcNow;
        var pollDelayMs = timing.FastPollDelayMs;
        var timeoutMs = GetQuantitySettleTimeoutMs(extraBaseDelayMs);
        var stableWindowMs = Math.Max(QuantitySettleStableWindowMs, pollDelayMs * 3);
        var changedQuantity = previousQuantity;
        var hasObservedChange = false;
        DateTime? lastChangeAtUtc = null;

        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();
            var now = DateTime.UtcNow;
            var currentQuantity = quantityProvider();
            if (!currentQuantity.HasValue)
            {
                await DelayAutomationAsync(pollDelayMs);
                continue;
            }

            if (!hasObservedChange)
            {
                if (currentQuantity.Value == previousQuantity)
                {
                    await DelayAutomationAsync(pollDelayMs);
                    continue;
                }

                changedQuantity = currentQuantity.Value;
                hasObservedChange = true;
                lastChangeAtUtc = now;
                await DelayAutomationAsync(pollDelayMs);
                continue;
            }

            if (currentQuantity.Value == changedQuantity)
            {
                if (lastChangeAtUtc.HasValue && (now - lastChangeAtUtc.Value).TotalMilliseconds >= stableWindowMs)
                {
                    return currentQuantity.Value;
                }

                await DelayAutomationAsync(pollDelayMs);
                continue;
            }

            changedQuantity = currentQuantity.Value;
            lastChangeAtUtc = now;
            await DelayAutomationAsync(pollDelayMs);
        }

        return hasObservedChange ? changedQuantity : previousQuantity;
    }

    private async Task<int?> WaitForPlayerInventoryQuantityToSettleAsync(string metadata, int? previousQuantity, int extraBaseDelayMs = 0)
    {
        if (!previousQuantity.HasValue)
        {
            return null;
        }

        return await WaitForQuantityToSettleAsync(metadata, previousQuantity.Value, () => TryGetVisiblePlayerInventoryMatchingQuantity(metadata), extraBaseDelayMs);
    }

    private async Task<int> WaitForPlayerInventorySlotFillToSettleAsync(IReadOnlyList<(int X, int Y)> expectedSlots, int previousFilledCount, int extraBaseDelayMs = 0)
    {
        if (expectedSlots == null || expectedSlots.Count <= 0)
        {
            return previousFilledCount;
        }

        return await WaitForQuantityToSettleAsync("player-inventory-slot-fill", previousFilledCount, () => CountOccupiedPlayerInventoryCells(expectedSlots), extraBaseDelayMs);
    }

    #endregion
}
