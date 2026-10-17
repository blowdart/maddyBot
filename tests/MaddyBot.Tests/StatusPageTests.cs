// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Net.Http.Headers;

using Xunit;

namespace MaddyBot.Tests;

/// <summary>Checks the home page's replay lag estimates without starting workers or contacting dependencies.</summary>
public sealed class StatusPageTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 16, 14, 54, 40, TimeSpan.Zero);

    /// <summary>Preserves plain text unless the client explicitly requests an acceptable HTML response.</summary>
    /// <param name="accept">The client's Accept header, or null when absent.</param>
    /// <param name="expected">Whether the response should be HTML.</param>
    [Theory]
    [InlineData(null, false)]
    [InlineData("*/*", false)]
    [InlineData("text/plain", false)]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", true)]
    [InlineData("TEXT/HTML", true)]
    [InlineData("text/html;q=0", false)]
    [InlineData("text/plain;q=1,text/html;q=0.5", false)]
    [InlineData("text/plain;q=0.5,text/html;q=1", true)]
    public void SelectsRequestedFormat(string? accept, bool expected)
    {
        IList<MediaTypeHeaderValue>? types = accept is null
            ? null
            : MediaTypeHeaderValue.ParseList([accept]);

        Assert.Equal(expected, StatusPage.PrefersHtml(types));
    }

    /// <summary>Keeps the complete UTC status available before browser-local enhancement runs.</summary>
    /// <param name="knownProgress">Whether handled replay progress has been observed.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HtmlPreservesEncodedPlainText(bool knownProgress)
    {
        ActionStatistics statistics = new(s_now);
        DateTimeOffset? witnessed = knownProgress ? s_now.AddSeconds(-5) : null;
        DateTimeOffset? observed = knownProgress ? s_now : null;
        string text = StatusPage.Render(statistics, witnessed, observed, s_now);

        string html = StatusPage.RenderHtml(statistics, witnessed, observed, s_now);

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains($"<pre id=\"status\">{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(text)}</pre>", html);
        Assert.Contains("new Intl.DateTimeFormat(undefined", html);
        Assert.Contains("timestamp.dateTime = instant", html);
        Assert.Contains("browser local", html);
        Assert.DoesNotContain("innerHTML", html);
    }

    /// <summary>Preserves the action count while explaining unknown progress at startup or ownership change.</summary>
    [Fact]
    public void ShowsUnknownProgress()
    {
        ActionStatistics statistics = new(s_now);
        statistics.RecordCompletion();

        string page = StatusPage.Render(statistics, null, null, s_now);

        Assert.StartsWith(statistics.Summary + "\n", page);
        Assert.Contains("Replay progress not yet known", page);
        Assert.DoesNotContain("behind", page);
        Assert.EndsWith("\n", page);
    }

    /// <summary>Shows the observed backlog and UTC timestamps without claiming durability or connectivity.</summary>
    [Fact]
    public void ShowsBacklog()
    {
        DateTimeOffset witnessed = new(2026, 10, 16, 9, 40, 10, TimeSpan.FromHours(1));
        string page = StatusPage.Render(new(s_now), witnessed, s_now.AddSeconds(-2), s_now);

        Assert.Contains("Catching up - 6h 14m 30s behind.", page);
        Assert.Contains("Last handled event witnessed: 2026-10-16 08:40:10Z.", page);
        Assert.Contains("Progress last observed: 2026-10-16 14:54:38Z (0h 0m 2s ago).", page);
        Assert.Contains("not a durable checkpoint or proof of connectivity or liveness", page);
        Assert.DoesNotContain("No recent handled progress", page);
        Assert.EndsWith("\n", page);
    }

    /// <summary>Uses the SDK's exact thirty-second near-current-time threshold.</summary>
    /// <param name="lagSeconds">The estimated event-time lag in seconds.</param>
    /// <param name="expected">The expected status description.</param>
    [Theory]
    [InlineData(0, "Near current time (within 30 seconds) - 0h 0m 0s behind.")]
    [InlineData(30, "Near current time (within 30 seconds) - 0h 0m 30s behind.")]
    [InlineData(31, "Catching up - 0h 0m 31s behind.")]
    [InlineData(90061, "Catching up - 25h 1m 1s behind.")]
    [InlineData(-5, "Replay event timestamp is 0h 0m 5s ahead of the local clock.")]
    public void DescribesLag(int lagSeconds, string expected)
    {
        string page = StatusPage.Render(new(s_now), s_now.AddSeconds(-lagSeconds), s_now, s_now);

        Assert.Contains(expected, page);
    }

    /// <summary>Recalculates lag and marks old observations without inventing new progress.</summary>
    [Fact]
    public void ShowsAgingObservation()
    {
        ActionStatistics statistics = new(s_now);
        string initial = StatusPage.Render(statistics, s_now, s_now, s_now.AddSeconds(30));
        string later = StatusPage.Render(statistics, s_now, s_now, s_now.AddSeconds(31));

        Assert.DoesNotContain("No recent handled progress", initial);
        Assert.Contains("Catching up - 0h 0m 31s behind.", later);
        Assert.Contains("2026-10-16 14:54:40Z (0h 0m 31s ago)", later);
        Assert.Contains("No recent handled progress observed; the reader may be waiting or recovering.", later);
    }

    /// <summary>Reports local clock movement explicitly rather than showing a negative observation age.</summary>
    [Fact]
    public void ShowsFutureObservation()
    {
        string page = StatusPage.Render(new(s_now), s_now, s_now.AddSeconds(5), s_now);

        Assert.Contains("(0h 0m 5s ahead of the local clock)", page);
    }
}