# Use the packaged Bot SDK

MaddyBot consumes `idunno.Bluesky.BotSdk` and
`idunno.Bluesky.BotSdk.PostgreSQL` at **8.0.0-prerelease.g699396cf24**.
The test project also uses `idunno.Bluesky.BotSdk.SQLite`. The runtime source
and its internal tests now live in [idunno.Bluesky](https://github.com/blowdart/idunno.Bluesky/tree/version/v8.0.0),
not in this repository.

## Packages and hosting

Versions are centralized in `Directory.Packages.props`. `nuget.config` maps
`idunno.AtProto`, `idunno.AtProto.*`, `idunno.Bluesky`, and `idunno.Bluesky.*`
to the public MyGet feed `https://www.myget.org/F/blowdart/api/v3/index.json`;
other dependencies restore from nuget.org. Keep the directly referenced idunno
packages on the same prerelease to avoid downgrade conflicts.

The core package includes ASP.NET Core hosting; there is no separate BotSdk
ASP.NET Core package. Runtime types use `idunno.Bluesky.BotSdk`; provider
extensions and interfaces use `idunno.Bluesky.BotSdk.DataProviders`.

```csharp
services.AddSingleton(options); // The sample policy needs its derived options.
services.AddBlueskyBot<MaddyReplyPolicy>(
    options,
    httpUserAgent: "MyWatcherBot/1.0",
    lookbackCutoff: cutoff)
    .WithPostgreSqlStorage(connectionString, options.DatabaseCaCertificate);
```

Register exactly one storage provider. The builder also supports SQLite, MySQL,
and in-memory storage through their respective packages. MaddyBot uses PostgreSQL
for persistent hosted storage; it does not reference MySQL. The PostgreSQL
registration owns its keyed connection pool. Pass the existing PEM CA certificate
as shown, and use `PostgreSqlBotStorageExtensions.CreateConnectionString` to enforce
production TLS verification and bounded connection settings. Do not add a second
pool or resolve an unkeyed `NpgsqlDataSource`.

`AddBlueskyBot` registers the policy and three hosted workers. Do not register
copies of those workers. `IBotPolicy.Evaluate()` returns complete action batches
without remote writes. MaddyBot's adapter calls `ReplyPolicy.Create()` and returns
only a reply or an empty batch.

`BotHosting.ParseLookback` parses the optional CLI duration. Convert it to a
single invocation-time cutoff; do not turn `--lookback` into recurring configuration.
`MapBlueskyBotHealthEndpoints()` maps memory-only health responses;
`MapBlueskyBotMetricsEndpoint()` separately queries durable state per scrape.
For doctor mode, resolve `IDatabaseProvider`, cast to `IBotDatabaseDiagnostics`,
and call only `CheckConnectionAsync`. Never initialize or acquire ownership for
a read-only connectivity check.

## Policies and custom actions

Use the maintained upstream guides and examples:

- [Durable Bot SDK](https://github.com/blowdart/idunno.Bluesky/blob/version/v8.0.0/docs/docs/bots/durableBotSdk.md)
  for host setup, built-in actions, storage, recovery, and operations.
- [Custom actions](https://github.com/blowdart/idunno.Bluesky/blob/version/v8.0.0/docs/docs/bots/customBotActions.md)
  for versioned payloads and `IBotActionHandler`.
- [Complete sample](https://github.com/blowdart/idunno.Bluesky/tree/version/v8.0.0/samples/Samples.BotSdk)
  for registration and a policy.

These links track the upstream branch. The packages used here correspond to
[`699396cf24f84dc44b393196db18a73646fe1fef`](https://github.com/blowdart/idunno.Bluesky/commit/699396cf24f84dc44b393196db18a73646fe1fef);
check that revision when diagnosing version-specific behavior.

Watch phrases, reply choices, excluded authors, and database CA configuration
remain application-owned settings. The SDK no longer owns `NeverReplyToDids`.
`Program.cs` calls the sample's `AddMaddyBot` helper in `BotRegistration.cs`,
which replaces only the built-in reply handler registration with
`ExcludingReplyActionHandler`, which composes the public `ReplyActionHandler`.
Registering both for the same kind and payload version fails explicitly.

The wrapper rejects excluded pending replies before remote access and checks
exclusions again before execution. For uncertain replies it first delegates
reconciliation so a previously accepted fixed-key record is still recognized.
Reconciliation failures remain failures; they are not silently treated as missing
records. No new remote write is admitted for an excluded author.

**Developer-mode difference:** the packaged dispatcher simulates actions without
calling handlers. Previously queued replies whose authors become excluded are
therefore recorded as `simulated`, rather than `not-sent`, in developer mode.
They still make no remote writes. New excluded events are filtered by the policy
in both modes. Live delivery continues to enforce the queued exclusion gate.

## Migrate an existing MaddyBot database

1. Stop every old bot instance and prevent overlapping old/new deployments.
2. Back up the database and the credential encryption key separately. Keep the
   configured DID, exact Jetstream service, developer mode, and CA trust unchanged.
3. Restore and build the application, then optionally use `--doctor` to check
   configuration/connectivity without modifying storage.
4. Start the packaged application against the **same database**. The provider
   initializes missing tables and migrates legacy reply rows in place. Do not
   delete tables, clear progress, or recreate the database.
5. Confirm resumed progress and queued outcomes before retiring the backup.

Both the legacy reply-only schema and the extracted action schema retain their
saved payloads and fixed record keys. The provider preserves eligibility start,
original request, full checkpoint (including live/fallback fields), processed-through
boundary, pending/uncertain work, outcomes, ownership state, credentials, rate
accounting, and cooldowns. Legacy reply identities become `reply` / `default` /
payload version `1`; the obsolete unique record-key constraint is removed.

Saved replay JSON retains its PascalCase names and null fields. Refresh credentials
retain their existing AES-GCM envelope and DID binding. Keep the original 32-byte
encryption key: a wrong key is fatal, not permission to create a fresh session.
If deliberately resetting authentication, stop all instances and clear **only**
`bot_state.credentials` after understanding that a new login is required. Do not
clear progress or queued work to recover authentication.

Switching developer mode to live requires a separate empty runtime database;
do not edit the saved mode. Restoring an old backup or rewinding still requires
remote reconciliation and does not acquire exactly-once delivery guarantees.

## Test ownership

This repository tests matching/configuration, sample-specific queued exclusions,
public host registration, doctor/endpoint behavior, and persisted donor database
compatibility through public package APIs. Run
`scripts\TestDatabaseProviders.ps1 -AllTests` for isolated PostgreSQL and SQLite
coverage without touching the running bot's binaries or database.

Internal replay, JSON, credential, dispatcher, transport, and provider suites are
maintained in upstream `test/idunno.Bluesky.BotSdk.Test`. They are not copied here
or reached through reflection. The upstream authentication container scripts
initialize a different schema; MaddyBot's scripts create disposable databases and
let the BotSdk provider initialize runtime tables.
