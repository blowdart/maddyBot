// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using idunno.AtProto.Jetstream;
using idunno.Bluesky.BotSdk.DataProviders;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;

namespace MaddyBot.Tests;

/// <summary>Verifies that the reusable SDK can register policies, workers, and AOT-safe probes without remote writes.</summary>
public sealed class BotHostingTests
{
    /// <summary>Resolves an application handler from DI using its exact persisted payload version.</summary>
    [Fact]
    public void RegistrationResolvesCustomHandlerVersion()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSqliteBotStorage("Data Source=unused-custom-handler.db");
        CustomHandler handler = new("application-action", 2);
        services.AddSingleton<IBotActionHandler>(handler);
        services.AddBlueskyBot<NoReplyPolicy>(TestRecords.Options(), "custom-handler-test/1.0");
        using ServiceProvider provider = services.BuildServiceProvider();
        ActionHandlerRegistry registry = provider.GetRequiredService<ActionHandlerRegistry>();
        using JsonDocument payload = JsonDocument.Parse("""{"message":"saved intent"}""");
        BotAction action = BotActions.Create(
            handler.Kind,
            new(new(TestRecords.Work().SourceUri), new(TestRecords.CidValue)),
            11,
            payload.RootElement,
            BotJsonContext.Default.JsonElement,
            payloadVersion: 2);
        registry.Validate(action);
        Assert.Same(handler, registry.Get(action));
        Assert.Throws<InvalidOperationException>(() => registry.Get(action with { PayloadVersion = 1 }));
        Assert.Equal(6, provider.GetServices<IBotActionHandler>().Count());
    }

    /// <summary>Reports conflicting application formats instead of silently replacing built-in handlers.</summary>
    /// <param name="kind">The conflicting action kind.</param>
    /// <param name="version">The conflicting payload version.</param>
    [Theory]
    [InlineData("reply", 1)]
    [InlineData("application-action", 2)]
    public void RejectsDuplicateHandlerFormats(string kind, int version)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSqliteBotStorage("Data Source=unused-collision-test.db");
        services.AddSingleton<IBotActionHandler>(new CustomHandler(kind, version));
        if (kind != "reply")
        {
            services.AddSingleton<IBotActionHandler>(new CustomHandler(kind, version));
        }
        services.AddBlueskyBot<NoReplyPolicy>(TestRecords.Options(), "custom-handler-test/1.0");
        using ServiceProvider provider = services.BuildServiceProvider();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<ActionHandlerRegistry>());
        Assert.Contains($"'{kind}' payload version {version}", error.Message);
    }

    /// <summary>Requires an explicit database provider rather than choosing an SDK default.</summary>
    [Fact]
    public void RegistrationRequiresStorage()
    {
        ServiceCollection services = new();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddBlueskyBot<NoReplyPolicy>(TestRecords.Options(), "registration-test/1.0"));
    }

    /// <summary>Preserves an application's direct singleton registration without creating a default connection pool.</summary>
    [Fact]
    public void RegistrationPreservesApplicationStorage()
    {
        ServiceCollection services = new();
        services.AddLogging();
        BotOptions options = TestRecords.Options();
        using NpgsqlDataSource source = NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused;Username=unused");
        IDatabaseProvider database = new PostgreSqlDatabaseProvider(
            source, options, TimeProvider.System, NullLogger<PostgreSqlDatabaseProvider>.Instance);
        services.AddSingleton(database);
        services.AddBlueskyBot<NoReplyPolicy>(options, "registration-test/1.0");

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        Assert.Same(database, provider.GetRequiredService<IDatabaseProvider>());
        Assert.Single(services, service => service.ServiceType == typeof(IDatabaseProvider));
        Assert.Null(provider.GetService<NpgsqlDataSource>());
    }

    /// <summary>Resolves the explicitly selected relational storage and rejects conflicting registrations.</summary>
    /// <param name="mysql">A value indicating whether to register MySQL rather than SQLite.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistrationResolvesSelectedStorage(bool mysql)
    {
        ServiceCollection services = new();
        services.AddLogging();
        if (mysql)
        {
            services.AddMySqlBotStorage("Server=127.0.0.1;Database=unused;User ID=unused");
        }
        else
        {
            services.AddSqliteBotStorage($"Data Source={Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db")}");
        }
        Assert.Throws<InvalidOperationException>(() =>
            services.AddPostgreSqlBotStorage("Host=127.0.0.1;Database=unused;Username=unused"));
        services.AddBlueskyBot<NoReplyPolicy>(TestRecords.Options(), "registration-test/1.0");

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        IDatabaseProvider database = provider.GetRequiredService<IDatabaseProvider>();
        if (mysql)
        {
            Assert.IsType<MySqlDatabaseProvider>(database);
        }
        else
        {
            Assert.IsType<SqliteDatabaseProvider>(database);
        }
        Assert.Null(provider.GetService<NpgsqlDataSource>());
    }

    /// <summary>Checks the public registration API resolves the sample policy and all workers.</summary>
    [Fact]
    public void RegistrationResolvesPolicyAndWorkers()
    {
        ServiceCollection services = new();
        services.AddLogging();
        BotOptions options = TestRecords.Options();
        services.AddSingleton(options);
        services.AddPostgreSqlBotStorage("Host=127.0.0.1;Database=unused;Username=unused");
        services.AddBlueskyBot<MaddyReplyPolicy>(
            options, "registration-test/1.0");

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        Assert.Same(options, provider.GetRequiredService<idunno.Bluesky.BotSdk.BotOptions>());
        IBotPolicy policy = provider.GetRequiredService<IBotPolicy>();
        Assert.IsType<MaddyReplyPolicy>(policy);
        Assert.Single(policy.Create(TestRecords.Event(), TestRecords.Start));
        Assert.IsType<PostgreSqlDatabaseProvider>(provider.GetRequiredService<IDatabaseProvider>());
        Assert.Collection(provider.GetServices<IHostedService>(),
            worker => Assert.IsType<OwnershipWorker>(worker),
            worker => Assert.IsType<ReaderWorker>(worker),
            worker => Assert.IsType<SenderWorker>(worker));
    }

    /// <summary>Checks public health mappings preserve JSON shapes and status codes without opening a database.</summary>
    /// <param name="dependencyRecovery">A value indicating whether the reader is recovering from a dependency outage.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthEndpointsPreserveResponseShapes(bool dependencyRecovery)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BotHostingTests).Assembly.FullName,
            Args = []
        });
        builder.Services.AddPostgreSqlBotStorage("Host=127.0.0.1;Database=unused;Username=unused");
        builder.Services.AddBlueskyBot<NoReplyPolicy>(TestRecords.Options(), "probe-test/1.0");
        await using WebApplication app = builder.Build();
        app.MapBlueskyBotHealthEndpoints();
        WorkerHealth health = app.Services.GetRequiredService<WorkerHealth>();
        if (dependencyRecovery)
        {
            health.Set("reader", "recovery", TimeSpan.FromSeconds(30), "archive");
        }

        RouteEndpoint[] endpoints = [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()];
        foreach (RouteEndpoint endpoint in endpoints)
        {
            DefaultHttpContext context = new() { RequestServices = app.Services };
            await using MemoryStream body = new();
            context.Response.Body = body;
            await endpoint.RequestDelegate!(context);
            body.Position = 0;
            using JsonDocument json = await JsonDocument.ParseAsync(body);
            if (endpoint.RoutePattern.RawText == "/health/live")
            {
                Assert.Equal(200, context.Response.StatusCode);
                Assert.Equal("healthy", json.RootElement.GetProperty("status").GetString());
                Assert.True(json.RootElement.GetProperty("workers").TryGetProperty("reader", out _));
            }
            else
            {
                Assert.Equal("/health/dependencies", endpoint.RoutePattern.RawText);
                Assert.Equal(dependencyRecovery ? 503 : 200, context.Response.StatusCode);
                Assert.Equal(dependencyRecovery ? "archive" : null, json.RootElement.GetProperty("reader").GetString());
            }
        }
        Assert.Equal(2, endpoints.Length);
    }

    /// <summary>Provides an application policy that deliberately creates no replies.</summary>
    public sealed class NoReplyPolicy : IBotPolicy
    {
        /// <inheritdoc/>
        public IReadOnlyList<BotAction> Create(
            JetstreamEvent item,
            DateTimeOffset startedAt) => [];
    }

    /// <summary>Provides a versioned application handler for registration tests without remote effects.</summary>
    /// <param name="kind">The stable custom action kind.</param>
    /// <param name="version">The exact supported payload version.</param>
    private sealed class CustomHandler(
        string kind,
        int version) : IBotActionHandler
    {
        /// <inheritdoc/>
        public string Kind => kind;

        /// <inheritdoc/>
        public int PayloadVersion => version;

        /// <inheritdoc/>
        public void Validate(BotAction action)
        {
            Assert.Equal(Kind, action.ActionKind);
            Assert.Equal(PayloadVersion, action.PayloadVersion);
        }

        /// <inheritdoc/>
        public Task<BotActionResult?> ReconcileAsync(
            BotAction action,
            IBotActionRemote remote,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Registration tests must not contact remote services.");

        /// <inheritdoc/>
        public Task<BotActionResult> ExecuteAsync(
            BotAction action,
            IBotActionRemote remote,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Registration tests must not perform remote actions.");
    }
}