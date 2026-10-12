// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;

using idunno.AtProto;

using Microsoft.Extensions.Configuration;

using Npgsql;

namespace idunno.Bluesky.BotSdk;

// Read once at startup. A restart applies changed policy to future ingestion and sending,
// but does not rewrite reply records already chosen and persisted in the outbox.
/// <summary>Configures durable replay, reply delivery, authentication, and conservative rate limits.</summary>
/// <remarks><para>Read options once at startup. Changes must not rewrite records already saved for delivery.</para></remarks>
public class BotOptions
{
    // Default to no posting so trying the sample cannot accidentally publish replies.
    /// <summary>Gets or sets a value that indicates whether replies are simulated without posting. The default is <see langword="true"/>.</summary>
    public bool DeveloperMode { get; set; } = true;
    /// <summary>Gets or sets the bot account's DID.</summary>
    public string Did { get; set; } = "";
    /// <summary>Gets or sets the bot account's login handle.</summary>
    public string Handle { get; set; } = "";
    /// <summary>Gets or sets the bot account's app password.</summary>
    public string AppPassword { get; set; } = "";

    // Archive access and account login are separate credentials with different purposes.
    /// <summary>Gets or sets the archive API key, separate from the account password.</summary>
    public string ArchiveApiKey { get; set; } = "";
    /// <summary>Gets or sets the base64-encoded 32-byte credential encryption key.</summary>
    public string CredentialEncryptionKey { get; set; } = "";
    /// <summary>Gets or sets the PostgreSQL server's trusted PEM CA certificate.</summary>
    public string DatabaseCaCertificate { get; set; } = "";
    /// <summary>Gets or sets the Jetstream v2 service. Saved checkpoints remain bound to this service.</summary>
    public string JetstreamService { get; set; } = "wss://jetstream.us-west.bsky.network";
    /// <summary>Gets or sets the live replay buffer capacity. The default is 8192 events.</summary>
    public int ReplayLiveBufferCapacity { get; set; } = 8192;
    /// <summary>Gets or sets the author DIDs excluded by reply policies using <see cref="Excludes"/>.</summary>
    public string[] NeverReplyToDids { get; set; } = [];

    // Stay below published Bluesky-hosted limits. These budgets cover this bot only, not other
    // applications sharing its account/IP; actual server cooldowns may require longer waits.
    /// <summary>Gets or sets the minimum outbound request interval in seconds. The default is 5.</summary>
    public int MinimumRequestIntervalSeconds { get; set; } = 5;
    /// <summary>Gets or sets the hourly write-point budget. The default is 4500.</summary>
    public int HourlyWritePoints { get; set; } = 4500;
    /// <summary>Gets or sets the daily write-point budget. The default is 30000.</summary>
    public int DailyWritePoints { get; set; } = 30000;
    /// <summary>Gets or sets the five-minute request budget. The default is 2400.</summary>
    public int RequestsPerFiveMinutes { get; set; } = 2400;
    /// <summary>Gets or sets the five-minute session-creation budget. The default is 20.</summary>
    public int SessionsPerFiveMinutes { get; set; } = 20;
    /// <summary>Gets or sets the daily session-creation budget. The default is 200.</summary>
    public int SessionsPerDay { get; set; } = 200;
    /// <summary>Gets or sets completed reply retention in days. The default is 7.</summary>
    public int CompletedRetentionDays { get; set; } = 7;

