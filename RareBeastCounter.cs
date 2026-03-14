using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace RareBeastCounter;

public class RareBeastCounter : BaseSettingsPlugin<RareBeastCounterSettings>
{
    private const string CounterLabel = "Rare Beasts Found";
    private static readonly GameStat? IsCapturableMonsterStat = TryGetCapturableMonsterStat();
    private static readonly Regex QuestProgressRegex = new(@"\((\d+)/(\d+)\)", RegexOptions.Compiled);
    private readonly HashSet<long> _countedRareBeastIds = new();
    private int _rareBeastsFound;

    public RareBeastCounter()
    {
        Name = "Rare Beast Counter";
    }

    public override void AreaChange(AreaInstance area)
    {
        ResetCounter();
    }

    public override void EntityAdded(Entity entity)
    {
        if (IsRareBeast(entity) && _countedRareBeastIds.Add(entity.Id))
        {
            _rareBeastsFound++;
        }
    }

    public override void Render()
    {
        if (!ShouldRenderOverlays())
        {
            return;
        }

        BuildCounterDisplay(out var counterText, out var allBeastsFound);
        var showCompletedCounterStyle = allBeastsFound || Settings.CompletedCounter.ShowWhileNotComplete.Value;

        var counterTextColor = showCompletedCounterStyle ? Settings.CompletedCounter.TextColor.Value : Settings.CounterWindow.TextColor.Value;
        var counterBorderColor = showCompletedCounterStyle ? Settings.CompletedCounter.BorderColor.Value : Settings.CounterWindow.BorderColor.Value;
        var counterTextScale = showCompletedCounterStyle ? Settings.CompletedCounter.TextScale.Value : Settings.CounterWindow.TextScale.Value;

        DrawOverlayWindow(
            "##RareBeastCounterOverlay",
            counterText,
            Settings.CounterWindow.XPos.Value,
            Settings.CounterWindow.YPos.Value,
            Settings.CounterWindow.Padding.Value,
            Settings.CounterWindow.BorderThickness.Value,
            Settings.CounterWindow.BorderRounding.Value,
            counterTextScale,
            counterTextColor,
            counterBorderColor,
            Settings.CounterWindow.BackgroundColor.Value);

        var shouldShowCompletedMessage =
            Settings.CompletedMessageWindow.Show.Value &&
            !string.IsNullOrWhiteSpace(Settings.CompletedMessageWindow.Text.Value) &&
            (allBeastsFound || Settings.CompletedMessageWindow.ShowWhileNotComplete.Value);

        if (shouldShowCompletedMessage)
        {
            DrawOverlayWindow(
                "##RareBeastCounterCompletedMessageOverlay",
                Settings.CompletedMessageWindow.Text.Value,
                Settings.CompletedMessageWindow.XPos.Value,
                Settings.CompletedMessageWindow.YPos.Value,
                Settings.CompletedMessageWindow.Padding.Value,
                Settings.CompletedMessageWindow.BorderThickness.Value,
                Settings.CompletedMessageWindow.BorderRounding.Value,
                Settings.CompletedMessageWindow.TextScale.Value,
                Settings.CompletedMessageWindow.TextColor.Value,
                Settings.CompletedMessageWindow.BorderColor.Value,
                Settings.CompletedMessageWindow.BackgroundColor.Value);
        }
    }

    private bool ShouldRenderOverlays()
    {
        var ingameState = GameController?.IngameState;
        var ingameUi = ingameState?.IngameUi;
        if (ingameState == null || ingameUi == null)
        {
            return false;
        }

        if (Settings.Visibility.HideInHideout.Value && GameController.Area?.CurrentArea?.IsHideout == true)
        {
            return false;
        }

        if (Settings.Visibility.HideOnFullscreenPanels.Value && ingameUi.FullscreenPanels.Any(x => x.IsVisible))
        {
            return false;
        }

        return true;
    }

