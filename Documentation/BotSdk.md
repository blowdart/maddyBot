# Build your own Bluesky bot

This guide takes you from a safe local run to a policy that can reply, like,
repost, bookmark, or add a post's author to a list. You do not need to write
database SQL or remote-retry code for those five actions.

The SDK is the reusable runtime in `src\idunno.Bluesky.BotSdk`. MaddyBot is a
small consuming application in `src\MaddyBot`: it watches configured phrases
and **only replies by default**. The SDK is currently referenced as a project;
there is no published Bot SDK NuGet package to install yet.

## Contents

- [Start with the sample](#start-with-the-sample)
- [Understand the flow](#understand-the-flow)
- [Create your own host](#create-your-own-host)
- [Choose storage](#choose-storage)
- [Write a policy](#write-a-policy)
- [Choose built-in actions](#choose-built-in-actions)
- [Action identity and recovery](#action-identity-and-recovery)
- [Custom actions](#custom-actions)
- [Operate safely](#operate-safely)
- [Troubleshooting](#troubleshooting)

## Start with the sample

1. Install the .NET 10 SDK selected by `global.json` and a usable Docker
   Linux-container engine, or the WSL container setup described in the
   [local-development instructions](../README.md#local-development).
2. Use a dedicated Bluesky bot account. Find its actual DID (stable account
   identifier), not its `@handle`, and obtain a Jetstream archive API key from
   [bsky.network](https://bsky.network/account#api-keys-section-heading).
3. Follow the README to create a **development** PostgreSQL database, save its
   connection string and the DID/archive key in user secrets, and run MaddyBot.
   Leave `Bot:DeveloperMode=true`. This mode reads and saves simulated work but
   does not log in or perform remote actions. No app password is needed.
4. Change `Bot:WatchPhrases`, `Bot:Replies`, and `Bot:NeverReplyToDids` in
   `src\MaddyBot\appsettings.json`. Also change `httpUserAgent: "maddyBot/1.0"`
   in `Program.cs` to your bot's name/version.
5. From another account, create a **new** post containing a configured phrase.
   Check the simulation logs and `/health/live`. Old posts, self-authored posts,
   excluded authors, updates, and sync backfills are intentionally ignored.
6. Only after checking the behavior, follow
   [Deliberately enable real actions](../README.md#deliberately-enable-real-actions).
   Confirm the account and intended actions, use a separate database, set an app
   password and a stable credential encryption key, then disable developer mode.

An archive API key permits replay access; an app password permits account login.
They are different credentials. Never put secrets in checked-in configuration.
.NET user secrets are local development storage, **not encrypted production
secret storage**.

For phrase-and-reply customization, you can stop here. To keep MaddyBot's matching
but add actions, follow
[Return several actions from the sample](../README.md#return-several-actions-from-the-sample).
For a separate bot, continue below.

## Understand the flow

```text
Jetstream event
    -> ReaderWorker -> your IBotPolicy -> list of BotAction intents
    -> database transaction: save every intent and event progress
    -> SenderWorker -> action handler -> Bluesky
```

An **intent** describes what you want to do, not something already done. An
**outbox** is the durable database queue of those intents. A **checkpoint** is
the SDK's complete replay position and request state, used after a restart.
A **lease** is time-limited permission for one process to read and send; the
`OwnershipWorker` initializes storage and renews that permission. This matters
when old and new deployments overlap.

The policy chooses actions without network writes. The reader commits the whole
event's action batch together with progress before consuming the next relevant
event. The sender processes saved actions independently, with durable retry
schedules and rate accounting. Saving a batch is atomic; performing several
remote actions is not. A failed like does not undo a reply already sent.

The legacy database table name `reply_work` is retained for migration
compatibility. It holds versioned action intents now; it does not restrict you
to replies. Do not rename tables or clear progress when changing policy.

## Create your own host

The easiest starting point is the sample. If you want a separate minimal web
host, create it **inside this repository** so it inherits the package/feed and
build conventions. Run these commands from the repository root:

```powershell
dotnet new web --name MyBot --output src\MyBot --framework net10.0
dotnet add src\MyBot\MyBot.csproj reference src\idunno.Bluesky.BotSdk\idunno.Bluesky.BotSdk.csproj
dotnet user-secrets init --project src\MyBot
```

Use C# 13, not preview features. If you move the application to another repository,
also carry over the required package source configuration; a project reference
to this SDK needs access to its exact dependencies. Do not guess a future NuGet
package name or version.

Replace the generated `Program.cs` with the host below and add the `MyPolicy.cs`
example in [Write a policy](#write-a-policy). The host deliberately uses the
same PostgreSQL setup as MaddyBot:

```csharp
using System.Text;

using idunno.Bluesky.BotSdk;
using idunno.Bluesky.BotSdk.DataProviders;

using MyBot;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

BotOptions options = builder.Configuration
    .GetRequiredSection("Bot")
    .Get<BotOptions>()
    ?? throw new InvalidOperationException("The Bot section is required.");

options.Validate();

string connectionString = BotOptions.DatabaseConnectionString(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    applicationName: "MyBot");

BotHosting.ConfigureConsoleLogging(builder.Logging);

builder.Services.AddPostgreSqlBotStorage(
    connectionString,
    options.DatabaseCaCertificate);

builder.Services.AddBlueskyBot<MyPolicy>(
    options,
    httpUserAgent: "MyBot/1.0");

WebApplication app = builder.Build();

app.MapGet("/", (ActionStatistics statistics) =>
    Results.Text(
        statistics.Summary,
        contentType: "text/plain",
        contentEncoding: Encoding.UTF8));

app.MapBlueskyBotHealthEndpoints();

await app.RunAsync();
```

`AddBlueskyBot<TPolicy>` registers the policy, the three workers, authentication,
rate accounting, and all five built-in handlers. It registers the supplied
`BotOptions` for policy injection. Do not register a second set of workers.
Storage must be registered **first**. `httpUserAgent` identifies the application,
not the account; replace `"MyBot/1.0"` with your own identity.

To run this new host safely, save settings to **its** user-secrets project:

```powershell
$connectionString = .\src\idunno.Bluesky.BotSdk\.artifacts\create-postgresql.ps1 -HostType Docker
dotnet user-secrets set --project src\MyBot "ConnectionStrings:Bot" $connectionString
dotnet user-secrets set --project src\MyBot "Bot:Did" "did:plc:<your-bot-did>"
dotnet user-secrets set --project src\MyBot "Bot:ArchiveApiKey" "<jetstream-archive-api-key>"
dotnet user-secrets set --project src\MyBot "Bot:DeveloperMode" "true"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8080'
dotnet run --project src\MyBot --no-launch-profile
```

Replace placeholders with actual values; the DID must parse as a valid DID.
The database helper refuses existing containers/volumes, so do not run it again
to obtain an existing database's password. Use a distinct development database
for a distinct bot, and keep the original secret connection string securely.
For WSL, use `-HostType WSL` with a configured WSL container engine.

`BotOptions` supplies conservative defaults for omitted runtime settings.
This example uses a constant match phrase in its policy, not MaddyBot's
`WatchPhrases`/`Replies` configuration. To make those configurable, derive your
own options type as the sample does, validate it, and register that derived type
if your policy constructor takes it.

## Choose storage

All three built-in providers are implemented. Registration extensions live in
`idunno.Bluesky.BotSdk.DataProviders`. Choose **one** before calling
`AddBlueskyBot`:

```csharp
builder.Services.AddPostgreSqlBotStorage(
    connectionString,
    caCertificate: options.DatabaseCaCertificate);
```

```csharp
builder.Services.AddMySqlBotStorage(
    connectionString);
```

```csharp
builder.Services.AddSqliteBotStorage(
    "Data Source=bot-state.db");
```

These snippets are alternatives, not a list to register together.

| Provider | Suitable use | Important setup |
|---|---|---|
| PostgreSQL | Local development or a database-backed hosted deployment; the MaddyBot default | Production verifies TLS with `SSL Mode=VerifyFull` and a trusted CA. The sample accepts PEM CA content via `Bot:DatabaseCaCertificate`. |
| MySQL | An application with a durable MySQL server | Configure server identity verification and trusted TLS outside local development. Supply a MySQL connection string, not a PostgreSQL one. |
| SQLite | Local development or a single host with persistent local disk | Use a persistent file. Do not use an in-memory database, a network filesystem, or an ephemeral container/platform directory. |

When switching the host above to MySQL or SQLite, also replace its call to
`BotOptions.DatabaseConnectionString`: that helper specifically parses and
hardens **PostgreSQL** connection strings. Read your provider's
`ConnectionStrings:Bot` value instead and apply that provider's secure settings.
Changing DI registration alone is not sufficient.

Database creation and test helpers are under:

```text
src\idunno.Bluesky.BotSdk\.artifacts\create-postgresql.ps1
src\idunno.Bluesky.BotSdk\.artifacts\create-mysql.ps1
src\idunno.Bluesky.BotSdk\.artifacts\create-sqlite.ps1
src\idunno.Bluesky.BotSdk\.artifacts\TestDatabaseProviders.ps1
```

Use `Get-Help <script-path> -Detailed` to check each helper's parameters before
running it. Use disposable databases for tests; never point test commands at
your running bot's database.

For the complete suite, including all three isolated storage providers and
non-provider tests, run from the repository root:

```powershell
.\src\idunno.Bluesky.BotSdk\.artifacts\TestDatabaseProviders.ps1 -AllTests
```

You may instead register your own singleton implementation:

```csharp
builder.Services.AddSingleton<IDatabaseProvider, MyDatabaseProvider>();
builder.Services.AddBlueskyBot<MyPolicy>(
    options,
    httpUserAgent: "MyBot/1.0");
```

There is no provider enum or required provider base class. The runtime uses the
registered singleton without replacing it. A custom provider must implement the
interface's complete contract: transactional batch/progress persistence, full
checkpoints and original request configuration, source/action identity
uniqueness, ownership fencing, uncertain-work recovery, credentials, rate
budgets, and safe pruning. It is not just a queue implementation.

DigitalOcean App Platform has ephemeral local files. Keep the README deployment
on PostgreSQL; do **not** replace it with a SQLite file in the image.

## Write a policy

`IBotPolicy.Create` receives an event and the **durable eligibility start time**.
That time survives restarts; do not replace it with a fresh clock reading in
your policy. Return `[]` for an ignored event, or an `IReadOnlyList<BotAction>`
for a match. Never log in or call remote write APIs here.

This standalone `MyPolicy.cs` replies and likes a matching post. It uses a
simple case-insensitive substring rule, not MaddyBot's separator and
`.NET`/`dotnet` normalization:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using idunno.AtProto;
using idunno.AtProto.Jetstream;
using idunno.AtProto.Repo;
using idunno.Bluesky;
using idunno.Bluesky.BotSdk;

namespace MyBot;

internal sealed class MyPolicy(BotOptions options) : IBotPolicy
{
    private static JsonTypeInfo<Post> PostMetadata { get; } =
        (JsonTypeInfo<Post>)BlueskyJsonSerializerOptions.Default.GetTypeInfo(typeof(Post));

    public IReadOnlyList<BotAction> Create(
        JetstreamEvent item,
        DateTimeOffset startedAt)
    {
        if (item is not JetstreamCommitEvent
            {
                IsSyncBackfill: false,
                Commit.Operation: JetstreamCommitOperation.Create
            } commit ||
            commit.Commit.Collection.ToString() != "app.bsky.feed.post" ||
            options.Excludes(commit.Did.ToString()))
        {
            return [];
        }

        DateTimeOffset witnessedAt = item.WitnessedAt
            ?? throw new InvalidDataException("A v2 commit has no witnessed timestamp.");

        if (witnessedAt < startedAt)
        {
            return [];
        }

        JsonElement record = commit.Commit.Record
            ?? throw new JsonException("Create event has no record.");
        Post post = record.Deserialize(PostMetadata)
            ?? throw new JsonException("Create event has a null post.");

        // CUSTOMIZE DETECTION: replace only the match rule to start with.
        if (!(post.Text ?? "").Contains(
            ".NET Aspire",
            StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        long sequence = item.Sequence
            ?? throw new InvalidDataException("A v2 event has no sequence.");
        AtUri uri = new($"at://{commit.Did}/{commit.Commit.Collection}/{commit.Commit.RKey}");
        Cid cid = commit.Commit.Cid
            ?? throw new InvalidDataException("Create event has no CID.");
        StrongReference source = new(
            uri,
            cid);
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        // CUSTOMIZE ACTIONS: choose the complete payload before it is saved.
        Post reply = new(
            text: "Thanks for sharing!",
            createdAt: createdAt,
            reply: new ReplyReferences(
                source,
                post.Reply?.Root ?? source));

        return
        [
            BotActions.Reply(
                source,
                sequence,
                reply),
            BotActions.Like(
                source,
                sequence,
                createdAt: createdAt)
        ];
    }
}
```

Keep the normal-create, post-collection, no-sync-backfill, self/excluded-author,
and witnessed-time filters when changing detection. Author `createdAt` is not
a trusted eligibility clock. `options.Excludes()` checks the bot's DID and
`NeverReplyToDids`; despite that legacy option name, use it for every action.
The sender also rechecks exclusions before writing.

Use the generated SDK `JsonTypeInfo<Post>` as shown, not reflection-based JSON
overloads. The complete post, original URI/CID, chosen creation time, and action
keys are saved once. A reply's parent is the source strong reference (URI plus
CID); its root is the source post's existing `Reply.Root`, or the parent for a
top-level post. Do not replace the root with the parent when replying in a thread.

## Choose built-in actions

In the policy above, `source` is the exact source post URI/CID, `sequence` is
the event sequence (not a timestamp), and `createdAt` is chosen once. Add or
remove factories in the returned collection:

```csharp
return
[
    BotActions.Reply(
        source,
        sequence,
        reply),
    BotActions.Like(
        source,
        sequence,
        createdAt: createdAt),
    BotActions.Repost(
        source,
        sequence,
        createdAt: createdAt),
    BotActions.Bookmark(
        source,
        sequence)
];
```

You do not need to return all these actions. Start with one and check simulations
before enabling real effects. Configuration alone does not select them.

| Factory | What it does | Recovery behavior |
|---|---|---|
| `Reply(source, sequence, reply, actionKey = "default")` | Publishes the complete supplied reply | Uses a fixed record key and verifies the saved record after an uncertain result; observes target deletion and reply restrictions. |
| `Like(source, sequence, createdAt = null, actionKey = "default")` | Likes the source post | Uses a fixed record key; respects viewer state when the bot already liked the post. |
| `Repost(source, sequence, createdAt = null, actionKey = "default")` | Reposts the source post | Uses a fixed record key; respects viewer state when the bot already reposted the post. |
| `Bookmark(source, sequence)` | Privately bookmarks the source post for the bot account | Uses the service-managed bookmark API, which has no caller-selected remote record key. |
| `AddAuthorToList(source, sequence, listUri, createdAt = null)` | Adds the source post's author to an existing bot-owned curation list | Uses a fixed key derived from list URI + author DID and scans for existing membership, including manually added members. |

For likes, reposts, and list membership, omitting `createdAt` chooses UTC time
when the factory is called. That value is then persisted; retries do not choose
a new time.

### Add authors to a list

First create a **curation list owned by the bot account** using Bluesky's list
tools. Use its AT URI, for example
`at://did:plc:<your-bot-did>/app.bsky.graph.list/<list-record-key>`, not a
`https://bsky.app/...` web link or a list-item URI. Replace both placeholders.
The list must have the `CurateList` purpose; moderation lists and starter-pack
reference lists are not accepted.

Add this factory to the policy's result:

```csharp
BotActions.AddAuthorToList(
    source,
    sequence,
    listUri: new AtUri("at://did:plc:<your-bot-did>/app.bsky.graph.list/<list-record-key>"),
    createdAt: createdAt)
```

The subject is the source **author**, not the post. Different matching posts by
the same author reuse the list/author membership key. This action does not create
a list, mute or block anyone, or subscribe to a list. Existing membership is
reconciled rather than intentionally duplicated.

Several queued intents can therefore identify the same fixed membership URI.
Each intent keeps its own complete chosen payload; reconciliation verifies the
same list and author, without requiring identical `createdAt` values from
different triggering posts.

### Bookmark limitations

Bookmarks are private, service-managed effects, not repository records you can
create at a chosen `rKey`. Reconciliation scans **all** bookmark pages before
concluding that the source URI is absent. Failed reads are not evidence of
absence. Execution preserves the original source CID saved in the intent.

Because the service does not accept a caller-selected idempotency key, a scan
and a subsequent create cannot be one atomic operation. Do not claim absolute
duplicate-free or exactly-once behavior, especially after a lost response or
concurrent manual changes.

## Action identity and recovery

The durable deduplication identity is:

```text
SourceUri + ActionKind + ActionKey
```

Different kinds are deduplicated independently. To intentionally produce
several records of the **same** kind for one source, supply different stable
policy keys where the factory supports `actionKey`:

```csharp
return
[
    BotActions.Reply(
        source,
        sequence,
        firstReply,
        actionKey: "acknowledgement"),
    BotActions.Reply(
        source,
        sequence,
        secondReply,
        actionKey: "follow-up")
];
```

Those variables must each contain a complete reply with correct thread
references. Keys describe the policy's intent; do not generate random keys on
each replay or reuse one key for different intents. Kind/key names are
case-sensitive and at most 128 characters. Bookmark has a default identity;
author-list membership derives its identity from the list. Those two factory
signatures do not take an arbitrary `actionKey`.

The fixed record key, complete serialized payload, and payload version are
persisted before sending. An attempt is marked `uncertain` before the remote
write. After a timeout or lost response, the appropriate handler reconciles the
unchanged saved intent before deciding whether another execution is safe.
Repository-record handlers can use a known URI; bookmarks and existing list
membership require exhaustive scans. Unknown outcomes must remain retryable,
not be recorded as success.

This reduces duplicates but cannot make the database and Bluesky one transaction.
Restoring an old backup or rewinding progress may discard known keys and
receipts; reconcile remote state before doing so. Do not delete production
outbox rows or change saved keys just to unblock a send.

Changed policies apply to newly ingested work after restart. They do not rewrite
saved actions or revisit consumed posts. Completed rows can be pruned only after
the durable processed-through boundary covers their source sequence;
pending/uncertain work is retained.

## Custom actions

You only need a custom handler for an action the built-ins do not implement.
Start with built-ins if possible: reconciliation is the hard part of a new
remote effect, not just calling an API.

1. Define a complete payload and source-generated JSON metadata.
2. Build an intent with `BotActions.Create<TPayload>` in your policy.
3. Implement `IBotActionHandler` for its stable `Kind` and `PayloadVersion`.
4. Register that handler as a **singleton before** `AddBlueskyBot`.

For example, an application-owned payload and metadata can look like this:

```csharp
using System.Text.Json.Serialization;

namespace MyBot;

internal sealed record NotificationPayload(string Message);

[JsonSerializable(typeof(NotificationPayload))]
internal partial class MyJsonContext : JsonSerializerContext
{
}
```

Inside the policy, the factory call is:

```csharp
BotAction notification = BotActions.Create(
    kind: "my-notification",
    source: source,
    sequence: sequence,
    payload: new NotificationPayload("A matching post was observed."),
    metadata: MyJsonContext.Default.NotificationPayload,
    actionKey: "matching-post",
    payloadVersion: 1);
```

Include `notification` in the returned collection only after implementing and
registering a handler for it. The example defines serialization, **not** a
ready-made notification transport. No factory call performs the remote action.

Registration, using your implemented `NotificationHandler`:

```csharp
builder.Services.AddSingleton<IBotActionHandler, NotificationHandler>();
builder.Services.AddBlueskyBot<MyPolicy>(
    options,
    httpUserAgent: "MyBot/1.0");
```

Each `(Kind, PayloadVersion)` pair must have exactly one registered handler.
Duplicate registrations throw a clear `InvalidOperationException`; registration
order does not silently override a built-in. Prefer a unique custom kind/version.
If deliberately replacing a built-in handler, remove its matching service
descriptor **after** `AddBlueskyBot` and retain compatibility with saved intents.

The handler contract is:

| Member | Required behavior |
|---|---|
| `Kind` | Stable, case-sensitive kind matching the factory's `kind`. Do not reuse built-in kinds. |
| `PayloadVersion` | The persisted version this handler supports. Preserve support for already queued versions during upgrades. |
| `Validate(BotAction action)` | Validate complete saved data without network operations. Do not substitute a newly generated payload or key. |
| `ReconcileAsync(BotAction action, IBotActionRemote remote, CancellationToken cancellationToken)` | Return a confirmed `BotActionResult`, or `null` **only after proving absence well enough that execution is safe**. A timeout, partial page scan, or unsuccessful response is not safe absence. |
| `ExecuteAsync(BotAction action, IBotActionRemote remote, CancellationToken cancellationToken)` | Execute the unchanged saved intent and return a confirmed `BotActionResult`. Unknown outcomes and transport failures must throw. |

`BotActionResult` contains `Succeeded`, `RemoteUri`, and a nonsecret `Reason`.
Use `Succeeded=false` only for confirmed terminal rejection, not for a transient
failure. Reconciliation may confirm either an existing effect or a terminal
rejection. Honor cancellation, and retain enough original information to
reconcile a lost response without inventing a new identity.

The runtime supplies authenticated `IBotActionRemote` for Bluesky operations.
Its public `BlueskyAgent Agent` property lets custom handlers call SDK operations
through the same protected, durably accounted transport. The runtime owns that
agent: do not dispose it, log in again, or replace its credentials. The
`FindRecordAsync` and `CreateRecordAsync` helpers support only built-in action
kind strings; do not pass arbitrary custom kinds to them. For custom Bluesky
actions use `remote.Agent` SDK methods; external services can use DI-injected
clients. Keep the protected SDK HTTP transport in place.

For custom services, design their authentication,
idempotency, request accounting, and recovery explicitly; do not assume the
Bluesky rate limiter covers arbitrary clients. Check unsuccessful server results
and throw appropriately. Never return success merely because a request was sent.

Use explicitly generated `JsonTypeInfo<TPayload>` as shown. Do not switch to
reflection-based serialization or suppress AOT/trimming warnings. Test payload
compatibility and lost-response recovery before using the handler in real mode.

## Operate safely

For the separate `src\MyBot` host, stop the developer run first. After explicitly
confirming the account and policy, provision a **different** real-mode database
and use its connection string below. Do not reuse the earlier development
connection string or change the mode of its durable state:

```powershell
dotnet user-secrets set --project src\MyBot "ConnectionStrings:Bot" "<separate-real-mode-database-connection-string>"
dotnet user-secrets set --project src\MyBot "Bot:Handle" "<bot.handle>"
dotnet user-secrets set --project src\MyBot "Bot:AppPassword" "<bot-app-password>"
$key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet user-secrets set --project src\MyBot "Bot:CredentialEncryptionKey" $key
dotnet user-secrets set --project src\MyBot "Bot:DeveloperMode" "false"
dotnet run --project src\MyBot --no-launch-profile
```

Use the bot account's app password, not its main password. Generate this key
once for the new real-mode database, then retain it for future restarts; do not
rerun the key-generation line on every launch. The DID/archive key and local
environment settings from the earlier setup still apply. For MaddyBot, use
the [sample's real-mode commands](../README.md#deliberately-enable-real-actions)
instead.

- **Developer mode first.** Simulations count as completed actions in
  `ActionStatistics`; they are not proof of remote effects or permissions.
- **Separate durable identities.** Service, bot DID, and developer mode are
  bound to saved state. Use separate databases for dry runs and production,
  rather than clearing a database to bypass a mismatch.
- **Explicit production confirmation.** Check the account, policy, exclusions,
  and list ownership. Configure `Bot:Handle`, `Bot:AppPassword`, and a stable
  base64-encoded 32-byte `Bot:CredentialEncryptionKey`, then explicitly set
  `Bot:DeveloperMode=false`. In hosted environments the corresponding names are
  `Bot__Handle`, `Bot__AppPassword`, `Bot__CredentialEncryptionKey`, and
  `Bot__DeveloperMode`.
- **Keep secrets recoverable and private.** Refresh credentials are encrypted
  in the database. Back up the encryption key separately and keep it unchanged
  across deployments. Never log credentials or secret connection strings.
- **Observe action outcomes.** `ActionDispatcher` logs carry the action kind,
  source URI, remote URI when available, and outcome reason. Completed-action
  counts include confirmed reconciliations and simulations, not failed attempts
  or terminal rejections.
- **Probe workers, not the last reply.** Map `/health/live` as the restart probe
  and inspect `/health/dependencies` separately for dependency recovery.
- **Leave recovery intact.** Resume complete checkpoints and original filters.
  Replay can repeat events; the runtime's processed boundary and action
  identities suppress already handled work. Archive availability/compaction
  still limit recovery; missing historical records cannot be recreated.
- **Treat `--lookback` as deliberate data loss.** MaddyBot's CLI uses it for a
  forward-only history skip, not routine recovery. It retains queued/uncertain
  actions. A custom host can pass a fixed `lookbackCutoff` to `AddBlueskyBot`,
  but must explicitly parse/authorize that choice; registration alone does not
  add the sample's CLI.
- **Test without real writes.** Add policy cases and fake-remote recovery cases.
  Run database coverage against disposable databases, not a running user's
  schema. See the [test instructions](../README.md#tests).

For Native AOT, use the sample's generated binding/JSON patterns and validate
your intended native target. The tested Docker deployment is Linux x64; a
managed development run does not establish native compatibility on every OS.
If enabling AOT analyzers without setting `PublishAot`, also enable the request
delegate generator (`EnableRequestDelegateGenerator`) for the host's `MapGet`
delegate; do not suppress trimming or dynamic-code warnings.

## Troubleshooting

| Symptom | Check |
|---|---|
| Startup rejects `Bot:Did` | Supply the actual complete account DID, not a handle or the example placeholder. It is required even in developer mode. |
| Archive authentication fails | `Bot:ArchiveApiKey` comes from the archive service, not Bluesky's app-password page. |
| Nothing matches | Create a new post from a different, nonexcluded account after eligibility starts. Check the actual phrase rule and that it is a normal create, not an update/backfill. |
| Logs simulate but no remote action appears | This is correct with `DeveloperMode=true`. Do not disable it until you deliberately choose a separate real-mode database and configure credentials. |
| Database identity mismatch | Check service, DID, developer mode, and connection string. Do not erase production state to suppress the error. |
| List action is rejected | Verify the AT URI points to an existing bot-owned `CurateList`, not a web link, moderation list, starter-pack reference list, or list-item record. |
| No immediate send after startup | A new lease owner waits for previous bounded requests to drain; rate budgets, cooldowns, and retry schedules may also delay work. Check logs rather than resetting state. |
| SQLite work disappears after deployment | Check that the file is on persistent local disk, not ephemeral platform/container storage. App Platform examples must use PostgreSQL. |
| Queued actions still use old text | Saved payloads intentionally remain unchanged. Policy/config changes affect future ingestion, not existing work. |
| Recovery cannot reach old history | Inspect the explicit archive-coverage failure. Do not assume endless replay recovery or silently jump to current time. |

Return to the [sample README](../README.md) for detailed replay policies, rate
limits, graceful shutdown, logging, Docker, and DigitalOcean instructions.
