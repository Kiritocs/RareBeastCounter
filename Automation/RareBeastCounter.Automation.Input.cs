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
    #region Shared input and timing

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

    private void DrawBestiaryStashTabSelector(string label, string idSuffix, BestiaryAutomationSettings automation, IReadOnlyList<string> stashTabNames)
    {
        DrawBestiaryStashTabSelector(label, idSuffix, automation?.StashTabSelector, automation?.SelectedTabName, stashTabNames, "Select tab");
    }

    private void DrawBestiaryStashTabSelector(string label, string idSuffix, CustomNode _, TextNode selectedTabName, IReadOnlyList<string> stashTabNames, string defaultPreviewText)
    {
        var previewText = string.IsNullOrWhiteSpace(selectedTabName?.Value) ? defaultPreviewText : selectedTabName.Value;
        ImGui.Text($"{label} stash tab");
        ImGui.SameLine();

        if (ImGui.BeginCombo($"##RareBeastCounterBestiaryStashTab{idSuffix}", previewText))
        {
            if (!string.IsNullOrWhiteSpace(defaultPreviewText) && defaultPreviewText != "Select tab")
            {
                var useDefault = string.IsNullOrWhiteSpace(selectedTabName?.Value);
                if (ImGui.Selectable(defaultPreviewText, useDefault))
                {
                    selectedTabName.Value = string.Empty;
                }

                if (useDefault)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            for (var i = 0; i < stashTabNames.Count; i++)
            {
                var tabName = stashTabNames[i];
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
        return await WaitForQuantityToSettleAsync(metadata, previousQuantity, () =>
        {
            var visibleItems = GetVisibleStashItems();
            return visibleItems == null ? (int?)null : CountMatchingItemQuantity(visibleItems, metadata);
        });
    }

    private int? TryGetVisiblePlayerInventoryMatchingQuantity(string metadata)
    {
        var visibleItems = GetVisiblePlayerInventoryItems();
        return visibleItems == null ? (int?)null : CountMatchingItemQuantity(visibleItems, metadata);
    }

    private async Task<int?> WaitForPlayerInventoryQuantityToChangeAsync(string metadata, int? previousQuantity, int extraBaseDelayMs = 0)
    {
        if (!previousQuantity.HasValue)
        {
            return null;
        }

        return await WaitForQuantityToSettleAsync(metadata, previousQuantity.Value, () => TryGetVisiblePlayerInventoryMatchingQuantity(metadata), extraBaseDelayMs);
    }

    private int ResolveConfiguredTabIndex(StashAutomationTargetSettings target)
    {
        return ResolveConfiguredTabIndex(target?.SelectedTabName.Value, target?.ItemName.Value, "target");
    }

    private async Task WaitForTargetStashReadyAsync(StashAutomationTargetSettings target, int tabIndex)
    {
        var automation = Settings.StashAutomation;
        var timing = AutomationTiming;
        var expectedInventoryType = GetExpectedInventoryType(target);
        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(
            timing.VisibleTabTimeoutMs,
            Math.Max(1500, automation.TabSwitchDelayMs.Value)));

        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();

            var stash = GameController?.IngameState?.IngameUi?.StashElement;
            if (IsTargetStashReady(stash, tabIndex, expectedInventoryType))
            {
                return;
            }

            await DelayAutomationAsync(timing.FastPollDelayMs);
        }

        LogAutomationDebug($"WaitForTargetStashReadyAsync timed out. targetTab={tabIndex}, expectedType={(expectedInventoryType.HasValue ? expectedInventoryType.Value.ToString() : "any")}, stash={DescribeStash(GameController?.IngameState?.IngameUi?.StashElement)}");
        throw new InvalidOperationException($"Timed out loading stash tab {tabIndex}.");
    }

    private static InventoryType? GetExpectedInventoryType(StashAutomationTargetSettings target)
    {
        var selectedTabName = target?.SelectedTabName.Value?.Trim();
        if (TryGetConfiguredMapTier(target).HasValue || string.Equals(selectedTabName, "Maps", StringComparison.OrdinalIgnoreCase))
        {
            return InventoryType.MapStash;
        }

        if (string.Equals(selectedTabName, "Fragments", StringComparison.OrdinalIgnoreCase))
        {
            return InventoryType.FragmentStash;
        }

        return null;
    }

    private static bool IsTargetStashReady(StashElement stash, int tabIndex, InventoryType? expectedInventoryType)
    {
        if (stash?.IsVisible != true || stash.IndexVisibleStash != tabIndex || stash.VisibleStash == null)
        {
            return false;
        }

        if (stash.VisibleStash.InvType == InventoryType.InvalidInventory)
        {
            return false;
        }

        return !expectedInventoryType.HasValue || stash.VisibleStash.InvType == expectedInventoryType.Value;
    }

    private int ResolveBestiaryCapturedMonsterStashTabIndex(bool preferRedBeastTab)
    {
        if (preferRedBeastTab)
        {
            var redBeastTabIndex = ResolveConfiguredTabIndex(
                Settings?.BestiaryAutomation?.SelectedRedBeastTabName.Value,
                "Red beasts",
                "Bestiary automation red beast stash");
            if (redBeastTabIndex >= 0)
            {
                return redBeastTabIndex;
            }
        }

        return ResolveConfiguredTabIndex(
            Settings?.BestiaryAutomation?.SelectedTabName.Value,
            preferRedBeastTab ? "Itemized beasts (fallback)" : "Itemized beasts",
            preferRedBeastTab ? "Bestiary automation captured beast stash fallback" : "Bestiary automation stash");
    }

    private int ResolveConfiguredTabIndex(string configuredTabNameValue, string subjectLabel, string selectionContext)
    {
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        if (stash?.IsVisible != true)
        {
            LogAutomationDebug("ResolveConfiguredTabIndex aborted because stash is not visible.");
            return -1;
        }

        var stashTabNames = GetAvailableStashTabNames(stash);
        var configuredTabName = configuredTabNameValue?.Trim();
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

            LogAutomationDebug($"Configured tab '{configuredTabName}' for {selectionContext} was not found. Available tabs: {string.Join(", ", stashTabNames.Select((name, index) => $"{index}:{name}"))}");
        }

        if (string.IsNullOrWhiteSpace(configuredTabName))
        {
            LogAutomationDebug($"No configured stash tab name for {selectionContext} '{subjectLabel}'. Available tabs: {string.Join(", ", stashTabNames.Select((name, index) => $"{index}:{name}"))}");
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

    private bool IsVisibleStashTabReady(int tabIndex)
    {
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        return stash?.IsVisible == true && stash.IndexVisibleStash == tabIndex && stash.VisibleStash != null;
    }

    private async Task SelectStashTabAsync(int tabIndex)
    {
        var automation = Settings.StashAutomation;
        var timing = AutomationTiming;
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

        if (IsVisibleStashTabReady(tabIndex))
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
                LogAutomationDebug($"SelectStashTabAsync reached target tab index {tabIndex} after {step} steps. Waiting for stash contents to load.");
                await WaitForVisibleTabAsync(tabIndex);
                return;
            }

            var key = tabIndex < currentIndex ? Keys.Left : Keys.Right;
            LogAutomationDebug($"SelectStashTabAsync step {step + 1}/{maxSteps}. currentIndex={currentIndex}, targetIndex={tabIndex}, key={key}");
            Input.KeyDown(key);
            await DelayAutomationAsync(timing.KeyTapDelayMs);
            Input.KeyUp(key);

            var changedIndex = await WaitForVisibleTabIndexChangeAsync(currentIndex, Math.Max(timing.TabChangeTimeoutMs, automation.TabSwitchDelayMs.Value));
            LogAutomationDebug($"SelectStashTabAsync step {step + 1} result. previousIndex={currentIndex}, changedIndex={changedIndex}");
            if (changedIndex == currentIndex)
            {
                await DelayAutomationAsync(Math.Max(timing.TabRetryDelayMs, automation.TabSwitchDelayMs.Value / 2));
            }
        }

        LogAutomationDebug($"SelectStashTabAsync exhausted step loop for targetIndex={tabIndex}. Waiting for visible tab. {DescribeStash(GameController?.IngameState?.IngameUi?.StashElement)}");
        await WaitForVisibleTabAsync(tabIndex);
    }

    private async Task EnsureFragmentStashScarabTabSelectedAsync()
    {
        var automation = Settings.StashAutomation;
        var timing = AutomationTiming;
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
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(timing.FragmentTabBaseTimeoutMs, automation.TabSwitchDelayMs.Value + timing.FragmentTabBaseTimeoutMs));
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
                    preClickDelayMs: timing.UiClickPreDelayMs,
                    postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs, automation.TabSwitchDelayMs.Value));
                _lastAutomationFragmentScarabTabIndex = stash.IndexVisibleStash;
                LogAutomationDebug($"Fragment scarab tab clicked. rememberedStashIndex={_lastAutomationFragmentScarabTabIndex}");
                return;
            }

            if (attempts == 1 || attempts % 5 == 0)
            {
                LogAutomationDebug($"Fragment scarab tab not found on attempt {attempts}. path={DescribePath(FragmentStashScarabTabPath)}, stash={DescribeStash(stash)}");
                LogAutomationDebug($"Fragment scarab path trace attempt {attempts}: {DescribePathLookup(stash, FragmentStashScarabTabPath)}");
            }

            await DelayAutomationAsync(timing.FastPollDelayMs);
        }

        LogAutomationDebug($"EnsureFragmentStashScarabTabSelectedAsync timed out after {attempts} attempts. path={DescribePath(FragmentStashScarabTabPath)}, stash={DescribeStash(GameController?.IngameState?.IngameUi?.StashElement)}");
    }

    private async Task WaitForVisibleTabAsync(int tabIndex)
    {
        var automation = Settings.StashAutomation;
        var timing = AutomationTiming;
        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(
            timing.VisibleTabTimeoutMs,
            Math.Max(1500, automation.TabSwitchDelayMs.Value)));
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();
            if (IsVisibleStashTabReady(tabIndex))
            {
                return;
            }

            await DelayAutomationAsync(timing.FastPollDelayMs);
        }

        LogAutomationDebug($"WaitForVisibleTabAsync timed out. targetTab={tabIndex}, stash={DescribeStash(GameController?.IngameState?.IngameUi?.StashElement)}");
        throw new InvalidOperationException($"Timed out switching to stash tab {tabIndex}.");
    }

    private async Task<int> WaitForVisibleTabIndexChangeAsync(int previousTabIndex, int timeoutMs)
    {
        var timing = AutomationTiming;
        var startedAt = DateTime.UtcNow;
        var adjustedTimeoutMs = GetAutomationTimeoutMs(timeoutMs);
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < adjustedTimeoutMs)
        {
            ThrowIfAutomationStopRequested();
            var stash = GameController?.IngameState?.IngameUi?.StashElement;
            if (stash?.IsVisible == true && stash.IndexVisibleStash != previousTabIndex)
            {
                return stash.IndexVisibleStash;
            }

            await DelayAutomationAsync(timing.FastPollDelayMs);
        }

        LogAutomationDebug($"WaitForVisibleTabIndexChangeAsync timed out. previousTabIndex={previousTabIndex}, timeoutMs={timeoutMs}, stash={DescribeStash(GameController?.IngameState?.IngameUi?.StashElement)}");
        return previousTabIndex;
    }

    private async Task CtrlClickInventoryItemAsync(NormalInventoryItem item)
    {
        var timing = AutomationTiming;
        await ClickAtAsync(
            item.GetClientRect().Center,
            holdCtrl: true,
            preClickDelayMs: timing.CtrlClickPreDelayMs,
            postClickDelayMs: timing.CtrlClickPostDelayMs);
    }

    private async Task CtrlRightClickInventoryItemAsync(NormalInventoryItem item)
    {
        var timing = AutomationTiming;
        Input.SetCursorPos(item.GetClientRect().Center);
        Input.MouseMove();
        await DelayAutomationAsync(timing.CtrlClickPreDelayMs);
        Input.KeyDown(Keys.LControlKey);
        Input.Click(MouseButtons.Right);
        Input.KeyUp(Keys.LControlKey);
        await DelayAutomationAsync(timing.CtrlClickPostDelayMs);
    }

    private async Task ShiftClickInventoryItemAsync(NormalInventoryItem item)
    {
        var timing = AutomationTiming;
        Input.SetCursorPos(item.GetClientRect().Center);
        Input.MouseMove();
        await DelayAutomationAsync(timing.CtrlClickPreDelayMs);
        Input.KeyDown(Keys.LShiftKey);
        Input.Click(MouseButtons.Left);
        Input.KeyUp(Keys.LShiftKey);
        await DelayAutomationAsync(timing.CtrlClickPostDelayMs);
    }

    private async Task RightClickInventoryItemAsync(NormalInventoryItem item)
    {
        var timing = AutomationTiming;
        Input.SetCursorPos(item.GetClientRect().Center);
        Input.MouseMove();
        await DelayAutomationAsync(timing.UiClickPreDelayMs);
        Input.Click(MouseButtons.Right);
        await DelayAutomationAsync(timing.CtrlClickPostDelayMs);
    }

    private async Task CtrlClickElementAsync(Element element)
    {
        var timing = AutomationTiming;
        await ClickAtAsync(
            element.GetClientRect().Center,
            holdCtrl: true,
            preClickDelayMs: timing.CtrlClickPreDelayMs,
            postClickDelayMs: timing.CtrlClickPostDelayMs);
    }

    private async Task ClickAtAsync(SharpDX.Vector2 position, bool holdCtrl, int preClickDelayMs, int postClickDelayMs)
    {
        Input.SetCursorPos(position);
        Input.MouseMove();

        await DelayAutomationAsync(preClickDelayMs);

        await ClickCurrentCursorAsync(holdCtrl, 0, postClickDelayMs);
    }

    private async Task ClickCurrentCursorAsync(bool holdCtrl, int preClickDelayMs, int postClickDelayMs)
    {
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

    private async Task SendChatCommandAsync(string command)
    {
        ThrowIfAutomationStopRequested();

        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        ImGui.SetClipboardText(command);

        var timing = AutomationTiming;
        await TapKeyAsync(Keys.Enter, timing.KeyTapDelayMs, timing.FastPollDelayMs);
        await PasteClipboardAsync();
        await DelayAutomationAsync(timing.FastPollDelayMs);
        await TapKeyAsync(Keys.Enter, timing.KeyTapDelayMs, timing.FastPollDelayMs);
    }

    private async Task PasteClipboardAsync()
    {
        var timing = AutomationTiming;
        Input.KeyDown(Keys.LControlKey);
        await DelayAutomationAsync(timing.KeyTapDelayMs);
        Input.KeyDown(Keys.V);
        await DelayAutomationAsync(timing.KeyTapDelayMs);
        Input.KeyUp(Keys.V);
        Input.KeyUp(Keys.LControlKey);
    }

    private async Task CtrlTapKeyAsync(Keys key, int holdDelayMs, int postDelayMs)
    {
        Input.KeyDown(Keys.LControlKey);
        await DelayAutomationAsync(Math.Max(1, holdDelayMs));
        Input.KeyDown(key);
        await DelayAutomationAsync(holdDelayMs);
        Input.KeyUp(key);
        Input.KeyUp(Keys.LControlKey);
        await DelayAutomationAsync(postDelayMs);
    }

    private async Task TapKeyAsync(Keys key, int holdDelayMs, int postDelayMs)
    {
        Input.KeyDown(key);
        await DelayAutomationAsync(holdDelayMs);
        Input.KeyUp(key);
        await DelayAutomationAsync(postDelayMs);
    }

    private async Task DelayForUiCheckAsync(int minimumDelayMs = 125)
    {
        var timing = AutomationTiming;
        var latencyDelayMs = GetServerLatencyMs();
        var uiDelayMs = Math.Max(minimumDelayMs, latencyDelayMs > 0 ? Math.Min(300, latencyDelayMs) : 0);
        uiDelayMs = Math.Max(uiDelayMs, timing.FastPollDelayMs + 25);

        await DelayAutomationAsync(uiDelayMs);
    }

    private bool IsInMenagerie()
    {
        return string.Equals(GameController?.Area?.CurrentArea?.Name, MenagerieAreaName, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsBestiaryChallengePanelOpen()
    {
        return TryGetBestiaryCapturedBeastsButton()?.IsVisible == true;
    }

    private bool IsBestiaryCapturedBeastsWindowOpen()
    {
        return TryGetBestiaryCapturedBeastsDisplay(out _, out _);
    }

    private async Task<bool> WaitForBestiaryConditionAsync(Func<bool> condition, int timeoutMs, int pollDelayMs = -1)
    {
        var timing = AutomationTiming;
        var startedAt = DateTime.UtcNow;
        var adjustedTimeoutMs = GetAutomationTimeoutMs(timeoutMs);
        var adjustedPollDelayMs = pollDelayMs >= 0 ? pollDelayMs : timing.FastPollDelayMs;
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < adjustedTimeoutMs)
        {
            ThrowIfAutomationStopRequested();
            if (condition())
            {
                return true;
            }

            await DelayAutomationAsync(adjustedPollDelayMs);
        }

        return condition();
    }

    private async Task<Entity> WaitForBestiaryEntityAsync(Func<Entity> resolver, int timeoutMs)
    {
        var startedAt = DateTime.UtcNow;
        var adjustedTimeoutMs = GetAutomationTimeoutMs(timeoutMs);
        var pollDelayMs = AutomationTiming.FastPollDelayMs;
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < adjustedTimeoutMs)
        {
            ThrowIfAutomationStopRequested();
            var entity = resolver();
            if (entity != null)
            {
                return entity;
            }

            await DelayAutomationAsync(pollDelayMs);
        }

        return resolver();
    }

    private async Task<bool> WaitForAreaNameAsync(string areaName, int timeoutMs)
    {
        return await WaitForBestiaryConditionAsync(
            () => string.Equals(GameController?.Area?.CurrentArea?.Name, areaName, StringComparison.OrdinalIgnoreCase),
            timeoutMs);
    }

    private async Task<Entity> WaitForMenagerieEinharAsync()
    {
        return await WaitForBestiaryEntityAsync(FindMenagerieEinharEntity, 5000);
    }

    private Entity FindMenagerieEinharEntity()
    {
        var entities = GameController?.EntityListWrapper?.Entities;
        var camera = GameController?.Game?.IngameState?.Camera;
        var window = GameController?.Window;
        if (entities == null || camera == null || window == null)
        {
            return null;
        }

        var windowRect = window.GetWindowRectangle();
        Entity closestEinhar = null;
        var closestDistance = float.MaxValue;

        foreach (var entity in entities)
        {
            if (entity?.IsValid != true || !string.Equals(entity.Metadata, MenagerieEinharMetadata, StringComparison.OrdinalIgnoreCase))
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
            closestEinhar = entity;
        }

        return closestEinhar;
    }

    private async Task CtrlClickWorldEntityAsync(Entity entity)
    {
        if (entity?.GetComponent<Render>() == null)
        {
            throw new InvalidOperationException($"Could not find a clickable world position for {DescribeEntity(entity)}.");
        }

        if (!await HoverWorldEntityAsync(entity, DescribeEntity(entity)))
        {
            throw new InvalidOperationException($"Could not hover {DescribeEntity(entity)} before clicking.");
        }

        var timing = AutomationTiming;
        await ClickCurrentCursorAsync(
            holdCtrl: true,
            preClickDelayMs: timing.UiClickPreDelayMs,
            postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs, Settings.StashAutomation.TabSwitchDelayMs.Value));
    }

    private Element TryGetBestiaryCapturedBeastsButton()
    {
        try
        {
            var buttonContainer = TryGetBestiaryCapturedBeastsButtonContainer();
            if (buttonContainer?.Children == null)
            {
                return null;
            }

            foreach (var child in buttonContainer.Children)
            {
                if (child == null)
                {
                    continue;
                }

                var tooltipText = GetElementTextRecursive(child.Tooltip, 6);
                if (tooltipText?.IndexOf("Captured Beasts", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return child;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private Element TryGetBestiaryCapturedBeastsButtonContainer()
    {
        try
        {
            Element element = GameController?.IngameState?.IngameUi;
            foreach (var childIndex in BestiaryCapturedBeastsButtonContainerPath)
            {
                element = element?.GetChildAtIndex(childIndex);
                if (element == null)
                {
                    return null;
                }
            }

            return element;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> WaitForBestiaryCapturedBeastsButtonAsync()
    {
        return await WaitForBestiaryConditionAsync(
            () => TryGetBestiaryCapturedBeastsButton()?.IsVisible == true,
            4000);
    }

    private async Task EnsureBestiaryCapturedBeastsWindowOpenAsync()
    {
        if (IsBestiaryCapturedBeastsWindowOpen())
        {
            return;
        }

        if (!IsBestiaryChallengePanelOpen())
        {
            var einhar = await WaitForMenagerieEinharAsync();
            if (einhar == null)
            {
                throw new InvalidOperationException("Could not find Einhar in The Menagerie.");
            }

            await CtrlClickWorldEntityAsync(einhar);
            if (!await WaitForBestiaryCapturedBeastsButtonAsync())
            {
                throw new InvalidOperationException("Timed out opening the challenge panel.");
            }
        }

        var capturedBeastsButton = TryGetBestiaryCapturedBeastsButton();
        if (capturedBeastsButton == null)
        {
            throw new InvalidOperationException("Could not find the captured beasts button.");
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await ClickBestiaryCapturedBeastsButtonAsync(capturedBeastsButton);
            await DelayForUiCheckAsync(200);
            if (await WaitForBestiaryCapturedBeastsDisplayAsync())
            {
                return;
            }

            capturedBeastsButton = TryGetBestiaryCapturedBeastsButton();
            if (capturedBeastsButton == null)
            {
                break;
            }
        }

        throw new InvalidOperationException("Timed out opening the captured beasts window.");
    }

    private async Task ClickBestiaryCapturedBeastsButtonAsync(Element button)
    {
        var timing = AutomationTiming;
        await ClickAtAsync(
            button.GetClientRect().Center,
            holdCtrl: false,
            preClickDelayMs: timing.UiClickPreDelayMs,
            postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs, Settings.StashAutomation.TabSwitchDelayMs.Value));
    }

    private async Task<bool> WaitForBestiaryCapturedBeastsDisplayAsync()
    {
        return await WaitForBestiaryConditionAsync(
            () => TryGetBestiaryCapturedBeastsDisplay(out _, out _),
            4000,
            Math.Max(AutomationTiming.FastPollDelayMs, 25));
    }

    private bool IsBestiaryWorldUiOpen()
    {
        return GameController?.IngameState?.IngameUi?.StashElement?.IsVisible == true ||
               IsBestiaryCapturedBeastsWindowOpen() ||
               IsBestiaryChallengePanelOpen() ||
               TryGetBestiaryDeleteConfirmationWindow()?.IsVisible == true;
    }

    private async Task CloseBestiaryWorldUiAsync()
    {
        if (!IsBestiaryWorldUiOpen())
        {
            return;
        }

        var timing = AutomationTiming;
        await TapKeyAsync(Keys.Space, timing.KeyTapDelayMs, timing.FastPollDelayMs);
        await DelayForUiCheckAsync(150);
    }

    private async Task<bool> WaitForBestiaryCapturedBeastsToPopulateAsync()
    {
        return await WaitForBestiaryConditionAsync(
            () => GetBestiaryTotalCapturedBeastCount() > 0 || GetVisibleBestiaryCapturedBeasts().Count > 0,
            500,
            Math.Max(AutomationTiming.FastPollDelayMs, 25));
    }

    private bool ShouldDeleteBestiaryBeasts()
    {
        return _bestiaryDeleteModeOverride ?? Settings?.BestiaryAutomation?.DeleteBeastsInsteadOfItemizing?.Value == true;
    }

    private Element TryGetBestiaryDeleteButton(Element beastElement)
    {
        for (var current = beastElement; current != null; current = current.Parent)
        {
            var deleteButton = TryGetChildFromIndicesQuietly(current, BestiaryDeleteButtonPathFromBeastRow);
            if (deleteButton != null)
            {
                return deleteButton;
            }
        }

        return null;
    }

    private Element TryGetBestiaryDeleteConfirmationWindow()
    {
        var popUpWindow = GameController?.IngameState?.IngameUi?.PopUpWindow;
        return TryGetChildFromIndicesQuietly(popUpWindow, BestiaryDeleteConfirmationWindowPath);
    }

    private Element TryGetBestiaryDeleteConfirmationOkayButton()
    {
        var popUpWindow = GameController?.IngameState?.IngameUi?.PopUpWindow;
        return TryGetChildFromIndicesQuietly(popUpWindow, BestiaryDeleteConfirmationOkayButtonPath);
    }

    private async Task<bool> WaitForBestiaryDeleteConfirmationWindowAsync()
    {
        return await WaitForBestiaryConditionAsync(
            () => TryGetBestiaryDeleteConfirmationWindow()?.IsVisible == true,
            1000);
    }

    private async Task ConfirmBestiaryDeleteAsync()
    {
        if (!await WaitForBestiaryDeleteConfirmationWindowAsync())
        {
            throw new InvalidOperationException("Timed out opening the Bestiary delete confirmation.");
        }

        var timing = AutomationTiming;
        await TapKeyAsync(Keys.Enter, timing.KeyTapDelayMs, timing.FastPollDelayMs);

        if (TryGetBestiaryDeleteConfirmationWindow()?.IsVisible == true)
        {
            var okayButton = TryGetBestiaryDeleteConfirmationOkayButton();
            if (okayButton == null)
            {
                throw new InvalidOperationException("Could not resolve the Bestiary delete confirmation OK button.");
            }

            await ClickAtAsync(
                okayButton.GetClientRect().Center,
                holdCtrl: false,
                preClickDelayMs: timing.UiClickPreDelayMs,
                postClickDelayMs: timing.FastPollDelayMs);
        }
    }

    private async Task<int> ClearCapturedBeastsAsync()
    {
        var consecutiveFailures = 0;
        var releasedBeastCount = 0;
        var deleteBeasts = ShouldDeleteBestiaryBeasts();
        DateTime? firstItemizedBeastAtUtc = null;

        ReleaseAutomationModifierKeys();

        var holdCtrlForBestiaryClicks = !deleteBeasts;
        if (holdCtrlForBestiaryClicks)
        {
            Input.KeyDown(Keys.LControlKey);
        }

        try
        {
            while (true)
            {
                ThrowIfAutomationStopRequested();

                if (!deleteBeasts && GetPlayerInventoryFreeCellCount() <= 0)
                {
                    if (firstItemizedBeastAtUtc.HasValue)
                    {
                        var elapsed = DateTime.UtcNow - firstItemizedBeastAtUtc.Value;
                        WriteAutomationLog(
                            $"Inventory filled after {RareBeastCounterHelpers.FormatDuration(elapsed)} ({elapsed.TotalSeconds:F1}s) from the first itemized beast.",
                            requireDebugLogging: false);
                        firstItemizedBeastAtUtc = null;
                    }

                    await StashCapturedMonstersAndReturnToBestiaryAsync();
                    if (holdCtrlForBestiaryClicks)
                    {
                        Input.KeyDown(Keys.LControlKey);
                    }

                    if (GetPlayerInventoryFreeCellCount() <= 0)
                    {
                        throw new InvalidOperationException("Inventory is full and itemized beasts could not be moved to stash.");
                    }
                }

                var visibleBeasts = GetVisibleBestiaryCapturedBeasts();
                if (visibleBeasts.Count <= 0)
                {
                    if (await WaitForBestiaryCapturedBeastsToPopulateAsync())
                    {
                        visibleBeasts = GetVisibleBestiaryCapturedBeasts();
                        if (visibleBeasts.Count > 0)
                        {
                            continue;
                        }
                    }

                    return releasedBeastCount;
                }

                var startingVisibleCount = GetBestiaryTotalCapturedBeastCount();
                var firstBeast = visibleBeasts[0];
                if (holdCtrlForBestiaryClicks)
                {
                    Input.KeyDown(Keys.LControlKey);
                }

                await ClickBestiaryBeastAsync(firstBeast, deleteBeasts);

                var currentVisibleCount = await WaitForBestiaryReleaseVisibleCountAsync(startingVisibleCount);
                var releaseConfirmed = currentVisibleCount < startingVisibleCount;

                if (!releaseConfirmed)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= 12)
                    {
                        throw new InvalidOperationException("Bestiary clear stalled while releasing captured beasts.");
                    }
                }
                else
                {
                    consecutiveFailures = 0;
                    var releasedThisClick = startingVisibleCount - currentVisibleCount;
                    releasedBeastCount += releasedThisClick;
                    if (!deleteBeasts && releasedThisClick > 0 && !firstItemizedBeastAtUtc.HasValue)
                    {
                        firstItemizedBeastAtUtc = DateTime.UtcNow;
                    }
                }
            }
        }
        finally
        {
            if (holdCtrlForBestiaryClicks)
            {
                Input.KeyUp(Keys.LControlKey);
            }
        }
    }

    private async Task<int> WaitForBestiaryReleaseVisibleCountAsync(int previousVisibleCount)
    {
        await WaitForBestiaryConditionAsync(
            () => GetBestiaryTotalCapturedBeastCount() < previousVisibleCount,
            BestiaryReleaseTimeoutMs);
        return GetBestiaryTotalCapturedBeastCount();
    }

    private Element ResolveBestiaryClickTarget(Element beastElement, bool deleteBeasts)
    {
        if (beastElement == null)
        {
            throw new InvalidOperationException("Could not resolve the Bestiary release click position.");
        }

        if (!deleteBeasts)
        {
            return beastElement;
        }

        var clickTarget = TryGetBestiaryDeleteButton(beastElement);
        if (clickTarget != null)
        {
            return clickTarget;
        }

        LogAutomationDebug($"Could not resolve Bestiary delete button for beast. beast={DescribeElement(beastElement)}, parent={DescribeElement(beastElement.Parent)}");
        throw new InvalidOperationException("Could not resolve the Bestiary delete button.");
    }

    private async Task ClickBestiaryBeastAsync(Element beastElement, bool deleteBeasts)
    {
        var clickTarget = ResolveBestiaryClickTarget(beastElement, deleteBeasts);

        var timing = AutomationTiming;
        await ClickAtAsync(
            clickTarget.GetClientRect().Center,
            holdCtrl: false,
            preClickDelayMs: timing.CtrlClickPreDelayMs,
            postClickDelayMs: timing.CtrlClickPostDelayMs);

        if (deleteBeasts)
        {
            await ConfirmBestiaryDeleteAsync();
        }
    }

    private static void AddBestiaryCapturedBeastCandidates(IEnumerable<Element> source, RectangleF visibleRect, ICollection<Element> destination)
    {
        if (source == null || destination == null)
        {
            return;
        }

        foreach (var beastElement in source)
        {
            if (IsBestiaryCapturedBeastCandidate(beastElement, visibleRect))
            {
                destination.Add(beastElement);
            }
        }
    }

    private static List<Element> DistinctAndOrderBestiaryCapturedBeasts(IEnumerable<Element> beasts)
    {
        return beasts
            .GroupBy(element =>
            {
                var rect = element.GetClientRect();
                return new { rect.Left, rect.Top, rect.Right, rect.Bottom };
            })
            .Select(group => group
                .OrderByDescending(element => element.Entity != null)
                .ThenByDescending(element => element.Children?.Count ?? 0)
                .First())
            .OrderBy(element => element.GetClientRect().Top)
            .ThenBy(element => element.GetClientRect().Left)
            .ToList();
    }

    private List<Element> GetVisibleBestiaryCapturedBeasts()
    {
        if (!TryGetBestiaryCapturedBeastsDisplay(out var beastsDisplay, out var visibleRect))
        {
            return [];
        }

        var visibleBeasts = new List<Element>();
        foreach (var familyGroup in beastsDisplay.Children)
        {
            if (familyGroup == null || !familyGroup.IsVisible)
            {
                continue;
            }

            var beastList = familyGroup.GetChildAtIndex(1);
            if (beastList?.Children == null)
            {
                continue;
            }

            AddBestiaryCapturedBeastCandidates(beastList.Children, visibleRect, visibleBeasts);
        }

        return DistinctAndOrderBestiaryCapturedBeasts(visibleBeasts);
    }

    private int GetBestiaryTotalCapturedBeastCount()
    {
        if (!TryGetBestiaryCapturedBeastsDisplay(out var beastsDisplay, out _))
        {
            return 0;
        }

        var totalBeasts = 0;
        foreach (var familyGroup in beastsDisplay.Children)
        {
            if (familyGroup == null || !familyGroup.IsVisible)
            {
                continue;
            }

            totalBeasts += familyGroup.GetChildAtIndex(1)?.Children?.Count ?? 0;
        }

        return totalBeasts;
    }

    private int GetPlayerInventoryFreeCellCount()
    {
        var occupiedSlots = GetPlayerInventoryOccupiedCells(out var columns, out var rows);
        if (occupiedSlots == null || columns <= 0 || rows <= 0)
        {
            return 0;
        }

        var freeCellCount = 0;
        for (var x = 0; x < columns; x++)
        {
            for (var y = 0; y < rows; y++)
            {
                if (!occupiedSlots[x, y])
                {
                    freeCellCount++;
                }
            }
        }

        return freeCellCount;
    }

    private bool[,] GetPlayerInventoryOccupiedCells(out int columns, out int rows)
    {
        columns = 0;
        rows = 0;

        var playerInventory = GameController?.Game?.IngameState?.ServerData?.PlayerInventories[(int)InventorySlotE.MainInventory1]?.Inventory;
        if (playerInventory == null || playerInventory.Columns <= 0 || playerInventory.Rows <= 0)
        {
            return null;
        }

        columns = playerInventory.Columns;
        rows = playerInventory.Rows;
        var occupiedSlots = new bool[columns, rows];
        foreach (var inventoryItem in playerInventory.InventorySlotItems)
        {
            var startX = Math.Max(0, inventoryItem.PosX);
            var startY = Math.Max(0, inventoryItem.PosY);
            var endX = Math.Min(columns, inventoryItem.PosX + inventoryItem.SizeX);
            var endY = Math.Min(rows, inventoryItem.PosY + inventoryItem.SizeY);

            for (var x = startX; x < endX; x++)
            {
                for (var y = startY; y < endY; y++)
                {
                    occupiedSlots[x, y] = true;
                }
            }
        }

        return occupiedSlots;
    }

    private List<(int X, int Y)> GetPlayerInventoryNextFreeCells(int maxCount)
    {
        var requestedCount = Math.Max(0, maxCount);
        if (requestedCount <= 0)
        {
            return [];
        }

        var occupiedSlots = GetPlayerInventoryOccupiedCells(out var columns, out var rows);
        if (occupiedSlots == null || columns <= 0 || rows <= 0)
        {
            return [];
        }

        var result = new List<(int X, int Y)>(requestedCount);
        for (var x = 0; x < columns && result.Count < requestedCount; x++)
        {
            for (var y = 0; y < rows && result.Count < requestedCount; y++)
            {
                if (!occupiedSlots[x, y])
                {
                    result.Add((x, y));
                }
            }
        }

        return result;
    }

    private int CountOccupiedPlayerInventoryCells(IReadOnlyList<(int X, int Y)> cells)
    {
        if (cells == null || cells.Count <= 0)
        {
            return 0;
        }

        var occupiedSlots = GetPlayerInventoryOccupiedCells(out var columns, out var rows);
        if (occupiedSlots == null || columns <= 0 || rows <= 0)
        {
            return 0;
        }

        var occupiedCount = 0;
        foreach (var (x, y) in cells)
        {
            if (x >= 0 && x < columns && y >= 0 && y < rows && occupiedSlots[x, y])
            {
                occupiedCount++;
            }
        }

        return occupiedCount;
    }

    private string DescribePlayerInventoryCells(IReadOnlyList<(int X, int Y)> cells)
    {
        if (cells == null || cells.Count <= 0)
        {
            return "none";
        }

        var occupiedSlots = GetPlayerInventoryOccupiedCells(out var columns, out var rows);
        return string.Join(" | ", cells.Select((cell, index) =>
        {
            var state = occupiedSlots != null && cell.X >= 0 && cell.X < columns && cell.Y >= 0 && cell.Y < rows
                ? occupiedSlots[cell.X, cell.Y] ? "filled" : "empty"
                : "unknown";
            return $"{index + 1}=({cell.X},{cell.Y}):{state}";
        }));
    }

    private IList<NormalInventoryItem> GetVisiblePlayerInventoryItems()
    {
        return GameController?.IngameState?.IngameUi?.InventoryPanel[InventoryIndex.PlayerInventory]?.VisibleInventoryItems;
    }

    private int GetVisiblePlayerInventoryMatchingQuantity(string metadata)
    {
        return CountMatchingItemQuantity(GetVisiblePlayerInventoryItems(), metadata);
    }

    private static int? GetKnownFullStackSize(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        return metadata.IndexOf("Metadata/Items/Scarabs/", StringComparison.OrdinalIgnoreCase) >= 0 ? 20 : null;
    }

    private Element TryGetCurrencyShiftClickMenu()
    {
        var ingameUi = GameController?.IngameState?.IngameUi;
        return TryGetPropertyValue<Element>(ingameUi, "CurrencyShiftClickMenu")
               ?? TryGetChildFromIndicesQuietly(ingameUi, CurrencyShiftClickMenuPath);
    }

    private Element TryGetCurrencyShiftClickMenuConfirmButton()
    {
        return TryGetChildFromIndicesQuietly(TryGetCurrencyShiftClickMenu(), CurrencyShiftClickMenuConfirmButtonPath);
    }

    private async Task<bool> WaitForCurrencyShiftClickMenuVisibleAsync()
    {
        return await WaitForBestiaryConditionAsync(
            () => TryGetCurrencyShiftClickMenu()?.IsVisible == true,
            1000,
            Math.Max(AutomationTiming.FastPollDelayMs, 10));
    }

    private async Task<bool> WaitForCurrencyShiftClickMenuHiddenAsync()
    {
        return await WaitForBestiaryConditionAsync(
            () => TryGetCurrencyShiftClickMenu()?.IsVisible != true,
            1000,
            Math.Max(AutomationTiming.FastPollDelayMs, 10));
    }

    private async Task InputCurrencyShiftClickQuantityAsync(int quantity)
    {
        if (quantity <= 0)
        {
            throw new InvalidOperationException("Partial transfer quantity must be greater than zero.");
        }

        if (!await WaitForCurrencyShiftClickMenuVisibleAsync())
        {
            throw new InvalidOperationException("Timed out waiting for partial transfer menu before entering quantity.");
        }

        LogAutomationDebug($"Partial transfer menu became visible. {DescribeCurrencyShiftClickMenuQuantityState()}");

        var quantityText = quantity.ToString();
        string observedQuantityText = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await TypeDigitTextAsync(quantityText);
            observedQuantityText = await WaitForCurrencyShiftClickMenuQuantityTextAsync(quantityText, 250) ?? GetCurrencyShiftClickMenuQuantityText();
            if (string.Equals(observedQuantityText, quantityText, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            LogAutomationDebug($"Partial transfer quantity text mismatch after typing attempt {attempt + 1}. expected='{quantityText}', observed='{observedQuantityText ?? "<null>"}'. {DescribeCurrencyShiftClickMenuQuantityState()}");
        }

        if (!string.Equals(observedQuantityText, quantityText, StringComparison.OrdinalIgnoreCase))
        {
            LogAutomationDebug($"Partial transfer quantity final mismatch state. expected='{quantityText}', observed='{observedQuantityText ?? "<null>"}'. {DescribeCurrencyShiftClickMenuQuantityState()}");
            throw new InvalidOperationException($"Partial transfer quantity text mismatch. Expected '{quantityText}', observed '{observedQuantityText ?? "<null>"}'.");
        }

        await TapKeyAsync(Keys.Enter, AutomationTiming.KeyTapDelayMs, 0);
        if (!await WaitForCurrencyShiftClickMenuHiddenAsync())
        {
            throw new InvalidOperationException("Timed out closing partial transfer menu after confirming quantity.");
        }

        LogAutomationDebug($"Partial transfer quantity entered with Enter confirmation. quantity={quantityText}, observedText='{observedQuantityText}', menu={DescribeElement(TryGetCurrencyShiftClickMenu())}");
    }

    private string GetCurrencyShiftClickMenuQuantityText()
    {
        var quantityTextElement = TryGetChildFromIndicesQuietly(TryGetCurrencyShiftClickMenu(), CurrencyShiftClickMenuQuantityTextPath);
        if (quantityTextElement == null)
        {
            return null;
        }

        return TryGetPropertyValueAsString(quantityTextElement, "TextNoTags")?.Trim()
               ?? TryGetPropertyValueAsString(quantityTextElement, "Text")?.Trim()
               ?? TryGetElementText(quantityTextElement)
               ?? GetElementTextRecursive(quantityTextElement, 1)?.Trim();
    }

    private async Task<string> WaitForCurrencyShiftClickMenuQuantityTextAsync(string expectedText, int timeoutMs)
    {
        var timing = AutomationTiming;
        var startedAt = DateTime.UtcNow;
        var adjustedTimeoutMs = GetAutomationTimeoutMs(timeoutMs);
        string lastObservedText = null;

        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < adjustedTimeoutMs)
        {
            ThrowIfAutomationStopRequested();

            lastObservedText = GetCurrencyShiftClickMenuQuantityText();
            if (!string.IsNullOrWhiteSpace(lastObservedText) &&
                (string.IsNullOrWhiteSpace(expectedText) || string.Equals(lastObservedText, expectedText, StringComparison.OrdinalIgnoreCase)))
            {
                return lastObservedText;
            }

            await DelayAutomationAsync(timing.FastPollDelayMs);
        }

        return lastObservedText;
    }

    private async Task TypeDigitTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var character in text)
        {
            var key = character switch
            {
                '0' => Keys.D0,
                '1' => Keys.D1,
                '2' => Keys.D2,
                '3' => Keys.D3,
                '4' => Keys.D4,
                '5' => Keys.D5,
                '6' => Keys.D6,
                '7' => Keys.D7,
                '8' => Keys.D8,
                '9' => Keys.D9,
                _ => Keys.None
            };

            if (key == Keys.None)
            {
                throw new InvalidOperationException($"Unsupported partial transfer quantity character '{character}'.");
            }

            await TapKeyAsync(key, AutomationTiming.KeyTapDelayMs, AutomationTiming.FastPollDelayMs);
        }
    }

    private string DescribeCurrencyShiftClickMenuQuantityState()
    {
        var menu = TryGetCurrencyShiftClickMenu();
        var quantityTextElement = TryGetCurrencyShiftClickMenuQuantityTextElement();
        var textNoTags = TryGetPropertyValueAsString(quantityTextElement, "TextNoTags")?.Trim();
        var text = TryGetPropertyValueAsString(quantityTextElement, "Text")?.Trim();
        var directGetText = TryGetElementText(quantityTextElement);
        var recursiveText = GetElementTextRecursive(quantityTextElement, 1)?.Trim();

        return $"menu={DescribeElement(menu)}, quantityPath={DescribePath(CurrencyShiftClickMenuQuantityTextPath)}, quantityPathTrace={DescribePathLookup(menu, CurrencyShiftClickMenuQuantityTextPath)}, quantityElement={DescribeElement(quantityTextElement)}, textNoTags='{textNoTags ?? "<null>"}', text='{text ?? "<null>"}', getText='{directGetText ?? "<null>"}', recursiveText='{recursiveText ?? "<null>"}'";
    }

    private Element TryGetCurrencyShiftClickMenuQuantityTextElement()
    {
        return TryGetChildFromIndicesQuietly(TryGetCurrencyShiftClickMenu(), CurrencyShiftClickMenuQuantityTextPath);
    }

    private SharpDX.Vector2? TryGetPlayerInventoryCellCenter(int x, int y)
    {
        var inventoryPanel = GameController?.IngameState?.IngameUi?.InventoryPanel[InventoryIndex.PlayerInventory];
        var playerInventory = GameController?.Game?.IngameState?.ServerData?.PlayerInventories[(int)InventorySlotE.MainInventory1]?.Inventory;
        if (inventoryPanel?.IsVisible != true || playerInventory == null || playerInventory.Columns <= 0 || playerInventory.Rows <= 0)
        {
            return null;
        }

        var rect = inventoryPanel.GetClientRect();
        if (rect.Width <= 0 || rect.Height <= 0 || x < 0 || x >= playerInventory.Columns || y < 0 || y >= playerInventory.Rows)
        {
            return null;
        }

        var cellWidth = rect.Width / playerInventory.Columns;
        var cellHeight = rect.Height / playerInventory.Rows;
        return new SharpDX.Vector2(
            rect.Left + (x + 0.5f) * cellWidth,
            rect.Top + (y + 0.5f) * cellHeight);
    }

    private async Task PlaceItemIntoPlayerInventoryCellAsync(int x, int y)
    {
        var cellCenter = TryGetPlayerInventoryCellCenter(x, y);
        if (!cellCenter.HasValue)
        {
            throw new InvalidOperationException($"Could not resolve player inventory cell center for ({x},{y}).");
        }

        var inventoryPlacementPreClickDelayMs = Math.Max(AutomationTiming.UiClickPreDelayMs, 100);
        await ClickAtAsync(
            cellCenter.Value,
            holdCtrl: false,
            preClickDelayMs: inventoryPlacementPreClickDelayMs,
            postClickDelayMs: AutomationTiming.CtrlClickPostDelayMs);
    }

    private List<NormalInventoryItem> GetVisibleCapturedMonsterInventoryItems()
    {
        var inventoryItems = GetVisiblePlayerInventoryItems();
        if (inventoryItems == null)
        {
            return [];
        }

        return inventoryItems
            .Where(IsCapturedMonsterInventoryItem)
            .OrderBy(item => item.GetClientRect().Top)
            .ThenBy(item => item.GetClientRect().Left)
            .ToList();
    }

    private static bool IsCapturedMonsterInventoryItem(NormalInventoryItem item)
    {
        var path = item?.Item?.Path;
        if (!string.IsNullOrWhiteSpace(path) && path.IndexOf(CapturedMonsterItemPathFragment, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        var metadata = item?.Item?.Metadata;
        return !string.IsNullOrWhiteSpace(metadata) && metadata.IndexOf(CapturedMonsterItemPathFragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsRedCapturedMonsterInventoryItem(NormalInventoryItem item)
    {
        if (item?.Item == null)
        {
            return false;
        }

        var capturedMonster = item.Item.GetComponent<CapturedMonster>();
        var monsterVariety = capturedMonster?.MonsterVariety;
        if (IsKnownRedCapturedMonsterIdentity(TryGetPropertyValueAsString(monsterVariety, "VarietyId")) ||
            IsKnownRedCapturedMonsterIdentity(TryGetPropertyValueAsString(monsterVariety, "BaseMonsterTypeIndex")) ||
            IsKnownRedCapturedMonsterIdentity(TryGetPropertyValueAsString(monsterVariety, "Name")) ||
            IsKnownRedCapturedMonsterIdentity(TryGetPropertyValueAsString(monsterVariety, "MonsterName")))
        {
            return true;
        }

        return IsKnownRedCapturedMonsterIdentity(item.Item.GetComponent<Base>()?.Name);
    }

    private static bool IsKnownRedCapturedMonsterIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return false;
        }

        return RareBeastCounterBeastData.AllRedBeasts.Any(beast =>
            string.Equals(beast.Name, identity, StringComparison.OrdinalIgnoreCase) ||
            beast.MetadataPatterns.Any(pattern => identity.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0));
    }

    private static string TryGetPropertyValueAsString(object instance, string propertyName)
    {
        try
        {
            return instance?.GetType().GetProperty(propertyName)?.GetValue(instance)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static T TryGetPropertyValue<T>(object instance, string propertyName) where T : class
    {
        try
        {
            return instance?.GetType().GetProperty(propertyName)?.GetValue(instance) as T;
        }
        catch
        {
            return null;
        }
    }

    private async Task<int> WaitForCapturedMonsterInventoryItemCountToChangeAsync(int previousCount)
    {
        var automation = Settings.StashAutomation;
        var timeoutMs = Math.Max(
            AutomationTiming.QuantityChangeBaseDelayMs,
            automation.ClickDelayMs.Value + AutomationTiming.QuantityChangeBaseDelayMs);

        await WaitForBestiaryConditionAsync(
            () => GetVisibleCapturedMonsterInventoryItems().Count < previousCount,
            timeoutMs);

        return GetVisibleCapturedMonsterInventoryItems().Count;
    }

    private async Task<bool> HoverWorldEntityAsync(Entity entity, string label)
    {
        var timing = AutomationTiming;
        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(350, timing.UiClickPreDelayMs + timing.FastPollDelayMs));
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();

            var labelCenter = TryGetWorldEntityLabelCenter(entity);
            if (labelCenter.HasValue)
            {
                Input.SetCursorPos(labelCenter.Value);
                Input.MouseMove();

                var hovered = await WaitForBestiaryConditionAsync(
                    () => IsHoveringEntityLabel(entity),
                    Math.Max(40, timing.UiClickPreDelayMs + timing.FastPollDelayMs),
                    Math.Max(10, timing.FastPollDelayMs));
                if (hovered)
                {
                    return true;
                }
            }

            await DelayAutomationAsync(Math.Max(10, timing.FastPollDelayMs));
        }

        LogAutomationDebug($"Failed to hover world entity '{label}'. entity={DescribeEntity(entity)}");
        return false;
    }

    private SharpDX.Vector2? TryGetWorldEntityLabelCenter(Entity entity)
    {
        var labelsOnGround = GameController?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible ??
                            GameController?.IngameState?.IngameUi?.ItemsOnGroundLabelElement?.LabelsOnGround;
        if (labelsOnGround == null || entity == null)
        {
            return null;
        }

        var label = labelsOnGround.FirstOrDefault(x =>
            x?.ItemOnGround != null &&
            x.Label?.IsVisible == true &&
            (x.ItemOnGround.Address != 0 && entity.Address != 0
                ? x.ItemOnGround.Address == entity.Address
                : x.ItemOnGround.Id == entity.Id ||
                  string.Equals(x.ItemOnGround.Path, entity.Path, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(x.ItemOnGround.Metadata, entity.Metadata, StringComparison.OrdinalIgnoreCase)));

        return label?.Label?.GetClientRect().Center;
    }

    private bool IsHoveringEntityLabel(Entity entity)
    {
        var itemsOnGroundLabelElement = GameController?.IngameState?.IngameUi?.ItemsOnGroundLabelElement;
        var hoverPath = itemsOnGroundLabelElement?.ItemOnHoverPath;
        var hoveredLabel = itemsOnGroundLabelElement?.LabelOnHover;
        if (entity == null || hoveredLabel == null || string.IsNullOrWhiteSpace(hoverPath))
        {
            return false;
        }

        return string.Equals(hoverPath, entity.Path, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hoverPath, entity.Metadata, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> StashCapturedMonstersAndReturnToBestiaryAsync()
    {
        var movedCount = await StashCapturedMonstersIntoConfiguredTabAsync();
        await CloseBestiaryWorldUiAsync();
        await EnsureBestiaryCapturedBeastsWindowOpenAsync();
        return movedCount;
    }

    private async Task<int> StashCapturedMonstersAndCloseUiAsync()
    {
        var movedCount = await StashCapturedMonstersIntoConfiguredTabAsync();
        await CloseBestiaryWorldUiAsync();
        return movedCount;
    }

    private async Task<int> StashCapturedMonstersIntoConfiguredTabAsync()
    {
        var capturedMonsterItems = GetVisibleCapturedMonsterInventoryItems();
        if (capturedMonsterItems.Count <= 0)
        {
            return 0;
        }

        await CloseBestiaryWorldUiAsync();

        if (!await EnsureStashOpenForAutomationAsync())
        {
            throw new InvalidOperationException("Could not open the stash to store itemized beasts.");
        }

        var itemizedBeastTabIndex = ResolveBestiaryCapturedMonsterStashTabIndex(preferRedBeastTab: false);
        var redBeastTabIndex = ResolveConfiguredTabIndex(
            Settings?.BestiaryAutomation?.SelectedRedBeastTabName.Value,
            "Red beasts",
            "Bestiary automation red beast stash");
        var currentConfiguredTabIndex = int.MinValue;
        if (itemizedBeastTabIndex < 0)
        {
            throw new InvalidOperationException("Select an Itemized Beasts stash tab before auto-stashing itemized beasts.");
        }

        UpdateAutomationStatus("Stashing itemized beasts...");

        var movedCount = 0;
        var consecutiveFailures = 0;
        while (true)
        {
            ThrowIfAutomationStopRequested();

            if (GameController?.IngameState?.IngameUi?.StashElement?.IsVisible != true)
            {
                if (!await EnsureStashOpenForAutomationAsync())
                {
                    throw new InvalidOperationException("Could not reopen the stash while storing itemized beasts.");
                }

                currentConfiguredTabIndex = int.MinValue;
            }

            capturedMonsterItems = GetVisibleCapturedMonsterInventoryItems();
            if (capturedMonsterItems.Count <= 0)
            {
                return movedCount;
            }

            var nextItem = capturedMonsterItems[0];
            var configuredTabIndex = IsRedCapturedMonsterInventoryItem(nextItem) && redBeastTabIndex >= 0
                ? redBeastTabIndex
                : itemizedBeastTabIndex;

            if (configuredTabIndex != currentConfiguredTabIndex)
            {
                await SelectStashTabAsync(configuredTabIndex);
                await DelayAutomationAsync(Settings.StashAutomation.TabSwitchDelayMs.Value);
                currentConfiguredTabIndex = configuredTabIndex;
            }

            var previousCount = capturedMonsterItems.Count;
            await CtrlClickInventoryItemAsync(nextItem);
            var currentCount = await WaitForCapturedMonsterInventoryItemCountToChangeAsync(previousCount);
            if (currentCount < previousCount)
            {
                movedCount += previousCount - currentCount;
                consecutiveFailures = 0;
                await DelayAutomationAsync(Settings.StashAutomation.ClickDelayMs.Value);
                continue;
            }

            if (GameController?.IngameState?.IngameUi?.StashElement?.IsVisible != true)
            {
                consecutiveFailures = 0;
                currentConfiguredTabIndex = int.MinValue;
                await DelayAutomationAsync(AutomationTiming.FastPollDelayMs);
                continue;
            }

            consecutiveFailures++;
            if (consecutiveFailures >= 3)
            {
                throw new InvalidOperationException("Stashing itemized beasts stalled while moving captured monster items to the configured stash tab.");
            }

            await DelayAutomationAsync(AutomationTiming.FastPollDelayMs);
        }
    }

    private static bool IsBestiaryCapturedBeastCandidate(Element beastElement, RectangleF visibleRect)
    {
        if (beastElement?.IsVisible != true)
        {
            return false;
        }

        var rect = beastElement.GetClientRect();
        if (!IsRectMostlyInside(rect, visibleRect))
        {
            return false;
        }

        if (rect.Width < 16 || rect.Height < 16)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(GetBestiaryBeastLabel(beastElement)))
        {
            return false;
        }

        return beastElement.Entity != null || EnumerateDescendants(beastElement).Any(child => child?.Entity != null);
    }

    private static string GetBestiaryBeastLabel(Element beastElement)
    {
        if (beastElement == null)
        {
            return null;
        }

        var entityName = beastElement.Entity?.GetComponent<Base>()?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(entityName))
        {
            return entityName;
        }

        var entityMetadata = beastElement.Entity?.Metadata?.Trim();
        if (!string.IsNullOrWhiteSpace(entityMetadata))
        {
            return entityMetadata;
        }

        return GetElementTextRecursive(beastElement, 2);
    }

    #endregion
}
