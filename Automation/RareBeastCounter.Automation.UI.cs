using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
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

        BeginAutomationRun();

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
            EndAutomationRun();
        }
    }

    #endregion
}
