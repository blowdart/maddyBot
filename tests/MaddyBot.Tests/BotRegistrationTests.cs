// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.Bluesky.BotSdk.DataProviders;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace MaddyBot.Tests;

/// <summary>Checks sample service registration without starting workers or connecting to a database.</summary>
public sealed class BotRegistrationTests
{
    /// <summary>Resolves sample options, policy, and queued exclusions with the packaged PostgreSQL runtime.</summary>
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
        BotReplayProgress progress = provider.GetRequiredService<BotReplayProgress>();
        Assert.Same(progress, provider.GetRequiredService<BotReplayProgress>());
        Assert.Null(progress.Snapshot.WitnessedAt);
        Assert.Null(progress.Snapshot.ObservedAt);
        Assert.NotNull(provider.GetRequiredService<TimeProvider>());
    }
}