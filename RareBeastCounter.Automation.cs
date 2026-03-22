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
    #region Constants and state

    private const string MenagerieAreaName = "The Menagerie";
    private const string MenagerieEinharMetadata = "Metadata/NPC/League/Bestiary/EinharMenagerie";
    private const string CapturedMonsterItemPathFragment = "CapturedMonster";
    private const string SettingsFileName = "RareBeastCounter_settings.json";
    private static readonly int[] BestiaryCapturedBeastsButtonContainerPath = [50, 2, 0, 1, 1, 15, 0, 19];
    private static readonly int[] BestiaryDeleteButtonPathFromBeastRow = [3];
    private static readonly int[] BestiaryDeleteConfirmationWindowPath = [0];
    private static readonly int[] BestiaryDeleteConfirmationOkayButtonPath = [0, 0, 3, 0];
    private static readonly int[] FragmentStashScarabTabPath = [2, 0, 0, 1, 1, 1, 0, 5, 0, 1];
    private static readonly int[] MapStashTierOneToNineTabPath = [2, 0, 0, 1, 1, 3, 0, 0];
    private static readonly int[] MapStashTierTenToSixteenTabPath = [2, 0, 0, 1, 1, 3, 0, 1];
    private static readonly int[] MapStashPageTabPath = [2, 0, 0, 1, 1, 3, 0, 3, 0];
    private static readonly int[] MapStashPageNumberPath = [0, 1];
    private static readonly int[] MapStashPageContentPath = [2, 0, 0, 1, 1, 3, 0, 4];
    private const int BestiaryReleaseTimeoutMs = 250;
    private const int MapTransferExtraConfirmationDelayMs = 10;
    private const int QuantitySettleStableWindowMs = 100;
    private static readonly AutomationTimingValues AutomationTiming = new();
    private string _lastAutomationStatusMessage;
    private bool _isAutomationRunning;
    private bool _isBestiaryClearRunning;
    private bool _isAutomationStopRequested;
    private bool? _bestiaryDeleteModeOverride;
    private int _lastAutomationFragmentScarabTabIndex = -1;
    private int _lastAutomationMapStashTierSelection = -1;
    private int _lastAutomationMapStashPageNumber = -1;
    private int _lastAutomationMapStashUiCacheKey = -1;
    private Element _lastAutomationMapStashTierGroupRoot;
    private Element _lastAutomationMapStashPageTabContainer;
    private Dictionary<int, Element> _lastAutomationMapStashPageTabsByNumber;
    private Element _lastAutomationMapStashPageContentRoot;
    private string _lastAutomationMapStashPageContentLogSignature;
    private string _lastAutomationMapStashPageTabsLogSignature;

    private sealed class AutomationTimingValues
    {
        public int KeyTapDelayMs { get; } = 1;
        public int CtrlClickPreDelayMs { get; } = 10;
        public int CtrlClickPostDelayMs { get; } = 10;
        public int UiClickPreDelayMs { get; } = 15;
        public int MinTabClickPostDelayMs { get; } = 15;
        public int FastPollDelayMs { get; } = 15;
        public int StashOpenPollDelayMs { get; } = 30;
        public int StashInteractionDistance { get; } = 100;
        public int TabRetryDelayMs { get; } = 20;
        public int TabChangeTimeoutMs { get; } = 50;
        public int QuantityChangeBaseDelayMs { get; } = 100;
        public int OpenStashPostClickDelayMs { get; } = 250;
        public int FragmentTabBaseTimeoutMs { get; } = 50;
        public int VisibleTabTimeoutMs { get; } = 100;
    }

    #endregion

    #region Settings UI

    private void DrawTargetTabSelectorPanel(string label, string idSuffix, StashAutomationTargetSettings target)
    {
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        if (stash?.IsVisible != true)
        {
            var selectedTabName = target?.SelectedTabName.Value?.Trim();
            ImGui.Text($"{label} tab");
            ImGui.SameLine();
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(selectedTabName) ? "Select tab" : selectedTabName);
            ImGui.TextDisabled("Open stash to change the selected stash tab.");
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

    private void DrawBestiaryStashTabSelectorPanel(BestiaryAutomationSettings automation)
    {
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        if (stash?.IsVisible != true)
        {
            var selectedTabName = automation?.SelectedTabName.Value?.Trim();
            ImGui.Text("Itemized beasts stash tab");
            ImGui.SameLine();
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(selectedTabName) ? "Select tab" : selectedTabName);
            var selectedRedTabName = automation?.SelectedRedBeastTabName.Value?.Trim();
            ImGui.Text("Red beasts stash tab");
            ImGui.SameLine();
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(selectedRedTabName) ? "Use itemized beasts tab" : selectedRedTabName);
            ImGui.TextDisabled("Open stash to change the selected stash tab.");
            return;
        }

        var stashTabNames = GetAvailableStashTabNames(stash);
        if (stashTabNames.Count <= 0)
        {
            ImGui.TextDisabled("No stash tabs available.");
            return;
        }

        DrawBestiaryStashTabSelector("Itemized beasts", "bestiary", automation, stashTabNames);
        DrawBestiaryStashTabSelector("Red beasts", "bestiaryRed", automation.RedBeastStashTabSelector, automation.SelectedRedBeastTabName, stashTabNames, "Use itemized beasts tab");
    }

    private void InitializeAutomationSettingsUi(StashAutomationSettings automation)
    {
        foreach (var (label, idSuffix, target) in GetAutomationTargets(automation))
        {
            target.TabSelector.DrawDelegate = () => DrawTargetTabSelectorPanel(label, idSuffix, target);
        }
    }

    private void InitializeBestiaryAutomationSettingsUi(BestiaryAutomationSettings automation)
    {
        automation.StashTabSelector.DrawDelegate = () => DrawBestiaryStashTabSelectorPanel(automation);
    }

    private void DrawMenagerieInventoryQuickButton()
    {
        if (Settings?.BestiaryAutomation?.ShowInventoryButton?.Value != true)
        {
            return;
        }

        if (!IsInMenagerie())
        {
            return;
        }

        var inventoryPanel = GameController?.IngameState?.IngameUi?.InventoryPanel[InventoryIndex.PlayerInventory];
        if (inventoryPanel?.IsVisible != true)
        {
            return;
        }

        var rect = inventoryPanel.GetClientRect();
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        ImGui.SetNextWindowPos(new Vector2(rect.Left - 8f, rect.Top), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(0.9f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration |
                                       ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoFocusOnAppearing |
                                       ImGuiWindowFlags.NoNav;

        if (!ImGui.Begin("##RareBeastCounterInventoryAutomationButton", flags))
        {
            ImGui.End();
            return;
        }

        if (_isAutomationRunning)
        {
            ImGui.TextDisabled("Automation running...");
            if (ImGui.Button("Stop##RareBeastCounterInventoryAutomation"))
            {
                RequestAutomationStop();
            }
        }
        else if (ImGui.Button("Right Click All Beasts##RareBeastCounterInventoryAutomation"))
        {
            _ = RunRightClickCapturedMonstersInInventoryAsync();
        }

        ImGui.End();
    }

    private void DrawBestiaryAutomationQuickButtons()
    {
        if (Settings?.BestiaryAutomation?.ShowBestiaryButtons?.Value != true)
        {
            return;
        }

        var buttonContainer = TryGetBestiaryCapturedBeastsButtonContainer();
        if (buttonContainer?.IsVisible != true)
        {
            return;
        }

        var rect = buttonContainer.GetClientRect();
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        ImGui.SetNextWindowPos(new Vector2(rect.Right + 8f, rect.Top), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.9f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration |
                                       ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoFocusOnAppearing |
                                       ImGuiWindowFlags.NoNav;

        if (!ImGui.Begin("##RareBeastCounterBestiaryAutomationButtons", flags))
        {
            ImGui.End();
            return;
        }

        if (_isAutomationRunning)
        {
            ImGui.TextDisabled("Automation running...");
            if (ImGui.Button("Stop##RareBeastCounterBestiaryAutomation"))
            {
                RequestAutomationStop();
            }
        }
        else
        {
            if (ImGui.Button("Itemize All##RareBeastCounterBestiaryAutomation"))
            {
                StartBestiaryClearAutomation(deleteBeastsInsteadOfItemizing: false, "button");
            }

            if (ImGui.Button("Delete All##RareBeastCounterBestiaryAutomation"))
            {
                StartBestiaryClearAutomation(deleteBeastsInsteadOfItemizing: true, "button");
            }
        }

        ImGui.End();
    }

    private void StartBestiaryClearAutomation(bool deleteBeastsInsteadOfItemizing, string triggerSource)
    {
        if (_isAutomationRunning)
        {
            RequestAutomationStop();
            return;
        }

        _bestiaryDeleteModeOverride = deleteBeastsInsteadOfItemizing;
        LogAutomationDebug($"Bestiary clear triggered by {triggerSource}. mode={(deleteBeastsInsteadOfItemizing ? "delete" : "itemize")}");
        _ = RunBestiaryClearAutomationFromHotkeyAsync();
    }

    private async Task RunRightClickCapturedMonstersInInventoryAsync()
    {
        if (_isAutomationRunning)
        {
            RequestAutomationStop();
            return;
        }

        _isAutomationRunning = true;
        _isAutomationStopRequested = false;
        ResetAutomationState();

        try
        {
            if (!IsInMenagerie())
            {
                UpdateAutomationStatus("This action is only available in The Menagerie.", forceLog: true);
                return;
            }

            var clickedCount = 0;
            var consecutiveFailures = 0;
            while (true)
            {
                ThrowIfAutomationStopRequested();

                var capturedMonsterItems = GetVisibleCapturedMonsterInventoryItems();
                if (capturedMonsterItems.Count <= 0)
                {
                    UpdateAutomationStatus(clickedCount > 0
                        ? $"Right-clicked {clickedCount} beast{(clickedCount == 1 ? string.Empty : "s")} from inventory."
                        : "No captured beasts were found in player inventory.", forceLog: true);
                    return;
                }

                var nextItem = capturedMonsterItems[0];
                var previousCount = capturedMonsterItems.Count;
                UpdateAutomationStatus($"Right-clicking beasts in inventory... {clickedCount}/{previousCount}");
                await RightClickInventoryItemAsync(nextItem);
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
                        throw new InvalidOperationException("Right-clicking captured beasts in inventory stalled.");
                    }

                    await DelayAutomationAsync(AutomationTiming.FastPollDelayMs);
                    continue;
                }

                consecutiveFailures = 0;
                clickedCount += previousCount - currentCount;
                await DelayAutomationAsync(Settings.StashAutomation.ClickDelayMs.Value);
            }
        }
        catch (OperationCanceledException)
        {
            UpdateAutomationStatus("Right-click inventory beasts cancelled.");
        }
        catch (Exception ex)
        {
            LogAutomationError("Right-click inventory beasts failed.", ex);
            UpdateAutomationStatus($"Right-click inventory beasts failed: {ex.Message}");
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

    #endregion

    #region Automation entry points

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
            LogAutomationError("Restock failed.", ex);
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

    private async Task RunBestiaryClearAutomationFromHotkeyAsync()
    {
        if (_isAutomationRunning)
        {
            RequestAutomationStop();
            return;
        }

        _isAutomationRunning = true;
        _isBestiaryClearRunning = true;
        _isAutomationStopRequested = false;
        ResetAutomationState();

        try
        {
            if (!IsInMenagerie())
            {
                await SendChatCommandAsync("/menagerie");
                if (!await WaitForAreaNameAsync(MenagerieAreaName, 5000))
                {
                    throw new InvalidOperationException("Timed out travelling to The Menagerie.");
                }
            }

            await EnsureBestiaryCapturedBeastsWindowOpenAsync();

            var deleteBeasts = ShouldDeleteBestiaryBeasts();
            if (!deleteBeasts)
            {
                var availableInventorySlots = GetPlayerInventoryFreeCellCount();
                LogAutomationDebug($"Bestiary clear starting. Can itemize up to {availableInventorySlots} beast{(availableInventorySlots == 1 ? string.Empty : "s")} based on free inventory slots.");

                if (availableInventorySlots <= 0)
                {
                    await StashCapturedMonstersAndReturnToBestiaryAsync();
                    availableInventorySlots = GetPlayerInventoryFreeCellCount();
                }

                if (availableInventorySlots <= 0)
                {
                    UpdateAutomationStatus("Bestiary clear stopped. Inventory is full.", forceLog: true);
                    return;
                }
            }
            else
            {
                LogAutomationDebug("Bestiary clear starting in delete mode.");
            }

            var releasedBeastCount = await ClearCapturedBeastsAsync();
            if (!deleteBeasts)
            {
                await StashCapturedMonstersAndCloseUiAsync();
            }

            var processedAnyBeasts = releasedBeastCount > 0;
            UpdateAutomationStatus(processedAnyBeasts
                ? $"Bestiary clear complete. {(deleteBeasts ? "Deleted" : "Itemized")} {releasedBeastCount} beast{(releasedBeastCount == 1 ? string.Empty : "s")}."
                : "Bestiary clear complete. No captured beasts were visible.", forceLog: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogAutomationError("Bestiary clear failed.", ex);
            UpdateAutomationStatus($"Bestiary clear failed: {ex.Message}");
        }
        finally
        {
            _isAutomationRunning = false;
            _isBestiaryClearRunning = false;
            _isAutomationStopRequested = false;
            _bestiaryDeleteModeOverride = null;
            ResetAutomationState();
            Input.KeyUp(Keys.ControlKey);
            Input.KeyUp(Keys.LControlKey);
        }
    }

    #endregion

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

    #region Shared automation state

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
        if (!_isBestiaryClearRunning)
        {
            UpdateAutomationStatus("Stopping restock...");
        }
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

                visiblePageItems = GetVisibleMapStashPageItems();
                nextPageItem = FindNextMatchingMapStashPageItem(visiblePageItems, sourceMetadata) ?? nextPageItem;
                var availableBeforeTransfer = GetVisibleMapStashPageMatchingQuantity(sourceMetadata);
                var inventoryBeforeTransfer = TryGetVisiblePlayerInventoryMatchingQuantity(sourceMetadata);
                var timing = AutomationTiming;
                var batchTransferTargets = (visiblePageItems ?? (nextPageItem?.Entity != null ? [nextPageItem] : []))
                    .Where(item => string.Equals(item?.Entity?.Metadata, sourceMetadata, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(item => item.GetClientRect().Top)
                    .ThenBy(item => item.GetClientRect().Left)
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

                LogAutomationDebug($"Batch transferring visible map stash page items. metadata='{sourceMetadata}', targetCount={batchTransferTargets.Count}, attemptedQuantity={attemptedTransferQuantity}, previousQuantity={availableBeforeTransfer}");
                foreach (var batchTarget in batchTransferTargets)
                {
                    ThrowIfAutomationStopRequested();
                    await ClickAtAsync(
                        batchTarget.Position,
                        holdCtrl: true,
                        preClickDelayMs: timing.CtrlClickPreDelayMs,
                        postClickDelayMs: timing.CtrlClickPostDelayMs);
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
            var nextItem = FindNextMatchingStashItem(visibleItems, sourceMetadata);
            if (nextItem?.Item == null)
            {
                return 0;
            }

            var availableBeforeItemTransfer = GetVisibleMatchingItemQuantity(sourceMetadata);
            var inventoryBeforeItemTransfer = TryGetVisiblePlayerInventoryMatchingQuantity(sourceMetadata);
            var stackSizeBeforeItemTransfer = Math.Max(1, nextItem.Item?.GetComponent<Stack>()?.Size ?? 1);
            await CtrlClickInventoryItemAsync(nextItem);
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
                    : stackSizeBeforeItemTransfer <= 1
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

    #region Stash interaction and target metadata

    private async Task<bool> EnsureStashOpenForAutomationAsync()
    {
        ThrowIfAutomationStopRequested();

        var timing = AutomationTiming;
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
            var statusMessage = distance.HasValue && distance.Value <= timing.StashInteractionDistance
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

    #endregion

    #region Map stash navigation and caching

    private async Task EnsureMapStashTierTabSelectedAsync(StashAutomationTargetSettings target)
    {
        var automation = Settings.StashAutomation;
        var timing = AutomationTiming;
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
            preClickDelayMs: timing.UiClickPreDelayMs,
            postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs, automation.TabSwitchDelayMs.Value));
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

        var pageTabsPathTrace = DescribePathLookup(GameController?.IngameState?.IngameUi?.OpenLeftPanel, MapStashPageTabPath);
        if (!string.Equals(_lastAutomationMapStashPageTabsLogSignature, pageTabsPathTrace, StringComparison.Ordinal))
        {
            _lastAutomationMapStashPageTabsLogSignature = pageTabsPathTrace;
            LogAutomationDebug($"Map stash page tabs path trace: {pageTabsPathTrace}");
        }

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

        var timing = AutomationTiming;
        var rect = pageTab.GetClientRect();
        var center = rect.Center;
        LogAutomationDebug($"Clicking map stash page {pageNumber}. sourceIndex={sourceIndex}, rect={DescribeRect(rect)}");

        await ClickAtAsync(
            center,
            holdCtrl: false,
            preClickDelayMs: timing.UiClickPreDelayMs,
            postClickDelayMs: Math.Max(timing.MinTabClickPostDelayMs, automation.TabSwitchDelayMs.Value));
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
        if (AreCachedMapStashPageTabsByNumberValid(pageTabContainer, _lastAutomationMapStashPageTabsByNumber))
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

        if (IsReusableMapStashPageTabContainer(openLeftPanel, _lastAutomationMapStashPageTabContainer))
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
        if (TryRememberMapStashPageContentRoot(openLeftPanel, pageContent, "fixed path"))
        {
            return pageContent;
        }

        if (IsReusableMapStashPageContentRoot(_lastAutomationMapStashPageContentRoot))
        {
            return _lastAutomationMapStashPageContentRoot;
        }

        var persistedContentRoot = TryResolvePersistedMapStashElementPath(
            openLeftPanel,
            GetAutomationDynamicHints()?.MapStashPageContentRootPath,
            IsReusableMapStashPageContentRoot,
            "map stash page content root");
        if (persistedContentRoot != null)
        {
            if (TryRememberMapStashPageContentRoot(openLeftPanel, persistedContentRoot, "persisted path"))
            {
                return persistedContentRoot;
            }

            _lastAutomationMapStashPageContentRoot = null;
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

            if (TryRememberMapStashPageContentRoot(openLeftPanel, dynamicContent, $"dynamic attempt {attempt + 1}"))
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
        _lastAutomationMapStashPageContentLogSignature = null;
        _lastAutomationMapStashPageTabsLogSignature = null;
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

    private bool IsReusableMapStashPageTabContainer(Element root, Element element)
    {
        return IsElementAttachedToRoot(root, element) && CountValidMapStashPageTabs(element) >= 6;
    }

    private bool AreCachedMapStashPageTabsByNumberValid(Element pageTabContainer, IReadOnlyDictionary<int, Element> pageTabsByNumber)
    {
        if (pageTabContainer == null || pageTabsByNumber == null || pageTabsByNumber.Count <= 0)
        {
            return false;
        }

        foreach (var entry in pageTabsByNumber)
        {
            if (!ReferenceEquals(entry.Value?.Parent, pageTabContainer))
            {
                return false;
            }

            if ((entry.Value.Parent?.Children?.IndexOf(entry.Value) ?? -1) < 0)
            {
                return false;
            }

            if (GetMapStashPageNumber(entry.Value) != entry.Key)
            {
                return false;
            }
        }

        return true;
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

    private List<int> TryPersistMapStashElementPath(
        Element root,
        Element target,
        Func<StashAutomationDynamicHintSettings, List<int>> getter,
        Action<StashAutomationDynamicHintSettings, List<int>> setter,
        string label)
    {
        var hints = GetAutomationDynamicHints();
        if (root == null || target == null || hints == null || getter == null || setter == null)
        {
            return null;
        }

        var resolvedPath = TryFindPathFromRoot(root, target);
        if (resolvedPath == null || resolvedPath.Count <= 0)
        {
            return null;
        }

        var existingPath = getter(hints);
        if (existingPath != null && existingPath.SequenceEqual(resolvedPath))
        {
            LogAutomationDebug($"Persisted {label} path unchanged ({DescribePath(resolvedPath)}); skipping settings snapshot save.");
            return resolvedPath;
        }

        setter(hints, resolvedPath);
        LogAutomationDebug($"Persisted {label} path {DescribePath(resolvedPath)}");
        TrySaveSettingsSnapshot();
        return resolvedPath;
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

    private bool IsElementAttachedToRoot(Element root, Element target)
    {
        return TryFindPathFromRoot(root, target) != null;
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

    private bool TryRememberMapStashPageContentRoot(Element root, Element element, string source)
    {
        if (!IsMapStashPageContentCandidate(element))
        {
            return false;
        }

        _lastAutomationMapStashPageContentRoot = element;
        var persistedPath = TryPersistMapStashElementPath(
            root,
            element,
            hints => hints.MapStashPageContentRootPath,
            (hints, path) => hints.MapStashPageContentRootPath = path,
            "map stash page content root");
        var logSignature = persistedPath != null
            ? DescribePath(persistedPath)
            : DescribeRect(element.GetClientRect());
        if (string.Equals(_lastAutomationMapStashPageContentLogSignature, logSignature, StringComparison.Ordinal))
        {
            return true;
        }

        _lastAutomationMapStashPageContentLogSignature = logSignature;
        if (persistedPath == null)
        {
            LogAutomationDebug($"Could not capture map stash page content root path from discovery root. source={source}, root={DescribeElement(root)}, content={DescribeElement(element)}");
        }
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
        var timing = AutomationTiming;
        var startedAt = DateTime.UtcNow;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(
            timing.QuantityChangeBaseDelayMs,
            automation.ClickDelayMs.Value + timing.QuantityChangeBaseDelayMs + GetServerLatencyMs()));

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

            await DelayAutomationAsync(timing.FastPollDelayMs);
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

    #endregion

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

        Input.KeyUp(Keys.ControlKey);
        Input.KeyUp(Keys.LControlKey);

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
        var playerInventory = GameController?.Game?.IngameState?.ServerData?.PlayerInventories[(int)InventorySlotE.MainInventory1]?.Inventory;
        if (playerInventory == null || playerInventory.Columns <= 0 || playerInventory.Rows <= 0)
        {
            return 0;
        }

        var occupiedSlots = new bool[playerInventory.Columns, playerInventory.Rows];
        foreach (var inventoryItem in playerInventory.InventorySlotItems)
        {
            var startX = Math.Max(0, inventoryItem.PosX);
            var startY = Math.Max(0, inventoryItem.PosY);
            var endX = Math.Min(playerInventory.Columns, inventoryItem.PosX + inventoryItem.SizeX);
            var endY = Math.Min(playerInventory.Rows, inventoryItem.PosY + inventoryItem.SizeY);

            for (var x = startX; x < endX; x++)
                for (var y = startY; y < endY; y++)
                    occupiedSlots[x, y] = true;
        }

        var freeCellCount = 0;
        for (var x = 0; x < playerInventory.Columns; x++)
            for (var y = 0; y < playerInventory.Rows; y++)
                if (!occupiedSlots[x, y])
                    freeCellCount++;

        return freeCellCount;
    }

    private IList<NormalInventoryItem> GetVisiblePlayerInventoryItems()
    {
        return GameController?.IngameState?.IngameUi?.InventoryPanel[InventoryIndex.PlayerInventory]?.VisibleInventoryItems;
    }

    private int GetVisiblePlayerInventoryMatchingQuantity(string metadata)
    {
        return CountMatchingItemQuantity(GetVisiblePlayerInventoryItems(), metadata);
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

    private async Task CloseStashForBestiaryAutomationAsync()
    {
        if (GameController?.IngameState?.IngameUi?.StashElement?.IsVisible != true)
        {
            return;
        }

        var timing = AutomationTiming;
        await TapKeyAsync(Keys.Space, timing.KeyTapDelayMs, timing.FastPollDelayMs);
        if (!await WaitForBestiaryConditionAsync(
                () => GameController?.IngameState?.IngameUi?.StashElement?.IsVisible != true,
                1000))
        {
            throw new InvalidOperationException("Timed out closing the stash before reopening the Bestiary window.");
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

    #region Quantity wait helpers

    private async Task<int> WaitForMapStashPageQuantityToChangeAsync(string metadata, int previousQuantity)
    {
        return await WaitForQuantityToChangeAsync(metadata, previousQuantity, () =>
        {
            var visibleItems = GetVisibleMapStashPageItems();
            return visibleItems == null ? (int?)null : CountMatchingMapStashPageItems(visibleItems, metadata);
        }, MapTransferExtraConfirmationDelayMs);
    }

    private async Task<int> WaitForMapStashPageQuantityToSettleAsync(string metadata, int previousQuantity)
    {
        return await WaitForQuantityToSettleAsync(metadata, previousQuantity, () =>
        {
            var visibleItems = GetVisibleMapStashPageItems();
            return visibleItems == null ? (int?)null : CountMatchingMapStashPageItems(visibleItems, metadata);
        }, MapTransferExtraConfirmationDelayMs);
    }

    private async Task<int> WaitForQuantityToChangeAsync(string metadata, int previousQuantity, Func<int?> quantityProvider, int extraBaseDelayMs = 0)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return previousQuantity;
        }

        var automation = Settings.StashAutomation;
        var timing = AutomationTiming;
        var startedAt = DateTime.UtcNow;
        var normalizedExtraBaseDelayMs = Math.Max(0, extraBaseDelayMs);
        var pollDelayMs = timing.FastPollDelayMs;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(
            timing.QuantityChangeBaseDelayMs + normalizedExtraBaseDelayMs,
            automation.ClickDelayMs.Value + timing.QuantityChangeBaseDelayMs + normalizedExtraBaseDelayMs));
        int? pendingQuantity = null;
        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();
            var currentQuantity = quantityProvider();
            if (!currentQuantity.HasValue)
            {
                pendingQuantity = null;
                await DelayAutomationAsync(pollDelayMs);
                continue;
            }

            if (currentQuantity.Value < previousQuantity)
            {
                return currentQuantity.Value;
            }

            if (currentQuantity.Value == previousQuantity)
            {
                pendingQuantity = null;
                await DelayAutomationAsync(pollDelayMs);
                continue;
            }

            if (pendingQuantity == currentQuantity.Value)
            {
                return currentQuantity.Value;
            }

            pendingQuantity = currentQuantity.Value;
            await DelayAutomationAsync(pollDelayMs);
        }

        return previousQuantity;
    }

    private async Task<int> WaitForQuantityToSettleAsync(string metadata, int previousQuantity, Func<int?> quantityProvider, int extraBaseDelayMs = 0)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return previousQuantity;
        }

        var automation = Settings.StashAutomation;
        var timing = AutomationTiming;
        var startedAt = DateTime.UtcNow;
        var normalizedExtraBaseDelayMs = Math.Max(0, extraBaseDelayMs);
        var pollDelayMs = timing.FastPollDelayMs;
        var timeoutMs = GetAutomationTimeoutMs(Math.Max(
            timing.QuantityChangeBaseDelayMs + normalizedExtraBaseDelayMs,
            automation.ClickDelayMs.Value + timing.QuantityChangeBaseDelayMs + normalizedExtraBaseDelayMs));
        var stableWindowMs = Math.Max(QuantitySettleStableWindowMs, pollDelayMs * 3);
        var changedQuantity = previousQuantity;
        var hasObservedChange = false;
        DateTime? lastChangeAtUtc = null;

        while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
        {
            ThrowIfAutomationStopRequested();
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
                lastChangeAtUtc = DateTime.UtcNow;
                await DelayAutomationAsync(pollDelayMs);
                continue;
            }

            if (currentQuantity.Value == changedQuantity)
            {
                if (lastChangeAtUtc.HasValue && (DateTime.UtcNow - lastChangeAtUtc.Value).TotalMilliseconds >= stableWindowMs)
                {
                    return currentQuantity.Value;
                }

                await DelayAutomationAsync(pollDelayMs);
                continue;
            }

            changedQuantity = currentQuantity.Value;
            lastChangeAtUtc = DateTime.UtcNow;
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

    #endregion

    #region Diagnostics

    private void UpdateAutomationStatus(string message, bool forceLog = false)
    {
        if (!forceLog && string.Equals(_lastAutomationStatusMessage, message, StringComparison.Ordinal))
        {
            return;
        }

        _lastAutomationStatusMessage = message;
        LogAutomationDebug($"STATUS: {message}");
    }

    private void LogAutomationDebug(string message)
    {
        WriteAutomationLog(message, requireDebugLogging: true);
    }

    private void LogAutomationError(string message, Exception ex)
    {
        var errorMessage = ex == null
            ? message
            : $"{message} {ex.GetType().Name}: {ex.Message}";
        WriteAutomationLog($"ERROR: {errorMessage}", requireDebugLogging: false);
    }

    private void WriteAutomationLog(string message, bool requireDebugLogging)
    {
        if (requireDebugLogging && Settings?.DebugLogging?.Value != true)
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

    #endregion
}