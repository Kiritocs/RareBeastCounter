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

    private List<Vector2> _explorationRoute = new();
    private readonly HashSet<int> _visitedWaypointIndices = new();
    private bool _routeNeedsRegen = true;
    private int _lastRouteDetectionRadius = -1;

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

        var pathData = GameController?.IngameState?.Data?.RawPathfindingData;
        var sorted = BuildCoverageRoute(unvisited, playerGridPos, pathData);

        // Rebuild: visited waypoints keep their leading positions; unvisited follow in new order.
        _explorationRoute = visited.Concat(sorted).ToList();
        _visitedWaypointIndices.Clear();
        for (var i = 0; i < visited.Count; i++)
            _visitedWaypointIndices.Add(i);

        // Invalidate the cached Radar path so it is immediately re-requested for the new next waypoint.
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

        var maxX = areaDimensions.Value.X;
        var maxY = Math.Min(pathData.Length, areaDimensions.Value.Y);

        var r = Math.Min(Settings.MapRender.ExplorationRoute.DetectionRadius.Value, Math.Min(maxX, maxY) / 4);
        if (r < 4) return;

        Vector2? playerPos = null;
        var positioned = GameController?.Game?.IngameState?.Data?.LocalPlayer?.GetComponent<Positioned>();
        if (positioned != null)
            playerPos = new Vector2(positioned.GridPosNum.X, positioned.GridPosNum.Y);

        var outsideBlockedMask = BuildOutsideBlockedMask(pathData, maxX, maxY);
        var baseStep = Math.Max(4, (int)(r * 0.9f));

        List<Vector2> candidates = null;
        foreach (var step in new[] { baseStep, Math.Max(4, (int)(r * 2f / 3f)), Math.Max(4, r / 2) }.Distinct())
        {
            candidates = CollectCandidates(pathData, outsideBlockedMask, maxX, maxY, r, step);
            if (candidates.Count >= 8) break;
        }

        if (candidates is { Count: > 0 })
        {
            // Drop waypoints in areas not reachable from the player's starting position
            // (e.g. isolated corner enclosures that are walkable but disconnected).
            if (playerPos.HasValue)
                candidates = FilterToReachableComponent(candidates, playerPos.Value, pathData, baseStep);

            if (playerPos.HasValue)
                TryAddPlayerAnchorCandidate(candidates, playerPos.Value, pathData, outsideBlockedMask, r);

            _explorationRoute = BuildCoverageRoute(candidates, playerPos, pathData);
        }
    }

    private static void TryAddPlayerAnchorCandidate(
        List<Vector2> candidates, Vector2 playerPos, int[][] pathData, bool[][] outsideBlockedMask, int r)
    {
        if (candidates.Count == 0) return;

        var minExistingDistSq = float.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            var d = Vector2.DistanceSquared(candidates[i], playerPos);
            if (d < minExistingDistSq) minExistingDistSq = d;
        }

        var minWantedDist = Math.Max(6, r / 3);
        if (minExistingDistSq <= minWantedDist * minWantedDist) return;

        var px = (int)playerPos.X;
        var py = (int)playerPos.Y;
        var maxSearch = Math.Max(10, r / 2);
        var clearanceLevels = new[] { Math.Max(6, r / 3), Math.Max(4, r / 4), 3 }.Distinct();

        foreach (var clearance in clearanceLevels)
        {
            for (var radius = 0; radius <= maxSearch; radius++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;

                    var x = px + dx;
                    var y = py + dy;
                    if (!IsWalkableCell(pathData, y, x)) continue;
                    if (!HasOutsideWallClearance(outsideBlockedMask, y, x, clearance)) continue;

                    candidates.Add(new Vector2(x, y));
                    return;
                }
            }
        }
    }

    private static List<Vector2> BuildCoverageRoute(List<Vector2> points, Vector2? startNear, int[][] pathData = null)
    {
        return OrderByNearestNeighbor(points, startNear, pathData);
    }

    private List<Vector2> CollectCandidates(int[][] pathData, bool[][] outsideBlockedMask, int maxX, int maxY,
                                            int r, int step)
    {
        var result = new List<Vector2>();
        var best   = new List<Vector2>();
        var clearanceLevels = new[]
        {
            Math.Max(6, r / 3),
            Math.Max(4, r / 5),
            Math.Max(3, r / 8),
            2,
        }.Distinct();

        var sampleMargin = Math.Max(2, r / 10);
        const int minDesiredCandidates = 6;

        foreach (var wallClearance in clearanceLevels)
        {
            result.Clear();

            for (var y = sampleMargin; y < maxY - sampleMargin; y += step)
            for (var x = sampleMargin; x < maxX - sampleMargin; x += step)
            {
                if (!IsWalkableCell(pathData, y, x)) continue;
                if (HasOutsideWallClearance(outsideBlockedMask, y, x, wallClearance))
                    result.Add(new Vector2(x, y));
            }

            if (result.Count > best.Count)
                best = new List<Vector2>(result);

            if (result.Count >= minDesiredCandidates)
                break;
        }

        return best;
    }

    private static bool HasOutsideWallClearance(bool[][] outsideBlockedMask, int y, int x, int clearance)
    {
        var cSq = clearance * clearance;
        for (var dy = -clearance; dy <= clearance; dy++)
        for (var dx = -clearance; dx <= clearance; dx++)
        {
            if (dx * dx + dy * dy > cSq) continue;
            if (IsOutsideBlockedCell(outsideBlockedMask, y + dy, x + dx)) return false;
        }
        return true;
    }

    private static bool[][] BuildOutsideBlockedMask(int[][] pathData, int maxX, int maxY)
    {
        var mask = new bool[maxY][];
        for (var y = 0; y < maxY; y++)
            mask[y] = new bool[maxX];

        var q = new Queue<(int x, int y)>();

        void EnqueueIfOutsideBlocked(int x, int y)
        {
            if (y < 0 || y >= maxY || x < 0 || x >= maxX) return;
            if (mask[y][x]) return;
            if (IsWalkableCell(pathData, y, x)) return;
            mask[y][x] = true;
            q.Enqueue((x, y));
        }

        for (var x = 0; x < maxX; x++)
        {
            EnqueueIfOutsideBlocked(x, 0);
            EnqueueIfOutsideBlocked(x, maxY - 1);
        }

        for (var y = 0; y < maxY; y++)
        {
            EnqueueIfOutsideBlocked(0, y);
            EnqueueIfOutsideBlocked(maxX - 1, y);
        }

        while (q.Count > 0)
        {
            var (cx, cy) = q.Dequeue();
            EnqueueIfOutsideBlocked(cx + 1, cy);
            EnqueueIfOutsideBlocked(cx - 1, cy);
            EnqueueIfOutsideBlocked(cx, cy + 1);
            EnqueueIfOutsideBlocked(cx, cy - 1);
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

    // Bresenham-style walkability trace; capped at 200 steps so it stays fast.
    // Returns false as soon as any non-walkable cell is crossed.
    private static bool HasLineOfSight(int[][] pathData, Vector2 from, Vector2 to)
    {
        var dx    = to.X - from.X;
        var dy    = to.Y - from.Y;
        var steps = (int)Math.Max(Math.Abs(dx), Math.Abs(dy));
        steps     = Math.Min(steps, 200);
        if (steps < 2) return true;

        for (var i = 1; i < steps; i++)
        {
            var x = (int)(from.X + dx * i / steps);
            var y = (int)(from.Y + dy * i / steps);
            if (!IsWalkableCell(pathData, y, x)) return false;
        }
        return true;
    }

    // Greedy nearest-neighbour TSP starting from the point nearest to startNear.
    // When pathData is provided, direct LOS candidates are always preferred first,
    // and non-LOS pairs are used only as fallback with a heavy distance penalty.
    private static List<Vector2> OrderByNearestNeighbor(
        List<Vector2> points, Vector2? startNear, int[][] pathData = null)
    {
        if (points.Count <= 1) return new List<Vector2>(points);

        var startIdx = 0;
        if (startNear.HasValue)
        {
            var bestDist = float.MaxValue;
            for (var i = 0; i < points.Count; i++)
            {
                var d = Vector2.DistanceSquared(points[i], startNear.Value);
                if (d < bestDist) { bestDist = d; startIdx = i; }
            }
        }

        var result = new List<Vector2>(points.Count);
        var used   = new bool[points.Count];
        var cur    = startIdx;
        used[cur]  = true;
        result.Add(points[cur]);

        for (var i = 1; i < points.Count; i++)
        {
            var bestLos      = -1;
            var bestLosScore = float.MaxValue;
            var bestAny      = -1;
            var bestAnyScore = float.MaxValue;

            for (var j = 0; j < points.Count; j++)
            {
                if (used[j]) continue;

                var d = Vector2.DistanceSquared(points[cur], points[j]);
                var hasLos = pathData == null || HasLineOfSight(pathData, points[cur], points[j]);

                if (hasLos && d < bestLosScore)
                {
                    bestLosScore = d;
                    bestLos = j;
                }

                if (!hasLos)
                    d *= 36f;

                if (d < bestAnyScore)
                {
                    bestAnyScore = d;
                    bestAny = j;
                }
            }

            var best = bestLos >= 0 ? bestLos : bestAny;
            if (best < 0) break;

            used[best] = true;
            cur        = best;
            result.Add(points[best]);
        }
        return result;
    }

    // BFS over the candidate grid (step-spaced points connected by LOS) to keep only
    // the component containing the player's spawn, dropping isolated patches.
    private static List<Vector2> FilterToReachableComponent(
        List<Vector2> candidates, Vector2 playerPos, int[][] pathData, int step)
    {
        if (candidates.Count == 0) return candidates;

        var startX = (int)playerPos.X;
        var startY = (int)playerPos.Y;

        // If the player's exact cell is unwalkable, expand outward to find a walkable seed.
        if (!IsWalkableCell(pathData, startY, startX))
        {
            var found = false;
            for (var r = 1; r <= step * 2 && !found; r++)
            for (var dy = -r; dy <= r && !found; dy++)
            for (var dx = -r; dx <= r && !found; dx++)
            {
                if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                if (!IsWalkableCell(pathData, startY + dy, startX + dx)) continue;
                startX += dx; startY += dy; found = true;
            }
            if (!found) return candidates; // can't determine reachability, keep all
        }

        // Full flood-fill on walkable cells from the player's spawn position.
        // This correctly routes around obstacles that would break a direct LOS check.
        var reachable = new HashSet<(int, int)>();
        var queue     = new Queue<(int, int)>();

        void TryEnqueue(int x, int y)
        {
            if (!IsWalkableCell(pathData, y, x)) return;
            if (!reachable.Add((x, y))) return;
            queue.Enqueue((x, y));
        }

        TryEnqueue(startX, startY);

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            TryEnqueue(cx + 1, cy);
            TryEnqueue(cx - 1, cy);
            TryEnqueue(cx, cy + 1);
            TryEnqueue(cx, cy - 1);
        }

        // Keep only candidates whose grid cell was reached by the flood-fill.
        return candidates.Where(c => reachable.Contains(((int)c.X, (int)c.Y))).ToList();
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
        var routeCol   = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.2f, 0.8f, 1f,   0.7f));
        var nextCol    = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f,   1f,   0f,   1f));

        for (var i = 0; i < _explorationRoute.Count - 1; i++)
        {
            var a = mapCenter + TranslateGridDeltaToMapDelta(_explorationRoute[i]     - playerGridPos, 0);
            var b = mapCenter + TranslateGridDeltaToMapDelta(_explorationRoute[i + 1] - playerGridPos, 0);
            var col = _visitedWaypointIndices.Contains(i) && _visitedWaypointIndices.Contains(i + 1)
                ? visitedCol : routeCol;
            _mapDrawList.AddLine(a, b, col, 1.5f);
        }

        for (var i = 0; i < _explorationRoute.Count; i++)
        {
            var pos = mapCenter + TranslateGridDeltaToMapDelta(_explorationRoute[i] - playerGridPos, 0);
            if (i == nextIdx)
                _mapDrawList.AddCircleFilled(pos, 5f, nextCol);
            else if (!_visitedWaypointIndices.Contains(i))
                _mapDrawList.AddCircleFilled(pos, 2f, routeCol);
        }

        DrawDetectionRadiusOnMap(mapCenter, Settings.MapRender.ExplorationRoute.DetectionRadius.Value);
    }

    private void DrawDetectionRadiusOnMap(Vector2 mapCenter, int radius)
    {
        const int segments = 48;
        var col = ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(1f, 1f, 0.2f, 0.45f));

        Vector2? prev = null;
        for (var i = 0; i <= segments; i++)
        {
            var angle  = i * 2f * MathF.PI / segments;
            var mapPos = mapCenter + TranslateGridDeltaToMapDelta(
                new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius), 0);
            if (prev.HasValue)
                _mapDrawList.AddLine(prev.Value, mapPos, col, 1.5f);
            prev = mapPos;
        }
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
