using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using GameOffsets.Native;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    private const int TileToGridConversion = 23;
    private const int TileToWorldConversion = 250;
    private const string CaptureMonsterTrappedBuffName = "capture_monster_trapped";
    private const string CaptureMonsterCapturedBuffName = "capture_monster_captured";
    private const float GridToWorldMultiplier = TileToWorldConversion / (float)TileToGridConversion;
    private const double CameraAngle = 38.7 * Math.PI / 180;
    private static readonly float CameraAngleCos = (float)Math.Cos(CameraAngle);
    private static readonly float CameraAngleSin = (float)Math.Sin(CameraAngle);
    private static readonly Vector2[] WorldCirclePoints = RareBeastCounterHelpers.CreateUnitCirclePoints(15, closeLoop: false);

    private double _mapScale;
    private RectangleF _mapRect;
    private ImDrawListPtr _mapDrawList;
    private readonly Vector2[] _worldCircleScreenPoints = new Vector2[WorldCirclePoints.Length];

    private CancellationTokenSource _pathFindingCts = new();
    private volatile List<Vector2i> _explorationPath;
    private int _explorationPathForIdx = -1;

    private void RequestExplorationPath(int waypointIdx, Vector2 gridPos)
    {
        var lookForRoute = GameController.PluginBridge
            .GetMethod<Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task>>("Radar.LookForRoute");
        if (lookForRoute == null) return;

        var token = _pathFindingCts.Token;
        _ = lookForRoute(gridPos, path =>
        {
            if (path != null && !token.IsCancellationRequested && _explorationPathForIdx == waypointIdx)
                _explorationPath = path;
        }, token);
    }

    internal void CancelBeastPaths()
    {
        _pathFindingCts.Cancel();
        _pathFindingCts = new CancellationTokenSource();
        _explorationPath = null;
        _explorationPathForIdx = -1;
    }

    private void DrawPathsToBeasts(Vector2 mapCenter)
    {
        EnsureExplorationRouteIsCurrent();

        if (_explorationRoute.Count == 0) return;

        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerPositioned = player?.GetComponent<Positioned>();
        if (playerPositioned == null) return;

        var playerGridPos = new Vector2(playerPositioned.GridPosNum.X, playerPositioned.GridPosNum.Y);
        UpdateVisitedWaypoints(playerGridPos);
        var nextIdx = GetNextWaypointIndex();
        if (nextIdx < 0) return;

        if (nextIdx != _explorationPathForIdx)
        {
            _explorationPathForIdx = nextIdx;
            _explorationPath = null;
            RequestExplorationPath(nextIdx, _explorationRoute[nextIdx]);
        }

        var routeColor = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.2f, 0.8f, 1f, 0.45f));
        var nextCol = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f, 0.65f, 0f, 1f));
        const float waypointRadius = 2f;
        const float nextWaypointRadius = 5f;

        for (var i = 0; i < _explorationRoute.Count; i++)
        {
            if (_visitedWaypointIndices.Contains(i)) continue;
            var wPos = mapCenter + TranslateGridDeltaToMapDelta(_explorationRoute[i] - playerGridPos, 0);
            _mapDrawList.AddCircleFilled(wPos, i == nextIdx ? nextWaypointRadius : waypointRadius, i == nextIdx ? nextCol : routeColor);
        }

        var path = _explorationPath;
        if (path == null) return;

        var playerRender = player?.GetComponent<ExileCore.PoEMemory.Components.Render>();
        if (playerRender == null) return;
        var playerHeight = -playerRender.RenderStruct.Height;
        var heightData = GameController.IngameState.Data.RawTerrainHeightData;
        var pathCol = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f, 0.65f, 0f, 0.85f));
        const float pathThickness = 2f;

        Vector2? prev = null;
        var skip = 0;
        foreach (var node in path)
        {
            if (++skip % 2 != 0) continue;
            float nodeHeight = 0;
            if (heightData != null && node.Y >= 0 && node.Y < heightData.Length &&
                node.X >= 0 && node.X < heightData[node.Y].Length)
                nodeHeight = heightData[node.Y][node.X];

            var pos = mapCenter + TranslateGridDeltaToMapDelta(
                new Vector2(node.X, node.Y) - playerGridPos, playerHeight + nodeHeight);
            if (prev.HasValue)
                _mapDrawList.AddLine(prev.Value, pos, pathCol, pathThickness);
            prev = pos;
        }
    }

    private void DrawInWorldBeasts(IReadOnlyList<TrackedBeastRenderInfo> beasts)
    {
        var mapRender = Settings.MapRender;
        var lineSpacing = mapRender.Layout.WorldTextLineSpacing.Value;
        var capturedTextSettings = mapRender.CapturedText;
        var worldPriceTextColor = mapRender.Colors.WorldPriceTextColor.Value;

        foreach (var beast in beasts)
        {
            var worldPos = GameController.IngameState.Data.ToWorldWithTerrainHeight(beast.Positioned.GridPosition);
            var screenPos = GameController.IngameState.Camera.WorldToScreen(worldPos);
            var hasCaptureState = beast.CaptureState != BeastCaptureState.None;
            var worldBeastColor = GetWorldBeastColor(beast.CaptureState);
            var capturedStatusText = GetDisplayedCaptureStatusText(beast.CaptureState);
            var capturedStatusColor = GetDisplayedCaptureStatusColor(beast.CaptureState);
            var useStatusOnlyText = hasCaptureState && capturedTextSettings.ReplaceNameAndPriceWithStatusText.Value;

            if (useStatusOnlyText)
            {
                DrawOutlinedText(capturedStatusText, screenPos, capturedStatusColor);
            }
            else
            {
                DrawOutlinedText(beast.BeastName, screenPos, worldBeastColor);

                var nextLineOffset = lineSpacing;
                if (TryGetBeastPriceText(beast.BeastName, out var priceText))
                {
                    DrawOutlinedText(priceText, screenPos + new Vector2(0, nextLineOffset), worldPriceTextColor);
                    nextLineOffset += lineSpacing;
                }

                if (hasCaptureState)
                {
                    DrawOutlinedText(capturedStatusText, screenPos + new Vector2(0, nextLineOffset), capturedStatusColor);
                }
            }

            DrawFilledCircleInWorld(
                worldPos,
                Settings.MapRender.Layout.WorldBeastCircleRadius.Value,
                GetWorldBeastCircleColor(beast.CaptureState));
        }
    }

    private void DrawBeastsOnLargeMap(IReadOnlyList<TrackedBeastRenderInfo> beasts)
    {
        var ingameUi = GameController.IngameState.IngameUi;
        _mapRect = GameController.Window.GetWindowRectangle() with { Location = SharpDX.Vector2.Zero };

        if (ingameUi.OpenRightPanel.IsVisible)
            _mapRect.Right = ingameUi.OpenRightPanel.GetClientRectCache.Left;
        if (ingameUi.OpenLeftPanel.IsVisible)
            _mapRect.Left = ingameUi.OpenLeftPanel.GetClientRectCache.Right;

        ImGui.SetNextWindowSize(new Vector2(_mapRect.Width, _mapRect.Height));
        ImGui.SetNextWindowPos(new Vector2(_mapRect.Left, _mapRect.Top));

        ImGui.Begin("##RareBeastMapOverlay",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoBackground);

        _mapDrawList = ImGui.GetWindowDrawList();

        var largeMap = ingameUi.Map.LargeMap;
        if (largeMap.IsVisible)
        {
            _mapScale = largeMap.MapScale;
            if (Settings.MapRender.ShowBeastsOnMap.Value)
                DrawBeastMarkersOnMap(largeMap.MapCenter, beasts);
            if (Settings.MapRender.ExplorationRoute.ShowPathsToBeasts.Value)
                DrawPathsToBeasts(largeMap.MapCenter);
            if (Settings.MapRender.ExplorationRoute.ShowExplorationRoute.Value)
                DrawExplorationRouteOnMap(largeMap.MapCenter);
        }

        var smallMiniMap = ingameUi.Map.SmallMiniMap;
        if (Settings.MapRender.ExplorationRoute.ShowCoverageOnMiniMap.Value &&
            smallMiniMap.IsValid && smallMiniMap.IsVisibleLocal)
        {
            var miniMapRect = smallMiniMap.GetClientRectCache;
            var miniMapCenter = new Vector2(miniMapRect.Center.X, miniMapRect.Center.Y);
            _mapScale = smallMiniMap.MapScale;
            _mapDrawList.PushClipRect(
                new Vector2(miniMapRect.Left, miniMapRect.Top),
                new Vector2(miniMapRect.Right, miniMapRect.Bottom),
                true);
            DrawExplorationCoverageOnMiniMap(miniMapCenter);
            _mapDrawList.PopClipRect();
        }

        ImGui.End();
    }

    private void DrawBeastMarkersOnMap(Vector2 mapCenter, IReadOnlyList<TrackedBeastRenderInfo> beasts)
    {
        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerRender = player?.GetComponent<ExileCore.PoEMemory.Components.Render>();
        var playerPositioned = player?.GetComponent<Positioned>();
        if (playerRender == null || playerPositioned == null) return;

        var playerPos = new Vector2(playerPositioned.GridPosNum.X, playerPositioned.GridPosNum.Y);
        var playerHeight = -playerRender.RenderStruct.Height;
        var heightData = GameController.IngameState.Data.RawTerrainHeightData;

        foreach (var beast in beasts)
        {
            var gridPos = beast.Positioned.GridPosNum;
            float beastHeight = 0;
            int bx = (int)gridPos.X, by = (int)gridPos.Y;
            if (heightData != null && by >= 0 && by < heightData.Length && bx >= 0 && bx < heightData[by].Length)
                beastHeight = heightData[by][bx];

            var mapDelta = TranslateGridDeltaToMapDelta(
                new Vector2(gridPos.X, gridPos.Y) - playerPos,
                playerHeight + beastHeight);
            var mapPos = mapCenter + mapDelta;

            DrawMapMarker(beast.BeastName, beast.CaptureState, mapPos);
        }
    }

    private Vector2 TranslateGridDeltaToMapDelta(Vector2 delta, float deltaZ)
    {
        deltaZ /= GridToWorldMultiplier;
        return (float)_mapScale * new Vector2(
            (delta.X - delta.Y) * CameraAngleCos,
            (deltaZ - (delta.X + delta.Y)) * CameraAngleSin);
    }

    private void DrawMapMarker(string beastName, BeastCaptureState captureState, Vector2 pos)
    {
        BuildMapMarkerTexts(beastName, captureState, out var primaryText, out var secondaryText);

        DrawMapLabel(
            primaryText,
            secondaryText,
            pos,
            captureState != BeastCaptureState.None && Settings.MapRender.CapturedText.ReplaceNameAndPriceWithStatusText.Value
                ? GetDisplayedCaptureStatusColor(captureState)
                : Settings.MapRender.Colors.MapMarkerTextColor.Value,
            GetDisplayedCaptureStatusColor(captureState));
    }

    private void DrawMapLabel(string primaryText, string secondaryText, Vector2 pos, Color primaryColor, Color secondaryColor)
    {
        var foregroundDrawList = ImGui.GetForegroundDrawList();
        var lineSpacing = Settings.MapRender.Layout.WorldTextLineSpacing.Value;
        var hasSecondaryText = !string.IsNullOrEmpty(secondaryText);
        var primarySize = ImGui.CalcTextSize(primaryText);
        var secondarySize = hasSecondaryText ? ImGui.CalcTextSize(secondaryText) : Vector2.Zero;
        var width = Math.Max(primarySize.X, secondarySize.X);
        var height = primarySize.Y + (hasSecondaryText ? secondarySize.Y + lineSpacing * 0.25f : 0f);
        var half = new Vector2(width / 2f, height / 2f);
        var pad = new Vector2(Settings.MapRender.Layout.MapLabelPaddingX.Value, Settings.MapRender.Layout.MapLabelPaddingY.Value);
        var backgroundColor = Settings.MapRender.Colors.MapMarkerBackgroundColor.Value;
        foregroundDrawList.AddRectFilled(pos - half - pad, pos + half + pad,
            RareBeastCounterHelpers.ToImGuiColorU32(backgroundColor));

        var primaryPos = new Vector2(pos.X - primarySize.X / 2f, hasSecondaryText ? pos.Y - height / 2f : pos.Y - primarySize.Y / 2f);
        foregroundDrawList.AddText(
            primaryPos,
            RareBeastCounterHelpers.ToImGuiColorU32(primaryColor),
            primaryText);

        if (hasSecondaryText)
        {
            var secondaryPos = new Vector2(pos.X - secondarySize.X / 2f, primaryPos.Y + primarySize.Y + lineSpacing * 0.25f);
            foregroundDrawList.AddText(
                secondaryPos,
                RareBeastCounterHelpers.ToImGuiColorU32(secondaryColor),
                secondaryText);
        }
    }

    private void DrawMapRenderStylePreviewWindow()
    {
        ImGui.SetNextWindowBgAlpha(0.9f);
        if (!ImGui.Begin("Beast Style Preview##RareBeastCounterStylePreview",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            return;
        }

        ImGui.Text("World Label Preview");
        DrawPreviewWorldLabel("Craicic Chimeral", BeastCaptureState.None);
        DrawPreviewWorldLabel("Craicic Chimeral", BeastCaptureState.Capturing);
        DrawPreviewWorldLabel("Craicic Chimeral", BeastCaptureState.Captured);

        ImGui.Separator();
        ImGui.Text("Large Map Label Preview");
        DrawPreviewMapLabel("Craicic Chimeral", BeastCaptureState.None);
        DrawPreviewMapLabel("Craicic Chimeral", BeastCaptureState.Capturing);
        DrawPreviewMapLabel("Craicic Chimeral", BeastCaptureState.Captured);

        ImGui.Separator();
        ImGui.Text("Tracked Beasts Window Preview");
        if (ImGui.BeginTable("##TrackedWindowPreviewTable", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV))
        {
            ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Beast", ImGuiTableColumnFlags.WidthStretch);

            DrawTrackedBeastPreviewRow("1c", "Craicic Chimeral", BeastCaptureState.None);
            DrawTrackedBeastPreviewRow("1c", "Craicic Chimeral", BeastCaptureState.Capturing);
            DrawTrackedBeastPreviewRow("1c", "Craicic Chimeral", BeastCaptureState.Captured);

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.Text("Circle Preview");
        DrawPreviewCircles();

        ImGui.End();
    }

    private void DrawPreviewWorldLabel(string beastName, BeastCaptureState captureState)
    {
        var drawList = ImGui.GetWindowDrawList();
        var size = new Vector2(280, 88);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##WorldPreview{beastName}{captureState}", size);

        var centerX = origin.X + size.X / 2f;
        var lineSpacing = Settings.MapRender.Layout.WorldTextLineSpacing.Value;
        var worldBeastColor = GetWorldBeastColor(captureState);
        var captureTextColor = GetDisplayedCaptureStatusColor(captureState);
        var statusText = GetDisplayedCaptureStatusText(captureState);
        var useCaptureTextOnly = captureState != BeastCaptureState.None && Settings.MapRender.CapturedText.ReplaceNameAndPriceWithStatusText.Value;

        if (useCaptureTextOnly)
        {
            DrawPreviewOutlinedText(drawList, statusText, new Vector2(centerX, origin.Y + 14), captureTextColor);
        }
        else
        {
            DrawPreviewOutlinedText(drawList, beastName, new Vector2(centerX, origin.Y + 8), worldBeastColor);
            DrawPreviewOutlinedText(drawList, "1c", new Vector2(centerX, origin.Y + 8 + lineSpacing), Settings.MapRender.Colors.WorldPriceTextColor.Value);
            if (captureState != BeastCaptureState.None)
                DrawPreviewOutlinedText(drawList, statusText, new Vector2(centerX, origin.Y + 8 + lineSpacing * 2), captureTextColor);
        }

        var circleCenter = new Vector2(origin.X + 24, origin.Y + size.Y - 22);
        DrawPreviewCircle(drawList, circleCenter, Settings.MapRender.Layout.WorldBeastCircleRadius.Value, captureState);
    }

    private void DrawPreviewMapLabel(string beastName, BeastCaptureState captureState)
    {
        var drawList = ImGui.GetWindowDrawList();
        var size = new Vector2(280, 72);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##MapPreview{beastName}{captureState}", size);

        BuildPreviewMapMarkerTexts(beastName, captureState, out var primaryText, out var secondaryText);

        var pos = origin + size / 2f;
        var lineSpacing = Settings.MapRender.Layout.WorldTextLineSpacing.Value;
        var primarySize = ImGui.CalcTextSize(primaryText);
        var hasSecondaryText = !string.IsNullOrEmpty(secondaryText);
        var secondarySize = hasSecondaryText ? ImGui.CalcTextSize(secondaryText) : Vector2.Zero;
        var width = Math.Max(primarySize.X, secondarySize.X);
        var height = primarySize.Y + (hasSecondaryText ? secondarySize.Y + lineSpacing * 0.25f : 0f);
        var half = new Vector2(width / 2f, height / 2f);
        var pad = new Vector2(Settings.MapRender.Layout.MapLabelPaddingX.Value, Settings.MapRender.Layout.MapLabelPaddingY.Value);
        var backgroundColor = Settings.MapRender.Colors.MapMarkerBackgroundColor.Value;
        drawList.AddRectFilled(pos - half - pad, pos + half + pad,
            RareBeastCounterHelpers.ToImGuiColorU32(backgroundColor));

        var primaryColor = captureState != BeastCaptureState.None && Settings.MapRender.CapturedText.ReplaceNameAndPriceWithStatusText.Value
            ? GetDisplayedCaptureStatusColor(captureState)
            : Settings.MapRender.Colors.MapMarkerTextColor.Value;
        var primaryPos = new Vector2(pos.X - primarySize.X / 2f, hasSecondaryText ? pos.Y - height / 2f : pos.Y - primarySize.Y / 2f);
        drawList.AddText(primaryPos,
            RareBeastCounterHelpers.ToImGuiColorU32(primaryColor),
            primaryText);

        if (hasSecondaryText)
        {
            var captureTextColor = GetDisplayedCaptureStatusColor(captureState);
            var secondaryPos = new Vector2(pos.X - secondarySize.X / 2f, primaryPos.Y + primarySize.Y + lineSpacing * 0.25f);
            drawList.AddText(secondaryPos,
                RareBeastCounterHelpers.ToImGuiColorU32(captureTextColor),
                secondaryText);
        }
    }

    private void DrawTrackedBeastPreviewRow(string priceText, string beastName, BeastCaptureState captureState)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(priceText);
        ImGui.TableNextColumn();
        ImGui.TextColored(RareBeastCounterHelpers.ToImGuiColor(GetTrackedWindowBeastColor()), beastName);
        if (captureState != BeastCaptureState.None)
        {
            ImGui.SameLine(0, 0);
            ImGui.TextColored(RareBeastCounterHelpers.ToImGuiColor(GetDisplayedCaptureStatusColor(captureState)),
                $" {GetDisplayedCaptureStatusText(captureState)}");
        }
    }

    private void DrawPreviewCircles()
    {
        var drawList = ImGui.GetWindowDrawList();
        var size = new Vector2(280, 58);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##CirclePreview", size);

        var normalCenter = new Vector2(origin.X + 46, origin.Y + size.Y / 2f);
        var capturingCenter = new Vector2(origin.X + 140, origin.Y + size.Y / 2f);
        var capturedCenter = new Vector2(origin.X + 234, origin.Y + size.Y / 2f);
        DrawPreviewCircle(drawList, normalCenter, Settings.MapRender.Layout.WorldBeastCircleRadius.Value, BeastCaptureState.None);
        DrawPreviewCircle(drawList, capturingCenter, Settings.MapRender.Layout.WorldBeastCircleRadius.Value, BeastCaptureState.Capturing);
        DrawPreviewCircle(drawList, capturedCenter, Settings.MapRender.Layout.WorldBeastCircleRadius.Value, BeastCaptureState.Captured);
        drawList.AddText(new Vector2(normalCenter.X - 26, normalCenter.Y + 18), 0xFFFFFFFF, "Normal");
        drawList.AddText(new Vector2(capturingCenter.X - 30, capturingCenter.Y + 18), 0xFFFFFFFF, GetDisplayedCaptureStatusText(BeastCaptureState.Capturing));
        drawList.AddText(new Vector2(capturedCenter.X - 26, capturedCenter.Y + 18), 0xFFFFFFFF, GetDisplayedCaptureStatusText(BeastCaptureState.Captured));
    }

    private void DrawPreviewCircle(ImDrawListPtr drawList, Vector2 center, float configuredRadius, BeastCaptureState captureState)
    {
        var radius = 8f + configuredRadius / 200f * 18f;
        var circleColor = GetWorldBeastCircleColor(captureState);

        var outlineColor = RareBeastCounterHelpers.ToImGuiColorU32(circleColor);
        var fillOpacity = Settings.MapRender.Layout.WorldBeastCircleFillOpacityPercent.Value / 100f;
        var fillColor = RareBeastCounterHelpers.ToImGuiColorU32(circleColor with { A = Color.ToByte((int)(fillOpacity * 255)) });
        drawList.AddCircleFilled(center, radius, fillColor, 24);
        drawList.AddCircle(center, radius, outlineColor, 24, Settings.MapRender.Layout.WorldBeastCircleOutlineThickness.Value);
    }

    private void DrawPreviewOutlinedText(ImDrawListPtr drawList, string text, Vector2 centerPosition, Color color)
    {
        var textSize = ImGui.CalcTextSize(text);
        var topLeft = centerPosition - textSize / 2f;
        var outlineColor = Settings.MapRender.Colors.WorldTextOutlineColor.Value;
        var outlineU32 = RareBeastCounterHelpers.ToImGuiColorU32(outlineColor);
        var textU32 = RareBeastCounterHelpers.ToImGuiColorU32(color);

        drawList.AddText(topLeft + new Vector2(-1, -1), outlineU32, text);
        drawList.AddText(topLeft + new Vector2(-1, 1), outlineU32, text);
        drawList.AddText(topLeft + new Vector2(1, -1), outlineU32, text);
        drawList.AddText(topLeft + new Vector2(1, 1), outlineU32, text);
        drawList.AddText(topLeft, textU32, text);
    }

    private void BuildPreviewMapMarkerTexts(string beastName, BeastCaptureState captureState, out string primaryText, out string secondaryText)
    {
        var baseLabel = Settings.MapRender.ShowNameInsteadOfPrice.Value ? beastName : $"{beastName} 1c";

        if (captureState == BeastCaptureState.None)
        {
            primaryText = baseLabel;
            secondaryText = null;
            return;
        }

        if (Settings.MapRender.CapturedText.ReplaceNameAndPriceWithStatusText.Value)
        {
            primaryText = GetDisplayedCaptureStatusText(captureState);
            secondaryText = null;
            return;
        }

        primaryText = baseLabel;
        secondaryText = GetDisplayedCaptureStatusText(captureState);
    }

    private void DrawTrackedBeastsWindow(IReadOnlyList<TrackedBeastRenderInfo> beasts)
    {
        if (beasts.Count == 0) return;

        var trackedWindowBeastColor = RareBeastCounterHelpers.ToImGuiColor(GetTrackedWindowBeastColor());

        ImGui.SetNextWindowBgAlpha(0.6f);
        ImGui.Begin("##RareBeastTrackerWindow", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize);

        if (ImGui.BeginTable("##TrackerTable", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV))
        {
            ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Beast", ImGuiTableColumnFlags.WidthStretch);

            foreach (var beast in beasts)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(TryGetBeastPriceText(beast.BeastName, out var priceText) ? priceText : "?");
                ImGui.TableNextColumn();
                ImGui.TextColored(trackedWindowBeastColor, beast.BeastName);
                if (beast.CaptureState != BeastCaptureState.None)
                {
                    ImGui.SameLine(0, 0);
                    ImGui.TextColored(RareBeastCounterHelpers.ToImGuiColor(GetDisplayedCaptureStatusColor(beast.CaptureState)),
                        $" {GetDisplayedCaptureStatusText(beast.CaptureState)}");
                }
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

    private void DrawInventoryBeasts()
    {
        var inventory = GameController.Game.IngameState.IngameUi.InventoryPanel[InventoryIndex.PlayerInventory];
        if (!inventory.IsVisible) return;
        DrawCapturedBeastItems(inventory.VisibleInventoryItems);
    }

    private void DrawStashBeasts()
    {
        var stash = GameController.Game.IngameState.IngameUi.StashElement;
        if (stash == null || !stash.IsVisible) return;
        var items = stash.VisibleStash?.VisibleInventoryItems;
        if (items == null) return;
        DrawCapturedBeastItems(items);
    }

    private void DrawMerchantBeasts()
    {
        var merchant = GameController.Game.IngameState.IngameUi.OfflineMerchantPanel;
        if (!merchant.IsVisible) return;
        var items = merchant.VisibleStash?.VisibleInventoryItems;
        if (items == null) return;
        DrawCapturedBeastItems(items);
    }

    private void DrawCapturedBeastItems(IList<NormalInventoryItem> items)
    {
        foreach (var item in items)
        {
            if (item?.Item == null) continue;
            if (item.Item.Metadata != "Metadata/Items/Currency/CurrencyItemisedCapturedMonster") continue;

            var monster = item.Item.GetComponent<CapturedMonster>();
            var monsterName = monster?.MonsterVariety?.MonsterName;
            var rect = item.GetClientRect();

            if (!string.IsNullOrEmpty(monsterName) &&
                _beastPrices.TryGetValue(monsterName, out var price) && price >= 0)
            {
                Graphics.DrawBox(rect, new Color(0, 0, 0, 25));
                Graphics.DrawText($"{price.ToString(CultureInfo.InvariantCulture)}c", rect.Center,
                    Color.White, FontAlign.Center);
            }
            else
            {
                Graphics.DrawBox(rect, new Color(255, 255, 0, 25));
                Graphics.DrawFrame(rect, new Color(255, 255, 0, 50), 1);
            }
        }
    }

    private void DrawBestiaryPanelPrices()
    {
        var ui = GameController.IngameState.IngameUi;
        var bestiaryRoot = ui.GetChildAtIndex(50)
            ?.GetChildAtIndex(2)?.GetChildAtIndex(0)
            ?.GetChildAtIndex(1)?.GetChildAtIndex(1)
            ?.GetChildAtIndex(11);

        if (bestiaryRoot == null || !bestiaryRoot.IsVisible) return;

        var capturedPanel = bestiaryRoot.GetChildAtIndex(0)?.GetChildAtIndex(18);
        if (capturedPanel == null || !capturedPanel.IsVisible) return;

        var beastsDisplay = capturedPanel.GetChildAtIndex(1)?.GetChildAtIndex(0);
        if (beastsDisplay == null) return;

        foreach (var beastContainer in beastsDisplay.Children)
        {
            if (beastContainer == null || !beastContainer.IsVisible) continue;
            var beastList = beastContainer.GetChildAtIndex(1);
            if (beastList == null) continue;

            foreach (var beastEl in beastList.Children)
            {
                try
                {
                    var nameText = beastEl?.Tooltip?.GetChildAtIndex(1)?.GetChildAtIndex(0)?.Text
                        ?.Replace("-", "").Trim();
                    if (string.IsNullOrEmpty(nameText)) continue;
                    if (!_beastPrices.TryGetValue(nameText, out var price) || price < 0) continue;

                    var rect = beastEl.GetClientRect();
                    var center = new Vector2(rect.Center.X, rect.Center.Y);

                    Graphics.DrawBox(rect, new Color(0, 0, 0, 128));
                    Graphics.DrawFrame(rect, Color.White, 1);
                    Graphics.DrawText(nameText, center, Color.White, FontAlign.Center);
                    Graphics.DrawText($"{price.ToString(CultureInfo.InvariantCulture)}c",
                        center + new Vector2(0, 18), Color.Yellow, FontAlign.Center);
                }
                catch
                {
                    // UI element navigation can fail on panel transitions
                }
            }
        }
    }

    private void DrawFilledCircleInWorld(Vector3 position, float radius, Color color)
    {
        for (var i = 0; i < WorldCirclePoints.Length; i++)
        {
            var point = WorldCirclePoints[i];
            _worldCircleScreenPoints[i] = GameController.Game.IngameState.Camera.WorldToScreen(
                position + new Vector3(point.X * radius, point.Y * radius, 0));
        }

        var fillOpacity = Settings.MapRender.Layout.WorldBeastCircleFillOpacityPercent.Value / 100f;
        Graphics.DrawConvexPolyFilled(_worldCircleScreenPoints, color with { A = Color.ToByte((int)(fillOpacity * 255)) });
        Graphics.DrawPolyLine(_worldCircleScreenPoints, color, Settings.MapRender.Layout.WorldBeastCircleOutlineThickness.Value);
    }

    private static BeastCaptureState GetBeastCaptureState(Entity entity)
    {
        if (entity.Buffs?.Find(b => b.Name == CaptureMonsterCapturedBuffName) != null)
        {
            return BeastCaptureState.Captured;
        }

        return entity.Buffs?.Find(b => b.Name == CaptureMonsterTrappedBuffName) != null
            ? BeastCaptureState.Capturing
            : BeastCaptureState.None;
    }

    private bool TryGetBeastPriceText(string beastName, out string priceText)
    {
        if (_beastPriceTexts.TryGetValue(beastName, out priceText))
        {
            return true;
        }

        priceText = null;
        return false;
    }

    private void DrawOutlinedText(string text, Vector2 position, Color color)
    {
        var outlineColor = Settings.MapRender.Colors.WorldTextOutlineColor.Value;
        Graphics.DrawText(text, position + new Vector2(-1, -1), outlineColor, FontAlign.Center);
        Graphics.DrawText(text, position + new Vector2(-1, 1), outlineColor, FontAlign.Center);
        Graphics.DrawText(text, position + new Vector2(1, -1), outlineColor, FontAlign.Center);
        Graphics.DrawText(text, position + new Vector2(1, 1), outlineColor, FontAlign.Center);
        Graphics.DrawText(text, position, color, FontAlign.Center);
    }

    private string GetMapMarkerLabel(string beastName)
    {
        var displayName = beastName;
        if (Settings.MapRender.ShowNameInsteadOfPrice.Value)
        {
            return displayName;
        }

        return TryGetBeastPriceText(beastName, out var priceText)
            ? $"{displayName} {priceText}"
            : displayName;
    }

    private string GetCapturedStatusText()
    {
        var statusText = Settings.MapRender.CapturedText.StatusText.Value;
        return string.IsNullOrWhiteSpace(statusText) ? "Captured" : statusText;
    }

    private string GetDisplayedCaptureStatusText(BeastCaptureState captureState)
    {
        return captureState == BeastCaptureState.Captured
            ? GetCapturedSafeToLeaveText()
            : GetCapturedStatusText();
    }

    private Color GetDisplayedCaptureStatusColor(BeastCaptureState captureState)
    {
        return captureState == BeastCaptureState.Captured
            ? Settings.MapRender.CapturedText.CapturedTextColor.Value
            : Settings.MapRender.CapturedText.CaptureTextColor.Value;
    }

    private string GetCapturedSafeToLeaveText()
    {
        var capturedStatusText = Settings.MapRender.CapturedText.CapturedStatusText.Value;
        return string.IsNullOrWhiteSpace(capturedStatusText) ? "catched" : capturedStatusText;
    }

    private void BuildMapMarkerTexts(string beastName, BeastCaptureState captureState, out string primaryText, out string secondaryText)
    {
        if (captureState == BeastCaptureState.None)
        {
            primaryText = GetMapMarkerLabel(beastName);
            secondaryText = null;
            return;
        }

        if (Settings.MapRender.CapturedText.ReplaceNameAndPriceWithStatusText.Value)
        {
            primaryText = GetDisplayedCaptureStatusText(captureState);
            secondaryText = null;
            return;
        }

        primaryText = GetMapMarkerLabel(beastName);
        secondaryText = GetDisplayedCaptureStatusText(captureState);
    }

    private Color GetWorldBeastColor(BeastCaptureState captureState) =>
        captureState != BeastCaptureState.None ? Settings.MapRender.Colors.WorldCapturedBeastColor.Value : Settings.MapRender.Colors.WorldBeastColor.Value;

    private Color GetWorldBeastCircleColor(BeastCaptureState captureState)
    {
        return captureState switch
        {
            BeastCaptureState.Captured => Settings.MapRender.Colors.WorldCapturedCircleColor.Value,
            BeastCaptureState.Capturing => Settings.MapRender.Colors.WorldCaptureRingColor.Value,
            _ => Settings.MapRender.Colors.WorldBeastCircleColor.Value,
        };
    }

    private Color GetTrackedWindowBeastColor() =>
        Settings.MapRender.Colors.TrackedWindowBeastColor.Value;

}
