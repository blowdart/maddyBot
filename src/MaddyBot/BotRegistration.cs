// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.Bluesky.BotSdk;
using idunno.Bluesky.BotSdk.DataProviders;

namespace MaddyBot;

/// <summary>Connects the sample's policy and queued-author exclusions to the packaged runtime.</summary>
internal static class BotRegistration
{
    /// <summary>Registers the sample without starting workers or modifying storage.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <param name="options">The validated sample and runtime settings.</param>
    /// <param name="httpUserAgent">The application identity sent to remote services.</param>
    /// <param name="connectionString">The PostgreSQL connection string with the required TLS settings.</param>
    /// <param name="lookbackCutoff">The optional one-time, forward-only history cutoff.</param>
    public static void AddMaddyBot(
        this IServiceCollection services,
        BotOptions options,
        string httpUserAgent,
        string connectionString,
        DateTimeOffset? lookbackCutoff = null)
    {
        services.AddSingleton(options);
        BlueskyBotBuilder bot = services.AddBlueskyBot<MaddyReplyPolicy>(
            options,
            httpUserAgent,
            lookbackCutoff)
            .WithPostgreSqlStorage(connectionString, options.DatabaseCaCertificate);

        // The registry rejects duplicate kind/version pairs; replace only the reply
        // handler, keeping all other SDK handlers and its protected transport.
        ServiceDescriptor replyHandler = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IBotActionHandler) &&
            descriptor.ImplementationType == typeof(ReplyActionHandler));
        services.Remove(replyHandler);
        bot.AddActionHandler<ExcludingReplyActionHandler>();
    }
}