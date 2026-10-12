// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;

using idunno.AtProto;
using idunno.AtProto.Jetstream;
using idunno.AtProto.Jetstream.Archive;
using idunno.AtProto.Repo;
using idunno.Bluesky;
using idunno.Bluesky.BotSdk.DataProviders;
using idunno.Bluesky.Feed;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;

namespace MaddyBot.Tests;

/// <summary>Marks PostgreSQL tests as explicitly skipped when no development connection was supplied.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute and checks whether the database connection was configured.</summary>
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MADDYBOT_TEST_CONNECTION")))
        {
            Skip = "Set MADDYBOT_TEST_CONNECTION to run PostgreSQL integration tests.";
        }
    }
}

/// <summary>Tests durable behavior against real PostgreSQL using an isolated, disposable schema per test.</summary>
public sealed class PostgresTests : IAsyncLifetime
{
    private readonly string _schema = $"test_{Guid.NewGuid():N}";
    private NpgsqlDataSource _source = null!;
    private PostgreSqlDatabaseProvider _database = null!;
    private readonly BotOptions _options = TestRecords.Options();
    private readonly ActionStatistics _statistics = new(TestRecords.Start);

    /// <summary>Creates an isolated schema and acquires a test lease without the production deployment delay.</summary>
    public async Task InitializeAsync()
    {
        NpgsqlConnectionStringBuilder builder = new(Environment.GetEnvironmentVariable("MADDYBOT_TEST_CONNECTION"))
        {
            Pooling = false,
            CommandTimeout = 15
        };

        await using (NpgsqlConnection connection = new(builder.ConnectionString))
        {
            await connection.OpenAsync();

            await using NpgsqlCommand create = new($"CREATE SCHEMA \"{_schema}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        builder.SearchPath = _schema;
        _source = NpgsqlDataSource.Create(builder.ConnectionString);
        _database = NewDatabase(_options);
        await _database.InitializeAsync(CancellationToken.None);
        Assert.NotNull(await _database.AcquireOrRenewAsync(CancellationToken.None));
        await SqlAsync("UPDATE bot_ownership SET ready_at = now()");
        await _database.LoadStateAsync(CancellationToken.None);
        await _database.SaveRequestAsync(new SnapshotRequest
        {
            AfterSeq = 10,
            Collections = [new CollectionSelector("app.bsky.feed.post")]
        }, CancellationToken.None);
    }

    /// <summary>Drops only this test's explicitly generated schema and releases its data source.</summary>
    public async Task DisposeAsync()
    {
        if (_source is not null)
        {
            await using (NpgsqlConnection connection = await _source.OpenConnectionAsync())
            {
                await using NpgsqlCommand drop = new($"DROP SCHEMA \"{_schema}\" CASCADE", connection);
                await drop.ExecuteNonQueryAsync();
            }

            await _source.DisposeAsync();
        }
    }

    /// <summary>Commits enqueue/progress together and suppresses repeated events even after safe terminal pruning.</summary>
    [PostgresFact]
    public async Task DeduplicatesAfterRestartAndPruning()
    {
        BotAction work = TestRecords.Work();
        Assert.True(await _database.HandleAsync(11, [work], null, CancellationToken.None) > 0);
        Assert.Equal(0, await _database.HandleAsync(11, [TestRecords.Work()], null, CancellationToken.None));
        Assert.Equal(11, (await _database.LoadStateAsync(CancellationToken.None)).ProcessedThrough);

        BotAction durable = (await _database.NextWorkAsync(CancellationToken.None))!;
        Assert.Equal(work.RecordKey, durable.RecordKey);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(work.RecordJson),
            JsonSerializer.Deserialize<JsonElement>(durable.RecordJson)));
        await _database.FinishAsync(work, "sent", "at://test", "test", CancellationToken.None);
        await SqlAsync("UPDATE reply_work SET completed_at = now() - interval '8 days'");
        await _database.PruneAsync(CancellationToken.None);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM reply_work"));
        Assert.Equal(0, await _database.HandleAsync(11, [TestRecords.Work()], null, CancellationToken.None));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM reply_work"));
    }