    private void DrawOverlayWindow(
        string windowId,
        string text,
        float xPosPercent,
        float yPosPercent,
        float padding,
        int borderThickness,
        float borderRounding,
        float textScale,
        Color textColor,
        Color borderColor,
        Color backgroundColor)
    {
        var windowRect = GameController.Window.GetWindowRectangle();
        var anchor = new Vector2(
            windowRect.Width * (xPosPercent / 100f),
            windowRect.Height * (yPosPercent / 100f));

        var baseTextSize = ImGui.CalcTextSize(text);
        var estimatedWindowSize = new Vector2(
            baseTextSize.X * textScale + padding * 2,
            baseTextSize.Y * textScale + padding * 2);

        var position = new Vector2(anchor.X - estimatedWindowSize.X / 2f, anchor.Y);

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(estimatedWindowSize, ImGuiCond.Always);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, ToImGuiColor(backgroundColor));
        ImGui.PushStyleColor(ImGuiCol.Border, ToImGuiColor(borderColor));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, borderRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, borderThickness);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoMove;

        ImGui.Begin(windowId, flags);
        ImGui.SetWindowFontScale(textScale);
        ImGui.TextColored(ToImGuiColor(textColor), text);
        ImGui.SetWindowFontScale(1f);
        ImGui.End();

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    private void ResetCounter()
    {
        _countedRareBeastIds.Clear();
        _rareBeastsFound = 0;
    }

    private static bool IsRareBeast(Entity entity)
    {
        return entity.Rarity == MonsterRarity.Rare &&
               IsCapturableMonsterStat is { } capturableStat &&
               entity.Stats?.ContainsKey(capturableStat) == true;
    }

    private static GameStat? TryGetCapturableMonsterStat()
    {
        return Enum.TryParse<GameStat>("IsCapturableMonster", out var stat) ? stat : null;
    }

    private void BuildCounterDisplay(out string text, out bool allBeastsFound)
    {
        allBeastsFound = false;

        if (TryGetBeastQuestProgress(out _, out var totalBeasts) && totalBeasts > 0)
        {
            text = $"{CounterLabel}: {_rareBeastsFound}/{totalBeasts}";
            allBeastsFound = _rareBeastsFound >= totalBeasts;
            return;
        }

        text = $"{CounterLabel}: {_rareBeastsFound}";
    }

    private bool TryGetBeastQuestProgress(out int current, out int total)
    {
        current = 0;
        total = 0;

        var questTracker = GetQuestTracker();
        if (questTracker == null)
        {
            return false;
        }

        if (TryParseBeastQuestProgress(GetPrimaryQuestText(questTracker), out current, out total))
        {
            return true;
        }

        var questEntries = GetQuestEntriesContainer(questTracker)?.Children;
        if (questEntries == null)
        {
            return false;
        }

        foreach (var questEntry in questEntries)
        {
            if (questEntry?.IsVisible != true)
            {
                continue;
            }

            if (TryParseBeastQuestProgress(GetQuestEntryText(questEntry), out current, out total))
            {
                return true;
            }
        }

        return false;
    }

    private Element GetQuestTracker()
    {
        return GameController?.IngameState?.IngameUi?.GetChildAtIndex(4);
    }

    private static Element GetQuestEntriesContainer(Element questTracker)
    {
        return questTracker
            .GetChildAtIndex(0)
            ?.GetChildAtIndex(0)
            ?.GetChildAtIndex(0);
    }

    private static string GetPrimaryQuestText(Element questTracker)
    {
        return GetQuestEntryText(GetQuestEntriesContainer(questTracker)?.GetChildAtIndex(0));
    }

    private static string GetQuestEntryText(Element questEntry)
    {
        return questEntry
            ?.GetChildAtIndex(0)
            ?.GetChildAtIndex(1)
            ?.GetChildAtIndex(0)
            ?.GetChildAtIndex(1)
            ?.Text;
    }

    private static bool TryParseBeastQuestProgress(string questText, out int current, out int total)
    {
        current = 0;
        total = 0;

        if (string.IsNullOrWhiteSpace(questText) ||
            !questText.Contains("beast", StringComparison.OrdinalIgnoreCase) &&
            !questText.Contains("einhar", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = QuestProgressRegex.Match(questText);
        if (!match.Success)
        {
            return false;
        }

        current = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        total = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static Vector4 ToImGuiColor(Color color)
    {
        return new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }
}
