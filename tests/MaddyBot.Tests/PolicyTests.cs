// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using idunno.AtProto;
using idunno.AtProto.Authentication;
using idunno.AtProto.Jetstream;
using idunno.AtProto.Jetstream.Archive;
using idunno.AtProto.Repo;
using idunno.Bluesky;

using Microsoft.Extensions.Logging;

using Xunit;

namespace MaddyBot.Tests;

/// <summary>Checks matching, configuration, cryptography, SDK compatibility, and liveness without network access.</summary>
public sealed class PolicyTests
{
    /// <summary>Generates lexicon-valid post TIDs before persisting work rather than generic GUID keys.</summary>
    [Fact]
    public void GeneratesValidFixedPostKeys()
    {
        BotAction first = TestRecords.Work();
        BotAction second = TestRecords.Work();
        Assert.True(TimestampIdentifier.TryParse(first.RecordKey, out _));
        Assert.True(TimestampIdentifier.TryParse(second.RecordKey, out _));
        Assert.NotEqual(first.RecordKey, second.RecordKey);
    }

    /// <summary>Ignores lookback only before a replay request exists, without changing first-run eligibility.</summary>
    [Fact]
    public void IgnoresLookbackForUninitializedReplay()
    {
        ReplayState state = new(TestRecords.Start, "wss://example.test", TestRecords.BotDid, true, null, null, 0);
        LookbackOverride lookback = new(TestRecords.Start.AddMinutes(-1));
        Assert.True(lookback.IgnoreIfUninitialized(state));
        Assert.Null(lookback.Cutoff);
        Assert.False(lookback.IgnoreIfUninitialized(state));
        Assert.Equal(TestRecords.Start, state.StartedAt);

        lookback = new(TestRecords.Start.AddMinutes(1));
        Assert.False(lookback.IgnoreIfUninitialized(state with { RequestJson = "{}" }));
        Assert.Equal(TestRecords.Start.AddMinutes(1), lookback.Cutoff);
    }

