// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Encodings.Web;

using idunno.Bluesky.BotSdk;

using Microsoft.Net.Http.Headers;

namespace MaddyBot;

/// <summary>Formats the memory-only home page without treating replay lag as a liveness failure.</summary>
internal static class StatusPage
{
    /// <summary>Selects HTML only when the client explicitly prefers it over plain text.</summary>
    /// <param name="acceptedTypes">The parsed Accept header values, if provided by the client.</param>
    /// <returns><see langword="true"/> for an HTML response; otherwise, <see langword="false"/>.</returns>
    public static bool PrefersHtml(IList<MediaTypeHeaderValue>? acceptedTypes)
    {
        if (acceptedTypes is null || !acceptedTypes.Any(
            type => type.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                && type.Quality.GetValueOrDefault(1) > 0))
        {
            return false;
        }

        double htmlQuality = EffectiveQuality(acceptedTypes, "text/html");
        double textQuality = EffectiveQuality(acceptedTypes, "text/plain");

        return htmlQuality > 0 && htmlQuality >= textQuality;
    }

    /// <summary>Uses the most specific matching media range before comparing response qualities.</summary>
    /// <param name="acceptedTypes">The client's parsed Accept header values.</param>
    /// <param name="mediaType">The response media type being considered.</param>
    /// <returns>The quality of the most specific matching range, or zero when none matches.</returns>
    private static double EffectiveQuality(IList<MediaTypeHeaderValue> acceptedTypes, string mediaType) =>
        acceptedTypes
            .Where(type => type.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase)
                || type.MediaType.Equals("text/*", StringComparison.OrdinalIgnoreCase)
                || type.MediaType.Equals("*/*", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(type => type.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase)
                ? 2
                : type.MediaType.Equals("text/*", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(type => type.Quality.GetValueOrDefault(1))
            .Select(type => type.Quality.GetValueOrDefault(1))
            .FirstOrDefault();

    /// <summary>Wraps the status text in a browser page that adds local times without replacing UTC.</summary>
    /// <param name="statistics">The current process's completed action count.</param>
    /// <param name="witnessedAt">The service witnessed time from the replay snapshot, or null before progress is known.</param>
    /// <param name="observedAt">The local observation time from the same snapshot, or null before progress is known.</param>
    /// <param name="now">The current UTC wall-clock time used to estimate lag and observation age.</param>
    /// <returns>The HTML status page with browser-local timestamps when JavaScript is available.</returns>
    public static string RenderHtml(
        ActionStatistics statistics,
        DateTimeOffset? witnessedAt,
        DateTimeOffset? observedAt,
        DateTimeOffset now)
    {
        string text = HtmlEncoder.Default.Encode(Render(statistics, witnessedAt, observedAt, now));

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>MaddyBot status</title>
              <style>
                body { font-family: "Cascadia Code", ui-monospace, Consolas, monospace; margin: 2rem; }
                pre { font-family: inherit; white-space: pre-wrap; overflow-wrap: anywhere; }
              </style>
            </head>
            <body>
              <h1>MaddyBot status</h1>
              <p>Times are UTC (Z). Browser-local times appear alongside them when JavaScript is enabled.</p>
              <pre id="status">{{text}}</pre>
              <script>
                const status = document.getElementById("status");
                const text = status.textContent;
                const formatter = new Intl.DateTimeFormat(undefined, {
                  dateStyle: "medium",
                  timeStyle: "long"
                });
                const fragment = document.createDocumentFragment();
                let position = 0;
                for (const match of text.matchAll(/\b\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z\b/g)) {
                  fragment.append(document.createTextNode(text.slice(position, match.index)));
                  const instant = match[0].replace(" ", "T");
                  const timestamp = document.createElement("time");
                  timestamp.dateTime = instant;
                  timestamp.textContent = match[0];
                  fragment.append(timestamp);
                  fragment.append(document.createTextNode(
                    " (" + formatter.format(new Date(instant)) + " browser local)"
                  ));
                  position = match.index + match[0].length;
                }
                fragment.append(document.createTextNode(text.slice(position)));
                status.replaceChildren(fragment);
              </script>
            </body>
            </html>
            """;
    }

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
            return $"{statistics.Summary}\nReplay progress not yet known; no handled event observed in this ownership period.\n";
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
            """) + "\n";
    }

    /// <summary>Formats a nonnegative duration with total hours so long backlogs do not wrap at midnight.</summary>
    /// <param name="duration">The elapsed duration or magnitude of a clock difference.</param>
    /// <returns>The culture-independent hours, minutes, and seconds.</returns>
    private static string Duration(TimeSpan duration) =>
        string.Create(CultureInfo.InvariantCulture, $"{(long)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s");
}