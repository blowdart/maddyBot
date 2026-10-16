// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using idunno.AtProto;
using idunno.AtProto.Jetstream;
using idunno.AtProto.Repo;

using Microsoft.Extensions.Configuration;

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
        BotAction reply = Assert.Single(new MaddyReplyPolicy(TestRecords.Options()).Evaluate(
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
        Assert.Empty(new MaddyReplyPolicy(options).Evaluate(TestRecords.Event("unrelated"), TestRecords.Start));
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