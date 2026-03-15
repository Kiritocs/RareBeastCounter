using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
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
    private const float GridToWorldMultiplier = TileToWorldConversion / (float)TileToGridConversion;
    private const double CameraAngle = 38.7 * Math.PI / 180;
    private static readonly float CameraAngleCos = (float)Math.Cos(CameraAngle);
    private static readonly float CameraAngleSin = (float)Math.Sin(CameraAngle);

    private double _mapScale;
    private RectangleF _mapRect;
    private ImDrawListPtr _mapDrawList;

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

        var dotCol  = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.2f, 0.8f, 1f,  0.45f));
        var nextCol = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f,   0.65f, 0f, 1f));

        for (var i = 0; i < _explorationRoute.Count; i++)
        {
            if (_visitedWaypointIndices.Contains(i)) continue;
            var wPos = mapCenter + TranslateGridDeltaToMapDelta(_explorationRoute[i] - playerGridPos, 0);
            _mapDrawList.AddCircleFilled(wPos, i == nextIdx ? 5f : 2f, i == nextIdx ? nextCol : dotCol);
        }

        var path = _explorationPath;
        if (path == null) return;

        var playerRender = player?.GetComponent<ExileCore.PoEMemory.Components.Render>();
        if (playerRender == null) return;
        var playerHeight = -playerRender.RenderStruct.Height;
        var heightData = GameController.IngameState.Data.RawTerrainHeightData;
        var pathCol = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f, 0.65f, 0f, 0.85f));

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
                _mapDrawList.AddLine(prev.Value, pos, pathCol, 2f);
            prev = pos;
        }
    }

    private void DrawInWorldBeasts()
    {
        foreach (var (_, entity) in _trackedBeastEntities)
        {
            if (!entity.IsValid) continue;
            if (entity.Buffs?.Find(b => b.Name == "capture_monster_trapped") != null) continue;

            var beastName = GetTrackedBeastName(entity.Metadata);
            if (beastName == null) continue;
            if (Settings.MapRender.ShowEnabledOnly.Value && !Settings.BeastPrices.EnabledBeasts.Contains(beastName)) continue;

            var positioned = entity.GetComponent<Positioned>();
            if (positioned == null) continue;

            var worldPos = GameController.IngameState.Data.ToWorldWithTerrainHeight(positioned.GridPosition);
            var screenPos = GameController.IngameState.Camera.WorldToScreen(worldPos);
            var color = GetBeastColor(beastName);

            Graphics.DrawText(beastName, screenPos, color, FontAlign.Center);
            DrawFilledCircleInWorld(worldPos, 50, color);
        }
    }

    private void DrawBeastsOnLargeMap()
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
                DrawBeastMarkersOnMap(largeMap.MapCenter);
            if (Settings.MapRender.ExplorationRoute.ShowPathsToBeasts.Value)
                DrawPathsToBeasts(largeMap.MapCenter);
            if (Settings.MapRender.ExplorationRoute.ShowExplorationRoute.Value)
                DrawExplorationRouteOnMap(largeMap.MapCenter);
        }

        ImGui.End();
    }

    private void DrawBeastMarkersOnMap(Vector2 mapCenter)
    {
        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerRender = player?.GetComponent<ExileCore.PoEMemory.Components.Render>();
        var playerPositioned = player?.GetComponent<Positioned>();
        if (playerRender == null || playerPositioned == null) return;

        var playerPos = new Vector2(playerPositioned.GridPosNum.X, playerPositioned.GridPosNum.Y);
        var playerHeight = -playerRender.RenderStruct.Height;
        var heightData = GameController.IngameState.Data.RawTerrainHeightData;

        foreach (var (_, entity) in _trackedBeastEntities)
        {
            if (!entity.IsValid) continue;
            if (entity.Buffs?.Find(b => b.Name == "capture_monster_trapped") != null) continue;

            var beastName = GetTrackedBeastName(entity.Metadata);
            if (beastName == null) continue;
            if (Settings.MapRender.ShowEnabledOnly.Value && !Settings.BeastPrices.EnabledBeasts.Contains(beastName)) continue;

            var positioned = entity.GetComponent<Positioned>();
            if (positioned == null) continue;

            var gridPos = positioned.GridPosNum;
            float beastHeight = 0;
            int bx = (int)gridPos.X, by = (int)gridPos.Y;
            if (heightData != null && by >= 0 && by < heightData.Length && bx >= 0 && bx < heightData[by].Length)
                beastHeight = heightData[by][bx];

            var mapDelta = TranslateGridDeltaToMapDelta(
                new Vector2(gridPos.X, gridPos.Y) - playerPos,
                playerHeight + beastHeight);
            var mapPos = mapCenter + mapDelta;

            var showName = Settings.MapRender.ShowNameInsteadOfPrice.Value;
            var label = showName
                ? beastName
                : (_beastPrices.TryGetValue(beastName, out var price) && price >= 0 ? $"{price:0}c" : beastName);

            DrawMapLabel(label, mapPos, GetBeastColor(beastName));
        }
    }

    private Vector2 TranslateGridDeltaToMapDelta(Vector2 delta, float deltaZ)
    {
        deltaZ /= GridToWorldMultiplier;
        return (float)_mapScale * new Vector2(
            (delta.X - delta.Y) * CameraAngleCos,
            (deltaZ - (delta.X + delta.Y)) * CameraAngleSin);
    }

    private void DrawMapLabel(string text, Vector2 pos, Color color)
    {
        var size = ImGui.CalcTextSize(text);
        var half = size / 2f;
        var pad = new Vector2(4, 2);
        _mapDrawList.AddRectFilled(pos - half - pad, pos + half + pad,
            ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0f, 0f, 0f, 0.7f)));
        _mapDrawList.AddText(pos - half,
            ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(color.R / 255f, color.G / 255f, color.B / 255f, 1f)),
            text);
    }

    private void DrawTrackedBeastsWindow()
    {
        var visible = _trackedBeastEntities.Values
            .Where(e => e.IsValid && e.Buffs?.Find(b => b.Name == "capture_monster_trapped") == null)
            .Select(e => GetTrackedBeastName(e.Metadata))
            .Where(n => n != null &&
                        (!Settings.MapRender.ShowEnabledOnly.Value || Settings.BeastPrices.EnabledBeasts.Contains(n)))
            .ToList();

        if (visible.Count == 0) return;

        ImGui.SetNextWindowBgAlpha(0.6f);
        ImGui.Begin("##RareBeastTrackerWindow", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize);

        if (ImGui.BeginTable("##TrackerTable", 2,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV))
        {
            ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 52);
            ImGui.TableSetupColumn("Beast", ImGuiTableColumnFlags.WidthStretch);

            foreach (var name in visible)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(_beastPrices.TryGetValue(name, out var price) && price >= 0 ? $"{price:0}c" : "?");
                ImGui.TableNextColumn();
                ImGui.TextColored(RareBeastCounterHelpers.ToImGuiColor(GetBeastColor(name)), name);
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
        var pts = new List<Vector2>();
        const int segments = 15;
        const float step = 2f * MathF.PI / segments;

        for (var i = 0; i < segments; i++)
        {
            var a0 = i * step;
            var a1 = a0 + step;
            pts.Add(GameController.Game.IngameState.Camera.WorldToScreen(
                position + new Vector3(MathF.Cos(a0) * radius, MathF.Sin(a0) * radius, 0)));
            pts.Add(GameController.Game.IngameState.Camera.WorldToScreen(
                position + new Vector3(MathF.Cos(a1) * radius, MathF.Sin(a1) * radius, 0)));
        }

        Graphics.DrawConvexPolyFilled(pts.ToArray(), color with { A = Color.ToByte((int)(0.2f * 255)) });
        Graphics.DrawPolyLine(pts.ToArray(), color, 2);
    }

    private static Color GetBeastColor(string _) => new(255, 165, 0, 255);

    private static string GetTrackedBeastName(string metadata) =>
        TryGetValuableTrackedBeastName(metadata, out var name) ? name : null;
}
