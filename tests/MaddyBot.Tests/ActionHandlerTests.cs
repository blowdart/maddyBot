// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using idunno.AtProto;
using idunno.AtProto.Repo;
using idunno.Bluesky;
using idunno.Bluesky.BotSdk.DataProviders;
using idunno.Bluesky.Feed;
using idunno.Bluesky.Record;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace MaddyBot.Tests;

/// <summary>Exercises built-in action recovery without publishing records or authenticating a real account.</summary>
public sealed class ActionHandlerTests
{
    /// <summary>Preserves list-item JSON through the SDK's generated public base-record contract.</summary>
    [Fact]
    public void PreservesListPayloadThroughSdkMetadata()
    {
        BotAction action = Create("list-item");
        using JsonDocument expected = JsonDocument.Parse(action.RecordJson);
        BlueskyRecord record = new()
        {
            ExtensionData = expected.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.Clone())
        };
        string serialized = JsonSerializer.Serialize(record, BotJson.BlueskyRecord);
        using JsonDocument actual = JsonDocument.Parse(serialized);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        BlueskyRecord deserialized = JsonSerializer.Deserialize(serialized, BotJson.BlueskyRecord)!;
        using JsonDocument roundTrip = JsonDocument.Parse(JsonSerializer.Serialize(deserialized, BotJson.BlueskyRecord));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, roundTrip.RootElement));
    }

    /// <summary>Reconciles a lost response for each built-in action using the original saved intent.</summary>
    /// <param name="kind">The built-in action kind.</param>
    [Theory]
    [InlineData("reply")]
    [InlineData("like")]
    [InlineData("repost")]
    [InlineData("bookmark")]
    [InlineData("list-item")]
    public async Task ReconcilesAcceptedWrites(string kind)
    {
        await using Store store = await Store.CreateAsync();
        BotAction action = Create(kind);
        await store.Database.HandleAsync(11, [action], null, CancellationToken.None);
        FakeRemote remote = new() { LoseResponse = true };
        ActionStatistics statistics = new(TestRecords.Start);
        ActionDispatcher dispatcher = new(
            store.Database,
            store.Options,
            NullLogger<ActionDispatcher>.Instance,
            statistics,
            TestRecords.Handlers());
        await Assert.ThrowsAsync<HttpRequestException>(() => dispatcher.SendAsync(action, remote, CancellationToken.None));
        BotAction uncertain = (await store.Database.NextWorkAsync(CancellationToken.None))!;
        Assert.Equal("uncertain", uncertain.Status);
        Assert.Equal(action.RecordJson, uncertain.RecordJson);
        Assert.Equal(action.RecordKey, uncertain.RecordKey);
        await dispatcher.SendAsync(uncertain, remote, CancellationToken.None);
        Assert.Equal(1, remote.Writes);
        Assert.Null(await store.Database.NextWorkAsync(CancellationToken.None));
        Assert.StartsWith("1 action completed since ", statistics.Summary);
    }

    /// <summary>Simulates all built-in actions without making even a remote read.</summary>
    /// <param name="kind">The built-in action kind.</param>
    [Theory]
    [InlineData("reply")]
    [InlineData("like")]
    [InlineData("repost")]
    [InlineData("bookmark")]
    [InlineData("list-item")]
    public async Task DeveloperModeNeverContactsRemote(string kind)
    {
        await using Store store = await Store.CreateAsync();
        store.Options.DeveloperMode = true;
        BotAction action = Create(kind);
        await store.Database.HandleAsync(11, [action], null, CancellationToken.None);
        ActionDispatcher dispatcher = new(
            store.Database,
            store.Options,
            NullLogger<ActionDispatcher>.Instance,
            new ActionStatistics(TestRecords.Start),
            TestRecords.Handlers());
        await dispatcher.SendAsync(action, null, CancellationToken.None);
        Assert.Null(await store.Database.NextWorkAsync(CancellationToken.None));
    }

    /// <summary>Retains uncertain work when reconciliation is unavailable rather than attempting another write.</summary>
    /// <param name="kind">The built-in action kind.</param>
    [Theory]
    [InlineData("reply")]
    [InlineData("like")]
    [InlineData("repost")]
    [InlineData("bookmark")]
    [InlineData("list-item")]
    public async Task FailedReconciliationDoesNotWrite(string kind)
    {
        await using Store store = await Store.CreateAsync();
        BotAction action = Create(kind);
        await store.Database.HandleAsync(11, [action], null, CancellationToken.None);
        await store.Database.MarkAttemptAsync(action, CancellationToken.None);
        ActionDispatcher dispatcher = new(
            store.Database,
            store.Options,
            NullLogger<ActionDispatcher>.Instance,
            new ActionStatistics(TestRecords.Start),
            TestRecords.Handlers());
        FakeRemote remote = new() { FailRead = true };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            dispatcher.SendAsync(action with { Status = "uncertain" }, remote, CancellationToken.None));
        Assert.Equal(0, remote.Writes);
        Assert.Equal("uncertain", (await store.Database.NextWorkAsync(CancellationToken.None))!.Status);
    }

    /// <summary>Rejects mismatched fixed repository records without overwriting them.</summary>
    /// <param name="kind">The repository action kind.</param>
    [Theory]
    [InlineData("reply")]
    [InlineData("like")]
    [InlineData("repost")]
    [InlineData("list-item")]
    public async Task MismatchedRecordDoesNotWrite(string kind)
    {
        await using Store store = await Store.CreateAsync();
        BotAction action = Create(kind);
        await store.Database.HandleAsync(11, [action], null, CancellationToken.None);
        await store.Database.MarkAttemptAsync(action, CancellationToken.None);
        string mismatched = kind switch
        {
            "reply" => JsonSerializer.Serialize(new Post("different", createdAt: TestRecords.Start), BotJson.Post),
            "list-item" => JsonSerializer.Serialize(new ListItemIntent(
                new AtUri($"at://{TestRecords.BotDid}/app.bsky.graph.list/another"),
                new Did(TestRecords.AuthorDid),
                TestRecords.Start), BotJsonContext.Default.ListItemIntent),
            _ => action.RecordJson.Replace(TestRecords.CidValue,
                "bafyreie5cvkm3zwfbhzbfw3f6bcnne64devyeuumdggodx6zfkwkt4vsge", StringComparison.Ordinal)
        };
        FakeRemote remote = new() { Record = mismatched };
        ActionDispatcher dispatcher = new(
            store.Database,
            store.Options,
            NullLogger<ActionDispatcher>.Instance,
            new ActionStatistics(TestRecords.Start),
            TestRecords.Handlers());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            dispatcher.SendAsync(action with { Status = "uncertain" }, remote, CancellationToken.None));
        Assert.Equal(0, remote.Writes);
    }

    /// <summary>Does not enqueue an unknown kind or an unsupported saved payload version.</summary>
    [Fact]
    public void RequiresRegisteredKindAndVersion()
    {
        ActionHandlerRegistry registry = TestRecords.Handlers();
        Assert.Throws<InvalidOperationException>(() => registry.Validate(Create("like") with { ActionKind = "unknown" }));
        Assert.Throws<InvalidOperationException>(() => registry.Validate(Create("like") with { PayloadVersion = 2 }));
    }

    /// <summary>Uses the same author/list identity across different triggering posts, but distinct identities for different lists.</summary>
    [Fact]
    public void ListMembershipKeyIsStable()
    {
        StrongReference first = Source();
        StrongReference second = new(
            new AtUri($"at://{TestRecords.AuthorDid}/app.bsky.feed.post/3mjfhkjsshs3q"),
            first.Cid);
        AtUri list = new($"at://{TestRecords.BotDid}/app.bsky.graph.list/example");
        BotAction a = BotActions.AddAuthorToList(first, 11, list, TestRecords.Start);
        BotAction b = BotActions.AddAuthorToList(second, 12, list, TestRecords.Start.AddMinutes(1));
        Assert.Equal(a.RecordKey, b.RecordKey);
        Assert.Equal(a.ActionKey, b.ActionKey);
        Assert.NotEqual(a.RecordKey, BotActions.AddAuthorToList(first, 11,
            new AtUri($"at://{TestRecords.BotDid}/app.bsky.graph.list/another")).RecordKey);
        using JsonDocument json = JsonDocument.Parse(a.RecordJson);
        Assert.Equal("app.bsky.graph.listitem", json.RootElement.GetProperty("$type").GetString());
        Assert.Equal(TestRecords.AuthorDid, json.RootElement.GetProperty("subject").GetString());
    }

    /// <summary>Accepts existing manually created bookmarks and author memberships without another write.</summary>
    /// <param name="kind">The service or membership action kind.</param>
    [Theory]
    [InlineData("bookmark")]
    [InlineData("list-item")]
    public async Task ExistingEffectDoesNotWrite(string kind)
    {
        await using Store store = await Store.CreateAsync();
        BotAction action = Create(kind);
        await store.Database.HandleAsync(11, [action], null, CancellationToken.None);
        FakeRemote remote = new() { BookmarkExists = true, ListMember = "at://example/existing/membership" };
        ActionDispatcher dispatcher = new(
            store.Database,
            store.Options,
            NullLogger<ActionDispatcher>.Instance,
            new ActionStatistics(TestRecords.Start),
            TestRecords.Handlers());
        await dispatcher.SendAsync(action, remote, CancellationToken.None);
        Assert.Equal(0, remote.Writes);
        Assert.Null(await store.Database.NextWorkAsync(CancellationToken.None));
    }

    private static StrongReference Source() => new(
        new AtUri(TestRecords.Work().SourceUri),
        new Cid(TestRecords.CidValue));

    private static BotAction Create(string kind) => kind switch
    {
        "reply" => TestRecords.Work(),
        "like" => BotActions.Like(Source(), 11, TestRecords.Start),
        "repost" => BotActions.Repost(Source(), 11, TestRecords.Start),
        "bookmark" => BotActions.Bookmark(Source(), 11),
        "list-item" => BotActions.AddAuthorToList(
            Source(),
            11,
            new AtUri($"at://{TestRecords.BotDid}/app.bsky.graph.list/example"),
            TestRecords.Start),
        _ => throw new ArgumentException("Unknown test kind.", nameof(kind))
    };

    private sealed class FakeRemote : IBotActionRemote
    {
        public BlueskyAgent Agent => throw new NotSupportedException("This test does not use a real SDK client.");

        public string BotDid => TestRecords.BotDid;
        public string? Record { get; set; }
        public bool BookmarkExists { get; set; }
        public string? ListMember { get; set; }
        public bool LoseResponse { get; set; }
        public bool FailRead { get; set; }
        public int Writes { get; private set; }

        public Task<string?> FindRecordAsync(BotAction action, CancellationToken cancellationToken)
        {
            Read();

            return Task.FromResult(Record);
        }

        public Task<BotActionResult> CreateRecordAsync(BotAction action, CancellationToken cancellationToken)
        {
            Record = action.RecordJson;
            Wrote();

            return Task.FromResult(new BotActionResult(true, ActionRecords.Uri(action, this), "created"));
        }

        public Task<PostView> TargetAsync(AtUri uri, CancellationToken cancellationToken)
        {
            Read();

            return Task.FromResult(TestRecords.View(uri));
        }

        public Task<bool> HasBookmarkAsync(AtUri uri, CancellationToken cancellationToken)
        {
            Read();

            return Task.FromResult(BookmarkExists);
        }

        public Task CreateBookmarkAsync(StrongReference subject, CancellationToken cancellationToken)
        {
            BookmarkExists = true;
            Wrote();

            return Task.CompletedTask;
        }

        public Task<string?> FindListMemberAsync(AtUri listUri, Did author, CancellationToken cancellationToken)
        {
            Read();

            return Task.FromResult(ListMember);
        }

        private void Read()
        {
            if (FailRead)
            {
                throw new HttpRequestException("Reconciliation unavailable.");
            }
        }

        private void Wrote()
        {
            Writes++;
            if (LoseResponse)
            {
                LoseResponse = false;
                throw new HttpRequestException("Accepted, response lost.");
            }
        }
    }

    private sealed class Store : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"bot-actions-{Guid.NewGuid():N}.db");
        public BotOptions Options { get; } = TestRecords.Options();
        public SqliteDatabaseProvider Database { get; }

        private Store()
        {
            Options.DeveloperMode = false;
            Database = new SqliteDatabaseProvider(
                $"Data Source={_path}",
                Options,
                TimeProvider.System,
                NullLogger<SqliteDatabaseProvider>.Instance);
        }

        public static async Task<Store> CreateAsync()
        {
            Store store = new();
            await store.Database.InitializeAsync(CancellationToken.None);
            await store.Database.AcquireOrRenewAsync(CancellationToken.None);
            await using SqliteConnection connection = new($"Data Source={store._path};Pooling=False");
            await connection.OpenAsync();
            await using SqliteCommand ready = new("UPDATE bot_ownership SET ready_at = 0", connection);
            await ready.ExecuteNonQueryAsync();
            await store.Database.LoadStateAsync(CancellationToken.None);
            await store.Database.SaveRequestAsync(new() { AfterSeq = 10 }, CancellationToken.None);

            return store;
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(_path);
            File.Delete(_path + "-wal");
            File.Delete(_path + "-shm");

            return ValueTask.CompletedTask;
        }
    }
}