    /// <summary>Uses an explicit positive decoded-event buffer independent of WebSocket byte buffers.</summary>
    [Fact]
    public void ValidatesReplayBufferCapacity()
    {
        BotOptions options = TestRecords.Options();
        Assert.Equal(8192, options.ReplayLiveBufferCapacity);
        options.Validate();
        Assert.Equal(8192, new JetstreamOptions { ReplayLiveBufferCapacity = options.ReplayLiveBufferCapacity }.ReplayLiveBufferCapacity);
        options.ReplayLiveBufferCapacity = 0;
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        options.ReplayLiveBufferCapacity = -1;
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    /// <summary>Converts UTC instants to server timestamp cursors without treating them as sequences.</summary>
    [Fact]
    public void ConvertsLookbackTimestampToMicroseconds()
    {
        DateTimeOffset cutoff = DateTimeOffset.UnixEpoch.AddDays(20000).AddTicks(1234560);
        Assert.Equal(1728000000123456, LookbackOverride.TimestampCursor(cutoff));
        Assert.Equal(LookbackOverride.TimestampCursor(cutoff),
            LookbackOverride.TimestampCursor(cutoff.ToOffset(TimeSpan.FromHours(2))));
    }

    /// <summary>Keeps the first timestamp-probe event eligible for exclusive archive replay.</summary>
    [Fact]
    public void LookbackBoundaryDoesNotSkipProbeEvent()
    {
        JetstreamCommitEvent item = TestRecords.Event(sequence: 26765134164);
        Assert.Equal(26765134163, LookbackOverride.BoundaryBefore(item));
        Assert.NotNull(ReplyPolicy.Create(item, TestRecords.Start, TestRecords.Options()));
        Assert.Throws<InvalidDataException>(() => LookbackOverride.BoundaryBefore(item with { Sequence = null }));
        Assert.Throws<InvalidDataException>(() => LookbackOverride.BoundaryBefore(item with { Sequence = 1 }));
        Assert.Throws<InvalidDataException>(() => LookbackOverride.BoundaryBefore(item with { WitnessedAt = null }));
    }

    /// <summary>Accepts explicit duration units without accepting negative, fractional, or overflowing lookbacks.</summary>
    [Theory]
    [InlineData("0s", 0)]
    [InlineData("30m", 1800)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    public void ParsesLookback(string value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), LookbackOverride.Parse(value));

    /// <summary>Rejects ambiguous or unsafe duration inputs.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("-1m")]
    [InlineData("1.5h")]
    [InlineData("30")]
    [InlineData("1w")]
    [InlineData("9223372036854775807d")]
    public void RejectsInvalidLookback(string value) =>
        Assert.Throws<FormatException>(() => LookbackOverride.Parse(value));

    /// <summary>Matches ordinal case-insensitive substrings with selective separators, never across sentence punctuation or line breaks.</summary>
    [Theory]
    [InlineData(".NET Aspire", true)]
    [InlineData("Using .net ASPIRE today", true)]
    [InlineData("x.NET AspireSuffix", true)]
    [InlineData("NET Aspire", false)]
    [InlineData(".NET  Aspire", true)]
    [InlineData(".NET     Aspire", true)]
    [InlineData(".NET\tAspire", true)]
    [InlineData(".NET-Aspire", true)]
    [InlineData(".NET_Aspire", true)]
    [InlineData(".NET - Aspire", true)]
    [InlineData(".NET \t-_ \tAspire", true)]
    [InlineData(".NET. Aspire", false)]
    [InlineData(".NET! Aspire", false)]
    [InlineData("My code is written in .NET. Aspire works for me", false)]
    [InlineData(".NET\nAspire", false)]
    [InlineData(".NET\r\nAspire", false)]
    [InlineData(".NET\u00a0Aspire", false)]
    [InlineData(".NET/Aspire", false)]
    [InlineData(".NET, Aspire", false)]
    [InlineData(".NETAspire", false)]
    [InlineData("dotnet Aspire", true)]
    [InlineData("DOTNET\tASPIRE", true)]
    [InlineData("dotnet-Aspire", true)]
    [InlineData("dotnet_Aspire", true)]
    [InlineData("dotnet. Aspire", false)]
    [InlineData("dotnet! Aspire", false)]
    [InlineData("My code is written in dotnet. Aspire works for me", false)]
    [InlineData("dotnet\nAspire", false)]
    [InlineData("Aspire", false)]
    public void MatchesLiteralSubstring(string text, bool matches) =>
        Assert.Equal(matches, ReplyPolicy.Create(TestRecords.Event(text), TestRecords.Start, TestRecords.Options()) is not null);

    /// <summary>Normalizes permitted separators in configured phrases and posts without modifying the source or reply.</summary>
    [Fact]
    public void MatchesConfiguredPhrases()
    {
        BotOptions options = TestRecords.Options();
        options.WatchPhrases = ["first\t_phrase", "second-phrase"];
        Assert.NotNull(ReplyPolicy.Create(TestRecords.Event("FIRST     PHRASE"), TestRecords.Start, options));
        Assert.NotNull(ReplyPolicy.Create(TestRecords.Event("a second   phrase here"), TestRecords.Start, options));
        Assert.Null(ReplyPolicy.Create(TestRecords.Event("first. phrase"), TestRecords.Start, options));
        Assert.Null(ReplyPolicy.Create(TestRecords.Event("second! phrase"), TestRecords.Start, options));
        Assert.Equal("It's just \"Aspire\" \U0001f92c",
            TestRecords.Post(ReplyPolicy.Create(TestRecords.Event("FIRST--PHRASE"), TestRecords.Start, options)!).Text);
        Assert.Equal(["bot", "\U0001f916"],
            TestRecords.Post(ReplyPolicy.Create(TestRecords.Event("FIRST--PHRASE"), TestRecords.Start, options)!).Tags);
        Assert.Null(ReplyPolicy.Create(TestRecords.Event(), TestRecords.Start, options));
        options.WatchPhrases = ["dotnet Aspire"];
        Assert.NotNull(ReplyPolicy.Create(TestRecords.Event(".NET_Aspire"), TestRecords.Start, options));
        Assert.NotNull(ReplyPolicy.Create(TestRecords.Event("DotNet-Aspire"), TestRecords.Start, options));
        Assert.Null(ReplyPolicy.Create(TestRecords.Event(".NET! Aspire"), TestRecords.Start, options));
        options.WatchPhrases = [];
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.WatchPhrases = [" "];
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    /// <summary>Filters updates, own posts, excluded DIDs, sync assertions, and events before the first-run time.</summary>
    [Fact]
    public void FiltersIneligibleEvents()
    {
        BotOptions options = TestRecords.Options();
        JetstreamCommitEvent item = TestRecords.Event();
        Assert.Null(ReplyPolicy.Create(item with { Did = new Did(options.Did) }, TestRecords.Start, options));
        Assert.Null(ReplyPolicy.Create(item with { IsSyncBackfill = true }, TestRecords.Start, options));
        Assert.Null(ReplyPolicy.Create(item with { WitnessedAt = TestRecords.Start.AddTicks(-1) }, TestRecords.Start, options));
        Assert.Null(ReplyPolicy.Create(item with
        {
            Commit = item.Commit with { Operation = JetstreamCommitOperation.Update }
        }, TestRecords.Start, options));
        options.NeverReplyToDids = [TestRecords.AuthorDid];
        Assert.Null(ReplyPolicy.Create(item, TestRecords.Start, options));
    }

    /// <summary>Uses correct parent/root references, permits old createdAt values, and emits the exact default reply.</summary>
    [Fact]
    public void ConstructsCorrectThreadReferences()
    {
        BotAction work = TestRecords.Work();
        Post reply = TestRecords.Post(work);
        Assert.Equal("It's just \"Aspire\" \U0001f92c", reply.Text);
        Assert.Equal(work.SourceUri, reply.Reply!.Parent.Uri.ToString());
        Assert.Equal(TestRecords.CidValue, reply.Reply.Parent.Cid.ToString());
        Assert.Equal(reply.Reply.Parent, reply.Reply.Root);

        StrongReference root = new(new AtUri($"at://{TestRecords.AuthorDid}/app.bsky.feed.post/root"),
            new Cid(TestRecords.CidValue));

        JetstreamCommitEvent nested = TestRecords.Event(reply: new ReplyReferences(root, root));
        BotAction nestedWork = ReplyPolicy.Create(nested, TestRecords.Start, TestRecords.Options())!;
        Assert.Equal(root, TestRecords.Post(nestedWork).Reply!.Root);
        Assert.Equal(nestedWork.SourceUri, TestRecords.Post(nestedWork).Reply!.Parent.Uri.ToString());
    }

    /// <summary>Chooses from the configured replies and captures that choice in the complete record.</summary>
    [Fact]
    public void CapturesConfiguredReply()
    {
        BotOptions options = TestRecords.Options();
        options.Replies = ["one", "two", "three"];
        for (int index = 0; index < 100; index++)
        {
            BotAction work = TestRecords.Work(options);
            Assert.Contains(TestRecords.Post(work).Text, options.Replies);

            Post roundTrip = JsonSerializer.Deserialize<Post>(work.RecordJson, BlueskyJsonSerializerOptions.Options)!;
            Assert.True(ReplyRecords.SameRecord(roundTrip, TestRecords.Post(work)));
            Assert.False(ReplyRecords.SameRecord(roundTrip with { CreatedAt = roundTrip.CreatedAt.AddSeconds(1) }, roundTrip));
        }
    }

    /// <summary>Requires valid, nonempty settings and defaults to non-posting developer mode.</summary>
    [Fact]
    public void ValidatesSettings()
    {
        BotOptions options = TestRecords.Options();
        Assert.True(options.DeveloperMode);
        options.Validate();
        options.Replies = [];
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.Replies = ["valid"];
        options.NeverReplyToDids = ["invalid"];
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.NeverReplyToDids = [];
        options.DeveloperMode = false;
        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>Classifies only the pinned SDK's generation failures, not arbitrary corruption.</summary>
    [Theory]
    [InlineData("Archive download ETag mismatch for segment 'seg_1'.", true)]
    [InlineData("The archive server did not resume the expected segment generation.", true)]
    [InlineData("Invalid block payload.", false)]
    public void ClassifiesGenerationMismatch(string message, bool expected) =>
        Assert.Equal(expected, ReplyRecords.IsGenerationMismatch(new InvalidDataException(message)));

    /// <summary>Checks that the actual pinned SDK rejects a checkpoint bound to different request/service state.</summary>
    [Fact]
    public void SdkRejectsNonportableCheckpoint()
    {
        using AtProtoJetstream jetstream = new(options: new JetstreamOptions { ApiKey = "not-a-real-key" });
        SnapshotRequest request = new() { AfterSeq = 10, Collections = [new CollectionSelector("app.bsky.feed.post")] };
        SnapshotCheckpoint checkpoint = new() { SealedTipSeq = 20, PlanAfterSeq = 10, RequestFingerprint = "wrong-host" };
        Assert.Throws<ArgumentException>(() => jetstream.ReplayAsync(request, checkpoint));
    }

    /// <summary>Honors both Retry-After forms and server reset information without depending on the local timezone.</summary>
    [Fact]
    public void CalculatesServerCooldowns()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
        Assert.Equal(TestRecords.Start.AddMinutes(2), OutboundHandler.Cooldown(response, TestRecords.Start));
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TestRecords.Start.AddMinutes(3));
        Assert.Equal(TestRecords.Start.AddMinutes(3), OutboundHandler.Cooldown(response, TestRecords.Start));
        response.Headers.TryAddWithoutValidation("ratelimit-remaining", "0");
        response.Headers.TryAddWithoutValidation("ratelimit-reset", TestRecords.Start.AddHours(1).ToUnixTimeSeconds().ToString());
        Assert.Equal(TestRecords.Start.AddHours(1), OutboundHandler.Cooldown(response, TestRecords.Start));
    }

    /// <summary>Liveness follows actual worker deadlines and is independent of replies or dependency availability.</summary>
    [Fact]
    public void DetectsHungAndFailedWorkers()
    {
        MutableTime time = new(TestRecords.Start);
        WorkerHealth health = new(time);

        foreach (string worker in new[] { "reader", "sender", "ownership" })
        {
            health.Set(worker, "recovering", TimeSpan.FromMinutes(1), "database");
        }

        Assert.True(health.Snapshot().Healthy);
        time.Now = TestRecords.Start.AddMinutes(2);
        Assert.False(health.Snapshot().Healthy);
        health.Set("reader", "idle", TimeSpan.FromDays(1));
        health.Fail("sender", "invariant violation");
        Assert.False(health.Snapshot().Healthy);
    }

    /// <summary>Extends replay liveness only while an actual bounded SDK timer is pending.</summary>
    [Fact]
    public void TracksRealSdkWaitDeadlines()
    {
        MutableTime time = new(TestRecords.Start);
        ReplayClock clock = new(time);
        DateTimeOffset ordinary = time.Now.AddMinutes(5);
        using (ITimer timer = clock.CreateTimer(_ => { }, null, TimeSpan.FromHours(2), Timeout.InfiniteTimeSpan))
        {
            Assert.Equal(time.Now.AddHours(2).AddSeconds(60), clock.Deadline(ordinary));
            Assert.True(timer.Change(TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan));
            Assert.Equal(time.Now.AddHours(1).AddSeconds(60), clock.Deadline(ordinary));
        }

        Assert.Equal(ordinary, clock.Deadline(ordinary));
        Assert.Throws<InvalidOperationException>(() =>
            clock.CreateTimer(_ => { }, null, TimeSpan.FromDays(2), Timeout.InfiniteTimeSpan));
    }

    /// <summary>Cancels the old ownership generation and issues a fresh token after reacquisition.</summary>
    [Fact]
    public async Task CancelsStaleOwnership()
    {
        Ownership ownership = new();
        WorkerHealth health = new(TimeProvider.System);
        ownership.Activate();

        CancellationToken old = await ownership.WaitAsync("reader", health, CancellationToken.None);
        ownership.Suspend();
        Assert.True(old.IsCancellationRequested);
        ownership.Activate();

        CancellationToken current = await ownership.WaitAsync("reader", health, CancellationToken.None);
        Assert.False(current.IsCancellationRequested);
        Assert.NotEqual(old, current);
        ownership.Suspend();
    }

    /// <summary>Reports actual event lag, throttles progress, announces catch-up, and distinguishes quiet reads.</summary>
    [Fact]
    public void ReportsReplayLagAndWaiting()
    {
        MutableTime time = new(TestRecords.Start);
        CapturingLogger logger = new();
        ReplayProgress progress = new(time, logger);
        progress.Observe(11, time.Now.AddMinutes(-45));
        Assert.Contains("catching up", logger.Messages.Single());
        Assert.Contains("00:45:00", logger.Messages.Single());
        Assert.Contains("2026-10-06T23:15:00", logger.Messages.Single());
        progress.Observe(12, time.Now.AddMinutes(-44));
        Assert.Single(logger.Messages);
        time.Now = time.Now.AddSeconds(15);
        progress.Observe(13, time.Now.AddMinutes(-40));
        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains("0.1/second", logger.Messages[1]);
        progress.Observe(14, time.Now.AddSeconds(-5));
        Assert.Contains("near current time", logger.Messages[2]);
        time.Now = time.Now.AddSeconds(15);
        progress.Waiting();
        Assert.Contains("Last consumed sequence 14", logger.Messages[3]);
        Assert.Contains("does not confirm the socket is connected", logger.Messages[3]);
        progress.Waiting();
        Assert.Equal(4, logger.Messages.Count);
        progress.Observe(15, time.Now.AddMinutes(-10));
        Assert.Contains("catching up", logger.Messages[4]);

        CapturingLogger startingLogger = new();
        ReplayProgress starting = new(time, startingLogger);
        time.Now = time.Now.AddSeconds(15);
        starting.Waiting();
        Assert.Contains("Waiting for the next Jetstream event", startingLogger.Messages.Single());
    }

    /// <summary>Encrypts refresh tokens and authenticates ciphertext and account binding.</summary>
    [Fact]
    public void ProtectsCredentials()
    {
        BotOptions options = TestRecords.Options();
        string payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = options.Did,
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        })).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        AccessCredentials credentials = new(new Uri("https://bsky.social"),
            AuthenticationType.UsernamePassword, $"eyJhbGciOiJIUzI1NiJ9.{payload}.c2lnbmF0dXJl", "test-refresh-token");

        CredentialVault vault = new(options);
        byte[] encrypted = vault.Encrypt(credentials);
        Assert.DoesNotContain("test-refresh-token", System.Text.Encoding.UTF8.GetString(encrypted));
        Assert.NotNull(vault.Decrypt(encrypted));
        encrypted[28] ^= 1;
        Assert.Throws<System.Security.Cryptography.AuthenticationTagMismatchException>(() => vault.Decrypt(encrypted));
    }
}

internal sealed class MutableTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public override long GetTimestamp() => Now.UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}

internal sealed class CapturingLogger : ILogger
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
}