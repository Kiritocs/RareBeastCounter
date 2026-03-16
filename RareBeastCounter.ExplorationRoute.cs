using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory.Components;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    // ── Exploration route ────────────────────────────────────────────────────

    private static readonly Vector2[] RadiusCirclePoints = RareBeastCounterHelpers.CreateUnitCirclePoints(48);
    private List<Vector2> _explorationRoute = new();
    private readonly HashSet<int> _visitedWaypointIndices = new();
    private bool _routeNeedsRegen = true;
    private int _lastRouteDetectionRadius = -1;
    private readonly record struct RouteBounds(int MinX, int MinY, int MaxX, int MaxY)
    {
        public bool IsEmpty => MaxX < MinX || MaxY < MinY;
    }

    internal int GetNextWaypointIndex()
    {
        for (var i = 0; i < _explorationRoute.Count; i++)
            if (!_visitedWaypointIndices.Contains(i)) return i;
        return -1;
    }

    internal void UpdateVisitedWaypoints(Vector2 playerGridPos)
    {
        var visitRadius = Settings.MapRender.ExplorationRoute.WaypointVisitRadius.Value;
        var visitSq     = (float)(visitRadius * visitRadius);
        var anyNew      = false;

        for (var i = 0; i < _explorationRoute.Count; i++)
        {
            if (_visitedWaypointIndices.Contains(i)) continue;
            var d = _explorationRoute[i] - playerGridPos;
            if (d.X * d.X + d.Y * d.Y <= visitSq)
            {
                _visitedWaypointIndices.Add(i);
                anyNew = true;
            }
        }

        if (anyNew)
            ReSortUnvisited(playerGridPos);
    }

    // Separates visited / unvisited waypoints, re-orders the unvisited list from the
    // player's current position, then rebuilds the route.
    private void ReSortUnvisited(Vector2 playerGridPos)
    {
        var visited   = new List<Vector2>();
        var unvisited = new List<Vector2>();
        for (var i = 0; i < _explorationRoute.Count; i++)
        {
            if (_visitedWaypointIndices.Contains(i)) visited.Add(_explorationRoute[i]);
            else                                     unvisited.Add(_explorationRoute[i]);
        }

        if (unvisited.Count == 0) return;

        var sorted = BuildCoverageRoute(unvisited, playerGridPos);

        _explorationRoute = visited.Concat(sorted).ToList();
        _visitedWaypointIndices.Clear();
        for (var i = 0; i < visited.Count; i++)
            _visitedWaypointIndices.Add(i);

        CancelBeastPaths();
    }

    private void GenerateExplorationRoute()
    {
        _explorationRoute.Clear();
        _visitedWaypointIndices.Clear();

        var pathData = GameController?.IngameState?.Data?.RawPathfindingData;
        if (pathData == null) return;

        var areaDimensions = GameController?.IngameState?.Data?.AreaDimensions;
        if (areaDimensions == null) return;

        var positioned = GameController?.Game?.IngameState?.Data?.LocalPlayer?.GetComponent<Positioned>();
        if (positioned == null) return;

        var playerPos = new Vector2(positioned.GridPosNum.X, positioned.GridPosNum.Y);
        var maxX = areaDimensions.Value.X;
        var maxY = Math.Min(pathData.Length, areaDimensions.Value.Y);

        var detectionRadius = Math.Min(
            Settings.MapRender.ExplorationRoute.DetectionRadius.Value,
            Math.Min(maxX, maxY) / 4);
        if (detectionRadius < 4) return;

        var routeStep = Math.Max(8, (int)(detectionRadius * 0.85f));
        var searchRadius = Math.Max(routeStep, detectionRadius);
        var outsideBlockedMask = BuildOutsideBlockedMask(pathData, maxX, maxY);

        if (!TryFindNearestWalkableCell(pathData, maxX, maxY, (int)playerPos.X, (int)playerPos.Y, searchRadius, out var startCell))
        {
            return;
        }

        var reachableMask = BuildReachableMask(pathData, maxX, maxY, startCell.x, startCell.y, out var reachableBounds);
        if (reachableBounds.IsEmpty)
        {
            return;
        }

        var candidates = CollectCoverageCandidates(reachableMask, outsideBlockedMask, reachableBounds, routeStep, detectionRadius);
        if (candidates.Count == 0)
        {
            return;
        }

        TryAddPlayerAnchorCandidate(candidates, playerPos, reachableMask, outsideBlockedMask, routeStep, detectionRadius);
        _explorationRoute = BuildCoverageRoute(candidates, playerPos);
    }

    private static List<Vector2> BuildCoverageRoute(List<Vector2> points, Vector2 startNear)
    {
        return OrderByNearestNeighbor(points, startNear);
    }

    private static bool TryFindNearestWalkableCell(int[][] pathData, int maxX, int maxY, int startX, int startY,
        int maxSearchRadius, out (int x, int y) cell)
    {
        if (IsWalkableCell(pathData, startY, startX))
        {
            cell = (startX, startY);
            return true;
        }

        for (var radius = 1; radius <= maxSearchRadius; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;

                var x = startX + dx;
                var y = startY + dy;
                if (x < 0 || x >= maxX || y < 0 || y >= maxY) continue;
                if (!IsWalkableCell(pathData, y, x)) continue;

                cell = (x, y);
                return true;
            }
        }

        cell = default;
        return false;
    }

    private static bool[][] BuildReachableMask(int[][] pathData, int maxX, int maxY, int startX, int startY,
        out RouteBounds bounds)
    {
        var reachableMask = new bool[maxY][];
        for (var y = 0; y < maxY; y++)
        {
            reachableMask[y] = new bool[maxX];
        }

        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((startX, startY));
        reachableMask[startY][startX] = true;

        var minX = startX;
        var minY = startY;
        var maxReachableX = startX;
        var maxReachableY = startY;

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();

            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxReachableX) maxReachableX = x;
            if (y > maxReachableY) maxReachableY = y;

            TryVisit(x + 1, y);
            TryVisit(x - 1, y);
            TryVisit(x, y + 1);
            TryVisit(x, y - 1);
        }

        bounds = new RouteBounds(minX, minY, maxReachableX, maxReachableY);
        return reachableMask;

        void TryVisit(int x, int y)
        {
            if (x < 0 || x >= maxX || y < 0 || y >= maxY) return;
            if (reachableMask[y][x]) return;
            if (!IsWalkableCell(pathData, y, x)) return;

            reachableMask[y][x] = true;
            queue.Enqueue((x, y));
        }
    }

    private static List<Vector2> CollectCoverageCandidates(bool[][] reachableMask, bool[][] outsideBlockedMask,
        RouteBounds bounds, int step, int detectionRadius)
    {
        var bestCandidates = new List<Vector2>();
        Span<int> clearanceLevels = stackalloc int[5];
        var clearanceLevelCount = FillClearanceLevels(clearanceLevels, detectionRadius);

        for (var clearanceIndex = 0; clearanceIndex < clearanceLevelCount; clearanceIndex++)
        {
            var clearance = clearanceLevels[clearanceIndex];
            var candidates = new List<Vector2>();

            for (var y = bounds.MinY; y <= bounds.MaxY; y += step)
            for (var x = bounds.MinX; x <= bounds.MaxX; x += step)
            {
                if (TryFindBestCandidateInBlock(reachableMask, outsideBlockedMask, x, y, step, clearance, out var candidate))
                {
                    candidates.Add(candidate);
                }
            }

            if (candidates.Count > bestCandidates.Count)
            {
                bestCandidates = candidates;
            }

            if (candidates.Count >= 6)
            {
                return candidates;
            }
        }

        return bestCandidates;
    }

    private static bool TryFindBestCandidateInBlock(bool[][] reachableMask, bool[][] outsideBlockedMask,
        int startX, int startY, int step, int outsideWallClearance, out Vector2 candidate)
    {
        candidate = default;

        var centerX = startX + step / 2f;
        var centerY = startY + step / 2f;
        var bestDistance = float.MaxValue;
        var found = false;

        var endY = Math.Min(reachableMask.Length, startY + step);
        for (var y = Math.Max(0, startY); y < endY; y++)
        {
            var row = reachableMask[y];
            if (row == null) continue;

            var endX = Math.Min(row.Length, startX + step);
            for (var x = Math.Max(0, startX); x < endX; x++)
            {
                if (!row[x]) continue;
                if (!HasOutsideWallClearance(outsideBlockedMask, y, x, outsideWallClearance)) continue;

                var dx = x - centerX;
                var dy = y - centerY;
                var distance = dx * dx + dy * dy;
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                candidate = new Vector2(x, y);
                found = true;
            }
        }

        return found;
    }

    private static void TryAddPlayerAnchorCandidate(List<Vector2> candidates, Vector2 playerPos, bool[][] reachableMask,
        bool[][] outsideBlockedMask, int step, int detectionRadius)
    {
        var minDistanceSq = float.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            var distance = Vector2.DistanceSquared(candidates[i], playerPos);
            if (distance < minDistanceSq)
            {
                minDistanceSq = distance;
            }
        }

        var minDesiredDistance = Math.Max(6, step / 2);
        if (minDistanceSq <= minDesiredDistance * minDesiredDistance)
        {
            return;
        }

        if (!TryFindNearestReachableCell(reachableMask, outsideBlockedMask, (int)playerPos.X, (int)playerPos.Y,
                step, detectionRadius, out var anchor))
        {
            return;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (Vector2.DistanceSquared(candidates[i], anchor) < 1f)
            {
                return;
            }
        }

        candidates.Add(anchor);
    }

    private static bool TryFindNearestReachableCell(bool[][] reachableMask, bool[][] outsideBlockedMask,
        int startX, int startY, int maxSearchRadius, int detectionRadius, out Vector2 cell)
    {
        Span<int> clearanceLevels = stackalloc int[5];
        var clearanceLevelCount = FillClearanceLevels(clearanceLevels, detectionRadius);

        for (var clearanceIndex = 0; clearanceIndex < clearanceLevelCount; clearanceIndex++)
        {
            var clearance = clearanceLevels[clearanceIndex];
            if (TryFindNearestReachableCellWithClearance(reachableMask, outsideBlockedMask, startX, startY, maxSearchRadius,
                    clearance, out cell))
            {
                return true;
            }
        }

        cell = default;
        return false;
    }

    private static bool TryFindNearestReachableCellWithClearance(bool[][] reachableMask, bool[][] outsideBlockedMask,
        int startX, int startY, int maxSearchRadius, int outsideWallClearance, out Vector2 cell)
    {
        if (IsReachableCell(reachableMask, startY, startX) &&
            HasOutsideWallClearance(outsideBlockedMask, startY, startX, outsideWallClearance))
        {
            cell = new Vector2(startX, startY);
            return true;
        }

        for (var radius = 1; radius <= maxSearchRadius; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;

                var x = startX + dx;
                var y = startY + dy;
                if (!IsReachableCell(reachableMask, y, x)) continue;
                if (!HasOutsideWallClearance(outsideBlockedMask, y, x, outsideWallClearance)) continue;

                cell = new Vector2(x, y);
                return true;
            }
        }

        cell = default;
        return false;
    }

    private static bool HasOutsideWallClearance(bool[][] outsideBlockedMask, int y, int x, int clearance)
    {
        var clearanceSq = clearance * clearance;
        for (var dy = -clearance; dy <= clearance; dy++)
        for (var dx = -clearance; dx <= clearance; dx++)
        {
            if (dx * dx + dy * dy > clearanceSq) continue;
            if (IsOutsideBlockedCell(outsideBlockedMask, y + dy, x + dx)) return false;
        }

        return true;
    }

    private static int FillClearanceLevels(Span<int> levels, int detectionRadius)
    {
        var count = 0;

        Span<int> candidates = stackalloc int[5];
        candidates[0] = Math.Max(3, detectionRadius);
        candidates[1] = Math.Max(3, detectionRadius * 2 / 3);
        candidates[2] = Math.Max(2, detectionRadius / 2);
        candidates[3] = Math.Max(2, detectionRadius / 3);
        candidates[4] = 1;

        for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
        {
            var value = candidates[candidateIndex];
            var exists = false;
            for (var i = 0; i < count; i++)
            {
                if (levels[i] == value)
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                levels[count++] = value;
            }
        }

        return count;
    }

    private static bool[][] BuildOutsideBlockedMask(int[][] pathData, int maxX, int maxY)
    {
        var mask = new bool[maxY][];
        for (var y = 0; y < maxY; y++)
        {
            mask[y] = new bool[maxX];
        }

        var queue = new Queue<(int x, int y)>();

        void TryMark(int x, int y)
        {
            if (y < 0 || y >= maxY || x < 0 || x >= maxX) return;
            if (mask[y][x]) return;
            if (IsWalkableCell(pathData, y, x)) return;

            mask[y][x] = true;
            queue.Enqueue((x, y));
        }

        for (var x = 0; x < maxX; x++)
        {
            TryMark(x, 0);
            TryMark(x, maxY - 1);
        }

        for (var y = 0; y < maxY; y++)
        {
            TryMark(0, y);
            TryMark(maxX - 1, y);
        }

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            TryMark(x + 1, y);
            TryMark(x - 1, y);
            TryMark(x, y + 1);
            TryMark(x, y - 1);
        }

        return mask;
    }

    private static bool IsOutsideBlockedCell(bool[][] outsideBlockedMask, int y, int x)
    {
        if (y < 0 || y >= outsideBlockedMask.Length) return true;
        var row = outsideBlockedMask[y];
        if (row == null || x < 0 || x >= row.Length) return true;
        return row[x];
    }

    private static bool IsReachableCell(bool[][] reachableMask, int y, int x)
    {
        if (y < 0 || y >= reachableMask.Length) return false;
        var row = reachableMask[y];
        if (row == null || x < 0 || x >= row.Length) return false;
        return row[x];
    }

    private static List<Vector2> OrderByNearestNeighbor(List<Vector2> points, Vector2 startNear)
    {
        if (points.Count <= 1) return new List<Vector2>(points);

        var startIndex = 0;
        var bestStartDistance = float.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            var distance = Vector2.DistanceSquared(points[i], startNear);
            if (distance >= bestStartDistance) continue;

            bestStartDistance = distance;
            startIndex = i;
        }

        var result = new List<Vector2>(points.Count);
        var used = new bool[points.Count];
        var currentIndex = startIndex;
        used[currentIndex] = true;
        result.Add(points[currentIndex]);

        for (var i = 1; i < points.Count; i++)
        {
            var nextIndex = -1;
            var bestNextDistance = float.MaxValue;

            for (var j = 0; j < points.Count; j++)
            {
                if (used[j]) continue;

                var distance = Vector2.DistanceSquared(points[currentIndex], points[j]);
                if (distance >= bestNextDistance) continue;

                bestNextDistance = distance;
                nextIndex = j;
            }

            if (nextIndex < 0) break;

            used[nextIndex] = true;
            currentIndex = nextIndex;
            result.Add(points[currentIndex]);
        }

        return result;
    }

    private static bool IsWalkableCell(int[][] pathData, int y, int x)
    {
        if (y < 0 || y >= pathData.Length || pathData[y] == null) return false;
        if (x < 0 || x >= pathData[y].Length) return false;
        var v = pathData[y][x];
        return v is >= 1 and <= 5;
    }

    private void DrawExplorationRouteOnMap(Vector2 mapCenter)
    {
        EnsureExplorationRouteIsCurrent();

        if (_explorationRoute.Count == 0) return;

        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerPositioned = player?.GetComponent<Positioned>();
        if (playerPositioned == null) return;

        var playerGridPos = new Vector2(playerPositioned.GridPosNum.X, playerPositioned.GridPosNum.Y);

        UpdateVisitedWaypoints(playerGridPos);
        var nextIdx = GetNextWaypointIndex();

        var visitedCol = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 0.25f));
        var routeCol = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.2f, 0.8f, 1f, 0.7f));
        var nextCol = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f, 1f, 0f, 1f));
        const float routeLineThickness = 1.5f;
        const float waypointRadius = 2f;
        const float nextWaypointRadius = 5f;

        for (var i = 0; i < _explorationRoute.Count - 1; i++)
        {
            var a = mapCenter + TranslateGridDeltaToMapDelta(_explorationRoute[i]     - playerGridPos, 0);
            var b = mapCenter + TranslateGridDeltaToMapDelta(_explorationRoute[i + 1] - playerGridPos, 0);
            var col = _visitedWaypointIndices.Contains(i) && _visitedWaypointIndices.Contains(i + 1)
                ? visitedCol : routeCol;
            _mapDrawList.AddLine(a, b, col, routeLineThickness);
        }

        for (var i = 0; i < _explorationRoute.Count; i++)
        {
            var pos = mapCenter + TranslateGridDeltaToMapDelta(_explorationRoute[i] - playerGridPos, 0);
            if (i == nextIdx)
                _mapDrawList.AddCircleFilled(pos, nextWaypointRadius, nextCol);
            else if (!_visitedWaypointIndices.Contains(i))
                _mapDrawList.AddCircleFilled(pos, waypointRadius, routeCol);
        }

        DrawDetectionRadiusOnMap(mapCenter, Settings.MapRender.ExplorationRoute.DetectionRadius.Value);
    }

    private void DrawExplorationCoverageOnMiniMap(Vector2 mapCenter)
    {
        EnsureExplorationRouteIsCurrent();

        if (_explorationRoute.Count == 0) return;

        var player = GameController.Game.IngameState.Data.LocalPlayer;
        var playerRender = player?.GetComponent<Render>();
        var playerPositioned = player?.GetComponent<Positioned>();
        if (playerRender == null || playerPositioned == null) return;

        var playerGridPos = new Vector2(playerPositioned.GridPosNum.X, playerPositioned.GridPosNum.Y);
        var playerHeight = -playerRender.RenderStruct.Height;
        var heightData = GameController.IngameState.Data.RawTerrainHeightData;

        UpdateVisitedWaypoints(playerGridPos);
        var nextIdx = GetNextWaypointIndex();
        var detectionRadius = Settings.MapRender.ExplorationRoute.DetectionRadius.Value;

        var coverageCol = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f, 1f, 0.2f, 0.18f));
        var visitedCol = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 0.20f));
        var routeCol = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.2f, 0.8f, 1f, 0.7f));
        var nextCol = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f, 1f, 0f, 1f));
        const float miniMapRouteLineThickness = 1.2f;
        const float miniMapWaypointRadius = 2f;
        const float miniMapNextWaypointRadius = 4f;
        const float miniMapCoverageThickness = 1f;

        for (var i = 0; i < _explorationRoute.Count; i++)
        {
            if (_visitedWaypointIndices.Contains(i)) continue;

            var waypointPos = mapCenter + TranslateGridDeltaToMiniMapDelta(
                _explorationRoute[i] - playerGridPos,
                GetHeightDeltaInGridUnits(heightData, _explorationRoute[i], playerHeight));
            DrawRadiusCircleOnMiniMap(waypointPos, detectionRadius, coverageCol, miniMapCoverageThickness);
        }

        for (var i = 0; i < _explorationRoute.Count - 1; i++)
        {
            var a = mapCenter + TranslateGridDeltaToMiniMapDelta(
                _explorationRoute[i] - playerGridPos,
                GetHeightDeltaInGridUnits(heightData, _explorationRoute[i], playerHeight));
            var b = mapCenter + TranslateGridDeltaToMiniMapDelta(
                _explorationRoute[i + 1] - playerGridPos,
                GetHeightDeltaInGridUnits(heightData, _explorationRoute[i + 1], playerHeight));
            var col = _visitedWaypointIndices.Contains(i) && _visitedWaypointIndices.Contains(i + 1)
                ? visitedCol : routeCol;
            _mapDrawList.AddLine(a, b, col, miniMapRouteLineThickness);
        }

        for (var i = 0; i < _explorationRoute.Count; i++)
        {
            var pos = mapCenter + TranslateGridDeltaToMiniMapDelta(
                _explorationRoute[i] - playerGridPos,
                GetHeightDeltaInGridUnits(heightData, _explorationRoute[i], playerHeight));
            if (i == nextIdx)
                _mapDrawList.AddCircleFilled(pos, miniMapNextWaypointRadius, nextCol);
            else if (!_visitedWaypointIndices.Contains(i))
                _mapDrawList.AddCircleFilled(pos, miniMapWaypointRadius, routeCol);
        }
    }

    private void DrawDetectionRadiusOnMap(Vector2 mapCenter, int radius)
    {
        var col = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f, 1f, 0.2f, 0.45f));
        DrawRadiusCircleOnMap(mapCenter, radius, col, 1.5f);
    }

    private void DrawRadiusCircleOnMap(Vector2 center, int radius, uint color, float thickness)
    {
        Vector2? prev = null;
        foreach (var point in RadiusCirclePoints)
        {
            var mapPos = center + TranslateGridDeltaToMapDelta(point * radius, 0);
            if (prev.HasValue)
                _mapDrawList.AddLine(prev.Value, mapPos, color, thickness);
            prev = mapPos;
        }
    }

    private void DrawRadiusCircleOnMiniMap(Vector2 center, int radius, uint color, float thickness)
    {
        Vector2? prev = null;
        foreach (var point in RadiusCirclePoints)
        {
            var miniMapPos = center + TranslateGridDeltaToMiniMapDelta(point * radius, 0);
            if (prev.HasValue)
                _mapDrawList.AddLine(prev.Value, miniMapPos, color, thickness);
            prev = miniMapPos;
        }
    }

    private static float GetHeightDeltaInGridUnits(float[][] heightData, Vector2 gridPos, float playerHeight)
    {
        if (heightData != null)
        {
            var x = (int)gridPos.X;
            var y = (int)gridPos.Y;
            if (y >= 0 && y < heightData.Length && x >= 0 && x < heightData[y].Length)
            {
                return (playerHeight + heightData[y][x]) / GridToWorldMultiplier;
            }
        }

        return playerHeight / GridToWorldMultiplier;
    }

    private Vector2 TranslateGridDeltaToMiniMapDelta(Vector2 delta, float deltaZ)
    {
        return (float)_mapScale * Vector2.Multiply(
            new Vector2(delta.X - delta.Y, deltaZ - (delta.X + delta.Y)),
            new Vector2(CameraAngleCos, CameraAngleSin));
    }

    private void EnsureExplorationRouteIsCurrent()
    {
        var detectionRadius = Settings.MapRender.ExplorationRoute.DetectionRadius.Value;
        if (_lastRouteDetectionRadius != detectionRadius)
        {
            _lastRouteDetectionRadius = detectionRadius;
            _routeNeedsRegen = true;
            CancelBeastPaths();
        }

        if (_routeNeedsRegen)
        {
            _routeNeedsRegen = false;
            GenerateExplorationRoute();
        }
    }
}