    /// <summary>Rolls back the outbox and boundary when enqueue fails, and never persists a speculative checkpoint.</summary>
    [PostgresFact]
    public async Task RollsBackFailedHandling()
    {
        BotAction work = TestRecords.Work() with { RecordJson = "not-json" };
        await Assert.ThrowsAsync<PostgresException>(() => _database.HandleAsync(11, [work], null, CancellationToken.None));

        ReplayState state = await _database.LoadStateAsync(CancellationToken.None);
        Assert.Equal(10, state.ProcessedThrough);
        Assert.Null(state.CheckpointJson);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM reply_work"));
    }

    /// <summary>Round-trips every archive, fallback, live, and fingerprint checkpoint field without reconstructing cursors.</summary>
    [PostgresFact]
    public async Task PreservesCompleteCheckpoint()
    {
        SnapshotCheckpoint checkpoint = new()
        {
            RequestFingerprint = "original-fingerprint",
            SealedTipSeq = 1234,
            PlanAfterSeq = 100,
            ReplayAfterSeq = 40,
            LiveAfterSeq = 1500,
            SegmentName = "seg_001.jss",
            SegmentChecksum = "abcdef0123456789",
            NextBlockIndex = 7,
            NextByteOffset = 123456
        };

        await _database.HandleAsync(1500, [], null, CancellationToken.None);
        _database.SaveCheckpoint(checkpoint, CancellationToken.None);

        ReplayState saved = await _database.LoadStateAsync(CancellationToken.None);
        Assert.Equal(checkpoint, JsonSerializer.Deserialize<SnapshotCheckpoint>(saved.CheckpointJson!));
        Assert.Equal(10, JsonSerializer.Deserialize<SnapshotRequest>(saved.RequestJson!)!.AfterSeq);
    }

    /// <summary>Rejects speculative live progress and keeps the last saved checkpoint unchanged.</summary>
    [PostgresFact]
    public async Task RejectsCheckpointAheadOfHandling()
    {
        SnapshotCheckpoint checkpoint = new() { SealedTipSeq = 10, PlanAfterSeq = 10, LiveAfterSeq = 11 };
        Assert.Throws<InvalidOperationException>(() => _database.SaveCheckpoint(checkpoint, CancellationToken.None));
        Assert.Null((await _database.LoadStateAsync(CancellationToken.None)).CheckpointJson);
    }

