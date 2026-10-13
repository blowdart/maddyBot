// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.Text;

using idunno.Bluesky.BotSdk;
using idunno.Bluesky.BotSdk.DataProviders;

using MaddyBot;

using BotOptions = MaddyBot.BotOptions;

// Use UTF-8 for replies and diagnostics, including emoji, both in the terminal and when
// output is redirected. A program reading redirected output must decode it as UTF-8 too.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (!Console.IsOutputRedirected)
{
    Console.Title = $"MaddyBot v{ThisAssembly.AssemblyInformationalVersion}";
}

DateTimeOffset startedAt = DateTimeOffset.UtcNow;

// Handle help and invalid commands before loading secrets or connecting to dependencies.
// Arguments not owned by this command are passed to ASP.NET Core's configuration provider.
Option<string> lookbackOption = new("--lookback")
{
    Description = "Once, skip unprocessed replay history older than this duration (for example 30m). Never rewind; retain queued replies."
};
Option<bool> doctorOption = new("--doctor")
{
    Description = "Validate configuration and database connectivity without initializing storage or starting workers."
};
RootCommand command = new("Bluesky Watcher and Reply Guy") { lookbackOption, doctorOption };
command.TreatUnmatchedTokensAsErrors = false;

ParseResult parsed = command.Parse(args);

if (parsed.Errors.Count > 0 ||
    parsed.GetResult(command.Options.OfType<HelpOption>().Single()) is not null ||
    command.Options.OfType<VersionOption>().Any(option => parsed.GetResult(option) is not null))
{
    Environment.ExitCode = parsed.Invoke();

    return;
}

DateTimeOffset? cutoff = null;

if (parsed.GetValue(lookbackOption) is string lookback)
{
    try
    {
        // Fix the cutoff at invocation time. Ownership waits and retries must not keep moving it forward.
        cutoff = DateTimeOffset.UtcNow - BotHosting.ParseLookback(lookback);
    }
    catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
    {
        Console.Error.WriteLine(exception.Message);
        Environment.ExitCode = 2;

        return;
    }
}

WebApplicationBuilder builder = WebApplication.CreateBuilder([.. parsed.UnmatchedTokens]);

BotOptions options = builder.Configuration.GetRequiredSection("Bot").Get<BotOptions>()
    ?? throw new InvalidOperationException("The Bot configuration section is required.");

// Reject unsafe configuration at startup, rather than halfway through consuming durable work.
options.Validate();

string connectionString = BotOptions.DatabaseConnectionString(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    applicationName: "maddyBot");

BotHosting.ConfigureConsoleLogging(builder.Logging);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new ActionStatistics(startedAt));
builder.Services.AddPostgreSqlBotStorage(connectionString, options.DatabaseCaCertificate);

if (parsed.GetValue(doctorOption))
{
    await using WebApplication doctorApp = builder.Build();
    IDatabaseProvider database = doctorApp.Services.GetRequiredService<IDatabaseProvider>();
    if (database is not IBotDatabaseDiagnostics diagnostics)
    {
        throw new InvalidOperationException("The registered database provider does not support the doctor connection check.");
    }
    await diagnostics.CheckConnectionAsync(CancellationToken.None);
    ILogger logger = doctorApp.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MaddyBot.Doctor");
    logger.LogInformation("Doctor check passed. Configuration is valid and the database is reachable; storage was not initialized and workers were not started.");

    return;
}

// Three background workers keep the bot running. The flow for a matching post is:
// Jetstream -> ReaderWorker -> PostgreSQL's reply_work table -> SenderWorker -> Bluesky.
//
// The database is the hand-off between reading and sending, not an in-memory queue. If the
// process stops after saving a reply but before sending it, the next run can find that work.
// Reading and sending run independently, so waiting for permission to send more replies
// does not stop us reading new posts and saving the replies we need to send.

// OwnershipWorker initializes storage and renews permission to work (a "lease"). Only the
// chosen process reads or sends during deployment overlap. ReaderWorker saves complete replies
// and replay progress; SenderWorker sends them later and reconciles uncertain writes.
// MaddyReplyPolicy is the sample-specific choice of which posts deserve which replies.

// CUSTOMIZE HTTP IDENTITY: change "maddyBot/1.0" to your bot's name and version.
// This identifies the application, not the bot account or its credentials.
builder.Services.AddBlueskyBot<MaddyReplyPolicy>(
    options,
    httpUserAgent: "maddyBot/1.0",
    lookbackCutoff: cutoff);

await using WebApplication app = builder.Build();

app.MapGet(
    "/",
    (ActionStatistics statistics) =>
    Results.Text(statistics.Summary, contentType: "text/plain", contentEncoding: Encoding.UTF8));

// Probes only inspect memory. Calling a dependency here would make outages slow the probe
// and could cause restart loops even while the workers are actively recovering.
app.MapBlueskyBotHealthEndpoints();
app.MapBlueskyBotMetricsEndpoint();

await app.RunAsync();