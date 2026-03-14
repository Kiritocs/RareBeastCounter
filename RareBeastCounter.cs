using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace RareBeastCounter;

public partial class RareBeastCounter : BaseSettingsPlugin<RareBeastCounterSettings>
{
    private const string CounterLabel = "Beasts Found";
    private const string MapTimePrefix = "Map Time:";
    private static readonly GameStat? IsCapturableMonsterStat = TryGetCapturableMonsterStat();
    private static readonly Regex QuestProgressRegex = new(@"\((\d+)/(\d+)\)", RegexOptions.Compiled);
    private static readonly string[] RedBeastMetadataPatterns =
    [
        // Craicic (The Deep) 9 beasts
        "GemFrogBestiary", "CrabSpiderBestiary", "FrogBestiary", "SandSpitterBestiary", "CrabParasiteLargeBestiary",
        "ShieldCrabBestiary", "SeaWitchSpawnBestiary", "ParasiticSquidBestiary", "SquidBestiary",

        // Farric (The Wilds) 15 beasts
        "TigerBestiary", "WolfBestiary", "LynxBestiary", "HellionBestiary", "HoundBestiary", "PitbullBestiary",
        "BestiaryMonkeyChiefBlood", "BestiaryMonkey", "BestiarySpiker", "GoatmanLeapSlamBestiary", "BeastCaveBestiary",
        "BestiaryBull", "DropBearBestiary", "MonkeyBloodBestiary", "PurgeHoundBestiary",

        // Fenumal (The Caverns) 7 beasts
        "SpiderPlatedBestiary", "SpiderPlagueBestiary", "RootSpiderBestiary", "InsectSpawnerBestiary", "Spider5Bestiary", "BlackScorpionBestiary",
        "SandLeaperBestiary",

        // Saqawine (The Sands) 7 beasts
        "MarakethBirdBestiary", "VultureBestiary", "SnakeBestiary", "SnakeBestiary2", "KiwethBestiary", "RhoaBestiary", "IguanaBestiary",

        // Spirit Bosses 4 beasts
        "MarakethBirdSpiritBoss", "NessaCrabBestiarySpiritBoss", "TigerBestiarySpiritBoss", "SpiderPlatedBestiarySpiritBoss",

        // Harvest & special 10 beasts
        "HarvestBeastT3", "HarvestHellionT3", "HarvestBrambleHulkT3", "HarvestGoatmanT3", "HarvestRhexT3", "HarvestNessaCrabT3",
        "HarvestVultureParasiteT3", "HarvestSquidT3", "HarvestPlatedScorpionT3", "GullGoliathBestiary"
    ];

    private static readonly TrackedBeast[] ValuableTrackedBeasts =
    [
        new("Farrul, First of the Plains",  ["TigerBestiarySpiritBoss"]),
        new("Fenumus, First of the Night",  ["SpiderPlatedBestiarySpiritBoss"]),

        new("Vivid Vulture",       ["HarvestVultureParasiteT3"]),
        new("Wild Bristle Matron", ["HarvestBeastT3"]),
        new("Wild Hellion Alpha",  ["HarvestHellionT3"]),
        new("Wild Brambleback",    ["HarvestBrambleHulkT3"]),

        new("Craicic Chimeral",         ["GemFrogBestiary"]),
        new("Fenumal Plagued Arachnid", ["SpiderPlagueBestiary"]),

        new("Vicious Hound",  ["ViciousHound", "PitbullBestiary"]),
        new("Black Mórrigan", ["GullGoliathBestiary", "Morrigan"]),

        // Yellow (bestiary tier) beasts — capturable but not red
        new("Sand Scorpion",       ["YellowScorpion"],            IsYellow: true),
        new("Goatman Fire-raiser", ["GoatmanShamanFireChampion"], IsYellow: true),
    ];

    private readonly HashSet<long> _countedRareBeastIds = new();
    private readonly HashSet<long> _sessionProcessedRareBeastIds = new();
    private readonly Dictionary<string, int> _valuableBeastCounts = ValuableTrackedBeasts.ToDictionary(x => x.Name, _ => 0);
    private bool _analyticsCollapsed;

    private int _rareBeastsFound;
    private int _totalRedBeastsSession;
    private DateTime _sessionStartUtc;
    private TimeSpan _sessionPausedDuration = TimeSpan.Zero;
    private DateTime? _pauseMenuSessionStartUtc;
    private DateTime? _currentMapStartUtc;
    private TimeSpan _currentMapElapsed = TimeSpan.Zero;
    private TimeSpan _completedMapsDuration = TimeSpan.Zero;
    private int _completedMapCount;
    private bool _isCurrentAreaTrackable;
    private string _activeMapAreaHash;
    private bool _wasBestiaryTabVisible;
    private Type _cachedGameType;
    private System.Reflection.PropertyInfo _cachedIsEscapeStateProperty;

    public RareBeastCounter()
    {
        Name = "Rare Beast Counter";
    }

