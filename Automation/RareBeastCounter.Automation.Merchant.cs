using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using SharpDX;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    #region Faustus merchant automation

    private const string FaustusHideoutMetadata = "Metadata/NPC/League/Kalguur/VillageFaustusHideout";
    private const int OfflineMerchantShopTabColumns = 12;
    private const int OfflineMerchantShopTabRows = 12;
    private static readonly int[] OfflineMerchantShopTabsPath = [2, 0, 0, 1, 1, 0, 0, 1, 0];
    private static readonly int[] OfflineMerchantShopTabTextPath = [0, 1];
    private static readonly int[] MerchantPopupEnteredPriceTextPath = [2, 0, 0];

    private async Task RunSellCapturedMonstersToFaustusAsync()
    {
        if (_isAutomationRunning)
        {
            RequestAutomationStop();
            return;
        }

        while ((Control.MouseButtons & MouseButtons.Left) != 0)
        {
            await Task.Delay(10);
        }

        BeginAutomationRun();

        try
        {
            ReleaseAutomationTriggerKeys();

            var listedCount = 0;
            var skippedNoPriceCount = 0;
            var consecutiveFailures = 0;

            if (!await EnsureFaustusMerchantPanelOpenAsync())
            {
                throw new InvalidOperationException("Could not open Faustus merchant panel.");
            }

            if (!await EnsureOfflineMerchantShopInventorySelectedAsync())
            {
                throw new InvalidOperationException("Could not switch Faustus to the Shop inventory.");
            }

            var configuredTabName = ResolveConfiguredFaustusShopTabName();
            if (string.IsNullOrWhiteSpace(configuredTabName))
            {
                throw new InvalidOperationException("Select a Faustus shop tab before listing itemized beasts.");
            }

            await SelectOfflineMerchantTabAsync(configuredTabName);

            while (true)
            {
                ThrowIfAutomationStopRequested();

                var merchantPanelWasVisible = GetOfflineMerchantPanel()?.IsVisible == true;

                if (!await EnsureFaustusMerchantPanelOpenAsync())
                {
                    throw new InvalidOperationException("Faustus merchant panel closed while listing itemized beasts.");
                }

                if (!merchantPanelWasVisible && !await EnsureOfflineMerchantShopInventorySelectedAsync())
                {
                    throw new InvalidOperationException("Could not keep Faustus on the Shop inventory while listing itemized beasts.");
                }

                if (!merchantPanelWasVisible)
                {
                    await SelectOfflineMerchantTabAsync(configuredTabName);
                }

                if (IsCurrentOfflineMerchantTabFull())
                {
                    throw new InvalidOperationException($"Faustus shop tab '{configuredTabName}' is full.");
                }

                if (!TryGetNextSellableCapturedMonsterInventoryItem(out var item, out var beastName, out var listingPriceChaos))
                {
                    skippedNoPriceCount = GetVisibleCapturedMonsterInventoryItems().Count;
                    break;
                }

                UpdateAutomationStatus($"Listing itemized beast {beastName} for {listingPriceChaos} chaos...");

                var previousCount = GetVisibleCapturedMonsterInventoryItems().Count;
                await CtrlClickInventoryItemAsync(item);

                if (!await WaitForMerchantPopupVisibilityAsync(expectedVisible: true, 1500))
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= 3)
                    {
                        throw new InvalidOperationException("Listing itemized beasts stalled while opening the Faustus price popup.");
                    }

                    await DelayAutomationAsync(AutomationTiming.FastPollDelayMs);
                    continue;
                }

                await EnterMerchantListingPriceAsync(listingPriceChaos);

                var currentCount = await WaitForCapturedMonsterInventoryItemCountToChangeAsync(previousCount);
                if (currentCount >= previousCount)
                {
                    await DelayForUiCheckAsync(250);
                    currentCount = GetVisibleCapturedMonsterInventoryItems().Count;
                }

                if (currentCount >= previousCount)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= 3)
                    {
                        throw new InvalidOperationException("Listing itemized beasts stalled while moving beasts into the Faustus shop tab.");
                    }

                    await DelayAutomationAsync(AutomationTiming.FastPollDelayMs);
                    continue;
                }

                listedCount += previousCount - currentCount;
                consecutiveFailures = 0;
                await DelayAutomationAsync(Settings.StashAutomation.ClickDelayMs.Value);
            }

            UpdateAutomationStatus(listedCount > 0
                ? skippedNoPriceCount > 0
                    ? $"Listed {listedCount} itemized beast{(listedCount == 1 ? string.Empty : "s")}. Skipped {skippedNoPriceCount} without price data."
                    : $"Listed {listedCount} itemized beast{(listedCount == 1 ? string.Empty : "s")}."
                : skippedNoPriceCount > 0
                    ? $"No sellable itemized beasts found. {skippedNoPriceCount} beast{(skippedNoPriceCount == 1 ? string.Empty : "s")} missing price data."
                    : "No itemized beasts were found in player inventory.", forceLog: true);
        }
        catch (OperationCanceledException)
        {
            UpdateAutomationStatus("Faustus beast listing cancelled.");
        }
        catch (Exception ex)
        {
            LogAutomationError("Faustus beast listing failed.", ex);
            UpdateAutomationStatus($"Faustus beast listing failed: {ex.Message}");
        }
        finally
        {
            EndAutomationRun();
        }
    }

    private void ReleaseAutomationTriggerKeys()
    {
        ReleaseAutomationModifierKeys();
        Input.KeyUp(Keys.Menu);
        Input.KeyUp(Keys.LMenu);
        Input.KeyUp(Keys.RMenu);
    }

    private bool TryGetNextSellableCapturedMonsterInventoryItem(out NormalInventoryItem item, out string beastName, out int listingPriceChaos)
    {
        item = null;
        beastName = null;
        listingPriceChaos = 0;

        foreach (var candidate in GetVisibleCapturedMonsterInventoryItems())
        {
            beastName = GetCapturedMonsterInventoryItemName(candidate);
            if (string.IsNullOrWhiteSpace(beastName))
            {
                continue;
            }

            if (!_beastPrices.TryGetValue(beastName, out var priceChaos) || priceChaos <= 0)
            {
                LogAutomationDebug($"Skipping itemized beast '{beastName}' because no beast price data is available.");
                continue;
            }

            item = candidate;
            listingPriceChaos = Math.Max(1, (int)Math.Ceiling(priceChaos));
            return true;
        }

        return false;
    }

    private static string GetCapturedMonsterInventoryItemName(NormalInventoryItem item)
    {
        if (item?.Item == null)
        {
            return null;
        }

        var capturedMonster = item.Item.GetComponent<CapturedMonster>();
        return TryGetPropertyValueAsString(capturedMonster?.MonsterVariety, "MonsterName")?.Trim()
               ?? TryGetPropertyValueAsString(capturedMonster?.MonsterVariety, "Name")?.Trim()
               ?? item.Item.GetComponent<Base>()?.Name?.Trim();
    }

    private async Task<bool> EnsureFaustusMerchantPanelOpenAsync()
    {
        if (GetOfflineMerchantPanel()?.IsVisible == true)
        {
            return true;
        }

        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(4000);
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();

            if (await WaitForBestiaryConditionAsync(() => GetOfflineMerchantPanel()?.IsVisible == true, 400, 25))
            {
                return true;
            }

            var faustus = await WaitForFaustusEntityAsync();
            if (faustus == null)
            {
                UpdateAutomationStatus("Could not find Faustus in the current area.", forceLog: true);
                return false;
            }

            var distance = GetPlayerDistanceToEntity(faustus);
            var statusMessage = distance.HasValue && distance.Value <= AutomationTiming.StashInteractionDistance
                ? "Opening Faustus shop..."
                : "Moving to Faustus...";

            if (!await CtrlAltClickWorldEntityAsync(faustus, statusMessage))
            {
                return false;
            }

            if (await WaitForBestiaryConditionAsync(() => GetOfflineMerchantPanel()?.IsVisible == true, 1000, 25))
            {
                return true;
            }

            await DelayAutomationAsync(AutomationTiming.StashOpenPollDelayMs);
        }

        return GetOfflineMerchantPanel()?.IsVisible == true;
    }

    private async Task<Entity> WaitForFaustusEntityAsync()
    {
        return await WaitForBestiaryEntityAsync(FindVisibleFaustusEntity, 2000);
    }

    private Entity FindVisibleFaustusEntity()
    {
        var entities = GameController?.EntityListWrapper?.Entities;
        var camera = GameController?.Game?.IngameState?.Camera;
        var window = GameController?.Window;
        if (entities == null || camera == null || window == null)
        {
            return null;
        }

        var windowRect = window.GetWindowRectangle();
        Entity closestFaustus = null;
        var closestDistance = float.MaxValue;

        foreach (var entity in entities)
        {
            if (entity?.IsValid != true || !string.Equals(entity.Metadata, FaustusHideoutMetadata, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var render = entity.GetComponent<Render>();
            if (render == null)
            {
                continue;
            }

            var screenPosition = camera.WorldToScreen(render.PosNum);
            if (!IsScreenPositionVisible(screenPosition, windowRect.Width, windowRect.Height))
            {
                continue;
            }

            var distance = GetPlayerDistanceToEntity(entity) ?? float.MaxValue;
            if (distance >= closestDistance)
            {
                continue;
            }

            closestDistance = distance;
            closestFaustus = entity;
        }

        return closestFaustus;
    }

    private async Task<bool> CtrlAltClickWorldEntityAsync(Entity entity, string statusMessage)
    {
        if (entity?.GetComponent<Render>() == null)
        {
            UpdateAutomationStatus("Could not find a clickable world position for Faustus.", forceLog: true);
            return false;
        }

        UpdateAutomationStatus(statusMessage);
        if (!await HoverWorldEntityAsync(entity, DescribeEntity(entity)))
        {
            UpdateAutomationStatus("Could not hover Faustus.", forceLog: true);
            return false;
        }

        await DelayAutomationAsync(AutomationTiming.UiClickPreDelayMs);
        Input.KeyDown(Keys.LControlKey);
        Input.KeyDown(Keys.LMenu);
        await DelayAutomationAsync(AutomationTiming.KeyTapDelayMs);
        Input.Click(MouseButtons.Left);
        Input.KeyUp(Keys.LMenu);
        Input.KeyUp(Keys.LControlKey);
        await DelayAutomationAsync(AutomationTiming.OpenStashPostClickDelayMs);
        return true;
    }

    private StashElement GetOfflineMerchantPanel() => GameController?.IngameState?.IngameUi?.OfflineMerchantPanel;

    private async Task<bool> EnsureOfflineMerchantShopInventorySelectedAsync()
    {
        var panel = GetOfflineMerchantPanel();
        if (panel?.IsVisible != true)
        {
            return false;
        }

        var inventoriesRoot = TryGetPropertyValue<Element>(panel, "Inventories") ?? panel;
        var shopInventory = FindOfflineMerchantInventorySwitch(inventoriesRoot, "Shop");
        if (shopInventory == null)
        {
            return panel.VisibleStash != null;
        }

        var tabButton = TryGetPropertyValue<Element>(shopInventory, "TabButton") ?? shopInventory;
        await ClickAtAsync(
            tabButton.GetClientRect().Center,
            holdCtrl: false,
            preClickDelayMs: AutomationTiming.UiClickPreDelayMs,
            postClickDelayMs: Math.Max(AutomationTiming.MinTabClickPostDelayMs, Settings.StashAutomation.TabSwitchDelayMs.Value));

        return await WaitForBestiaryConditionAsync(
            () => GetOfflineMerchantPanel()?.VisibleStash != null,
            1000,
            Math.Max(AutomationTiming.FastPollDelayMs, 25));
    }

    private static Element FindOfflineMerchantInventorySwitch(Element inventoriesRoot, string tabName)
    {
        if (inventoriesRoot == null || string.IsNullOrWhiteSpace(tabName))
        {
            return null;
        }

        return EnumerateDescendants(inventoriesRoot, includeSelf: true)
            .FirstOrDefault(element =>
                string.Equals(TryGetPropertyValueAsString(element, "TabName"), tabName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetElementTextRecursive(element, 2)?.Trim(), tabName, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveConfiguredFaustusShopTabName()
    {
        var panel = GetOfflineMerchantPanel();
        if (panel?.IsVisible != true)
        {
            LogAutomationDebug("ResolveConfiguredFaustusShopTabName aborted because Faustus merchant panel is not visible.");
            return null;
        }

        var configuredTabName = Settings?.MerchantAutomation?.SelectedFaustusShopTabName.Value?.Trim();
        var tabNames = GetAvailableOfflineMerchantShopTabNames(panel);
        if (!string.IsNullOrWhiteSpace(configuredTabName))
        {
            if (tabNames.Any(name => string.Equals(name, configuredTabName, StringComparison.OrdinalIgnoreCase)))
            {
                return configuredTabName;
            }

            LogAutomationDebug($"Configured Faustus shop tab '{configuredTabName}' was not found. Available tabs: {string.Join(", ", tabNames.Select((name, index) => $"{index}:{name}"))}");
        }
        else
        {
            LogAutomationDebug($"No configured Faustus shop tab. Available tabs: {string.Join(", ", tabNames.Select((name, index) => $"{index}:{name}"))}");
        }

        return null;
    }

    private static List<string> GetAvailableOfflineMerchantShopTabNames(StashElement panel)
    {
        var tabsRoot = TryGetElementByPathQuietly(panel, OfflineMerchantShopTabsPath);
        if (tabsRoot?.Children == null)
        {
            return [];
        }

        return tabsRoot.Children
            .Where(child => child?.Children?.Count > 0)
            .Select(GetOfflineMerchantShopTabName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetOfflineMerchantShopTabName(Element tabElement)
    {
        return TryGetChildFromIndicesQuietly(tabElement, OfflineMerchantShopTabTextPath)?.Text?.Trim()
               ?? TryGetPropertyValueAsString(TryGetChildFromIndicesQuietly(tabElement, OfflineMerchantShopTabTextPath), "Text")?.Trim();
    }

    private IList<NormalInventoryItem> GetVisibleOfflineMerchantInventoryItems()
    {
        return GetOfflineMerchantPanel()?.VisibleStash?.VisibleInventoryItems;
    }

    private int GetVisibleOfflineMerchantInventoryItemCount()
    {
        return GetVisibleOfflineMerchantInventoryItems()?.Count ?? 0;
    }

    private bool IsCurrentOfflineMerchantTabFull()
    {
        return !HasOfflineMerchantSpaceForItem(1, 1);
    }

    private bool HasOfflineMerchantSpaceForItem(int requiredWidth, int requiredHeight)
    {
        requiredWidth = Math.Max(1, requiredWidth);
        requiredHeight = Math.Max(1, requiredHeight);

        var occupied = GetOfflineMerchantOccupiedCells();
        if (occupied == null)
        {
            return false;
        }

        for (var x = 0; x <= OfflineMerchantShopTabColumns - requiredWidth; x++)
        {
            for (var y = 0; y <= OfflineMerchantShopTabRows - requiredHeight; y++)
            {
                var canFit = true;
                for (var checkX = x; checkX < x + requiredWidth && canFit; checkX++)
                {
                    for (var checkY = y; checkY < y + requiredHeight; checkY++)
                    {
                        if (occupied[checkX, checkY])
                        {
                            canFit = false;
                            break;
                        }
                    }
                }

                if (canFit)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool[,] GetOfflineMerchantOccupiedCells()
    {
        var items = GetVisibleOfflineMerchantInventoryItems();
        if (items == null)
        {
            return null;
        }

        var occupied = new bool[OfflineMerchantShopTabColumns, OfflineMerchantShopTabRows];
        foreach (var item in items)
        {
            if (item == null)
            {
                continue;
            }

            var startX = Math.Max(0, TryGetIntPropertyValue(item, "PosX") ?? 0);
            var startY = Math.Max(0, TryGetIntPropertyValue(item, "PosY") ?? 0);
            var itemInfo = TryGetPropertyValue<object>(item.Item, "ItemInfo");
            var width = Math.Max(1, TryGetIntPropertyValue(itemInfo, "Width") ?? 1);
            var height = Math.Max(1, TryGetIntPropertyValue(itemInfo, "Height") ?? 1);
            var endX = Math.Min(OfflineMerchantShopTabColumns, startX + width);
            var endY = Math.Min(OfflineMerchantShopTabRows, startY + height);

            for (var x = startX; x < endX; x++)
            {
                for (var y = startY; y < endY; y++)
                {
                    occupied[x, y] = true;
                }
            }
        }

        return occupied;
    }

    private static int? TryGetIntPropertyValue(object instance, string propertyName)
    {
        var valueText = TryGetPropertyValueAsString(instance, propertyName);
        return int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private async Task SelectOfflineMerchantTabAsync(string tabName)
    {
        var panel = GetOfflineMerchantPanel();
        if (panel?.IsVisible != true)
        {
            throw new InvalidOperationException("Faustus merchant panel is not open.");
        }

        if (string.IsNullOrWhiteSpace(tabName))
        {
            throw new InvalidOperationException("Select a valid Faustus shop tab before listing itemized beasts.");
        }

        var tabsRoot = TryGetElementByPathQuietly(panel, OfflineMerchantShopTabsPath);
        var tabElement = tabsRoot?.Children?
            .Where(child => child?.Children?.Count > 0)
            .FirstOrDefault(child => string.Equals(GetOfflineMerchantShopTabName(child), tabName, StringComparison.OrdinalIgnoreCase));
        if (tabElement == null)
        {
            throw new InvalidOperationException($"Could not find the Faustus shop tab '{tabName}'.");
        }

        var clickTarget = TryGetPropertyValue<Element>(tabElement, "TabButton") ?? tabElement;
        await ClickAtAsync(
            clickTarget.GetClientRect().Center,
            holdCtrl: false,
            preClickDelayMs: AutomationTiming.UiClickPreDelayMs,
            postClickDelayMs: Math.Max(AutomationTiming.MinTabClickPostDelayMs, Settings.StashAutomation.TabSwitchDelayMs.Value));
        await DelayAutomationAsync(Settings.StashAutomation.TabSwitchDelayMs.Value);
    }

    private bool IsMerchantPopupVisible() => GameController?.IngameState?.IngameUi?.PopUpWindow?.IsVisible == true;

    private async Task<bool> WaitForMerchantPopupVisibilityAsync(bool expectedVisible, int timeoutMs)
    {
        return await WaitForBestiaryConditionAsync(
            () => IsMerchantPopupVisible() == expectedVisible,
            timeoutMs,
            Math.Max(AutomationTiming.FastPollDelayMs, 10));
    }

    private async Task EnterMerchantListingPriceAsync(int priceChaos)
    {
        if (priceChaos <= 0)
        {
            throw new InvalidOperationException("Merchant listing price must be greater than zero.");
        }

        if (!await WaitForMerchantPopupVisibilityAsync(expectedVisible: true, 1000))
        {
            throw new InvalidOperationException("Timed out waiting for the Faustus price popup.");
        }

        ReleaseAutomationTriggerKeys();
        var expectedPriceText = priceChaos.ToString(CultureInfo.InvariantCulture);
        await CtrlTapKeyAsync(Keys.A, AutomationTiming.KeyTapDelayMs, AutomationTiming.KeyTapDelayMs);
        await TapKeyAsync(Keys.Back, AutomationTiming.KeyTapDelayMs, AutomationTiming.FastPollDelayMs);
        await TypeDigitTextAsync(expectedPriceText);

        var observedPriceText = await WaitForMerchantPopupEnteredPriceTextAsync(expectedPriceText, 500);
        if (!IsMerchantPopupPriceTextMatch(observedPriceText, expectedPriceText))
        {
            throw new InvalidOperationException($"Merchant price text mismatch. Expected '{expectedPriceText}', observed '{observedPriceText ?? "<null>"}'.");
        }

        await TapKeyAsync(Keys.Enter, AutomationTiming.KeyTapDelayMs, 0);

        if (!await WaitForMerchantPopupVisibilityAsync(expectedVisible: false, 1000))
        {
            throw new InvalidOperationException("Timed out closing the Faustus price popup.");
        }
    }

    private string GetMerchantPopupEnteredPriceText()
    {
        var textElement = TryGetChildFromIndicesQuietly(
            GameController?.IngameState?.IngameUi?.PopUpWindow,
            MerchantPopupEnteredPriceTextPath);
        if (textElement == null)
        {
            return null;
        }

        return textElement.Text?.Trim()
               ?? TryGetPropertyValueAsString(textElement, "Text")?.Trim()
               ?? GetElementTextRecursive(textElement, 1)?.Trim();
    }

    private async Task<string> WaitForMerchantPopupEnteredPriceTextAsync(string expectedText, int timeoutMs)
    {
        var startedAt = DateTime.UtcNow;
        var adjustedTimeoutMs = GetAutomationTimeoutMs(timeoutMs);
        string lastObservedText = null;

        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < adjustedTimeoutMs)
        {
            ThrowIfAutomationStopRequested();

            lastObservedText = GetMerchantPopupEnteredPriceText();
            if (IsMerchantPopupPriceTextMatch(lastObservedText, expectedText))
            {
                return lastObservedText;
            }

            await DelayAutomationAsync(AutomationTiming.FastPollDelayMs);
        }

        return lastObservedText;
    }

    private static bool IsMerchantPopupPriceTextMatch(string observedText, string expectedText)
    {
        return string.Equals(NormalizeMerchantPopupPriceText(observedText), NormalizeMerchantPopupPriceText(expectedText), StringComparison.Ordinal);
    }

    private static string NormalizeMerchantPopupPriceText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return new string(text.Where(char.IsDigit).ToArray());
    }

    private void DrawFaustusShopTabSelectorPanel(MerchantAutomationSettings automation)
    {
        var panel = GetOfflineMerchantPanel();
        if (panel?.IsVisible != true)
        {
            var selectedTabNameText = automation?.SelectedFaustusShopTabName.Value?.Trim();
            ImGui.Text("Faustus shop tab");
            ImGui.SameLine();
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(selectedTabNameText) ? "Select tab" : selectedTabNameText);
            ImGui.TextDisabled("Open Faustus shop to change the selected shop tab.");
            return;
        }

        var tabNames = GetAvailableOfflineMerchantShopTabNames(panel);
        if (tabNames.Count <= 0)
        {
            ImGui.TextDisabled("No Faustus shop tabs available.");
            return;
        }

        var selectedTabName = automation?.SelectedFaustusShopTabName;
        var previewText = string.IsNullOrWhiteSpace(selectedTabName?.Value) ? "Select tab" : selectedTabName.Value;
        ImGui.Text("Faustus shop tab");
        ImGui.SameLine();

        if (ImGui.BeginCombo("##RareBeastCounterFaustusShopTab", previewText))
        {
            for (var i = 0; i < tabNames.Count; i++)
            {
                var tabName = tabNames[i];
                var isSelected = string.Equals(selectedTabName?.Value, tabName, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{i}: {tabName}", isSelected))
                {
                    selectedTabName.Value = tabName;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }
    }

    #endregion
}
