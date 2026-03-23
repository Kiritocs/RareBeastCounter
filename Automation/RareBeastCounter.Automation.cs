using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared.Enums;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    #region Constants and state

    private const string MenagerieAreaName = "The Menagerie";
    private const string MenagerieEinharMetadata = "Metadata/NPC/League/Bestiary/EinharMenagerie";
    private const string CapturedMonsterItemPathFragment = "CapturedMonster";
    private const string SettingsFileName = "RareBeastCounter_settings.json";
    private static readonly int[] BestiaryPanelPath = [50, 2, 0, 1, 1, 15];
    private static readonly int[] BestiaryCapturedBeastsTabPath = [50, 2, 0, 1, 1, 15, 0, 18];
    private static readonly int[] BestiaryCapturedBeastsButtonContainerPath = [50, 2, 0, 1, 1, 15, 0, 19];
    private static readonly int[] BestiaryDeleteButtonPathFromBeastRow = [3];
    private static readonly int[] BestiaryDeleteConfirmationWindowPath = [0];
    private static readonly int[] BestiaryDeleteConfirmationOkayButtonPath = [0, 0, 3, 0];
    private static readonly int[] CurrencyShiftClickMenuPath = [0];
    private static readonly int[] CurrencyShiftClickMenuConfirmButtonPath = [0, 1];
    private static readonly int[] CurrencyShiftClickMenuQuantityTextPath = [0, 0, 1];
    private static readonly int[] FragmentStashScarabTabPath = [2, 0, 0, 1, 1, 1, 0, 5, 0, 1];
    private static readonly int[] MapStashTierOneToNineTabPath = [2, 0, 0, 1, 1, 3, 0, 0];
    private static readonly int[] MapStashTierTenToSixteenTabPath = [2, 0, 0, 1, 1, 3, 0, 1];
    private static readonly int[] MapStashPageTabPath = [2, 0, 0, 1, 1, 3, 0, 3, 0];
    private static readonly int[] MapStashPageNumberPath = [0, 1];
    private static readonly int[] MapStashPageContentPath = [2, 0, 0, 1, 1, 3, 0, 4];
    private const int MenagerieTravelTimeoutMs = 15000;
    private const int BestiaryReleaseTimeoutMs = 250;
    private const int MapTransferExtraConfirmationDelayMs = 10;
    private const int QuantitySettleStableWindowMs = 100;
    private static readonly AutomationTimingValues AutomationTiming = new();
    private string _lastAutomationStatusMessage;
    private bool _isAutomationRunning;
    private bool _isBestiaryClearRunning;
    private bool _isAutomationStopRequested;
    private bool? _bestiaryDeleteModeOverride;
    private bool? _bestiaryAutoStashOverride;
    private bool _bestiaryInventoryFullStop;
    private string _activeBestiarySearchRegex;
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
    #region Automation entry points

    private void BeginAutomationRun(bool isBestiaryClearRunning = false)
    {
        _isAutomationRunning = true;
        _isBestiaryClearRunning = isBestiaryClearRunning;
        _isAutomationStopRequested = false;
        ResetAutomationState();
    }

    private void EndAutomationRun(bool clearBestiaryDeleteModeOverride = false)
    {
        _isAutomationRunning = false;
        _isBestiaryClearRunning = false;
        _isAutomationStopRequested = false;

        if (clearBestiaryDeleteModeOverride)
        {
            _bestiaryDeleteModeOverride = null;
        }

        ResetAutomationState();
        ReleaseAutomationModifierKeys();
    }

    private bool TryGetEnabledStashAutomation(out StashAutomationSettings automation)
    {
        automation = Settings?.StashAutomation;
        if (automation?.Enabled.Value == true)
        {
            return true;
        }

        UpdateAutomationStatus("Stash automation is disabled.");
        return false;
    }

    private bool TryGetVisibleStashForAutomation(out StashElement stash)
    {
        stash = GameController?.IngameState?.IngameUi?.StashElement;
        if (stash?.IsVisible == true)
        {
            return true;
        }

        UpdateAutomationStatus("Open the stash before running restock.");
        return false;
    }

    private void LogConfiguredAutomationTargets((string Label, string IdSuffix, StashAutomationTargetSettings Target)[] automationTargets)
    {
        LogAutomationDebug($"Configured targets: {string.Join(" | ", automationTargets.Select(x => $"{x.Label} [{DescribeTarget(x.Target)}]"))}");
    }

    private async Task<bool> EnsureBestiaryItemizingCapacityAsync()
    {
        var availableInventorySlots = GetPlayerInventoryFreeCellCount();
        LogAutomationDebug($"Bestiary clear starting. Can itemize up to {availableInventorySlots} beast{(availableInventorySlots == 1 ? string.Empty : "s")} based on free inventory slots.");

        if (availableInventorySlots > 0)
        {
            return true;
        }

        if (!ShouldAutoStashBestiaryItemizedBeasts())
        {
            _bestiaryInventoryFullStop = true;
            UpdateAutomationStatus("Bestiary clear stopped. Inventory is full and regex itemize auto-stash is disabled.", forceLog: true);
            return false;
        }

        await StashCapturedMonstersAndReturnToBestiaryAsync();
        if (GetPlayerInventoryFreeCellCount() > 0)
        {
            return true;
        }

        UpdateAutomationStatus("Bestiary clear stopped. Inventory is full.", forceLog: true);
        return false;
    }

    private async Task RunStashAutomationAsync()
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

        if (!TryGetVisibleStashForAutomation(out var stash))
        {
            return;
        }

        LogAutomationDebug($"Run started. {DescribeStash(stash)}");

        BeginAutomationRun();
        try
        {
            var automationTargets = GetAutomationTargets(automation);
            LogConfiguredAutomationTargets(automationTargets);
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
            EndAutomationRun();
        }
    }

    private async Task RunStashAutomationFromHotkeyAsync()
    {
        if (_isAutomationRunning)
        {
            RequestAutomationStop();
            return;
        }

        if (!TryGetEnabledStashAutomation(out _))
        {
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

        BeginAutomationRun(isBestiaryClearRunning: true);

        try
        {
            await EnsureTravelToMenagerieAsync();

            await EnsureBestiaryCapturedBeastsWindowOpenAsync();
            EnsureBestiaryCapturedBeastsTabVisible("starting Bestiary clear automation");

            var deleteBeasts = ShouldDeleteBestiaryBeasts();
            if (!deleteBeasts && !await EnsureBestiaryItemizingCapacityAsync())
            {
                return;
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
            EndAutomationRun(clearBestiaryDeleteModeOverride: true);
        }
    }

    private async Task RunBestiaryRegexItemizeAutomationFromHotkeyAsync()
    {
        if (_isAutomationRunning)
        {
            RequestAutomationStop();
            return;
        }

        var regex = GetConfiguredBestiaryRegex();
        if (string.IsNullOrWhiteSpace(regex))
        {
            UpdateAutomationStatus("Bestiary regex itemize stopped. Bestiary Regex is empty.", forceLog: true);
            return;
        }

        _bestiaryDeleteModeOverride = false;
        BeginAutomationRun(isBestiaryClearRunning: true);
        _bestiaryAutoStashOverride = Settings.BestiaryAutomation.RegexItemizeAutoStash.Value;

        try
        {
            await EnsureTravelToMenagerieAsync();

            await EnsureBestiaryCapturedBeastsWindowOpenAsync();

            UpdateAutomationStatus("Applying Bestiary Regex...");
            _activeBestiarySearchRegex = regex;
            await ApplyBestiarySearchRegexAsync(regex);

            if (!await EnsureBestiaryItemizingCapacityAsync())
            {
                return;
            }

            UpdateAutomationStatus("Itemizing Bestiary regex matches...");
            var itemizedBeastCount = await ClearCapturedBeastsAsync();
            if (ShouldAutoStashBestiaryItemizedBeasts())
            {
                await StashCapturedMonstersAndCloseUiAsync();
            }

            UpdateAutomationStatus(_bestiaryInventoryFullStop
                ? $"Bestiary regex itemize stopped. Itemized {itemizedBeastCount} beast{(itemizedBeastCount == 1 ? string.Empty : "s")}. Inventory is full."
                : itemizedBeastCount > 0
                    ? $"Bestiary regex itemize complete. Itemized {itemizedBeastCount} beast{(itemizedBeastCount == 1 ? string.Empty : "s")}."
                    : "Bestiary regex itemize complete. No captured beasts matched the configured Bestiary Regex.", forceLog: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogAutomationError("Bestiary regex itemize failed.", ex);
            UpdateAutomationStatus($"Bestiary regex itemize failed: {ex.Message}");
        }
        finally
        {
            EndAutomationRun(clearBestiaryDeleteModeOverride: true);
        }
    }

    #endregion
    #region Shared automation state

    private async Task EnsureTravelToMenagerieAsync()
    {
        if (IsInMenagerie())
        {
            return;
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            UpdateAutomationStatus("Travelling to The Menagerie...");
            LogAutomationDebug($"Travelling to The Menagerie. attempt={attempt}, currentArea='{GameController?.Area?.CurrentArea?.Name ?? "<null>"}'");

            await SendChatCommandAsync("/menagerie");
            if (await WaitForAreaNameAsync(MenagerieAreaName, MenagerieTravelTimeoutMs) || IsInMenagerie())
            {
                return;
            }

            LogAutomationDebug($"Menagerie travel attempt {attempt} timed out. currentArea='{GameController?.Area?.CurrentArea?.Name ?? "<null>"}'");
            await DelayForUiCheckAsync(250);
        }

        throw new InvalidOperationException($"Timed out travelling to The Menagerie. Current area: '{GameController?.Area?.CurrentArea?.Name ?? "<null>"}'.");
    }

    private void ResetAutomationState()
    {
        _bestiaryAutoStashOverride = null;
        _bestiaryInventoryFullStop = false;
        _activeBestiarySearchRegex = null;
        _lastAutomationFragmentScarabTabIndex = -1;
        _lastAutomationMapStashTierSelection = -1;
        _lastAutomationMapStashPageNumber = -1;
    }

    private bool ShouldAutoStashBestiaryItemizedBeasts()
    {
        return _bestiaryAutoStashOverride ?? true;
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

    private void ReleaseAutomationModifierKeys()
    {
        Input.KeyUp(Keys.ControlKey);
        Input.KeyUp(Keys.LControlKey);
    }

    private bool IsMapStashTarget(StashAutomationTargetSettings target)
    {
        var stash = GameController?.IngameState?.IngameUi?.StashElement;
        return stash?.VisibleStash?.InvType == InventoryType.MapStash && TryGetConfiguredMapTier(target).HasValue;
    }

    #endregion
}