    /// <summary>Commits ignored-event progress and its full checkpoint together, without speculative live progress.</summary>
    [PostgresFact]
    public async Task CommitsCoalescedProgressWithCheckpoint()
    {
        BotAction work = TestRecords.Work();
        await _database.HandleAsync(11, [work], null, CancellationToken.None);

        SnapshotCheckpoint checkpoint = new() { SealedTipSeq = 10, PlanAfterSeq = 10, LiveAfterSeq = 5000 };
        _database.SaveCheckpoint(checkpoint, CancellationToken.None, 5000);

        ReplayState state = await _database.LoadStateAsync(CancellationToken.None);
        Assert.Equal(5000, state.ProcessedThrough);
        Assert.Equal(checkpoint, JsonSerializer.Deserialize<SnapshotCheckpoint>(state.CheckpointJson!));
        Assert.NotNull(await _database.NextWorkAsync(CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() =>
            _database.SaveCheckpoint(checkpoint with { LiveAfterSeq = 6000 }, CancellationToken.None, 5500));
        Assert.Equal(5000, (await _database.LoadStateAsync(CancellationToken.None)).ProcessedThrough);
        Assert.Equal(checkpoint, JsonSerializer.Deserialize<SnapshotCheckpoint>(
            (await _database.LoadStateAsync(CancellationToken.None)).CheckpointJson!));
    }

    /// <summary>Overrides forward only, preserving filters, existing queued replies, and an audit of skipped history.</summary>
    [PostgresFact]
    public async Task OverridesLookbackWithoutRewinding()
    {
        BotAction work = TestRecords.Work();
        await _database.HandleAsync(11, [work], null, CancellationToken.None);
        await _database.MarkAttemptAsync(work, CancellationToken.None);

        SnapshotCheckpoint checkpoint = new() { SealedTipSeq = 10, PlanAfterSeq = 10, LiveAfterSeq = 11 };
        _database.SaveCheckpoint(checkpoint, CancellationToken.None);

        ReplayState before = await _database.LoadStateAsync(CancellationToken.None);
        DateTimeOffset cutoff = before.StartedAt.AddMinutes(1);
        SnapshotRequest replacement = new() { AfterSeq = 100 };
        Assert.Equal(100, await _database.OverrideLookbackAsync(cutoff, replacement, CancellationToken.None));

        ReplayState after = await _database.LoadStateAsync(CancellationToken.None);
        Assert.Equal(cutoff, after.StartedAt);
        Assert.Null(after.CheckpointJson);
        Assert.Equal(100, after.ProcessedThrough);

        SnapshotRequest saved = JsonSerializer.Deserialize<SnapshotRequest>(after.RequestJson!)!;
        Assert.Equal("app.bsky.feed.post", saved.Collections!.Single().ToString());
        Assert.Equal("uncertain", (await _database.NextWorkAsync(CancellationToken.None))!.Status);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM skipped_data WHERE scope = 'lookback override'"));
        Assert.Equal(100, await _database.OverrideLookbackAsync(cutoff.AddMinutes(1),
            replacement with { AfterSeq = 50 }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _database.OverrideLookbackAsync(cutoff, replacement, CancellationToken.None));
        Assert.Equal(100, (await _database.LoadStateAsync(CancellationToken.None)).ProcessedThrough);
    }

    /// <summary>Refuses to reinterpret simulated progress as production progress or move checkpoints to another host.</summary>
    [PostgresFact]
    public async Task BindsDurableStateToConfiguration()
    {
        _options.DeveloperMode = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _database.LoadStateAsync(CancellationToken.None));
        _options.DeveloperMode = true;
        _options.JetstreamService = "wss://different.example";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _database.LoadStateAsync(CancellationToken.None));
    }

    /// <summary>Prevents a second owner and rejects every stale-owner operation after a database lease takeover.</summary>
    [PostgresFact]
    public async Task FencesDeploymentOverlap()
    {
        PostgreSqlDatabaseProvider other = NewDatabase(_options);
        Assert.Null(await other.AcquireOrRenewAsync(CancellationToken.None));
        await SqlAsync("UPDATE bot_ownership SET lease_until = now() - interval '1 second'");
        Assert.NotNull(await other.AcquireOrRenewAsync(CancellationToken.None));
        await SqlAsync("UPDATE bot_ownership SET ready_at = now()");
        await Assert.ThrowsAsync<OwnershipLostException>(() => _database.HandleAsync(11, [TestRecords.Work()], null, CancellationToken.None));
        await Assert.ThrowsAsync<OwnershipLostException>(() => _database.ReserveRequestAsync(3, false, CancellationToken.None));
        Assert.True(await other.HandleAsync(11, [TestRecords.Work()], null, CancellationToken.None) > 0);
    }

