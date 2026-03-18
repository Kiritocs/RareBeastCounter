using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ExileCore.PoEMemory.MemoryObjects;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    private void SaveSessionSnapshotToFile()
    {
        try
        {
            var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "RareBeastCounterSessions");
            Directory.CreateDirectory(directory);

            var analyticsLines = new List<string>(4 + Settings.BeastPrices.EnabledBeasts.Count);
            BuildAnalyticsLines(analyticsLines, includeBeastBreakdown: true);
            var now = DateTime.Now;

            var fileName = $"RareBeastCounter_{now:yyyyMMdd_HHmmss}.csv";
            var filePath = Path.Combine(directory, fileName);

            var lines = new List<string>(analyticsLines.Count)
            {
                RareBeastCounterHelpers.CsvEscape(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            };

            foreach (var analyticsLine in analyticsLines)
            {
                if (analyticsLine.StartsWith(MapTimePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                lines.Add(RareBeastCounterHelpers.CsvEscape(analyticsLine.Trim()));
            }

            File.WriteAllLines(filePath, lines);
        }
        catch
        {
        }
    }

    private void RegisterSessionRareBeast(Entity entity)
    {
        _sessionBeastsFound++;

        if (!TryGetTrackedBeastNameCached(entity.Metadata, out var beastName))
        {
            return;
        }

        _totalRedBeastsSession++;
        _valuableBeastCounts[beastName]++;
    }

    private bool TryGetTrackedBeastNameCached(string metadata, out string beastName)
    {
        beastName = null;

        if (string.IsNullOrWhiteSpace(metadata))
        {
            return false;
        }

        if (_trackedBeastNameCache.TryGetValue(metadata, out var cachedName))
        {
            if (cachedName == MissingTrackedBeastName)
            {
                return false;
            }

            beastName = cachedName;
            return true;
        }

        if (TryGetValuableTrackedBeastName(metadata, out beastName))
        {
            _trackedBeastNameCache[metadata] = beastName;
            return true;
        }

        _trackedBeastNameCache[metadata] = MissingTrackedBeastName;
        return false;
    }

    private static bool TryGetValuableTrackedBeastName(string metadata, out string beastName)
    {
        beastName = null;

        if (string.IsNullOrWhiteSpace(metadata))
        {
            return false;
        }

        foreach (var (pattern, trackedBeastName) in TrackedBeastMetadataLookup)
        {
            if (metadata.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                beastName = trackedBeastName;
                return true;
            }
        }

        return false;
    }

    private void BuildAnalyticsLines(List<string> lines, bool includeBeastBreakdown = true)
    {
        lines.Clear();

        var enabledBeasts = Settings.BeastPrices.EnabledBeasts;
        lines.Capacity = Math.Max(lines.Capacity, includeBeastBreakdown ? 4 + enabledBeasts.Count : 1);
        var now = DateTime.UtcNow;

        var currentMapTime = _currentMapElapsed;
        if (_isCurrentAreaTrackable && _currentMapStartUtc.HasValue)
        {
            currentMapTime += now - _currentMapStartUtc.Value;
        }

        lines.Add($"Map Time: {RareBeastCounterHelpers.FormatDuration(currentMapTime)}");

        if (!includeBeastBreakdown)
        {
            return;
        }

        var totalSessionTime = now - _sessionStartUtc - _sessionPausedDuration;
        if (_pauseMenuSessionStartUtc.HasValue)
        {
            totalSessionTime -= now - _pauseMenuSessionStartUtc.Value;
        }

        if (totalSessionTime < TimeSpan.Zero)
        {
            totalSessionTime = TimeSpan.Zero;
        }

        var averageMapTime = _completedMapCount > 0
            ? TimeSpan.FromTicks(_completedMapsDuration.Ticks / _completedMapCount)
            : TimeSpan.Zero;

        lines.Add($"Avg Map: {(_completedMapCount > 0 ? RareBeastCounterHelpers.FormatDuration(averageMapTime) : "n/a")} ({_completedMapCount} maps)");
        lines.Add($"Session: {RareBeastCounterHelpers.FormatDuration(totalSessionTime)}");
        lines.Add($"Beasts Found (Session): {_sessionBeastsFound.ToString("N0", CultureInfo.InvariantCulture)}");

        var denominator = _totalRedBeastsSession;
        var denominatorText = denominator.ToString("N0", CultureInfo.InvariantCulture);

        foreach (var tracked in AllRedBeasts)
        {
            if (!enabledBeasts.Contains(tracked.Name))
            {
                continue;
            }

            var count = _valuableBeastCounts[tracked.Name];
            var countText = count.ToString("N0", CultureInfo.InvariantCulture);

            double pct = denominator > 0 ? count * 100d / denominator : 0d;
            var freqText = count > 0
                ? $"1 every ~{(denominator / (double)count).ToString("0", CultureInfo.InvariantCulture)} reds"
                : "no sightings yet";

            lines.Add($"{tracked.Name}: {countText} / {denominatorText} red beasts = {pct.ToString("0.000", CultureInfo.InvariantCulture)}% ({freqText})");
        }

    }
}









