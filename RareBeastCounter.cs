using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.Components;
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
    private const string MissingTrackedBeastName = "\0";
    private static readonly GameStat? IsCapturableMonsterStat = TryGetCapturableMonsterStat();
    private static readonly Regex QuestProgressRegex = new(@"\((\d+)/(\d+)\)", RegexOptions.Compiled);
    private static readonly TrackedBeast[] AllRedBeasts =
    [
        // Craicic (The Deep)
        new("Craicic Chimeral",      ["GemFrogBestiary"],           "cic c"),
        new("Craicic Spider Crab",   ["CrabSpiderBestiary"],        "c sp"),
        new("Craicic Maw",           ["FrogBestiary"],              "cic m"),
        new("Craicic Sand Spitter",  ["SandSpitterBestiary"],       "c san"),
        new("Craicic Savage Crab",   ["CrabParasiteLargeBestiary"], "c sav"),
        new("Craicic Shield Crab",   ["ShieldCrabBestiary"],        "c sh"),
        new("Craicic Squid",         ["SeaWitchSpawnBestiary"],     "sq"),
        new("Craicic Vassal",        ["ParasiticSquidBestiary"],    "c v"),
        new("Craicic Watcher",       ["SquidBestiary"],             "c wa"),

        // Farric (The Wilds) — Chieftain must precede Ape so "BestiaryMonkey" substring matches correctly
        new("Farric Tiger Alpha",         ["TigerBestiary"],             "c ti"),
        new("Farric Wolf Alpha",          ["WolfBestiary"],              "f a"),
        new("Farric Lynx Alpha",          ["LynxBestiary"],              "c l"),
        new("Farric Flame Hellion Alpha", ["HellionBestiary"],           "c fl"),
        new("Farric Magma Hound",         ["HoundBestiary"],             "ma h"),
        new("Farric Pit Hound",           ["PitbullBestiary"],           "c pi"),
        new("Farric Chieftain",           ["BestiaryMonkeyChiefBlood"], "rric c"),
        new("Farric Ape",                 ["BestiaryMonkey", "MonkeyBloodBestiary"], "c a"),
        new("Farric Goliath",             ["BestiarySpiker"],            "c gol"),
        new("Farric Goatman",             ["GoatmanLeapSlamBestiary"],  "c goa"),
        new("Farric Gargantuan",          ["BeastCaveBestiary"],        "c ga"),
        new("Farric Taurus",              ["BestiaryBull"],             "ic ta"),
        new("Farric Ursa",                ["DropBearBestiary"],         "c u"),
        new("Vicious Hound",              ["PurgeHoundBestiary"],       "s ho"),

        // Fenumal (The Caverns)
        new("Fenumal Hybrid Arachnid",  ["SpiderPlatedBestiary"],   "l hy"),
        new("Fenumal Plagued Arachnid", ["SpiderPlagueBestiary"],   "l pla"),
        new("Fenumal Devourer",         ["RootSpiderBestiary"],     "mal d"),
        new("Fenumal Queen",            ["InsectSpawnerBestiary"],  "l q"),
        new("Fenumal Widow",            ["Spider5Bestiary"],        "l w"),
        new("Fenumal Scorpion",         ["BlackScorpionBestiary"],  "l sco"),
        new("Fenumal Scrabbler",        ["SandLeaperBestiary"],     "l scr"),

        // Saqawine (The Sands)
        new("Saqawine Rhex",        ["MarakethBirdBestiary"], "e rhe"),
        new("Saqawine Vulture",     ["VultureBestiary"],      "e vu"),
        new("Saqawine Cobra",       ["SnakeBestiary"],        "ne co"),
        new("Saqawine Blood Viper", ["SnakeBestiary2"],       "ne b"),
        new("Saqawine Retch",       ["KiwethBestiary"],       "ne re"),
        new("Saqawine Rhoa",        ["RhoaBestiary"],         "ine rho"),
        new("Saqawine Chimeral",    ["IguanaBestiary"],       "ne ch"),

        // Spirit Bosses
        new("Saqawal, First of the Sky",    ["MarakethBirdSpiritBoss"],         "al, f"),
        new("Craiceann, First of the Deep", ["NessaCrabBestiarySpiritBoss"],    "n, f"),
        new("Farrul, First of the Plains",  ["TigerBestiarySpiritBoss"],        "ul, f"),
        new("Fenumus, First of the Night",  ["SpiderPlatedBestiarySpiritBoss"], "s, f"),

        // Harvest T3 & special
        new("Wild Bristle Matron",   ["HarvestBeastT3"],            "le m"),
        new("Wild Hellion Alpha",    ["HarvestHellionT3"],          "ld h"),
        new("Wild Brambleback",      ["HarvestBrambleHulkT3"],      "d bra"),
        new("Primal Cystcaller",     ["HarvestGoatmanT3"],          "cy"),
        new("Primal Rhex Matriarch", ["HarvestRhexT3"],             "x ma"),
        new("Primal Crushclaw",      ["HarvestNessaCrabT3"],        "l cru"),
        new("Vivid Vulture",         ["HarvestVultureParasiteT3"],  "id v"),
        new("Vivid Watcher",         ["HarvestSquidT3"],            "id w"),
        new("Vivid Abberarach",      ["HarvestPlatedScorpionT3"],   "d ab"),
        new("Black Mórrigan",        ["GullGoliathBestiary", "Morrigan"], "k m"),
    ];
    private static readonly (string Pattern, string BeastName)[] TrackedBeastMetadataLookup =
        AllRedBeasts.SelectMany(beast => beast.MetadataPatterns.Select(pattern => (pattern, beast.Name))).ToArray();

    private static readonly string[] DefaultEnabledBeasts =
    [
        "Farrul, First of the Plains",
        "Fenumus, First of the Night",
        "Vivid Vulture",
        "Wild Bristle Matron",
        "Wild Hellion Alpha",
        "Wild Brambleback",
        "Craicic Chimeral",
        "Fenumal Plagued Arachnid",
        "Vicious Hound",
        "Black Mórrigan",
    ];

    private readonly HashSet<long> _countedRareBeastIds = new();
    private readonly Dictionary<long, Entity> _trackedBeastEntities = new();
    private readonly Dictionary<string, string> _trackedBeastNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TrackedBeastRenderInfo> _trackedBeastRenderBuffer = new();
    private readonly Dictionary<string, int> _valuableBeastCounts = AllRedBeasts.ToDictionary(x => x.Name, _ => 0);
    private bool _analyticsCollapsed;

    private int _rareBeastsFound;
    private int _sessionBeastsFound;
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

    private enum BeastCaptureState
    {
        None,
        Capturing,
        Captured,
    }

    private readonly record struct TrackedBeastRenderInfo(Entity Entity, Positioned Positioned, string BeastName, BeastCaptureState CaptureState);

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
        Settings.AnalyticsWindow.ResetMapAverage.OnPressed = ResetMapAverageAnalytics;
        Settings.BeastPrices.FetchPrices.OnPressed = async () => await FetchBeastPricesAsync();
        Settings.BeastPrices.BeastPickerPanel.DrawDelegate = DrawBeastPickerPanel;

        if (Settings.BeastPrices.EnabledBeasts.Count == 0)
        {
            foreach (var name in DefaultEnabledBeasts)
                Settings.BeastPrices.EnabledBeasts.Add(name);
        }

        Task.Run(FetchBeastPricesAsync);
    }

    public override void AreaChange(AreaInstance area)
    {
        var now = DateTime.UtcNow;
        var newAreaHash = RareBeastCounterHelpers.TryGetAreaHashText(area);
        var newAreaTrackable = area is { IsTown: false, IsHideout: false };

        _trackedBeastEntities.Clear();
        _routeNeedsRegen = true;
        CancelBeastPaths();
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
        if (!IsRareBeast(entity)) return;
        _trackedBeastEntities[entity.Id] = entity;
        if (_countedRareBeastIds.Add(entity.Id))
        {
            _rareBeastsFound++;
            RegisterSessionRareBeast(entity);
        }
    }

    public override void EntityRemoved(Entity entity)
    {
        _trackedBeastEntities.Remove(entity.Id);
    }

    private IReadOnlyList<TrackedBeastRenderInfo> CollectTrackedBeastRenderInfo()
    {
        _trackedBeastRenderBuffer.Clear();

        foreach (var (_, entity) in _trackedBeastEntities)
        {
            if (!entity.IsValid) continue;
            if (!TryGetTrackedBeastNameCached(entity.Metadata, out var beastName)) continue;
            if (Settings.MapRender.ShowEnabledOnly.Value && !Settings.BeastPrices.EnabledBeasts.Contains(beastName)) continue;

            var positioned = entity.GetComponent<Positioned>();
            if (positioned == null) continue;

            _trackedBeastRenderBuffer.Add(new TrackedBeastRenderInfo(
                entity,
                positioned,
                beastName,
                GetBeastCaptureState(entity)));
        }

        return _trackedBeastRenderBuffer;
    }

    public override void Render()
    {
        var now = DateTime.UtcNow;
        ApplyPauseMenuTimerState(now);
        ApplyBestiaryClipboard();

        var autoRefreshMinutes = Settings.BeastPrices.AutoRefreshMinutes.Value;
        if (autoRefreshMinutes > 0 && !_isFetchingPrices &&
            (now - _lastPriceFetchAttempt).TotalMinutes >= autoRefreshMinutes)
        {
            Task.Run(FetchBeastPricesAsync);
        }

        var shouldCollectTrackedBeastRenderInfo =
            Settings.MapRender.ShowBeastLabelsInWorld.Value ||
            Settings.MapRender.ShowBeastsOnMap.Value ||
            Settings.MapRender.ShowTrackedBeastsWindow.Value;
        IReadOnlyList<TrackedBeastRenderInfo> trackedBeasts = Array.Empty<TrackedBeastRenderInfo>();
        if (shouldCollectTrackedBeastRenderInfo)
        {
            trackedBeasts = CollectTrackedBeastRenderInfo();
        }

        if (Settings.MapRender.ShowBeastLabelsInWorld.Value && trackedBeasts.Count > 0) DrawInWorldBeasts(trackedBeasts);
        if (Settings.MapRender.ShowBeastsOnMap.Value ||
            Settings.MapRender.ExplorationRoute.ShowExplorationRoute.Value ||
            Settings.MapRender.ExplorationRoute.ShowPathsToBeasts.Value ||
            Settings.MapRender.ExplorationRoute.ShowCoverageOnMiniMap.Value)
            DrawBeastsOnLargeMap(trackedBeasts);
        if (Settings.MapRender.ShowStylePreviewWindow.Value) DrawMapRenderStylePreviewWindow();
        if (Settings.MapRender.ShowTrackedBeastsWindow.Value && trackedBeasts.Count > 0) DrawTrackedBeastsWindow(trackedBeasts);
        if (Settings.MapRender.ShowPricesInInventory.Value) DrawInventoryBeasts();
        if (Settings.MapRender.ShowPricesInStash.Value) DrawStashBeasts();
        DrawMerchantBeasts();
        if (Settings.MapRender.ShowPricesInBestiary.Value) DrawBestiaryPanelPrices();

        var shouldRenderCounterAndMessage = ShouldRenderCounterAndMessageOverlays();
        var shouldRenderAnalytics = ShouldRenderAnalyticsOverlay();

        if (!shouldRenderCounterAndMessage && !(shouldRenderAnalytics && Settings.AnalyticsWindow.Show.Value))
        {
            return;
        }

        if (shouldRenderCounterAndMessage)
        {
            BuildCounterDisplay(out var counterText, out var allBeastsFound);
            var completedCounter = Settings.CounterWindow.CompletedStyle;
            var completedMessage = Settings.CounterWindow.CompletedMessage;
            var showCompletedCounterStyle = allBeastsFound || completedCounter.ShowWhileNotComplete.Value;

            var counterTextColor = showCompletedCounterStyle ? completedCounter.TextColor.Value : Settings.CounterWindow.TextColor.Value;
            var counterBorderColor = showCompletedCounterStyle ? completedCounter.BorderColor.Value : Settings.CounterWindow.BorderColor.Value;
            var counterTextScale = showCompletedCounterStyle ? completedCounter.TextScale.Value : Settings.CounterWindow.TextScale.Value;

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
                completedMessage.Show.Value &&
                !string.IsNullOrWhiteSpace(completedMessage.Text.Value) &&
                (allBeastsFound || completedMessage.ShowWhileNotComplete.Value);

            if (shouldShowCompletedMessage)
            {
                DrawOverlayWindow(
                    "##RareBeastCounterCompletedMessageOverlay",
                    completedMessage.Text.Value,
                    completedMessage.XPos.Value,
                    completedMessage.YPos.Value,
                    completedMessage.Padding.Value,
                    completedMessage.BorderThickness.Value,
                    completedMessage.BorderRounding.Value,
                    completedMessage.TextScale.Value,
                    completedMessage.TextColor.Value,
                    completedMessage.BorderColor.Value,
                    completedMessage.BackgroundColor.Value);
            }
        }

        if (shouldRenderAnalytics && Settings.AnalyticsWindow.Show.Value)
        {
            DrawAnalyticsWindow();
        }
    }

    private void DrawAnalyticsWindow()
    {
        var includeBeastBreakdown = !_analyticsCollapsed;
        var allLines = BuildAnalyticsLines(includeBeastBreakdown);
        if (allLines.Count == 0) return;

        var displayText = includeBeastBreakdown
            ? string.Join("\n", allLines)
            : allLines[0];

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

        var position = new Vector2(anchor.X - estimatedWindowSize.X / 2f, anchor.Y);

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
            var regex = Settings.BestiaryClipboard.UseAutoRegex.Value
                ? BuildAutoRegexFromEnabledBeasts()
                : (Settings.BestiaryClipboard.BeastRegex.Value ?? string.Empty);
            ImGui.SetClipboardText(regex);
        }

        _wasBestiaryTabVisible = isVisible;
    }

    private string BuildAutoRegexFromEnabledBeasts()
    {
        var enabled = Settings.BeastPrices.EnabledBeasts;
        if (enabled.Count == 0) return string.Empty;

        var enabledBeastSet = new HashSet<string>(enabled);
        var builder = new StringBuilder();

        foreach (var beast in AllRedBeasts)
        {
            if (!enabledBeastSet.Contains(beast.Name) || string.IsNullOrEmpty(beast.RegexFragment))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('|');
            }

            builder.Append(beast.RegexFragment);
        }

        return builder.ToString();
    }

    private readonly record struct TrackedBeast(string Name, string[] MetadataPatterns, string RegexFragment = "");
}