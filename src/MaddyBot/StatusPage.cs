// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

using idunno.Bluesky.BotSdk;

namespace MaddyBot;

/// <summary>Formats the memory-only home page without treating replay lag as a liveness failure.</summary>
internal static class StatusPage
{
    /// <summary>Combines the current run's action count with one atomic handled-event observation.</summary>
    /// <param name="statistics">The current process's completed action count.</param>
    /// <param name="witnessedAt">The service witnessed time from the replay snapshot, or null before progress is known.</param>
    /// <param name="observedAt">The local observation time from the same snapshot, or null before progress is known.</param>
    /// <param name="now">The current UTC wall-clock time used to estimate lag and observation age.</param>
    /// <returns>The plain-text status page, including unknown progress and clock differences.</returns>
    public static string Render(
        ActionStatistics statistics,
        DateTimeOffset? witnessedAt,
        DateTimeOffset? observedAt,
        DateTimeOffset now)
    {
        if (witnessedAt is not DateTimeOffset witnessed || observedAt is not DateTimeOffset observed)
        {
            return $"{statistics.Summary}\nReplay progress not yet known; no handled event observed in this ownership period.";
        }

        TimeSpan lag = now - witnessed;
        TimeSpan age = now - observed;
        string position = lag < TimeSpan.Zero
            ? $"Replay event timestamp is {Duration(-lag)} ahead of the local clock."
            : lag > TimeSpan.FromSeconds(30)
                ? $"Catching up - {Duration(lag)} behind."
                : $"Near current time (within 30 seconds) - {Duration(lag)} behind.";
        string observationAge = age < TimeSpan.Zero
            ? $"{Duration(-age)} ahead of the local clock"
            : $"{Duration(age)} ago";
        string activity = age > TimeSpan.FromSeconds(30)
            ? "\nNo recent handled progress observed; the reader may be waiting or recovering."
            : "";

        return string.Create(CultureInfo.InvariantCulture, $"""
            {statistics.Summary}
            {position}
            Last handled event witnessed: {witnessed.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z.
            Progress last observed: {observed.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z ({observationAge}).{activity}
            Lag is an event-time estimate, not a durable checkpoint or proof of connectivity or liveness.
            """);
    }

    /// <summary>Formats a nonnegative duration with total hours so long backlogs do not wrap at midnight.</summary>
    /// <param name="duration">The elapsed duration or magnitude of a clock difference.</param>
    /// <returns>The culture-independent hours, minutes, and seconds.</returns>
    private static string Duration(TimeSpan duration) =>
        string.Create(CultureInfo.InvariantCulture, $"{(long)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s");
}