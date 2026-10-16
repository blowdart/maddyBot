// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;

using idunno.AtProto;
using idunno.AtProto.Jetstream;
using idunno.AtProto.Repo;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace MaddyBot.Tests;

/// <summary>Checks the sample's matching and complete reply construction against the packaged SDK.</summary>
public sealed class PolicyTests
{
    /// <summary>Binds existing sample settings and application-owned exclusions and CA material alongside SDK options.</summary>
    [Fact]
    public void BindsSampleConfiguration()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "bot-appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bot:Did"] = TestRecords.BotDid,
                ["Bot:ArchiveApiKey"] = "not-a-real-key",
                ["Bot:NeverReplyToDids:0"] = TestRecords.AuthorDid,
                ["Bot:DatabaseCaCertificate"] = "test-only-pem"
            }).Build();
        BotOptions options = configuration.GetRequiredSection("Bot").Get<BotOptions>()!;
        options.Validate();
        Assert.True(options.DeveloperMode);
        Assert.Contains(".NET Aspire", options.WatchPhrases);
        Assert.All(configuration.GetSection("Bot:Replies").GetChildren(),
            entry => Assert.Contains(entry.Value!, options.Replies));
        Assert.Equal(8192, options.ReplayLiveBufferCapacity);
        Assert.True(options.Excludes(TestRecords.AuthorDid));
        Assert.True(options.Excludes(TestRecords.BotDid));
        Assert.Equal("test-only-pem", options.DatabaseCaCertificate);
    }

    /// <summary>Matches accepted aliases and separators without crossing sentence punctuation or line breaks.</summary>
    /// <param name="text">The source post text.</param>
    /// <param name="matches">Whether the sample should reply.</param>
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

    /// <summary>Normalizes configured phrases without modifying the saved reply.</summary>
    [Fact]
    public void MatchesConfiguredPhrases()
    {
        BotOptions options = TestRecords.Options();
        options.WatchPhrases = ["first\t_phrase", "second-phrase"];
        Assert.NotNull(ReplyPolicy.Create(TestRecords.Event("FIRST     PHRASE"), TestRecords.Start, options));
        Assert.NotNull(ReplyPolicy.Create(TestRecords.Event("a second   phrase here"), TestRecords.Start, options));
        Assert.Null(ReplyPolicy.Create(TestRecords.Event("first. phrase"), TestRecords.Start, options));
        Assert.Null(ReplyPolicy.Create(TestRecords.Event("second! phrase"), TestRecords.Start, options));
        Assert.Null(ReplyPolicy.Create(TestRecords.Event(), TestRecords.Start, options));
        options.WatchPhrases = ["dotnet Aspire"];
        Assert.NotNull(ReplyPolicy.Create(TestRecords.Event(".NET_Aspire"), TestRecords.Start, options));
        Assert.NotNull(ReplyPolicy.Create(TestRecords.Event("DotNet-Aspire"), TestRecords.Start, options));
        Assert.Null(ReplyPolicy.Create(TestRecords.Event(".NET! Aspire"), TestRecords.Start, options));
    }

    /// <summary>Returns exactly one complete reply with the source URI, CID, tags, and a valid fixed key.</summary>
    [Fact]
    public void MatchingPostCreatesReplyOnly()
    {
        BotAction reply = Assert.Single(new MaddyReplyPolicy(TestRecords.Options(), NullLogger<MaddyReplyPolicy>.Instance).Evaluate(
            TestRecords.Event(),
            TestRecords.Start));
        Assert.Equal("reply", reply.ActionKind);
        Assert.True(TimestampIdentifier.TryParse(reply.RecordKey, out _));
        Assert.NotEqual(reply.RecordKey, TestRecords.Work().RecordKey);
        idunno.Bluesky.Post post = TestRecords.Post(reply);
        Assert.Equal("It's just \"Aspire\" \U0001f92c", post.Text);
        Assert.Equal(["bot", "\U0001f916"], post.Tags);
        Assert.Equal(reply.SourceUri, post.Reply!.Parent.Uri.ToString());
        Assert.Equal(TestRecords.CidValue, post.Reply.Parent.Cid.ToString());
        Assert.Equal(post.Reply.Parent, post.Reply.Root);
    }

    /// <summary>Preserves the existing thread root when replying to a reply.</summary>
    [Fact]
    public void ConstructsCorrectThreadReferences()
    {
        StrongReference root = new(
            new AtUri($"at://{TestRecords.AuthorDid}/app.bsky.feed.post/root"),
            new Cid(TestRecords.CidValue));
        BotAction work = ReplyPolicy.Create(
            TestRecords.Event(reply: new(root, root)),
            TestRecords.Start,
            TestRecords.Options())!;
        Assert.Equal(root, TestRecords.Post(work).Reply!.Root);
        Assert.Equal(work.SourceUri, TestRecords.Post(work).Reply!.Parent.Uri.ToString());
    }

    /// <summary>Ignores unrelated text, updates, own posts, excluded authors, backfills, and pre-start events.</summary>
    [Fact]
    public void FiltersIneligibleEvents()
    {
        BotOptions options = TestRecords.Options();
        JetstreamCommitEvent item = TestRecords.Event();
        Assert.Empty(new MaddyReplyPolicy(options, NullLogger<MaddyReplyPolicy>.Instance).Evaluate(TestRecords.Event("unrelated"), TestRecords.Start));
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

    /// <summary>Reports posts that violate SDK constructor constraints as malformed events for audited skipping.</summary>
    /// <param name="length">The number of ASCII characters in the invalid post.</param>
    [Theory]
    [InlineData(301)]
    [InlineData(3001)]
    public void RejectsPostTextBeyondLexiconLimitAsMalformed(int length)
    {
        JetstreamCommitEvent item = TestRecords.Event();
        JsonObject record = JsonNode.Parse(item.Commit.Record!.Value.GetRawText())!.AsObject();
        record["text"] = new string('x', length);
        item = item with
        {
            Commit = item.Commit with
            {
                Record = JsonSerializer.SerializeToElement(record)
            }
        };

        MalformedEventException exception = Assert.Throws<MalformedEventException>(() =>
            ReplyPolicy.Create(item, TestRecords.Start, TestRecords.Options()));

        Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
    }

    /// <summary>Reports missing, null, and invalid source records through the SDK's explicit skip contract.</summary>
    /// <param name="json">The source record JSON, or no record.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("""{"text":42,"createdAt":"2026-10-07T00:00:00Z"}""")]
    [InlineData("""{"text":null,"createdAt":"2026-10-07T00:00:00Z"}""")]
    public void RejectsInvalidPostJsonAsMalformed(string? json)
    {
        using JsonDocument? record = json is null ? null : JsonDocument.Parse(json);
        JetstreamCommitEvent item = TestRecords.Event();
        item = item with
        {
            Commit = item.Commit with
            {
                Record = record?.RootElement.Clone()
            }
        };

        MalformedEventException exception = Assert.Throws<MalformedEventException>(() =>
            new MaddyReplyPolicy(TestRecords.Options(), NullLogger<MaddyReplyPolicy>.Instance).Evaluate(item, TestRecords.Start));

        Assert.IsType<JsonException>(exception.InnerException);
    }

    /// <summary>Reports source constructor argument validation failures through the explicit skip contract.</summary>
    [Fact]
    public void RejectsNullPostTagAsMalformed()
    {
        JetstreamCommitEvent item = TestRecords.Event();
        JsonObject record = JsonNode.Parse(item.Commit.Record!.Value.GetRawText())!.AsObject();
        record["tags"] = new JsonArray((JsonNode?)null);

        item = item with
        {
            Commit = item.Commit with
            {
                Record = JsonSerializer.SerializeToElement(record)
            }
        };

        PolicyLogger logger = new();
        MalformedEventException exception = Assert.Throws<MalformedEventException>(() =>
            new MaddyReplyPolicy(TestRecords.Options(), logger).Evaluate(item, TestRecords.Start));

        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Equal(LogLevel.Warning, logger.Level);
        Assert.Equal(1, logger.EventId.Id);
        Assert.Equal("InvalidSourcePost", logger.EventId.Name);
        Assert.Same(exception, logger.Exception);
        Assert.Contains(
            new KeyValuePair<string, object?>("SourceUri", $"at://{TestRecords.AuthorDid}/app.bsky.feed.post/{item.Commit.RKey}"),
            logger.State);
    }

    /// <summary>Captures the structured malformed-source warning without writing logs externally.</summary>
    private sealed class PolicyLogger : ILogger<MaddyReplyPolicy>
    {
        public LogLevel Level { get; private set; }
        public EventId EventId { get; private set; }
        public Exception? Exception { get; private set; }
        public IReadOnlyList<KeyValuePair<string, object?>> State { get; private set; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            EventId = eventId;
            Exception = exception;
            State = Assert.IsType<IReadOnlyList<KeyValuePair<string, object?>>>(state, exactMatch: false);
        }
    }

    /// <summary>Captures a configured choice in the complete saved JSON, rather than choosing again on send.</summary>
    [Fact]
    public void CapturesConfiguredReply()
    {
        BotOptions options = TestRecords.Options();
        options.Replies = ["one", "two", "three"];
        for (int index = 0; index < 100; index++)
        {
            BotAction work = TestRecords.Work(options);
            Assert.Contains(TestRecords.Post(work).Text, options.Replies);
            using JsonDocument json = JsonDocument.Parse(work.RecordJson);
            Assert.Equal(TestRecords.Post(work).Text, json.RootElement.GetProperty("text").GetString());
        }
    }

    /// <summary>Validates sample settings as well as packaged runtime requirements.</summary>
    [Fact]
    public void ValidatesSettings()
    {
        BotOptions options = TestRecords.Options();
        Assert.True(options.DeveloperMode);
        options.Validate();
        options.WatchPhrases = [];
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.WatchPhrases = [" "];
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.WatchPhrases = [".NET Aspire"];
        options.Replies = [];
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.Replies = ["valid"];
        options.NeverReplyToDids = ["invalid"];
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.NeverReplyToDids = [];
        options.DeveloperMode = false;
        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>Uses the packaged lookback parser for explicit whole-number durations.</summary>
    /// <param name="value">The command-line duration.</param>
    /// <param name="seconds">The expected number of seconds.</param>
    [Theory]
    [InlineData("0s", 0)]
    [InlineData("30m", 1800)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    public void ParsesLookback(string value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), BotHosting.ParseLookback(value));

    /// <summary>Rejects ambiguous or unsafe lookback durations.</summary>
    /// <param name="value">The invalid command-line duration.</param>
    [Theory]
    [InlineData("")]
    [InlineData("-1m")]
    [InlineData("1.5h")]
    [InlineData("30")]
    [InlineData("1w")]
    [InlineData("9223372036854775807d")]
    public void RejectsInvalidLookback(string value) =>
        Assert.Throws<FormatException>(() => BotHosting.ParseLookback(value));
}