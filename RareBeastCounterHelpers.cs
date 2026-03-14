using System;
using System.Globalization;
using SharpDX;
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