    public override void OnLoad()
    {
        _sessionStartUtc = DateTime.UtcNow;

        var currentArea = GameController?.Area?.CurrentArea;
        _isCurrentAreaTrackable = currentArea is { IsTown: false, IsHideout: false };
        if (_isCurrentAreaTrackable)
        {
            _activeMapAreaHash = RareBeastCounterHelpers.TryGetAreaHashText(currentArea);
            _currentMapStartUtc = DateTime.UtcNow;
        }

        Settings.AnalyticsWindow.ResetSession.OnPressed = ResetSessionAnalytics;
        Settings.AnalyticsWindow.SaveSessionToFile.OnPressed = SaveSessionSnapshotToFile;
    }

    public override void AreaChange(AreaInstance area)
    {
        var now = DateTime.UtcNow;
        var newAreaHash = RareBeastCounterHelpers.TryGetAreaHashText(area);
        var newAreaTrackable = area is { IsTown: false, IsHideout: false };

        PauseCurrentMapTimer(now);

        if (!newAreaTrackable)
        {
            _isCurrentAreaTrackable = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_activeMapAreaHash) &&
            string.Equals(newAreaHash, _activeMapAreaHash, StringComparison.Ordinal))
        {
            _isCurrentAreaTrackable = true;
            _currentMapStartUtc = now;
            return;
        }

        FinalizePausedMap();

        _activeMapAreaHash = newAreaHash;
        _currentMapElapsed = TimeSpan.Zero;
        _isCurrentAreaTrackable = true;
        _currentMapStartUtc = now;

        ResetCounter();
    }

    public override void EntityAdded(Entity entity)
    {
        if (IsRareBeast(entity) && _countedRareBeastIds.Add(entity.Id))
        {
            _rareBeastsFound++;
            RegisterSessionRareBeast(entity);
        }
    }

    public override void Render()
    {
        ApplyPauseMenuTimerState(DateTime.UtcNow);
        ApplyBestiaryClipboard();

        var shouldRenderCounterAndMessage = ShouldRenderCounterAndMessageOverlays();
        var shouldRenderAnalytics = ShouldRenderAnalyticsOverlay();

        if (!shouldRenderCounterAndMessage && !(shouldRenderAnalytics && Settings.AnalyticsWindow.Show.Value))
        {
            return;
        }

        if (shouldRenderCounterAndMessage)
        {
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

        if (shouldRenderAnalytics && Settings.AnalyticsWindow.Show.Value)
        {
            DrawAnalyticsWindow();
        }
    }

    private void DrawAnalyticsWindow()
    {
        var allLines = BuildAnalyticsLines();
        if (allLines.Count == 0) return;

        var displayText = _analyticsCollapsed
            ? allLines[0].Trim()
            : string.Join("\n", allLines);

        var s = Settings.AnalyticsWindow;
        var windowRect = GameController.Window.GetWindowRectangle();
        var anchor = new Vector2(
            windowRect.Width * (s.XPos.Value / 100f),
            windowRect.Height * (s.YPos.Value / 100f));

        var baseTextSize = ImGui.CalcTextSize(displayText);
        var estimatedWindowSize = new Vector2(
            baseTextSize.X * s.TextScale.Value + s.Padding.Value * 2,
            baseTextSize.Y * s.TextScale.Value + s.Padding.Value * 2);

        var position = new Vector2(anchor.X, anchor.Y);

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(estimatedWindowSize, ImGuiCond.Always);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, RareBeastCounterHelpers.ToImGuiColor(s.BackgroundColor.Value));
        ImGui.PushStyleColor(ImGuiCol.Border, RareBeastCounterHelpers.ToImGuiColor(s.BorderColor.Value));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, s.BorderRounding.Value);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, s.BorderThickness.Value);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(s.Padding.Value, s.Padding.Value));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoMove;

        ImGui.Begin("##RareBeastCounterAnalyticsOverlay", flags);
        ImGui.SetWindowFontScale(s.TextScale.Value);
        ImGui.TextColored(RareBeastCounterHelpers.ToImGuiColor(s.TextColor.Value), displayText);
        ImGui.SetWindowFontScale(1f);

        if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _analyticsCollapsed = !_analyticsCollapsed;
        }

        ImGui.End();

        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
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

        var position = new Vector2(anchor.X, anchor.Y);

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(estimatedWindowSize, ImGuiCond.Always);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, RareBeastCounterHelpers.ToImGuiColor(backgroundColor));
        ImGui.PushStyleColor(ImGuiCol.Border, RareBeastCounterHelpers.ToImGuiColor(borderColor));
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
        ImGui.TextColored(RareBeastCounterHelpers.ToImGuiColor(textColor), text);
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

    private void ApplyBestiaryClipboard()
    {
        if (!Settings.BestiaryClipboard.EnableAutoCopy.Value)
        {
            _wasBestiaryTabVisible = false;
            return;
        }

        var isVisible = IsBestiaryTabVisible();
        if (isVisible && !_wasBestiaryTabVisible)
        {
            ImGui.SetClipboardText(Settings.BestiaryClipboard.BeastRegex.Value ?? string.Empty);
        }

        _wasBestiaryTabVisible = isVisible;
    }

    private readonly record struct TrackedBeast(string Name, string[] MetadataPatterns, bool IsYellow = false);
}
