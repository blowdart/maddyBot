// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using idunno.AtProto.Jetstream.Archive;
using idunno.Bluesky;
using idunno.Bluesky.BotSdk.DataProviders;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Npgsql;

using Xunit;
using Xunit.Sdk;

namespace MaddyBot.Tests;

/// <summary>Supplies PostgreSQL coverage only when a disposable connection is explicitly configured.</summary>
public sealed class PostgreSqlDataAttribute : DataAttribute
{
    /// <summary>Gets or sets whether both saved schema generations are supplied.</summary>
    public bool IncludeActionSchema { get; set; }
    /// <summary>Initializes provider data and reports unavailable database coverage as skipped.</summary>
    public PostgreSqlDataAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MADDYBOT_TEST_CONNECTION")))
        {
            Skip = "Set MADDYBOT_TEST_CONNECTION to a disposable PostgreSQL database.";
        }
    }

    /// <inheritdoc/>
    public override IEnumerable<object[]> GetData(MethodInfo testMethod) =>
        IncludeActionSchema ? [[true, false], [true, true]] : [[true]];
}

/// <summary>Exercises the sample's public package boundary and previously saved state without remote Bluesky writes.</summary>
public sealed class PackageIntegrationTests
{
    private const string SavedRequest = """{"AfterSeq":10,"BeforeSeq":100,"Kinds":null,"Collections":["app.bsky.feed.post"],"Dids":null}""";
    private const string SavedCheckpoint = """{"SealedTipSeq":11,"PlanAfterSeq":10,"ReplayAfterSeq":8,"LiveAfterSeq":11,"RequestFingerprint":"fingerprint","SegmentName":"segment","SegmentChecksum":"checksum","NextBlockIndex":4,"NextByteOffset":2048}""";

    /// <summary>Resolves the sample policy and packaged workers without starting them or connecting.</summary>
    [Fact]
    public void RegistersPackagedRuntime()
    {
        ServiceCollection services = new();
        services.AddLogging();
        BotOptions options = TestRecords.Options();
        services.AddMaddyBot(options, "maddybot-test/1.0", "Host=127.0.0.1;Database=unused;Username=unused");
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        Assert.Same(options, provider.GetRequiredService<idunno.Bluesky.BotSdk.BotOptions>());
        Assert.IsType<MaddyReplyPolicy>(provider.GetRequiredService<IBotPolicy>());
        Assert.Equal(3, provider.GetServices<IHostedService>().Count());
        Assert.IsType<ExcludingReplyActionHandler>(
            Assert.Single(provider.GetServices<IBotActionHandler>(), handler => handler.Kind == "reply"));
        Assert.IsType<PostgreSqlDatabaseProvider>(provider.GetRequiredService<IDatabaseProvider>());
    }