    /// <summary>Validates identities, secrets, replay configuration, rate limits, and retention.</summary>
    /// <exception cref="InvalidOperationException">The configuration is invalid or unsafe.</exception>
    /// <exception cref="ArgumentException">A required secret is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The live buffer capacity is not positive.</exception>
    public virtual void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReplayLiveBufferCapacity);

        if (!idunno.AtProto.Did.TryParse(Did, out _))
        {
            throw new InvalidOperationException("Bot:Did must be the bot account's DID, including in developer mode.");
        }

        if (!Uri.TryCreate(JetstreamService, UriKind.Absolute, out Uri? service) ||
            service.Scheme != "wss" || service.AbsolutePath != "/" || !string.IsNullOrEmpty(service.Query) ||
            !string.IsNullOrEmpty(service.Fragment) || !string.IsNullOrEmpty(service.UserInfo))
        {
            throw new InvalidOperationException("Bot:JetstreamService must be a wss:// Jetstream v2 host without a path, query, or credentials.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ArchiveApiKey, nameof(ArchiveApiKey));

        if (!DeveloperMode)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Handle, nameof(Handle));
            ArgumentException.ThrowIfNullOrWhiteSpace(AppPassword, nameof(AppPassword));

            if (!idunno.AtProto.Handle.TryParse(Handle, out _))
            {
                throw new InvalidOperationException("Bot:Handle must be a valid Bluesky handle.");
            }
        }

        if (NeverReplyToDids is null || NeverReplyToDids.Any(did => !idunno.AtProto.Did.TryParse(did, out _)))
        {
            throw new InvalidOperationException("Every Bot:NeverReplyToDids entry must be a valid DID.");
        }

        if (MinimumRequestIntervalSeconds is < 5 or > 3600 ||
            HourlyWritePoints is < 3 or > 4500 || DailyWritePoints is < 3 or > 30000 ||
            RequestsPerFiveMinutes is < 1 or > 2400 || SessionsPerFiveMinutes is < 1 or > 20 ||
            SessionsPerDay is < 1 or > 200 || CompletedRetentionDays is < 1 or > 365)
        {
            throw new InvalidOperationException("Rate limits or retention are outside the supported conservative bounds.");
        }

        if (!DeveloperMode)
        {
            byte[] key;

            try
            {
                key = Convert.FromBase64String(CredentialEncryptionKey);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("Bot:CredentialEncryptionKey must be a base64-encoded 32-byte key.", exception);
            }

            if (key.Length != 32)
            {
                throw new InvalidOperationException("Bot:CredentialEncryptionKey must be a base64-encoded 32-byte key.");
            }

            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Determines whether an author is the bot itself or an explicitly excluded author.</summary>
    /// <param name="did">The author DID to check.</param>
    /// <returns><see langword="true"/> if the author is excluded; otherwise, <see langword="false"/>.</returns>
    public bool Excludes(string did) =>

        // DIDs are stable identities. Always exclude ourselves to avoid a self-reply loop.
        string.Equals(did, Did, StringComparison.Ordinal) || NeverReplyToDids.Contains(did, StringComparer.Ordinal);

    /// <summary>Builds a bounded PostgreSQL connection configuration with production TLS verification.</summary>
    /// <param name="configuration">The configuration containing <c>ConnectionStrings:Bot</c>.</param>
    /// <param name="development"><see langword="true"/> to allow local development TLS settings; otherwise, <see langword="false"/>.</param>
    /// <param name="applicationName">The application identity sent to PostgreSQL.</param>
    /// <returns>A configured PostgreSQL connection string.</returns>
    /// <exception cref="InvalidOperationException">The connection string is missing or production TLS is unsafe.</exception>
    public static string DatabaseConnectionString(
        IConfiguration configuration,
        bool development,
        string applicationName = "idunno.Bluesky.BotSdk")
    {
        string configured = configuration.GetConnectionString("Bot")
            ?? throw new InvalidOperationException("ConnectionStrings:Bot is required.");

        // Pooling avoids reconnecting for every checkpoint or outbox operation. A small cap
        // leaves connections for deployment overlap on the initial development-tier database.
        // Finite connect/command deadlines also make worker liveness bounds meaningful.
        NpgsqlConnectionStringBuilder builder = new(configured)
        {
            Pooling = true,
            MaxPoolSize = 5,
            Timeout = 10,
            CommandTimeout = 15,
            KeepAlive = 10,
            ApplicationName = applicationName
        };

        if (!development && builder.SslMode != SslMode.VerifyFull)
        {
            // Production must authenticate the database server, not merely encrypt the transport.
            throw new InvalidOperationException("Production PostgreSQL connections must use SSL Mode=VerifyFull and a trusted server CA.");
        }

        return builder.ConnectionString;
    }
}