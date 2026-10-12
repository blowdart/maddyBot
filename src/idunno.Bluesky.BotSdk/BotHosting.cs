// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

using idunno.AtProto;
using idunno.Bluesky.BotSdk.DataProviders;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace idunno.Bluesky.BotSdk;

/// <summary>Registers and hosts a durable database-backed watcher-and-action bot.</summary>
public static class BotHosting
{
    /// <summary>Registers replay, ownership, authentication, rate accounting, and built-in action delivery services.</summary>
    /// <typeparam name="TPolicy">The application's event policy implementation.</typeparam>
    /// <param name="services">The host service collection.</param>
    /// <param name="options">The validated runtime options, including any application-specific derived options.</param>
    /// <param name="httpUserAgent">The application's HTTP identity, not its account handle.</param>
    /// <param name="lookbackCutoff">The optional fixed cutoff for an explicit forward-only history skip.</param>
    /// <returns>The service collection for further configuration.</returns>
    /// <remarks>
    /// <para>Register dependencies of <typeparamref name="TPolicy"/> before building the host. The host owns registered
    /// storage services and workers; callers must not separately register another set of workers.
    /// The generic parameter annotation preserves public policy constructors for DI under trimming and Native AOT.</para>
    /// <para>Register a singleton <see cref="IDatabaseProvider"/> before this method using a storage registration extension
    /// or your own implementation. This method does not choose a database.</para>
    /// <para>Remote writes cannot share a database transaction. Fixed keys and reconciliation reduce duplicates
    /// but do not provide an absolute exactly-once guarantee.</para>
    /// </remarks>
    public static IServiceCollection AddBlueskyBot<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPolicy>(
            this IServiceCollection services,
            BotOptions options,
            string httpUserAgent,
            DateTimeOffset? lookbackCutoff = null)
        where TPolicy : class, IBotPolicy
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpUserAgent);
        options.Validate();
        if (!services.Any(service => service.ServiceType == typeof(IDatabaseProvider)))
        {
            throw new InvalidOperationException("Register an IDatabaseProvider before adding the bot runtime.");
        }

        services.AddSingleton(options);
        services.AddSingleton<IBotPolicy, TPolicy>();
        services.AddSingleton(new LookbackOverride(lookbackCutoff));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => new ActionStatistics(provider.GetRequiredService<TimeProvider>().GetUtcNow()));
        services.AddSingleton<Ownership>();
        services.AddSingleton<WorkerHealth>();
        services.AddSingleton<CredentialVault>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBotActionHandler, ReplyActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBotActionHandler, LikeActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBotActionHandler, RepostActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBotActionHandler, BookmarkActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBotActionHandler, ListItemActionHandler>());
        services.AddSingleton<ActionHandlerRegistry>();
        services.AddSingleton<ActionDispatcher>();
        services.AddTransient<OutboundHandler>();

        // Extend the SDK client without replacing its SSRF-protected primary transport.
        services.AddAtProtoHttpClient(new HttpClientOptions(httpUserAgent: httpUserAgent, timeout: TimeSpan.FromSeconds(30)));
        services.AddHttpClient("idunno.atproto").AddHttpMessageHandler<OutboundHandler>();
        services.Configure<HostOptions>(host =>
        {
            host.ShutdownTimeout = TimeSpan.FromSeconds(45);
            host.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });

        // Initialize and renew the lease first; ingestion and sending wait for ownership.
        services.AddHostedService<OwnershipWorker>();
        services.AddHostedService<ReaderWorker>();
        services.AddHostedService<SenderWorker>();

        return services;
    }

    /// <summary>Maps memory-only liveness and dependency endpoints without contacting remote services.</summary>
    /// <param name="endpoints">The host endpoint builder.</param>
    /// <returns>The endpoint builder for further mapping.</returns>
    public static IEndpointRouteBuilder MapBlueskyBotHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/health/live",
            async context =>
        {
            WorkerHealth health = context.RequestServices.GetRequiredService<WorkerHealth>();
            (bool healthy, Dictionary<string, HealthState> workers) = health.Snapshot();

            await Results.Json(
                new LivenessResponse(healthy ? "healthy" : "unhealthy", workers),
                BotJsonContext.Default.LivenessResponse,
                statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
        });
        endpoints.MapGet(
            "/health/dependencies",
            async context =>
        {
            WorkerHealth health = context.RequestServices.GetRequiredService<WorkerHealth>();
            (bool Healthy, Dictionary<string, HealthState> Workers) = health.Snapshot();

            await Results.Json(
                Workers.ToDictionary(pair => pair.Key, pair => pair.Value.Dependency),
                BotJsonContext.Default.DictionaryStringString,
                statusCode: Workers.Values.Any(state => state.Dependency is not null) ? 503 : 200).ExecuteAsync(context);
        });

        return endpoints;
    }

    /// <summary>Configures timestamped console diagnostics for the bot runtime.</summary>
    /// <param name="logging">The host logging builder.</param>
    public static void ConfigureConsoleLogging(ILoggingBuilder logging) => BotLog.ConfigureConsole(logging);

    /// <summary>Parses an explicit history-skip duration with a whole-number count and s, m, h, or d suffix.</summary>
    /// <param name="value">The duration text, such as <c>30m</c>.</param>
    /// <returns>The duration to subtract from invocation time.</returns>
    /// <exception cref="FormatException">The duration syntax is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The duration exceeds the supported range.</exception>
    public static TimeSpan ParseLookback(string value) => LookbackOverride.Parse(value);
}