using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WhatsAppSaaS.Application.Common;

/// <summary>
/// Parses Business.Schedule strings like "Lun-Vie 8am-6pm" or "Lun-Sab 10:00-22:00"
/// to determine if the business is currently open.
/// Returns null (open) if the schedule can't be parsed — safe fallback.
/// </summary>
public static class ScheduleParser
{
    private static readonly Regex TimeRangeRegex = new(
        @"(\d{1,2})(?::(\d{2}))?\s*(am|pm|AM|PM)?\s*[-–a]\s*(\d{1,2})(?::(\d{2}))?\s*(am|pm|AM|PM)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[][] DayGroups =
    [
        ["lun", "mon"],
        ["mar", "tue"],
        ["mi[eé]", "wed"],
        ["jue", "thu"],
        ["vie", "fri"],
        ["s[aá]b", "sat"],
        ["dom", "sun"],
    ];

    /// <summary>
    /// Returns true if the business is currently closed based on the schedule string.
    /// Returns false (= open) if schedule is null, empty, or unparseable — safe fallback.
    /// </summary>
    public static bool IsClosed(string? schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
            return false; // No schedule = always open (safe default)

        try
        {
            return IsClosedInternal(schedule);
        }
        catch
        {
            return false; // Parse error = assume open
        }
    }

    private static bool IsClosedInternal(string schedule)
    {
        var now = DateTime.UtcNow;
        // Try to use Venezuela timezone (UTC-4) as default for this market
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Caracas");
            now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch
        {
            now = DateTime.UtcNow.AddHours(-4); // fallback
        }

        var currentDay = (int)now.DayOfWeek; // Sunday=0, Monday=1, ..., Saturday=6
        // Remap to our array: Monday=0, ..., Sunday=6
        var dayIndex = currentDay == 0 ? 6 : currentDay - 1;

        var lower = schedule.ToLowerInvariant();

        // Check if schedule contains "24h" or "24 horas" — always open
        if (lower.Contains("24h") || lower.Contains("24 horas") || lower.Contains("siempre"))
            return false;

        // Check if today is a listed closed day (e.g., "cerrado domingos")
        if (lower.Contains("cerrado"))
        {
            // Check if any day pattern matching today appears after "cerrado"
            var closedIdx = lower.IndexOf("cerrado", StringComparison.Ordinal);
            var afterClosed = lower[closedIdx..];
            if (DayMatchesText(afterClosed, dayIndex))
                return true;
        }

        // Parse day range (e.g., "Lun-Vie", "Lun-Sab")
        var dayRangeMatch = Regex.Match(lower, @"([a-záéíóú]+)\s*[-–a]\s*([a-záéíóú]+)");
        if (dayRangeMatch.Success)
        {
            var startDay = FindDayIndex(dayRangeMatch.Groups[1].Value);
            var endDay = FindDayIndex(dayRangeMatch.Groups[2].Value);

            if (startDay >= 0 && endDay >= 0)
            {
                bool inDayRange;
                if (startDay <= endDay)
                    inDayRange = dayIndex >= startDay && dayIndex <= endDay;
                else
                    inDayRange = dayIndex >= startDay || dayIndex <= endDay; // wraps around weekend

                if (!inDayRange)
                    return true; // Today is outside the operating days
            }
        }

        // Parse time range
        var timeMatch = TimeRangeRegex.Match(schedule);
        if (!timeMatch.Success)
            return false; // Can't parse time = assume open

        var openHour = ParseHour(timeMatch.Groups[1].Value, timeMatch.Groups[2].Value, timeMatch.Groups[3].Value);
        var closeHour = ParseHour(timeMatch.Groups[4].Value, timeMatch.Groups[5].Value, timeMatch.Groups[6].Value);

        var currentMinutes = now.Hour * 60 + now.Minute;

        if (openHour < closeHour)
        {
            // Normal range (e.g., 8:00-22:00)
            return currentMinutes < openHour || currentMinutes >= closeHour;
        }
        else
        {
            // Overnight range (e.g., 22:00-6:00)
            return currentMinutes < openHour && currentMinutes >= closeHour;
        }
    }

    private static int ParseHour(string hourStr, string minuteStr, string ampmStr)
    {
        var hour = int.Parse(hourStr);
        var minute = string.IsNullOrEmpty(minuteStr) ? 0 : int.Parse(minuteStr);
        var ampm = ampmStr?.ToLowerInvariant() ?? "";

        if (ampm == "pm" && hour < 12) hour += 12;
        if (ampm == "am" && hour == 12) hour = 0;

        return hour * 60 + minute;
    }

    private static int FindDayIndex(string text)
    {
        for (int i = 0; i < DayGroups.Length; i++)
        {
            foreach (var pattern in DayGroups[i])
            {
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                    return i;
            }
        }
        return -1;
    }

    private static bool DayMatchesText(string text, int dayIndex)
    {
        if (dayIndex < 0 || dayIndex >= DayGroups.Length) return false;
        foreach (var pattern in DayGroups[dayIndex])
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }
}
