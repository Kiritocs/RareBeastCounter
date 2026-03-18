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
    private static readonly int[] FragmentStashScarabTabPath = [41, 2, 0, 0, 1, 1, 2, 0, 5, 0, 1, 0];
    private static readonly string[] FragmentStashScarabTabTexts = ["Scarabs", "Scarab"];
    private string _lastAutomationStatusMessage;
    private bool _isAutomationRunning;
    private int _lastAutomationFragmentScarabTabIndex = -1;

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

    private async Task RunStashAutomationAsync()
    {
        if (_isAutomationRunning)
        {
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
        _lastAutomationFragmentScarabTabIndex = -1;
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
        catch (Exception ex)
        {
            UpdateAutomationStatus($"Restock failed: {ex.Message}");
        }
        finally
        {
            _isAutomationRunning = false;
            _lastAutomationFragmentScarabTabIndex = -1;
            Input.KeyUp(Keys.ControlKey);
            Input.KeyUp(Keys.LControlKey);
        }
    }

    private async Task RunStashAutomationFromHotkeyAsync()
    {
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

    private async Task<int> RestockConfiguredTargetAsync(string label, StashAutomationTargetSettings target)
    {
        if (!target.Enabled.Value || target.Quantity.Value <= 0)
        {
            return 0;
        }

        UpdateAutomationStatus($"Loading {label}...");

        var tabIndex = ResolveConfiguredTabIndex(target);
        await SelectStashTabAsync(tabIndex);
        await EnsureFragmentStashScarabTabSelectedAsync();
        await DelayAutomationAsync(Settings.StashAutomation.TabSwitchDelayMs.Value);

        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        var visibleStash = stash?.VisibleStash;
        var visibleItems = visibleStash?.VisibleInventoryItems;
        if (visibleItems == null)
        {
            throw new InvalidOperationException($"No visible stash items found for {label}.");
        }

        var sourceItem = FindConfiguredSourceItem(visibleItems, target);
        if (sourceItem?.Item == null)
        {
            throw new InvalidOperationException($"No source item found for {label}.");
        }

        var sourceMetadata = sourceItem.Item.Metadata;
        if (string.IsNullOrWhiteSpace(sourceMetadata))
        {
            throw new InvalidOperationException($"Source item metadata is unavailable for {label}.");
        }

        var availableInStash = CountMatchingItemQuantity(visibleItems, sourceMetadata);
        if (availableInStash <= 0)
        {
            throw new InvalidOperationException($"No {label} were found in the visible stash tab.");
        }

        UpdateAutomationStatus($"Loading {label}: 0/{target.Quantity.Value} (available {availableInStash})");

        var transferred = 0;

        while (transferred < target.Quantity.Value)
        {
            visibleItems = stash?.VisibleStash?.VisibleInventoryItems;
            var nextItem = FindNextMatchingStashItem(visibleItems, sourceMetadata);
            if (nextItem?.Item == null)
            {
                break;
            }

            var availableBeforeTransfer = CountMatchingItemQuantity(visibleItems, sourceMetadata);
            await CtrlClickInventoryItemAsync(nextItem);
            var availableAfterTransfer = await WaitForMatchingItemQuantityToChangeAsync(sourceMetadata, availableBeforeTransfer);
            var movedAmount = Math.Max(0, availableBeforeTransfer - availableAfterTransfer);
            if (movedAmount <= 0)
            {
                break;
            }

            transferred += movedAmount;
            UpdateAutomationStatus($"Loading {label}: {Math.Min(transferred, target.Quantity.Value)}/{target.Quantity.Value} (available {availableInStash})");
            await DelayAutomationAsync(Settings.StashAutomation.ClickDelayMs.Value);
        }

        if (transferred <= 0)
        {
            throw new InvalidOperationException($"No {label} were transferred.");
        }

        return transferred;
    }

    private async Task<bool> EnsureStashOpenForAutomationAsync()
    {
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        if (stash?.IsVisible == true)
        {
            return true;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var stashEntity = FindNearestVisibleStashEntity();
            if (stashEntity == null)
            {
                UpdateAutomationStatus("No nearby visible stash found.");
                return false;
            }

            var render = stashEntity.GetComponent<Render>();
            if (render == null)
            {
                UpdateAutomationStatus("Could not find a clickable stash position.");
                return false;
            }

            UpdateAutomationStatus(attempt == 0 ? "Moving to stash..." : "Opening stash...");
            await ClickAtAsync(
                GameController.IngameState.Camera.WorldToScreen(render.PosNum),
                holdCtrl: false,
                preClickDelayMs: 35,
                postClickDelayMs: 150);

            if (await WaitForStashOpenAsync(2500))
            {
                return true;
            }
        }

        UpdateAutomationStatus("Could not open stash.");
        return false;
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
            .OrderBy(x => DistanceSquared(playerGridPos, x.Positioned.GridPosNum))
            .Select(x => x.Entity)
            .FirstOrDefault();
    }

    private static bool IsScreenPositionVisible(Vector2 position, float width, float height)
    {
        return !float.IsNaN(position.X) && !float.IsNaN(position.Y) &&
               !float.IsInfinity(position.X) && !float.IsInfinity(position.Y) &&
               position.X >= 0 && position.Y >= 0 && position.X <= width && position.Y <= height;
    }

    private static float DistanceSquared(Vector2 a, Vector2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static (string Label, string IdSuffix, StashAutomationTargetSettings Target)[] GetAutomationTargets(StashAutomationSettings automation)
    {
        return
        [
            (GetAutomationTargetLabel(automation.Target1, "Target 1"), "target1", automation.Target1),
            (GetAutomationTargetLabel(automation.Target2, "Target 2"), "target2", automation.Target2),
            (GetAutomationTargetLabel(automation.Target3, "Target 3"), "target3", automation.Target3)
        ];
    }

    private static string GetAutomationTargetLabel(StashAutomationTargetSettings target, string fallbackLabel)
    {
        var configuredItemName = target?.ItemName.Value?.Trim();
        return string.IsNullOrWhiteSpace(configuredItemName) ? fallbackLabel : configuredItemName;
    }

    private async Task DelayAutomationAsync(int baseDelayMs)
    {
        var adjustedDelayMs = GetAutomationDelayMs(baseDelayMs);
        if (adjustedDelayMs > 0)
        {
            await Task.Delay(adjustedDelayMs);
        }
    }

    private int GetAutomationDelayMs(int baseDelayMs)
    {
        if (baseDelayMs <= 0)
        {
            return 0;
        }

        var automation = Settings?.StashAutomation;
        if (automation == null)
        {
            return baseDelayMs;
        }

        return Math.Max(0, baseDelayMs + automation.FlatExtraDelayMs.Value);
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

    private static NormalInventoryItem FindConfiguredSourceItem(IList<NormalInventoryItem> items, StashAutomationTargetSettings target)
    {
        var configuredTabName = target.SelectedTabName.Value?.Trim();
        if (string.IsNullOrWhiteSpace(configuredTabName))
        {
            return null;
        }

        var configuredItemName = target.ItemName.Value?.Trim();
        if (string.IsNullOrWhiteSpace(configuredItemName))
        {
            return null;
        }

        return FindStashItemByName(items, configuredItemName);
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

    private static NormalInventoryItem FindNextMatchingStashItem(
        IList<NormalInventoryItem> items,
        string metadata)
    {
        if (items == null)
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

        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationDelayMs(Math.Max(100, Settings.StashAutomation.ClickDelayMs.Value + 100));
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            var visibleItems = GameController?.IngameState?.IngameUi?.StashElement?.VisibleStash?.VisibleInventoryItems;
            var currentQuantity = CountMatchingItemQuantity(visibleItems, metadata);
            if (currentQuantity != previousQuantity)
            {
                return currentQuantity;
            }

            await DelayAutomationAsync(15);
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

        for (var attempt = 0; attempt < 3; attempt++)
        {
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

            var travelDistance = tabIndex - currentIndex;
            var key = travelDistance < 0 ? Keys.Left : Keys.Right;
            for (var i = 0; i < Math.Abs(travelDistance); i++)
            {
                Input.KeyDown(key);
                await DelayAutomationAsync(1);
                Input.KeyUp(key);
                await DelayAutomationAsync(15);
            }

            await DelayAutomationAsync(Math.Max(20, Settings.StashAutomation.TabSwitchDelayMs.Value / 2));
        }

        await WaitForVisibleTabAsync(tabIndex);
    }

    private async Task EnsureFragmentStashScarabTabSelectedAsync()
    {
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
        var timeoutMs = GetAutomationDelayMs(Math.Max(250, Settings.StashAutomation.TabSwitchDelayMs.Value + 250));
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            stash = GameController?.IngameState?.IngameUi?.StashElement;
            if (stash?.IsVisible != true || stash.VisibleStash?.InvType != InventoryType.FragmentStash)
            {
                _lastAutomationFragmentScarabTabIndex = -1;
                return;
            }

            var scarabTab = FindFragmentStashScarabTab(stash);
            if (scarabTab != null)
            {
                await ClickAtAsync(
                    scarabTab.GetClientRect().Center,
                    holdCtrl: false,
                    preClickDelayMs: 35,
                    postClickDelayMs: Math.Max(25, Settings.StashAutomation.TabSwitchDelayMs.Value));
                _lastAutomationFragmentScarabTabIndex = stash.IndexVisibleStash;
                return;
            }

            await DelayAutomationAsync(15);
        }
    }

    private static Element FindFragmentStashScarabTab(StashElement stash)
    {
        var scarabTab = stash.GetChildFromIndices(FragmentStashScarabTabPath);
        if (scarabTab != null)
        {
            return scarabTab;
        }

        return FindElementByText(stash, FragmentStashScarabTabTexts);
    }

    private static Element FindElementByText(Element root, IReadOnlyList<string> candidateTexts)
    {
        if (root == null || candidateTexts == null || candidateTexts.Count == 0)
        {
            return null;
        }

        var rootRect = root.GetClientRect();
        Element bestMatch = null;
        var bestPriority = int.MaxValue;
        var bestTop = float.MaxValue;
        var bestLeft = float.MaxValue;

        var stack = new Stack<Element>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == null)
            {
                continue;
            }

            var text = current.GetText(128)?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var priority = GetElementTextMatchPriority(text, candidateTexts);
                if (priority >= 0)
                {
                    var rect = current.GetClientRect();
                    if (rect.Width > 0 && rect.Height > 0 && rootRect.Intersects(rect) &&
                        (priority < bestPriority ||
                         priority == bestPriority && (rect.Top < bestTop || Math.Abs(rect.Top - bestTop) < 0.5f && rect.Left < bestLeft)))
                    {
                        bestMatch = current;
                        bestPriority = priority;
                        bestTop = rect.Top;
                        bestLeft = rect.Left;
                    }
                }
            }

            var children = current.Children;
            if (children == null)
            {
                continue;
            }

            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] != null)
                {
                    stack.Push(children[i]);
                }
            }
        }

        return bestMatch;
    }

    private static int GetElementTextMatchPriority(string text, IReadOnlyList<string> candidateTexts)
    {
        for (var i = 0; i < candidateTexts.Count; i++)
        {
            if (string.Equals(text, candidateTexts[i], StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        for (var i = 0; i < candidateTexts.Count; i++)
        {
            if (text.IndexOf(candidateTexts[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return candidateTexts.Count + i;
            }
        }

        return -1;
    }

    private async Task WaitForVisibleTabAsync(int tabIndex)
    {
        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationDelayMs(1000);
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            var stash = GameController?.IngameState?.IngameUi?.StashElement;
            if (stash?.IsVisible == true && stash.IndexVisibleStash == tabIndex && stash.VisibleStash != null)
            {
                return;
            }

            await DelayAutomationAsync(15);
        }

        throw new InvalidOperationException($"Timed out switching to stash tab {tabIndex}.");
    }

    private async Task<bool> WaitForStashOpenAsync(int timeoutMs)
    {
        var startedAt = DateTime.UtcNow;
        var adjustedTimeoutMs = GetAutomationDelayMs(timeoutMs);
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < adjustedTimeoutMs)
        {
            if (GameController?.IngameState?.IngameUi?.StashElement?.IsVisible == true)
            {
                return true;
            }

            await DelayAutomationAsync(30);
        }

        return false;
    }

    private async Task CtrlClickInventoryItemAsync(NormalInventoryItem item)
    {
        await ClickAtAsync(
            item.GetClientRect().Center,
            holdCtrl: true,
            preClickDelayMs: 20,
            postClickDelayMs: 5);
    }

    private async Task ClickAtAsync(SharpDX.Vector2 position, bool holdCtrl, int preClickDelayMs, int postClickDelayMs)
    {
        Input.SetCursorPos(position);
        Input.MouseMove();

        if (preClickDelayMs > 0)
        {
            await DelayAutomationAsync(preClickDelayMs);
        }

        if (holdCtrl)
        {
            Input.KeyDown(Keys.LControlKey);
        }

        Input.Click(MouseButtons.Left);

        if (holdCtrl)
        {
            Input.KeyUp(Keys.LControlKey);
        }

        if (postClickDelayMs > 0)
        {
            await DelayAutomationAsync(postClickDelayMs);
        }
    }

    private Task ClickAtAsync(Vector2 position, bool holdCtrl, int preClickDelayMs, int postClickDelayMs)
    {
        return ClickAtAsync(new SharpDX.Vector2(position.X, position.Y), holdCtrl, preClickDelayMs, postClickDelayMs);
    }

    private void UpdateAutomationStatus(string message)
    {
        if (string.Equals(_lastAutomationStatusMessage, message, StringComparison.Ordinal))
        {
            return;
        }

        _lastAutomationStatusMessage = message;
        if (Settings?.StashAutomation?.DebugLogging.Value == true)
        {
            LogMessage($"Automation: {message}");
        }
    }
}