    /// <summary>Retains pending and uncertain work and completed work beyond the safe processed-through boundary.</summary>
    [PostgresFact]
    public async Task PrunesOnlySafeTerminalWork()
    {
        BotAction work = TestRecords.Work();
        await _database.HandleAsync(11, [work], null, CancellationToken.None);
        await _database.MarkAttemptAsync(work, CancellationToken.None);
        await SqlAsync("UPDATE reply_work SET completed_at = now() - interval '8 days'");
        await _database.PruneAsync(CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM reply_work"));
        await _database.FinishAsync(work, "sent", null, "test", CancellationToken.None);
        await SqlAsync("UPDATE reply_work SET source_sequence = 999, completed_at = now() - interval '8 days'");
        await _database.PruneAsync(CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM reply_work"));
    }

    /// <summary>Uses durable point accounting, cooldowns, and spacing rather than successful-write counts.</summary>
    [PostgresFact]
    public async Task PersistsBudgetsAndCooldowns()
    {
        _options.HourlyWritePoints = 3;
        Assert.Null(await _database.ReserveRequestAsync(3, false, CancellationToken.None));
        await SqlAsync("UPDATE rate_state SET next_request = '-infinity'");

        DateTimeOffset? limited = await _database.ReserveRequestAsync(3, false, CancellationToken.None);
        Assert.True(limited > DateTimeOffset.UtcNow.AddMinutes(55));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM rate_accounting"));

        DateTimeOffset cooldown = DateTimeOffset.UtcNow.AddHours(2);
        await _database.CooldownAsync(cooldown, CancellationToken.None);

        DateTimeOffset? readWait = await _database.ReserveRequestAsync(0, false, CancellationToken.None);
        Assert.True(readWait >= cooldown.AddMilliseconds(-1));
    }

    /// <summary>Durably stores session-creation limits and monotonic archive cooldowns.</summary>
    [PostgresFact]
    public async Task PersistsSessionAndArchiveLimits()
    {
        _options.SessionsPerFiveMinutes = 1;
        Assert.Null(await _database.ReserveRequestAsync(0, true, CancellationToken.None));
        await SqlAsync("UPDATE rate_state SET next_request = '-infinity'");
        Assert.True(await _database.ReserveRequestAsync(0, true, CancellationToken.None) >
            DateTimeOffset.UtcNow.AddMinutes(4));
        Assert.Null(await _database.ReserveRequestAsync(0, false, CancellationToken.None));

        DateTimeOffset until = DateTimeOffset.UtcNow.AddMinutes(10);
        await _database.SaveArchiveCooldownAsync(until, CancellationToken.None);
        Assert.True(await _database.ArchiveCooldownAsync(CancellationToken.None) >= until.AddMilliseconds(-1));
        await _database.SaveArchiveCooldownAsync(until.AddMinutes(-5), CancellationToken.None);
        Assert.True(await _database.ArchiveCooldownAsync(CancellationToken.None) >= until.AddMilliseconds(-1));
    }

    /// <summary>Records a dry-run outcome without making any remote call, including reads.</summary>
    [PostgresFact]
    public async Task DeveloperModeNeverPosts()
    {
        BotAction work = TestRecords.Work();
        await _database.HandleAsync(11, [work], null, CancellationToken.None);

        ActionDispatcher dispatcher = new(_database, _options, NullLogger<ActionDispatcher>.Instance, _statistics, TestRecords.Handlers());
        await dispatcher.SendAsync(work, null, CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM reply_work WHERE status = 'simulated'"));
        Assert.Null(await _database.NextWorkAsync(CancellationToken.None));
        Assert.StartsWith("1 action completed since ", _statistics.Summary);
        await dispatcher.SendAsync(work, null, CancellationToken.None);
        Assert.StartsWith("1 action completed since ", _statistics.Summary);
    }

    /// <summary>Checks newly configured exclusions against work queued before the setting changed.</summary>
    [PostgresFact]
    public async Task ExcludesQueuedWork()
    {
        BotAction work = TestRecords.Work();
        await _database.HandleAsync(11, [work], null, CancellationToken.None);
        _options.NeverReplyToDids = [work.SourceDid];

        ActionDispatcher dispatcher = new(_database, _options, NullLogger<ActionDispatcher>.Instance, _statistics, TestRecords.Handlers());
        await dispatcher.SendAsync(work, null, CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM reply_work WHERE status = 'not-sent'"));
        Assert.StartsWith("0 actions completed since ", _statistics.Summary);
    }

    /// <summary>Reconciles a server-accepted write after a timeout using the same key, content, and createdAt.</summary>
    [PostgresFact]
    public async Task ReconcilesUnknownWriteOutcome()
    {
        _options.DeveloperMode = false;

        BotAction work = TestRecords.Work();
        await _database.HandleAsync(11, [work], null, CancellationToken.None);

        FakeRemote remote = new(_options) { LoseFirstResponse = true };
        ActionDispatcher dispatcher = new(_database, _options, NullLogger<ActionDispatcher>.Instance, _statistics, TestRecords.Handlers());
        await Assert.ThrowsAsync<HttpRequestException>(() => dispatcher.SendAsync(work, remote, CancellationToken.None));
        Assert.StartsWith("0 actions completed since ", _statistics.Summary);

        BotAction uncertain = (await _database.NextWorkAsync(CancellationToken.None))!;
        Assert.Equal("uncertain", uncertain.Status);
        await dispatcher.SendAsync(uncertain, remote, CancellationToken.None);
        Assert.Equal(1, remote.Writes);
        Assert.Equal(work.RecordKey, remote.Key);
        Assert.True(ReplyRecords.SameRecord(TestRecords.Post(work), remote.Saved!));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM reply_work WHERE status = 'sent'"));
        Assert.StartsWith("1 action completed since ", _statistics.Summary);
    }

    /// <summary>Retries a confirmed missing uncertain write with the original key, timestamp, and complete content.</summary>
    [PostgresFact]
    public async Task RetriesMissingUncertainRecordWithSameKey()
    {
        _options.DeveloperMode = false;

        BotAction work = TestRecords.Work();
        await _database.HandleAsync(11, [work], null, CancellationToken.None);
        await _database.MarkAttemptAsync(work, CancellationToken.None);

        FakeRemote remote = new(_options);
        ActionDispatcher dispatcher = new(_database, _options, NullLogger<ActionDispatcher>.Instance, _statistics, TestRecords.Handlers());
        await dispatcher.SendAsync(work with { Status = "uncertain" }, remote, CancellationToken.None);
        Assert.Equal(1, remote.Writes);
        Assert.Equal(work.RecordKey, remote.Key);
        Assert.True(ReplyRecords.SameRecord(TestRecords.Post(work), remote.Saved!));
        Assert.StartsWith("1 action completed since ", _statistics.Summary);
    }

    /// <summary>Reconciles uncertain sends before applying a later source deletion.</summary>
    [PostgresFact]
    public async Task ReconcilesBeforeApplyingDeletion()
    {
        _options.DeveloperMode = false;

        BotAction work = TestRecords.Work();
        await _database.HandleAsync(11, [work], null, CancellationToken.None);
        await _database.MarkAttemptAsync(work, CancellationToken.None);
        await _database.HandleAsync(12, [], null, CancellationToken.None, work.SourceUri);

        BotAction uncertain = (await _database.NextWorkAsync(CancellationToken.None))!;
        Assert.True(uncertain.TargetDeleted);

        FakeRemote remote = new(_options) { Saved = TestRecords.Post(work), Key = work.RecordKey };
        ActionDispatcher dispatcher = new(_database, _options, NullLogger<ActionDispatcher>.Instance, _statistics, TestRecords.Handlers());
        await dispatcher.SendAsync(uncertain, remote, CancellationToken.None);
        Assert.Equal(0, remote.Writes);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM reply_work WHERE status = 'sent'"));
    }

    /// <summary>Observes root deletions and does not admit another write for pending replies in that thread.</summary>
    [PostgresFact]
    public async Task DoesNotReplyToDeletedRoot()
    {
        _options.DeveloperMode = false;

        StrongReference root = new(new AtUri($"at://{TestRecords.AuthorDid}/app.bsky.feed.post/root"),
            new Cid(TestRecords.CidValue));

        BotAction work = ReplyPolicy.Create(TestRecords.Event(reply: new ReplyReferences(root, root)),
            TestRecords.Start, _options)!;
        await _database.HandleAsync(11, [work], null, CancellationToken.None);
        await _database.HandleAsync(12, [], null, CancellationToken.None, root.Uri.ToString());

        BotAction deleted = (await _database.NextWorkAsync(CancellationToken.None))!;
        Assert.True(deleted.TargetDeleted);
        Assert.Equal(0, await _database.MarkAttemptAsync(deleted, CancellationToken.None));

        FakeRemote remote = new(_options);
        ActionDispatcher dispatcher = new(_database, _options, NullLogger<ActionDispatcher>.Instance, _statistics, TestRecords.Handlers());
        await dispatcher.SendAsync(deleted, remote, CancellationToken.None);
        Assert.Equal(0, remote.Writes);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM reply_work WHERE status = 'not-sent'"));
    }

    /// <summary>Records confirmed restrictions as terminal outcomes without writing or bypassing them.</summary>
    [PostgresFact]
    public async Task DoesNotBypassReplyRestrictions()
    {
        _options.DeveloperMode = false;

        BotAction work = TestRecords.Work();
        await _database.HandleAsync(11, [work], null, CancellationToken.None);

        FakeRemote remote = new(_options) { Restricted = true };
        ActionDispatcher dispatcher = new(_database, _options, NullLogger<ActionDispatcher>.Instance, _statistics, TestRecords.Handlers());
        await dispatcher.SendAsync(work, remote, CancellationToken.None);
        Assert.Equal(0, remote.Writes);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM reply_work WHERE status = 'not-sent'"));
    }

    /// <summary>Stops on a mismatched record and leaves the uncertain item available for operator reconciliation.</summary>
    [PostgresFact]
    public async Task DoesNotOverwriteMismatchedReply()
    {
        _options.DeveloperMode = false;

        BotAction work = TestRecords.Work();
        await _database.HandleAsync(11, [work], null, CancellationToken.None);
        await _database.MarkAttemptAsync(work, CancellationToken.None);

        FakeRemote remote = new(_options) { Saved = new Post("different content", TestRecords.Start), Key = work.RecordKey };
        ActionDispatcher dispatcher = new(_database, _options, NullLogger<ActionDispatcher>.Instance, _statistics, TestRecords.Handlers());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            dispatcher.SendAsync(work with { Status = "uncertain" }, remote, CancellationToken.None));
        Assert.Equal(0, remote.Writes);
        Assert.NotNull(await _database.NextWorkAsync(CancellationToken.None));
    }

    /// <summary>Persists authorized data-loss audit records before allowing progress past malformed input.</summary>
    [PostgresFact]
    public async Task AuditsExplicitSkips()
    {
        _database.RecordSkip(null, "archive block", "bad compressed data");
        _database.RecordSkip(11, "archive record", "bad CBOR");
        await _database.HandleAsync(12, [], "bad post", CancellationToken.None);
        Assert.Equal(3L, await ScalarAsync("SELECT count(*) FROM skipped_data"));
        Assert.Equal(12, (await _database.LoadStateAsync(CancellationToken.None)).ProcessedThrough);
    }

    private PostgreSqlDatabaseProvider NewDatabase(BotOptions options) =>
        new(_source, options, TimeProvider.System, NullLogger<PostgreSqlDatabaseProvider>.Instance);

    private async Task SqlAsync(string sql)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using NpgsqlConnection connection = await _source.OpenConnectionAsync();
        await using NpgsqlCommand command = new(sql, connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FakeRemote(BotOptions options) : IBotActionRemote
    {
        public BlueskyAgent Agent => throw new NotSupportedException("This test does not use a real SDK client.");

        public string BotDid => options.Did;
        public Post? Saved { get; set; }
        public string? Key { get; set; }
        public int Writes { get; private set; }
        public bool LoseFirstResponse { get; set; }
        public bool Restricted { get; set; }

        public Task<string?> FindRecordAsync(
            BotAction action,
            CancellationToken cancellationToken) =>
            Task.FromResult(Saved is null ? null : JsonSerializer.Serialize(Saved, BotJson.Post));

        public Task<PostView> TargetAsync(
            AtUri uri,
            CancellationToken cancellationToken) =>
            Task.FromResult(TestRecords.View(uri, Restricted));

        public Task<BotActionResult> CreateRecordAsync(
            BotAction action,
            CancellationToken cancellationToken)
        {
            Writes++;
            Saved = TestRecords.Post(action);
            Key = action.RecordKey;

            if (LoseFirstResponse)
            {
                LoseFirstResponse = false;
                throw new HttpRequestException("Server accepted the write; response was lost.");
            }

            return Task.FromResult(new BotActionResult(true, ActionRecords.Uri(action, this), "created"));
        }

        public Task<bool> HasBookmarkAsync(AtUri uri, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CreateBookmarkAsync(StrongReference subject, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> FindListMemberAsync(AtUri listUri, Did author, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}