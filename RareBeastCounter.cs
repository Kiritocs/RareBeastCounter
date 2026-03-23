using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json.Linq;
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

    private static readonly TrackedBeast[] AllRedBeasts = RareBeastCounterBeastData.AllRedBeasts;

    private readonly HashSet<long> _countedRareBeastIds = new();
    private readonly Dictionary<long, Entity> _trackedBeastEntities = new();
    private readonly Dictionary<string, string> _trackedBeastNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TrackedBeastRenderInfo> _trackedBeastRenderBuffer = new();
    private readonly List<string> _analyticsLineBuffer = new();
    private readonly StringBuilder _analyticsTextBuilder = new();
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
    private bool _isBestiaryClipboardPasteRunning;
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
        var now = DateTime.UtcNow;
        _sessionStartUtc = now;

        InitializeCurrentAreaTracking(now);

        var analyticsWindow = Settings.AnalyticsWindow;
        analyticsWindow.ResetSession.OnPressed = ResetSessionAnalytics;
        analyticsWindow.SaveSessionToFile.OnPressed = SaveSessionSnapshotToFile;
        analyticsWindow.ResetMapAverage.OnPressed = ResetMapAverageAnalytics;

        var beastPrices = Settings.BeastPrices;
        beastPrices.FetchPrices.OnPressed = QueuePriceFetch;
        beastPrices.BeastPickerPanel.DrawDelegate = DrawBeastPickerPanel;

        var stashAutomation = Settings.StashAutomation;
        InitializeAutomationSettingsUi(stashAutomation);
        InitializeBestiaryAutomationSettingsUi(Settings.BestiaryAutomation);
        InitializeMerchantAutomationSettingsUi(Settings.MerchantAutomation);

        LoadPersistedBeastPriceSettings();
        QueuePriceFetch();
    }

    public override void OnClose()
    {
        base.OnClose();
        SavePersistedBeastPriceSettings();
    }

    private void InitializeCurrentAreaTracking(DateTime now)
    {
        var currentArea = GameController?.Area?.CurrentArea;
        _isCurrentAreaTrackable = currentArea is { IsTown: false, IsHideout: false };
        if (_isCurrentAreaTrackable)
        {
            _activeMapAreaHash = RareBeastCounterHelpers.TryGetAreaHashText(currentArea);
            _currentMapStartUtc = now;
        }
    }

    private void LoadPersistedBeastPriceSettings()
    {
        try
        {
            var settingsPath = GetRareBeastCounterSettingsFilePath();
            if (!File.Exists(settingsPath))
            {
                return;
            }

            var root = JObject.Parse(File.ReadAllText(settingsPath));
            if (root["BeastPrices"] is not JObject beastPricesSection)
            {
                return;
            }

            Settings.BeastPrices.LastUpdated = beastPricesSection["LastUpdated"]?.Value<string>() ?? Settings.BeastPrices.LastUpdated;

            if (beastPricesSection["EnabledBeasts"] is JArray enabledBeastsArray)
            {
                Settings.BeastPrices.EnabledBeasts = new HashSet<string>(
                    enabledBeastsArray.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            DebugWindow.LogMsg($"[RareBeastCounter] Failed to load persisted beast price settings: {ex.Message}");
        }
    }

    private void SavePersistedBeastPriceSettings()
    {
        try
        {
            var settingsPath = GetRareBeastCounterSettingsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

            JObject root;
            if (File.Exists(settingsPath))
            {
                var content = File.ReadAllText(settingsPath);
                root = string.IsNullOrWhiteSpace(content) ? new JObject() : JObject.Parse(content);
            }
            else
            {
                root = new JObject();
            }

            var beastPricesSection = root["BeastPrices"] as JObject ?? new JObject();
            beastPricesSection["LastUpdated"] = Settings.BeastPrices.LastUpdated;
            beastPricesSection["EnabledBeasts"] = new JArray(Settings.BeastPrices.EnabledBeasts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            root["BeastPrices"] = beastPricesSection;

            File.WriteAllText(settingsPath, root.ToString());
        }
        catch (Exception ex)
        {
            DebugWindow.LogMsg($"[RareBeastCounter] Failed to save persisted beast price settings: {ex.Message}");
        }
    }

    private static string GetRareBeastCounterSettingsFilePath()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "config", "global", SettingsFileName);
    }

    private void QueuePriceFetch()
    {
        _ = Task.Run(FetchBeastPricesAsync);
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
        var showEnabledOnly = Settings.MapRender.ShowEnabledOnly.Value;
        var enabledBeasts = Settings.BeastPrices.EnabledBeasts;

        foreach (var (_, entity) in _trackedBeastEntities)
        {
            if (!entity.IsValid) continue;
            if (!TryGetTrackedBeastNameCached(entity.Metadata, out var beastName)) continue;
            if (showEnabledOnly && !enabledBeasts.Contains(beastName)) continue;

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
        HandleAutomationHotkey();
        DrawBestiaryAutomationQuickButtons();
        DrawMenagerieInventoryQuickButton();

        var beastPrices = Settings.BeastPrices;
        var mapRender = Settings.MapRender;
        var analyticsWindow = Settings.AnalyticsWindow;

        TryScheduleAutoPriceRefresh(now, beastPrices);

        var shouldCollectTrackedBeastRenderInfo = ShouldCollectTrackedBeastRenderInfo(mapRender);
        IReadOnlyList<TrackedBeastRenderInfo> trackedBeasts = Array.Empty<TrackedBeastRenderInfo>();
        if (shouldCollectTrackedBeastRenderInfo)
        {
            trackedBeasts = CollectTrackedBeastRenderInfo();
        }

        RenderMapOverlays(mapRender, trackedBeasts);
        RenderPriceOverlays(mapRender);
        RenderAnalyticsOverlay(analyticsWindow);
    }

    private void RenderMapOverlays(MapRenderSettings mapRender, IReadOnlyList<TrackedBeastRenderInfo> trackedBeasts)
    {
        if (mapRender.ShowBeastLabelsInWorld.Value && trackedBeasts.Count > 0)
        {
            DrawInWorldBeasts(trackedBeasts);
        }

        if (ShouldDrawLargeMapOverlay(mapRender))
        {
            DrawBeastsOnLargeMap(trackedBeasts);
        }

        if (mapRender.ShowStylePreviewWindow.Value)
        {
            DrawMapRenderStylePreviewWindow();
        }

        if (mapRender.ShowTrackedBeastsWindow.Value && trackedBeasts.Count > 0)
        {
            DrawTrackedBeastsWindow(trackedBeasts);
        }
    }

    private void RenderPriceOverlays(MapRenderSettings mapRender)
    {
        if (mapRender.ShowPricesInInventory.Value)
        {
            DrawInventoryBeasts();
        }

        if (mapRender.ShowPricesInStash.Value)
        {
            DrawStashBeasts();
        }

        DrawMerchantBeasts();

        if (mapRender.ShowPricesInBestiary.Value)
        {
            DrawBestiaryPanelPrices();
        }
    }

    private void RenderAnalyticsOverlay(AnalyticsWindowSettings analyticsWindow)
    {
        GetOverlayVisibility(out var shouldRenderCounterAndMessage, out var shouldRenderAnalytics);

        if (!shouldRenderCounterAndMessage && !(shouldRenderAnalytics && analyticsWindow.Show.Value))
        {
            return;
        }

        if (shouldRenderCounterAndMessage)
        {
            DrawCounterAndCompletedMessage();
        }

        if (shouldRenderAnalytics && analyticsWindow.Show.Value)
        {
            DrawAnalyticsWindow();
        }
    }

    private void TryScheduleAutoPriceRefresh(DateTime now, BeastPricesSettings beastPrices)
    {
        var autoRefreshMinutes = beastPrices.AutoRefreshMinutes.Value;
        if (autoRefreshMinutes <= 0 || _isFetchingPrices ||
            (now - _lastPriceFetchAttempt).TotalMinutes < autoRefreshMinutes)
        {
            return;
        }

        QueuePriceFetch();
    }

    private void HandleAutomationHotkey()
    {
        var automation = Settings.StashAutomation;

        var bestiaryRegexItemizeHotkey = Settings.BestiaryAutomation.RegexItemizeHotkey;
        if (bestiaryRegexItemizeHotkey.Value != Keys.None && bestiaryRegexItemizeHotkey.PressedOnce())
        {
            LogAutomationDebug($"Bestiary regex itemize hotkey pressed. key={bestiaryRegexItemizeHotkey.Value}");
            _ = RunBestiaryRegexItemizeAutomationFromHotkeyAsync();
            return;
        }

        var bestiaryClearHotkey = Settings.BestiaryAutomation.ClearHotkey;
        if (bestiaryClearHotkey.Value != Keys.None && bestiaryClearHotkey.PressedOnce())
        {
            LogAutomationDebug($"Bestiary clear hotkey pressed. key={bestiaryClearHotkey.Value}");
            _ = RunBestiaryClearAutomationFromHotkeyAsync();
            return;
        }

        var faustusListHotkey = Settings.MerchantAutomation.FaustusListHotkey;
        if (faustusListHotkey.Value != Keys.None && faustusListHotkey.PressedOnce())
        {
            LogAutomationDebug($"Faustus list hotkey pressed. key={faustusListHotkey.Value}");
            _ = RunSellCapturedMonstersToFaustusAsync();
            return;
        }

        var restockHotkey = automation.RestockHotkey;
        if (restockHotkey.Value == Keys.None)
        {
            return;
        }

        if (restockHotkey.PressedOnce())
        {
            LogAutomationDebug($"Restock hotkey pressed. key={restockHotkey.Value}");
            _ = RunStashAutomationFromHotkeyAsync();
        }
    }

    private static bool ShouldCollectTrackedBeastRenderInfo(MapRenderSettings mapRender)
    {
        return mapRender.ShowBeastLabelsInWorld.Value ||
               mapRender.ShowBeastsOnMap.Value ||
               mapRender.ShowTrackedBeastsWindow.Value;
    }

    private static bool ShouldDrawLargeMapOverlay(MapRenderSettings mapRender)
    {
        return mapRender.ShowBeastsOnMap.Value ||
               mapRender.ExplorationRoute.ShowExplorationRoute.Value ||
               mapRender.ExplorationRoute.ShowPathsToBeasts.Value ||
               mapRender.ExplorationRoute.ShowCoverageOnMiniMap.Value;
    }

    private void DrawCounterAndCompletedMessage()
    {
        BuildCounterDisplay(out var counterText, out var allBeastsFound);

        var counterWindow = Settings.CounterWindow;
        var completedCounter = counterWindow.CompletedStyle;
        var completedMessage = counterWindow.CompletedMessage;
        var showCompletedCounterStyle = allBeastsFound || completedCounter.ShowWhileNotComplete.Value;

        if (counterWindow.Show.Value)
        {
            var counterTextColor = showCompletedCounterStyle ? completedCounter.TextColor.Value : counterWindow.TextColor.Value;
            var counterBorderColor = showCompletedCounterStyle ? completedCounter.BorderColor.Value : counterWindow.BorderColor.Value;
            var counterTextScale = showCompletedCounterStyle ? completedCounter.TextScale.Value : counterWindow.TextScale.Value;

            DrawOverlayWindow(
                "##RareBeastCounterOverlay",
                counterText,
                counterWindow.XPos.Value,
                counterWindow.YPos.Value,
                counterWindow.Padding.Value,
                counterWindow.BorderThickness.Value,
                counterWindow.BorderRounding.Value,
                counterTextScale,
                counterTextColor,
                counterBorderColor,
                counterWindow.BackgroundColor.Value);
        }

        var shouldShowCompletedMessage =
            completedMessage.Show.Value &&
            !string.IsNullOrWhiteSpace(completedMessage.Text.Value) &&
            (allBeastsFound || completedMessage.ShowWhileNotComplete.Value);

        if (!shouldShowCompletedMessage)
        {
            return;
        }

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

    private void DrawAnalyticsWindow()
    {
        var includeBeastBreakdown = !_analyticsCollapsed;
        var allLines = _analyticsLineBuffer;
        BuildAnalyticsLines(allLines, includeBeastBreakdown);
        if (allLines.Count == 0) return;

        var displayText = includeBeastBreakdown
            ? BuildAnalyticsDisplayText(allLines)
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

    private string BuildAnalyticsDisplayText(List<string> lines)
    {
        _analyticsTextBuilder.Clear();

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                _analyticsTextBuilder.Append('\n');
            }

            _analyticsTextBuilder.Append(lines[i]);
        }

        return _analyticsTextBuilder.ToString();
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
            var regex = GetConfiguredBestiaryRegex();
            ImGui.SetClipboardText(regex);

            if (!_isAutomationRunning &&
                Settings.BestiaryClipboard.AutoPasteAfterCopy.Value &&
                !_isBestiaryClipboardPasteRunning &&
                !string.IsNullOrWhiteSpace(regex))
            {
                _ = AutoPasteBestiaryClipboardAsync(regex);
            }
        }

        _wasBestiaryTabVisible = isVisible;
    }

    private async Task AutoPasteBestiaryClipboardAsync(string regex)
    {
        if (_isAutomationRunning || _isBestiaryClipboardPasteRunning || string.IsNullOrWhiteSpace(regex))
        {
            return;
        }

        _isBestiaryClipboardPasteRunning = true;
        try
        {
            await DelayForUiCheckAsync(50);
            if (_isAutomationRunning || !IsBestiaryTabVisible())
            {
                return;
            }

            await ApplyBestiarySearchRegexAsync(regex);
        }
        catch (Exception ex)
        {
            LogAutomationDebug($"Bestiary clipboard auto-paste skipped. {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _isBestiaryClipboardPasteRunning = false;
        }
    }

    private string GetConfiguredBestiaryRegex()
    {
        return Settings.BestiaryClipboard.UseAutoRegex.Value
            ? BuildAutoRegexFromEnabledBeasts()
            : (Settings.BestiaryClipboard.BeastRegex.Value ?? string.Empty);
    }

    private string BuildAutoRegexFromEnabledBeasts()
    {
        var enabledBeasts = Settings.BeastPrices.EnabledBeasts;
        if (enabledBeasts.Count == 0) return string.Empty;

        var builder = new StringBuilder();

        foreach (var beast in AllRedBeasts)
        {
            if (!enabledBeasts.Contains(beast.Name) || string.IsNullOrEmpty(beast.RegexFragment))
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
}