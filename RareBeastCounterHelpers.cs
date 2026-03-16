using System;
using System.Globalization;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace RareBeastCounter;

internal static class RareBeastCounterHelpers
{
    public static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    public static Vector4 ToImGuiColor(Color color)
    {
        return new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }

    public static Vector2[] CreateUnitCirclePoints(int segments, bool closeLoop = true)
    {
        var pointCount = closeLoop ? segments + 1 : segments;
        var points = new Vector2[pointCount];
        for (var i = 0; i < segments; i++)
        {
            var angle = i * 2f * MathF.PI / segments;
            points[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }

        if (closeLoop)
        {
            points[segments] = points[0];
        }

        return points;
    }

    public static string CsvEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    public static string TryGetAreaHashText(object area)
    {
        if (area == null)
        {
            return null;
        }

        static string ReadHash(object value, string propertyName)
        {
            var prop = value.GetType().GetProperty(propertyName);
            return prop?.GetValue(value)?.ToString();
        }

        return ReadHash(area, "AreaHash") ?? ReadHash(area, "Hash");
    }
}