    /// <summary>Retains bounded PostgreSQL defaults and refuses non-verifying production TLS.</summary>
    [Fact]
    public void EnforcesProductionDatabaseTls()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Bot"] = "Host=database.example;Database=bot;Username=bot;SSL Mode=Disable"
        }).Build();
        Assert.Throws<InvalidOperationException>(() =>
            PostgreSqlBotStorageExtensions.CreateConnectionString(configuration, development: false));
        configuration["ConnectionStrings:Bot"] = "Host=database.example;Database=bot;Username=bot;SSL Mode=VerifyFull";
        NpgsqlConnectionStringBuilder connection = new(
            PostgreSqlBotStorageExtensions.CreateConnectionString(configuration, development: false, applicationName: "maddyBot"));
        Assert.Equal(SslMode.VerifyFull, connection.SslMode);
        Assert.Equal(5, connection.MaxPoolSize);
        Assert.Equal(10, connection.Timeout);
        Assert.Equal(15, connection.CommandTimeout);
        Assert.Equal(10, connection.KeepAlive);
        Assert.Equal("maddyBot", connection.ApplicationName);
    }

    /// <summary>Opens the donor schema in place, preserving progress, uncertain reply payloads, credentials, and cooldowns.</summary>
    /// <param name="postgres">Whether to exercise the production PostgreSQL provider instead of SQLite.</param>
    /// <param name="actionSchema">Whether the donor already has versioned action identities.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [PostgreSqlData(IncludeActionSchema = true)]
    public async Task PreservesExistingDurableState(bool postgres, bool actionSchema)
    {
        await using Store store = await Store.CreateAsync(postgres);
        BotAction work = TestRecords.Work();
        await store.SeedDonorStateAsync(work, actionSchema);
        IDatabaseProvider database = store.Database;
        await database.InitializeAsync(CancellationToken.None);
        await database.InitializeAsync(CancellationToken.None);
        Assert.NotNull(await database.AcquireOrRenewAsync(CancellationToken.None));
        await store.SqlAsync(postgres
            ? "UPDATE bot_ownership SET ready_at = now()"
            : "UPDATE bot_ownership SET ready_at = 0");
        ReplayState state = await database.LoadStateAsync(CancellationToken.None);
        Assert.Equal(TestRecords.Start, state.StartedAt);
        Assert.Equal(11, state.ProcessedThrough);
        JsonSerializerOptions json = new() { TypeInfoResolver = BlueskyJsonSerializerOptions.TypeInfoResolver };
        JsonTypeInfo<SnapshotRequest> requestType = (JsonTypeInfo<SnapshotRequest>)json.GetTypeInfo(typeof(SnapshotRequest));
        SnapshotRequest request = JsonSerializer.Deserialize(state.RequestJson!, requestType)!;
        Assert.Equal(10, request.AfterSeq);
        Assert.Equal(100, request.BeforeSeq);
        Assert.Equal("app.bsky.feed.post", Assert.Single(request.Collections!).ToString());
        JsonTypeInfo<SnapshotCheckpoint> checkpointType = (JsonTypeInfo<SnapshotCheckpoint>)json.GetTypeInfo(typeof(SnapshotCheckpoint));
        SnapshotCheckpoint checkpoint = JsonSerializer.Deserialize(state.CheckpointJson!, checkpointType)!;
        Assert.Equal(JsonSerializer.Deserialize(SavedCheckpoint, checkpointType), checkpoint);
        Assert.Equal(new byte[] { 1, 2, 3 }, await database.LoadCredentialsAsync(CancellationToken.None));

        BotAction restored = (await database.NextWorkAsync(CancellationToken.None))!;
        Assert.Equal(work.RecordKey, restored.RecordKey);
        using JsonDocument expectedRecord = JsonDocument.Parse(work.RecordJson);
        using JsonDocument actualRecord = JsonDocument.Parse(restored.RecordJson);
        Assert.True(JsonElement.DeepEquals(expectedRecord.RootElement, actualRecord.RootElement));
        Assert.Equal("uncertain", restored.Status);
        Assert.Equal(2, restored.Attempts);
        Assert.Equal("reply", restored.ActionKind);
        Assert.Equal("default", restored.ActionKey);
        Assert.Equal(1, restored.PayloadVersion);
        Assert.Equal(0, await database.HandleAsync(11, [work], null, CancellationToken.None));

        database.SaveCheckpoint(checkpoint, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() =>
            database.SaveCheckpoint(checkpoint with { LiveAfterSeq = 12 }, CancellationToken.None));
        ReplayState saved = await database.LoadStateAsync(CancellationToken.None);
        Assert.Equal(11, saved.ProcessedThrough);
        Assert.Equal(checkpoint, JsonSerializer.Deserialize(saved.CheckpointJson!, checkpointType));
        Assert.True(await database.ArchiveCooldownAsync(CancellationToken.None) > DateTimeOffset.UtcNow);
        Assert.True(await database.ReserveRequestAsync(0, false, CancellationToken.None) > DateTimeOffset.UtcNow);
    }

    /// <summary>Checks doctor-style reads do not initialize tables, then validates public health and metrics shapes.</summary>
    /// <param name="postgres">Whether to use the production PostgreSQL provider.</param>
    [Theory]
    [InlineData(false)]
    [PostgreSqlData]
    public async Task DoctorAndEndpointsUsePackagedStorage(bool postgres)
    {
        await using Store store = await Store.CreateAsync(postgres);
        IBotDatabaseDiagnostics diagnostics = Assert.IsType<IBotDatabaseDiagnostics>(store.Database, exactMatch: false);
        if (postgres)
        {
            await diagnostics.CheckConnectionAsync(CancellationToken.None);
            Assert.Equal(0, await store.CountTablesAsync());
        }
        else
        {
            await Assert.ThrowsAsync<SqliteException>(() => diagnostics.CheckConnectionAsync(CancellationToken.None));
            Assert.False(File.Exists(store.Path));
        }

        await store.Database.InitializeAsync(CancellationToken.None);
        store.App.MapBlueskyBotHealthEndpoints();
        store.App.MapBlueskyBotMetricsEndpoint();
        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)store.App).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()];
        Assert.Equal(
            ["/health/dependencies", "/health/live", "/metrics"],
            endpoints.Select(endpoint => endpoint.RoutePattern.RawText).Order());
        foreach (RouteEndpoint endpoint in endpoints)
        {
            DefaultHttpContext context = new() { RequestServices = store.App.Services };
            await using MemoryStream body = new();
            context.Response.Body = body;
            await endpoint.RequestDelegate!(context);
            body.Position = 0;
            using StreamReader reader = new(body);
            string response = await reader.ReadToEndAsync();
            Assert.Equal(200, context.Response.StatusCode);
            if (endpoint.RoutePattern.RawText == "/metrics")
            {
                Assert.Contains("bluesky_bot_actions{status=\"not-sent\"} 0", response, StringComparison.Ordinal);
                Assert.Contains("bluesky_bot_retry_due_actions 0", response, StringComparison.Ordinal);
                Assert.DoesNotContain("status=\"retry_due\"", response, StringComparison.Ordinal);
            }
            else
            {
                using JsonDocument json = JsonDocument.Parse(response);
                Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
                if (endpoint.RoutePattern.RawText == "/health/live")
                {
                    Assert.Equal("healthy", json.RootElement.GetProperty("status").GetString());
                    Assert.True(json.RootElement.GetProperty("workers").TryGetProperty("reader", out _));
                }
                else
                {
                    Assert.Equal("/health/dependencies", endpoint.RoutePattern.RawText);
                    Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("reader").ValueKind);
                }
            }
        }
    }

    /// <summary>Owns only a unique disposable PostgreSQL schema or SQLite file and a non-started test host.</summary>
    private sealed class Store : IAsyncDisposable
    {
        private readonly bool _postgres;
        private readonly string _name = "migration_" + Guid.NewGuid().ToString("N");
        private readonly string _connectionString;
        /// <summary>Gets the unique SQLite file path, absent before initialization.</summary>
        public string Path { get; }
        /// <summary>Gets the non-started host that owns the test's package services.</summary>
        public WebApplication App { get; }
        /// <summary>Gets the packaged provider registered for this isolated store.</summary>
        public IDatabaseProvider Database => App.Services.GetRequiredService<IDatabaseProvider>();

        /// <summary>Initializes isolated connection settings and the packaged runtime without starting workers.</summary>
        /// <param name="postgres">Whether to create a unique PostgreSQL schema.</param>
        private Store(bool postgres)
        {
            _postgres = postgres;
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), _name + ".db");
            _connectionString = postgres
                ? new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MADDYBOT_TEST_CONNECTION"))
                {
                    SearchPath = _name,
                    Pooling = false
                }.ConnectionString
                : new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ConnectionString;
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(PackageIntegrationTests).Assembly.FullName,
                Args = []
            });
            BotOptions options = TestRecords.Options();
            builder.Services.AddSingleton(options);
            BlueskyBotBuilder bot = builder.Services.AddBlueskyBot<MaddyReplyPolicy>(options, "migration-test/1.0");
            if (postgres)
            {
                bot.WithPostgreSqlStorage(_connectionString);
            }
            else
            {
                bot.WithSqliteStorage(_connectionString);
            }
            App = builder.Build();
        }

        /// <summary>Creates only the uniquely named schema; SQLite remains absent until initialized.</summary>
        /// <param name="postgres">Whether to exercise PostgreSQL.</param>
        /// <returns>The isolated test store.</returns>
        public static async Task<Store> CreateAsync(bool postgres)
        {
            Store store = new(postgres);
            if (postgres)
            {
                await store.SqlAsync($"CREATE SCHEMA \"{store._name}\"");
            }

            return store;
        }

        /// <summary>Creates donor-format state and a pre-action-schema uncertain reply for migration checks.</summary>
        /// <param name="work">The complete fixed-key reply saved by the sample.</param>
        /// <param name="actionSchema">Whether to create the extracted SDK's action schema instead of its legacy reply schema.</param>
        public async Task SeedDonorStateAsync(BotAction work, bool actionSchema)
        {
            await SqlAsync(_postgres
                ? $$"""
                CREATE TABLE bot_state (
                    id boolean PRIMARY KEY DEFAULT true CHECK(id), started_at timestamptz NOT NULL, service text NOT NULL,
                    bot_did text NOT NULL, developer_mode boolean NOT NULL, request jsonb, checkpoint jsonb,
                    processed_through bigint NOT NULL DEFAULT 0, credentials bytea, archive_cooldown timestamptz NOT NULL DEFAULT '-infinity');
                CREATE TABLE reply_work (
                    source_uri text NOT NULL, source_sequence bigint NOT NULL, source_did text NOT NULL,
                    record_key text NOT NULL {{(actionSchema ? "" : "UNIQUE")}}, record jsonb NOT NULL, status text NOT NULL DEFAULT 'pending',
                    attempts integer NOT NULL DEFAULT 0, next_retry timestamptz NOT NULL DEFAULT now(), outcome text,
                    reply_uri text, completed_at timestamptz, target_deleted boolean NOT NULL DEFAULT false,
                    {{(actionSchema ? "action_kind text NOT NULL DEFAULT 'reply', action_key text NOT NULL DEFAULT 'default', payload_version integer NOT NULL DEFAULT 1," : "")}}
                    PRIMARY KEY ({{(actionSchema ? "source_uri,action_kind,action_key" : "source_uri")}}));
                CREATE TABLE rate_state (id boolean PRIMARY KEY, next_request timestamptz NOT NULL DEFAULT '-infinity', cooldown_until timestamptz NOT NULL DEFAULT '-infinity');
                INSERT INTO rate_state VALUES(true, now(), now()+interval '1 day');
                """
                : $$"""
                CREATE TABLE bot_state (
                    id INTEGER PRIMARY KEY, started_at BIGINT NOT NULL, service TEXT NOT NULL,
                    bot_did TEXT NOT NULL, developer_mode INTEGER NOT NULL, request TEXT, checkpoint TEXT,
                    processed_through BIGINT NOT NULL DEFAULT 0, credentials BLOB, archive_cooldown BIGINT NOT NULL DEFAULT 0);
                CREATE TABLE reply_work (
                    source_hash BLOB NOT NULL, source_uri TEXT NOT NULL, source_sequence BIGINT NOT NULL,
                    source_did TEXT NOT NULL, record_key TEXT NOT NULL {{(actionSchema ? "" : "UNIQUE")}}, record TEXT NOT NULL,
                    root_uri TEXT, status TEXT NOT NULL DEFAULT 'pending', attempts INTEGER NOT NULL DEFAULT 0, next_retry BIGINT NOT NULL,
                    outcome TEXT, reply_uri TEXT, completed_at BIGINT, target_deleted INTEGER NOT NULL DEFAULT 0,
                    {{(actionSchema ? "action_kind text NOT NULL DEFAULT 'reply', action_key text NOT NULL DEFAULT 'default', payload_version integer NOT NULL DEFAULT 1," : "")}}
                    PRIMARY KEY ({{(actionSchema ? "source_hash,action_kind,action_key" : "source_hash")}}));
                CREATE TABLE rate_state (id INTEGER PRIMARY KEY, next_request INTEGER NOT NULL DEFAULT 0, cooldown_until INTEGER NOT NULL DEFAULT 0);
                INSERT INTO rate_state VALUES(1,0,9223372036854775);
                """);
            await using DbConnection connection = Connection();
            await connection.OpenAsync();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = _postgres
                ? """
                INSERT INTO bot_state VALUES(true,@started,@service,@did,true,CAST(@request AS jsonb),CAST(@checkpoint AS jsonb),11,@credentials,now()+interval '1 day');
                INSERT INTO reply_work(source_uri,source_sequence,source_did,record_key,record,status,attempts,next_retry)
                    VALUES(@uri,11,@author,@key,CAST(@record AS jsonb),'uncertain',2,now()-interval '1 second');
                """
                : """
                INSERT INTO bot_state VALUES(1,@started,@service,@did,1,@request,@checkpoint,11,@credentials,@cooldown);
                INSERT INTO reply_work(source_hash,source_uri,source_sequence,source_did,record_key,record,status,attempts,next_retry)
                    VALUES(@hash,@uri,11,@author,@key,@record,'uncertain',2,0);
                """;
            foreach ((string name, object value) in new (string, object)[]
            {
                ("started", _postgres ? TestRecords.Start : TestRecords.Start.ToUnixTimeMilliseconds() * 1000),
                ("service", TestRecords.Options().JetstreamService),
                ("did", TestRecords.BotDid),
                ("request", SavedRequest),
                ("checkpoint", SavedCheckpoint),
                ("credentials", new byte[] { 1, 2, 3 }),
                ("uri", work.SourceUri.ToString()),
                ("author", work.SourceDid.ToString()),
                ("key", work.RecordKey),
                ("record", work.RecordJson),
                ("hash", System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(work.SourceUri.ToString()))),
                ("cooldown", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds() * 1000)
            })
            {
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Creates a connection restricted to this test's schema or file.</summary>
        /// <returns>The unopened isolated connection.</returns>
        private DbConnection Connection() => _postgres ? new NpgsqlConnection(_connectionString) : new SqliteConnection(_connectionString);

        /// <summary>Executes fixture SQL only in the isolated store.</summary>
        /// <param name="sql">The fixture statement.</param>
        public async Task SqlAsync(string sql)
        {
            await using DbConnection connection = Connection();
            await connection.OpenAsync();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Counts tables created in this test's PostgreSQL schema.</summary>
        /// <returns>The number of tables.</returns>
        public async Task<long> CountTablesAsync()
        {
            await using DbConnection connection = Connection();
            await connection.OpenAsync();
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{_name}'";

            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        /// <summary>Disposes the test host before removing only its own schema or SQLite files.</summary>
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            if (_postgres)
            {
                await SqlAsync($"DROP SCHEMA \"{_name}\" CASCADE");
            }
            else
            {
                File.Delete(Path);
                File.Delete(Path + "-wal");
                File.Delete(Path + "-shm");
            }
        }
    }
}