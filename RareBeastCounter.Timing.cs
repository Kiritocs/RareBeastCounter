using System;
using ImGuiNET;

namespace RareBeastCounter;

public partial class RareBeastCounter
{
    private void ApplyPauseMenuTimerState(DateTime now)
    {
        var pauseMenuOpen = IsPauseMenuOpen();

        if (pauseMenuOpen)
        {
            if (!_pauseMenuSessionStartUtc.HasValue)
            {
                _pauseMenuSessionStartUtc = now;
            }

            if (_isCurrentAreaTrackable)
            {
                PauseCurrentMapTimer(now);
            }

            return;
        }

        if (_pauseMenuSessionStartUtc.HasValue)
        {
            var paused = now - _pauseMenuSessionStartUtc.Value;
            if (paused > TimeSpan.Zero)
            {
                _sessionPausedDuration += paused;
            }

            _pauseMenuSessionStartUtc = null;
        }

        if (_isCurrentAreaTrackable && !_currentMapStartUtc.HasValue)
        {
            _currentMapStartUtc = now;
        }
    }

    private bool IsPauseMenuOpen()
    {
        try
        {
            var game = GameController?.Game;
            if (game == null)
            {
                return false;
            }

            var gameType = game.GetType();
            if (_cachedGameType != gameType)
            {
                _cachedGameType = gameType;
                _cachedIsEscapeStateProperty = gameType.GetProperty("IsEscapeState");
            }

            return _cachedIsEscapeStateProperty?.PropertyType == typeof(bool) &&
                   _cachedIsEscapeStateProperty.GetValue(game) is bool value && value;
        }
        catch
        {
            return false;
        }
    }

    private void PauseCurrentMapTimer(DateTime now)
    {
        if (_currentMapStartUtc.HasValue)
        {
            var elapsed = now - _currentMapStartUtc.Value;
            if (elapsed > TimeSpan.Zero)
            {
                _currentMapElapsed += elapsed;
            }

            _currentMapStartUtc = null;
        }
    }

    private void FinalizePausedMap()
    {
        if (_currentMapElapsed > TimeSpan.Zero)
        {
            _completedMapsDuration += _currentMapElapsed;
            _completedMapCount++;
        }

        _currentMapElapsed = TimeSpan.Zero;
    }

    private void ResetSessionAnalytics()
    {
        if (!ImGui.GetIO().KeyShift)
        {
            return;
        }

        _sessionStartUtc = DateTime.UtcNow;
        _sessionPausedDuration = TimeSpan.Zero;
        _pauseMenuSessionStartUtc = null;
        _totalRedBeastsSession = 0;
        _sessionProcessedRareBeastIds.Clear();

        foreach (var tracked in AllRedBeasts)
        {
            _valuableBeastCounts[tracked.Name] = 0;
        }

        _completedMapsDuration = TimeSpan.Zero;
        _completedMapCount = 0;
        _currentMapElapsed = TimeSpan.Zero;
        _currentMapStartUtc = _isCurrentAreaTrackable ? DateTime.UtcNow : null;
    }

    private void ResetMapAverageAnalytics()
    {
        if (!ImGui.GetIO().KeyShift)
        {
            return;
        }

        _completedMapsDuration = TimeSpan.Zero;
        _completedMapCount = 0;
    }
}
