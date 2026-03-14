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

            var analyticsLines = BuildAnalyticsLines();

            var fileName = $"RareBeastCounter_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            var filePath = Path.Combine(directory, fileName);

            var lines = new List<string>
            {
                RareBeastCounterHelpers.CsvEscape(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
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
        if (!_sessionProcessedRareBeastIds.Add(entity.Id))
        {
            return;
        }

        if (!IsRedBeastByMetadata(entity.Metadata))
        {
            // Still track if this is a valuable yellow beast (not counted toward red total)
            if (TryGetValuableTrackedBeastName(entity.Metadata, out var yellowBeastName))
            {
                _valuableBeastCounts[yellowBeastName]++;
            }
            return;
        }

        _totalRedBeastsSession++;

        if (TryGetValuableTrackedBeastName(entity.Metadata, out var beastName))
        {
            _valuableBeastCounts[beastName]++;
        }
    }

    private static bool IsRedBeastByMetadata(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return false;
        }

        foreach (var pattern in RedBeastMetadataPatterns)
        {
            if (metadata.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetValuableTrackedBeastName(string metadata, out string beastName)
    {
        beastName = null;

        if (string.IsNullOrWhiteSpace(metadata))
        {
            return false;
        }

        foreach (var tracked in ValuableTrackedBeasts)
        {
            foreach (var pattern in tracked.MetadataPatterns)
            {
                if (metadata.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    beastName = tracked.Name;
                    return true;
                }
            }
        }

        return false;
    }

    private List<string> BuildAnalyticsLines()
    {
        var lines = new List<string>(4 + ValuableTrackedBeasts.Length);
        var now = DateTime.UtcNow;

        var currentMapTime = _currentMapElapsed;
        if (_isCurrentAreaTrackable && _currentMapStartUtc.HasValue)
        {
            currentMapTime += now - _currentMapStartUtc.Value;
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

        lines.Add($"Map Time: {RareBeastCounterHelpers.FormatDuration(currentMapTime)}");
        lines.Add($"Avg Map: {(_completedMapCount > 0 ? RareBeastCounterHelpers.FormatDuration(averageMapTime) : "n/a")} ({_completedMapCount} maps)");
        lines.Add($"Session: {RareBeastCounterHelpers.FormatDuration(totalSessionTime)}");
        lines.Add($"Beasts Found (Session): {_sessionProcessedRareBeastIds.Count.ToString("N0", CultureInfo.InvariantCulture)}");

        var denominator = _totalRedBeastsSession;
        var denominatorText = denominator.ToString("N0", CultureInfo.InvariantCulture);

        foreach (var tracked in ValuableTrackedBeasts)
        {
            var count = _valuableBeastCounts[tracked.Name];
            var countText = count.ToString("N0", CultureInfo.InvariantCulture);

            if (tracked.IsYellow)
            {
                var freqText = count > 0
                    ? $"1 every ~{(denominator / (double)count).ToString("0", CultureInfo.InvariantCulture)} reds"
                    : "no sightings yet";
                lines.Add($"{tracked.Name}: {countText} ({freqText})");
            }
            else
            {
                double pct = denominator > 0 ? count * 100d / denominator : 0d;
                var freqText = count > 0
                    ? $"1 every ~{(denominator / (double)count).ToString("0", CultureInfo.InvariantCulture)} reds"
                    : "no sightings yet";
                lines.Add($"{tracked.Name}: {countText} / {denominatorText} red beasts = {pct.ToString("0.000", CultureInfo.InvariantCulture)}% ({freqText})");
            }
        }

        return lines;
    }
}
