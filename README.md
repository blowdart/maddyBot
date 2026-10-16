# maddyBot

A C# 13 / .NET 10 Bluesky bot that watches Jetstream v2 for configurable
substrings, defaulting to `.NET Aspire`, using `StringComparison.OrdinalIgnoreCase`.
Runs of ordinary spaces, tabs, ASCII hyphens (`-`), and underscores (`_`) in posts
and phrases are normalized to one space before matching. `.NET  Aspire`,
`.NET\tAspire` (with a real tab), `.NET-Aspire`, and `.NET_Aspire` match.
Periods, exclamation marks, other punctuation, and line breaks remain barriers:
`.NET. Aspire`, `.NET! Aspire`, and
`My code is written in .NET. Aspire works for me` do not match `.NET Aspire`.
The leading dot stays literal; `NET Aspire` does not match.
`dotnet` and `.NET` are equivalent, case-insensitively, in both posts and watch
phrases. `dotnet-Aspire` matches the default phrase, but `dotnet. Aspire` does not.
Its default reply is:

> It's just "Aspire" 🤬

Built on the published **`idunno.Bluesky.BotSdk`** NuGet package, the ASP.NET Core
host runs a replay reader and a durable-outbox sender. An
**outbox** is database work saved before attempting a remote action, so a restart
does not forget it. The **`idunno.Bluesky.BotSdk.PostgreSQL`** package provides
storage through Npgsql and parameterized SQL; there is no Entity Framework.
This repository contains the sample application, not a copy of the SDK.

**New here?** Follow [Local development](#local-development) for a safe, no-write
run of the sample, then [Make this sample your own](#make-this-sample-your-own).
For a step-by-step SDK host, all five built-in actions, and custom actions, see
the [upstream Bot SDK guide](https://github.com/blowdart/idunno.Bluesky/blob/version/v8.0.0/docs/docs/bots/durableBotSdk.md).

## Bot SDK and sample

The `idunno.Bluesky.BotSdk` package owns the reusable watcher-and-action runtime:
archive/live replay, full checkpoints, durable action work, database access,
ownership leases, saved authentication, rate accounting, sending/reconciliation,
diagnostics, and memory-only health endpoints. It does not contain MaddyBot's
phrase matching, reply text, or tags.

| Package | Role in this sample |
|---|---|
| `idunno.Bluesky.BotSdk` | Hosted workers, durable actions, authentication, replay recovery, diagnostics, and health probes. |
| `idunno.Bluesky.BotSdk.PostgreSQL` | PostgreSQL storage, schema initialization/migration, and the host-owned connection pool. |
| `idunno.AtProto` and `idunno.Bluesky` | Protocol and Bluesky types used by the application and its policy. |

`src\MaddyBot` is the consuming application: its `BotOptions` extends the SDK's
options with watch phrases, reply choices, exclusions, and CA settings; `MaddyReplyPolicy` implements
`IBotPolicy`, and `Program.cs` owns configuration, CLI arguments, HTTP identity,
and web hosting. `BotRegistration.cs` connects the policy to the packaged runtime,
selects PostgreSQL storage with the configured CA certificate, and replaces the
built-in reply handler with the sample's queued-author exclusion wrapper.
`Program.cs` maps the SDK health endpoints. Runtime and provider implementation
changes belong in the [upstream SDK repository](https://github.com/blowdart/idunno.Bluesky/tree/version/v8.0.0),
not in this sample.

A policy returns `IReadOnlyList<BotAction>`: `[]` for an ignored event, or one or
more complete intents built with `BotActions`. The reader persists every action
in that event's batch atomically with replay progress. The sender then handles
each action independently; the remote effects are **not** an atomic batch.
Policies must not perform remote writes. Choose content, creation time, and
thread references before saving; retries reuse the saved payload and fixed key.

`AddBlueskyBot` automatically registers handlers for **replies, likes, reposts,
private bookmarks, and adding the source author to an existing bot-owned curation
list**. Application-defined actions use `BotActions.Create<TPayload>` and a
singleton `IBotActionHandler`; see the [upstream guide](https://github.com/blowdart/idunno.Bluesky/blob/version/v8.0.0/docs/docs/bots/customBotActions.md).
MaddyBot intentionally creates only a reply for each eligible match. For a new
host, alternative storage providers, or custom provider contracts, follow the
[upstream SDK guide](https://github.com/blowdart/idunno.Bluesky/blob/version/v8.0.0/docs/docs/bots/durableBotSdk.md).

## Make this sample your own

For a bot that watches different phrases and posts different replies, start with
`src\MaddyBot\appsettings.json`: change `Bot:WatchPhrases` to what you want to
detect and `Bot:Replies` to what you want to say. No C# changes are needed for
that. Set up your own bot account, secrets and database using the instructions
below, and keep `Bot:DeveloperMode` enabled while checking the results.

**Also change the HTTP User-Agent** in `src\MaddyBot\Program.cs`: replace
`httpUserAgent: "maddyBot/1.0"` with your own bot's name and version, for example
`"MyWatcherBot/1.0"`. This identifies your application to the services it calls;
it is not the bot account's handle or password.

For changes beyond configured phrases and reply text, use these entry points.
The same places are marked with customization comments in the code:

| What you want to change | Where to change it |
|---|---|
| Phrases, reply texts, or authors to ignore | `Bot:WatchPhrases`, `Bot:Replies`, and `Bot:NeverReplyToDids` in `src\MaddyBot\appsettings.json`. See [Watch phrases, replies, and exclusions](#watch-phrases-replies-and-exclusions) for configuration overrides. |
| How a post counts as a match | The **CUSTOMIZE DETECTION** block in `src\MaddyBot\ReplyPolicy.cs`, inside `ReplyPolicy.Create()`. `NormalizePhrase()` defines the accepted separators and the `.NET`/`dotnet` alias; change those rules if they do not suit your bot. |
| How to build a reply after a match, such as choosing text from the source post | The **CUSTOMIZE REPLY** block in `ReplyPolicy.Create()`, where `Post reply` is constructed. This runs before the reply is saved, not on each send attempt. |
| Add likes, reposts, bookmarks, or author-list membership | Adapt the policy to return more `BotAction` intents. See the [upstream SDK guide](https://github.com/blowdart/idunno.Bluesky/blob/version/v8.0.0/docs/docs/bots/durableBotSdk.md). No dispatcher changes are needed for built-in actions. |
| Add an application-defined remote action | Implement `IBotActionHandler` and use `BotActions.Create<TPayload>`; see [Custom actions](https://github.com/blowdart/idunno.Bluesky/blob/version/v8.0.0/docs/docs/bots/customBotActions.md). Runtime sending and recovery remain package-owned. |
| The HTTP application identity | The **CUSTOMIZE HTTP IDENTITY** comment in `src\MaddyBot\Program.cs`, above `httpUserAgent`. |

The basic flow is **Jetstream -> ReaderWorker -> saved actions in a database ->
SenderWorker -> Bluesky**. `OwnershipWorker` decides which running copy may work.
Workers depend on the singleton `IDatabaseProvider` chosen by the application
through DI; this sample registers the packaged PostgreSQL provider.
`Program.cs` explains how these three workers fit together. Keep detection and
action construction free of remote writes: the reader must save each intended
action before the sender attempts it. Keep the saved record key, content,
creation time and thread references unchanged on retries.

### Adapt the sample to return several actions

`MaddyReplyPolicy.Evaluate()` adapts the sample's nullable reply helper into an
empty batch or a batch containing only the reply. The sample does not perform
other actions. To add a built-in action, use the helper overload that also
returns the source reference, then include both the reply and the new action in
the returned collection. For a standalone multi-action policy, see the
[SDK example](https://github.com/blowdart/idunno.Bluesky/tree/version/v8.0.0/samples/Samples.BotSdk).

Do not remove the normal-create, post-collection, sync-backfill, self/excluded-DID,
or witnessed-start-time checks when adapting the sample. Reply roots remain the
source post's existing `Reply.Root`, or the parent for a top-level post.
Built-in action recovery is already supplied; do not replace the remote create
call by hand. Multiple actions can complete at different times, and a terminal
rejection of one does not roll back actions already completed.

Add examples for your new matching/reply rules in
`tests\MaddyBot.Tests\PolicyTests.cs`. Queued exclusions are covered in
`ReplyHandlerTests.cs`; sample service registration is covered in
`BotRegistrationTests.cs` without opening a database. Provider and internal
runtime tests live upstream with the SDK. Changed settings apply on
restart; they do not rewrite already queued replies or revisit consumed posts.
Use separate databases for development and production, rather than clearing
stored progress to test a new rule. The legacy database table name `reply_work`
is retained for safe migration; its rows now represent versioned actions, not
only replies.

## Build and packages

Install the .NET 10 SDK. Docker is needed for local PostgreSQL and the native
container build, not for the sample test suite. Restore the published packages
and build the sample directly; no SDK source checkout or project reference is
required:

```powershell
dotnet restore MaddyBot.slnx
dotnet build MaddyBot.slnx --no-restore
dotnet test --solution MaddyBot.slnx --no-build
```

Package versions are centralized in `Directory.Packages.props`; feed configuration
and package-source mapping are centralized in `nuget.config`. Direct idunno
packages are pinned to `8.0.0-prerelease.g699396cf24`, corresponding to
[`699396cf24f84dc44b393196db18a73646fe1fef`](https://github.com/blowdart/idunno.Bluesky/commit/699396cf24f84dc44b393196db18a73646fe1fef).
The application references the core BotSdk and PostgreSQL provider packages.
There is no SQLite dependency or separate ASP.NET Core BotSdk package.
These are NuGet packages published to the public MyGet feed at
`https://www.myget.org/F/blowdart/api/v3/index.json`, not nuget.org for this pinned
prerelease. The committed `nuget.config` maps idunno packages to that feed;
other dependencies come from nuget.org. Dependabot uses the same source.
This repository does not maintain per-project package lock files.

Upgrade packages through `Directory.Packages.props`, keeping the directly
referenced idunno packages on the same verified published version. Review
upstream replay checkpoint compatibility, stored-state migration behavior, and
the SDK-specific generation-mismatch classifier before deploying an upgrade.

### Existing database rollout

For an existing deployment, stop all old instances and back up the database and
credential encryption key before first packaged startup. Reuse the same database,
identity, Jetstream service, developer mode, and CA settings; do not reset durable
state. The packaged provider migrates legacy tables in place.
New replies receive a lexicon-valid TID from `TimestampIdentifier.Next()` before
enqueue; generic keys such as `maddy-<GUID>` are not valid post record keys.
Existing queued work keeps its saved key and is never silently re-keyed.

### Versioning

MaddyBot uses [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
to stamp its assembly and executable with version information derived from Git.
It is an application build dependency, not part of the BotSdk runtime. The committed
`version.json` specifies `1.0`; no Git tags are required. CI fetches full
history so version height and commit identity are calculated for the build.

Version intent is recorded in `version.json`, not injected during release builds.
To start the next prerelease line, commit `"version":
"1.1-alpha"`. Builds from `main` are configured as public releases; other refs
include a commit identifier so their versions remain distinct. The informational
version includes the Git commit ID. Managed builds in a Git working tree mark
uncommitted changes `dirty`; the Docker build stamps the supplied commit ID
without copying Git metadata into its context.

### Native AOT

The application enables `PublishAot` and generated configuration binding. Normal
`dotnet run`, builds and tests still use .NET; publishing produces a self-contained
native executable. The Dockerfile builds and runs that executable for **Linux
x64**, using the native compiler tools in the build stage and a `runtime-deps`
image rather than an ASP.NET runtime image. No .NET runtime is needed in the
resulting container. Use Docker's Linux-container mode:

```powershell
$commit = git rev-parse HEAD
$ref = git symbolic-ref HEAD
docker build --platform linux/amd64 --build-arg GITHUB_ACTIONS=true --build-arg "GITHUB_SHA=$commit" --build-arg "GITHUB_REF=$ref" --tag maddybot:dev .
```

The Docker build does not copy `.git` into its context or image. Pass the commit
and ref as build arguments so the native executable retains its commit identity.
Because the build context has no Git history, the Docker build cannot calculate
version height; CI's full-history managed build does.

The package owns generated JSON metadata for credentials, health responses,
posts, replay requests, and complete checkpoints. Stored property names and
probe response shapes are preserved; no database reset is needed. The sample
uses `BlueskyJsonSerializerOptions` for SDK types; do not copy internal runtime
JSON helpers or use reflection-based serializer overloads in the application.

Native publish must run on the target operating system with its native toolchain.
For a different architecture or OS, restore/publish for that runtime identifier,
update the project's `RuntimeIdentifiers` deliberately, and validate the resulting executable; do
not assume the tested Linux-x64 image can run on ARM or Windows. A Windows native
publish also needs the Visual Studio C++ build tools. AOT/trimming warnings are
not suppressed by this application.

The project declares both `win-x64` for local Windows development and `linux-x64`
for the container, so an ordinary restore works on either host. The
native runtime checks performed for this sample target Linux x64; Windows native
publishing has not been verified.

`.editorconfig`, `.ssh-allowed-signers`, and `.ssh-allowed-signers.md` are copied
from `blowdart/idunno.Bluesky`. The signers guide explains per-clone verification.

## Local development

The development database uses **PostgreSQL 16**. Match its major version to the
database offered by your hosting provider. If that version changes, update your
deployment configuration and
`scripts\create-postgresql.ps1` before recreating or migrating local storage.

```powershell
$connectionString = .\scripts\create-postgresql.ps1 -HostType Docker
dotnet user-secrets set --project src\MaddyBot "ConnectionStrings:Bot" $connectionString
```

The script binds port **54329** to loopback only, waits for readiness, and uses the
named volume `maddybot-postgres16`. It generates a cryptographically random
256-bit password and outputs the complete connection string, including the password.
Treat that output as a secret; terminal transcripts may retain it.
The script refuses existing containers or volumes, because a new generated
password cannot change the credentials in an existing database.
`-HostType` is required: choose `Docker` or `WSL` (WSL containers
through `wslc`, requiring WSL 2.9.3 or later). The selected host must already be
configured and usable; there is no automatic fallback. Docker uses
`unless-stopped` restart behavior. WSL containers do not support that option;
start their retained container manually after restarting the host.
Use `127.0.0.1` in local connection strings to match the IPv4-only port binding;
`localhost` can resolve to an unbound IPv6 address.
`docker stop maddybot-postgres` / `docker start maddybot-postgres` preserve data.
Use `wslc container stop` / `wslc container start` for WSL containers.
Container environment inspection can reveal the database password; use only a
development password on a trusted machine. Never reuse production credentials.

Configuration comes from `appsettings.json`, .NET user secrets in Development,
and environment variables (last wins). The project already has a user-secrets ID:

```powershell
dotnet user-secrets set --project src\MaddyBot "Bot:Did" "did:plc:<your-bot-did>"
dotnet user-secrets set --project src\MaddyBot "Bot:ArchiveApiKey" "<jetstream-archive-api-key>"
dotnet user-secrets set --project src\MaddyBot "Bot:DeveloperMode" "true"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8080'
dotnet run --project src\MaddyBot --no-launch-profile -- --doctor
dotnet run --project src\MaddyBot --no-launch-profile
```

Substitute the complete account DID, not the placeholder. A DID is the account's
stable `did:plc:...` or `did:web:...` identifier, not its `@handle`; use your bot's
actual DID. Obtain the archive key from
[bsky.network](https://bsky.network/account#api-keys-section-heading).
User secrets are not encrypted; they are for local development, not production.
`--no-launch-profile` prevents local launch settings from overriding the HTTP
address above. The optional `--doctor` invocation validates the bound
configuration and checks database connectivity without initializing schema,
acquiring ownership, or starting workers.

**Developer mode is enabled by default.** It needs the database, archive key, and
bot DID, but does not log in or perform remote actions. It records `simulated`
outcomes and emits structured simulation logs including the action kind.
You do not need an app password or credential encryption key for this first run.
Keep the bot running and create a new matching post from a different account;
old posts and your bot's own posts are intentionally ignored. Check console
simulation logs and `http://127.0.0.1:8080/health/live`. The root page counts
completed actions, including simulations, not real writes alone.

Use **separate databases for developer mode and production**. Service, bot DID,
and developer mode are bound to durable state; changing them with an existing
database fails explicitly. A dry run must not silently consume real reply work.
Never clear production state just to bypass that safety check.

### Deliberately enable real actions

Stop the development run. **Confirm that you want this account to perform the
actions your policy returns**, check phrases and exclusions, and provision a
different database for the real run. Change `ConnectionStrings:Bot` to that
database; do not reuse the developer-mode database.

Create an app password in the bot account's Bluesky settings, not your main
account password. Configure it and a newly generated credential encryption key:

```powershell
dotnet user-secrets set --project src\MaddyBot "Bot:Handle" "<bot.handle>"
dotnet user-secrets set --project src\MaddyBot "Bot:AppPassword" "<bot-app-password>"
$key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet user-secrets set --project src\MaddyBot "Bot:CredentialEncryptionKey" $key
dotnet user-secrets set --project src\MaddyBot "Bot:DeveloperMode" "false"
dotnet run --project src\MaddyBot --no-launch-profile
```

The handle must identify the same account as `Bot:Did`. The archive API key and
app password are different secrets. Keep the encryption key stable across
restarts and backed up separately from the database; losing it prevents restoring
saved refresh credentials. Do not put any of these values in source control,
screenshots, logs, or shared shell transcripts. Hosted deployments should use
their platform's secret environment variables, not local user secrets.

### Watch phrases, replies, and exclusions

Set these arrays in `src\MaddyBot\appsettings.json`, or override indexed entries
through user secrets / environment variables:

```json
{
  "Bot": {
    "DeveloperMode": true,
    "WatchPhrases": [ ".NET Aspire" ],
    "Replies": [
      "It's just \"Aspire\" \ud83e\udd2c"
    ],
    "NeverReplyToDids": [
      "did:plc:7e6kocyzb77xkncplrkoojej"
    ]
  }
}
```

```powershell
dotnet user-secrets set --project src\MaddyBot "Bot:Replies:0" 'It is just Aspire.'
dotnet user-secrets set --project src\MaddyBot "Bot:WatchPhrases:0" '.NET Aspire'
dotnet user-secrets set --project src\MaddyBot "Bot:Replies:1" 'Please call it Aspire.'
dotnet user-secrets set --project src\MaddyBot "Bot:NeverReplyToDids:0" "did:plc:7e6kocyzb77xkncplrkoojej"
```

`Replies` must be nonempty and every entry must be a valid Bluesky post. A reply is
chosen uniformly at random **once, when enqueueing**, and its complete record,
creation time, and fixed record key are persisted. Changing the array does not
change pending work or pick a different reply on retry.

`WatchPhrases` must contain at least one nonblank phrase. Matching any phrase
enqueues one reply, even if several phrases match.
Separator normalization is only used for matching; original post content and
reply text remain unchanged.
Settings apply on restart and do not reprocess already-consumed posts.

The bot never enqueues replies to its own DID or an excluded DID. The sender
checks exclusions again before writing, including already queued work after a
restart. DIDs are compared ordinally. Settings are read at startup; restart to
apply changes. A newly excluded DID cannot undo a reply already sent or an
in-flight request admitted before shutdown.

The sample replaces the SDK's built-in reply handler with a wrapper around its
public `ReplyActionHandler` to retain this queued exclusion check. Uncertain
replies are reconciled before rejection so an already accepted remote reply
is still recognized. In developer mode the package bypasses action handlers:
previously queued excluded replies become `simulated` rather than `not-sent`,
with no remote writes. New excluded events are always filtered by the policy.

## Processing and recovery policies

| Policy | Behavior |
|---|---|
| First run | Durably record the first owned run's UTC eligibility time before accessing Jetstream. Inspect archive segment metadata to select a positive sequence boundary covering that time; never replay from sequence zero. |
| Restart | Restore the original service, request bounds/filters, complete `SnapshotCheckpoint`, and application processed-through boundary. |
| Eligibility | Normal create events only. Ignore updates, sync-backfill assertions, own posts, and excluded DIDs. Use the server's witnessed time for initial eligibility, not an author's untrusted `createdAt`. |
| Historical work | No age limit or backlog expiry for eligible posts since the recorded start. Downtime can create a large queue. |
| Malformed data | **Explicitly authorized data loss:** persist an audit entry, log a warning, and skip malformed individual archive records, malformed blocks, and invalid post records. Valid records in an undecodable block can also be lost. Unsafe framing, truncated transport, and other non-skippable SDK errors are not blindly skipped. |
| Generation mismatch | Re-enumerate from the latest durable checkpoint with the unchanged request, at most five times per process, with cancellable 30-second waits. Other invalid-data errors are not treated as compaction races. |
| Deleted/restricted targets | Observe source deletes in the reader; reconcile uncertain sends first, then record `not-sent` for deleted targets or server-confirmed reply restrictions. AppView absence alone is retried because indexing can lag. Known target-rejection errors are recorded as terminal outcomes. |
| Unknown remote outcome | Retain `uncertain` work and use its kind's reconciliation handler before retrying. Replies, likes, and reposts use fixed keys; list membership uses a fixed list/author key. Bookmarks use exhaustive service scans, not a caller-selected remote key. Never generate replacement identities on retry. |

For thread references, the parent is the source URI/CID and the root is its
existing `Reply.Root`, or the parent for a top-level post. Replies are made by the
account configured in `Bot:Did` / `Bot:Handle`; a mismatched login fails.

The reader transaction inserts all actions for a matching event and advances the processed-through
boundary together. Repeated events at or below that boundary are consumed without
re-enqueueing, so the SDK can still advance its checkpoint. Checkpoint callbacks
return only after bounded database writes have committed; all earlier emitted
events have already been durably handled.

Nonmatching events with no durable side effects do not incur a transaction per
post. Their consumed boundary and complete SDK checkpoint are committed together
at most once per second. Matching work, deletion handling, and malformed-record
audits still commit before reading the next event. A crash can replay ignored
events since the last checkpoint, but cannot lose an enqueued action.

`ReplayAsync()` handles archive-to-live handoff, transient live disconnections,
expired live cursors, archive quotas, and validated range resumes. Recovery is
limited by archive availability and compaction: physically deleted historical
records may no longer be recoverable. Missing initial archive coverage fails
explicitly rather than inventing a newer starting point.

Terminal action rows and skip audits are retained for seven days by default.
Action rows are pruned only if the processed-through boundary covers their source
sequence. Pending/uncertain work is never pruned; rate reservations live for the
full rolling day. `Bot:CompletedRetentionDays` changes the audit retention.

**Restoring an older database backup or deliberately rewinding needs operator
reconciliation with the bot's remote repository first.** It can discard known
record keys and completed receipts; normal durable action deduplication does not
protect arbitrary rewinds. Action identity is source URI + action kind + stable
action key. The database and remote services cannot share a transaction; fixed
keys and reconciliation reduce duplicates but do not promise absolute
exactly-once delivery, especially for service-managed bookmarks.

### Explicit forward-only catch-up override

Stop the bot, then optionally skip unprocessed history older than a duration:

```powershell
dotnet run --project src\MaddyBot --no-launch-profile -- --lookback 30m
```

Durations use whole numbers with `s`, `m`, `h`, or `d`; `0s` selects the invocation
time. This is an intentional **data-loss override**, not normal recovery.
If no replay request has been saved, the switch is ignored with an informational
log and the bot starts normally with first-run eligibility. Otherwise it requires
a cutoff later than the stored eligibility start.
It never decreases the processed-through sequence. If processing is already
beyond the requested boundary, it does not rewind.

Under database ownership it preserves the original filters and upper bound, clears
the old checkpoint, advances the eligibility cutoff, and records a warning and a
durable skip audit. Already queued pending/uncertain replies, credentials, and rate
accounting remain intact. Archive coverage must be available; no cursor is invented.
The cutoff is fixed when the command is invoked and applied once in that process.
For recent history, an unfiltered live timestamp-cursor probe on the same host
obtains a real sequence. Replay begins just before the first returned event so
that event is not lost; the original replay filters and witnessed-time eligibility
check still apply. The lookup is bounded to 30 seconds and does not modify saved
state on failure. If the server explicitly reports `CursorTooOld`, the bot logs
a warning and selects a conservative archive segment boundary instead, which
may require reading events older than the cutoff.
Remove the switch on subsequent starts to resume the new saved state normally.
`--help` describes the switch without requiring secrets or opening the database.

### Ownership and graceful shutdown

A database **lease** is temporary permission for one process to do work. It
covers both workers, so overlapping deployments do not both consume and send.
Every durable operation checks ownership
transactionally; outbound requests are admitted only with at least 35 seconds of
lease remaining. Leases last 90 seconds and are checked/renewed every 10 seconds.
A new owner waits 40 seconds before processing, longer than the 30-second HTTP
deadline, to drain a previous deployment's bounded requests. Database recovery
suspends workers and cancels their active operations. No stale owner can enqueue
or reserve another outbound request. Fixed record keys also protect uncertain
writes that a server might finish after the client disconnects.

Database connections are pooled with a five-connection limit per instance to
leave capacity for overlapping deployments on the small development database.

Shutdown cancels replay/sends and allows 45 seconds for cleanup. A send interrupted
after its attempt was recorded remains uncertain and is reconciled next time.
The lease is released after worker shutdown, or expires after a crash.

### Rate limits and authentication

All HTTP requests through the long-lived Bluesky agent, including reads,
identity resolution, retries, session creation, and token refresh, reserve durable
accounting **before** touching the network. Record creates reserve three write
points even if they fail or time out. Defaults are conservative:

| Budget | Bot default | Published Bluesky-hosted limit |
|---|---:|---:|
| Repository write points/hour | 4,500 | 5,000 |
| Repository write points/day | 30,000 | 35,000 |
| PDS requests/five minutes | 2,400 | 3,000 per IP |
| Session creations/five minutes | 20 | 30 per account |
| Session creations/day | 200 | 300 per account |

Requests are also paced at least five seconds apart. Server `Retry-After` and
near-exhausted rate-limit reset headers extend a durable shared cooldown. Budgets,
cooldowns, and per-item retries survive restarts. Published limits can change,
and traffic from other applications using the same account/IP must be allowed for.
Do not use this bot's accounting as a global limiter for unrelated applications.

One agent is kept for the lifetime of the sender. Background refresh is disabled
so refresh operations are serialized with sending; credentials are refreshed
before use when needed. `CredentialsUpdatedAsync` persists refreshed credentials
with AES-256-GCM, bound to the configured DID. Keep the 32-byte encryption key
outside the database and source control, stable across deployments, and included
in your secret-recovery plan. Do not change it without deliberately migrating or
resetting authentication state. Access tokens are not persisted.

## Health and logs

The root route (`/`) returns a UTF-8 plain-text summary from `ActionStatistics`:
`<count> action(s) completed since <UTC date> 🔥`. The count resets on each process
start and includes newly confirmed actions (including reconciled writes) or
developer-mode simulations, not skipped targets or failed attempts.

`/health/live` is a fast, in-memory probe of the ownership, reader, and sender
state machines. It returns 503 for failed workers or overdue operations.
Processing, idle polls, rate waits, standby, and actively progressing dependency
recovery are healthy. `/health/dependencies` reports recovery dependencies
separately; do not use it as the restart probe. `/metrics` provides a separate
Prometheus text endpoint for durable action counts and the processed-through
sequence. It queries PostgreSQL on each scrape and is not a health check; restrict
network access and avoid high-frequency scraping.

Replay reads have a five-minute slice and bounded cancellation/close deadlines.
Real SDK quota/retry timers extend the slice to their requested deadline plus a
small margin (no SDK wait above one day is accepted). An idle slice reconnects
from durable progress; it does not jump to “now.” Probes never use time since the
last reply, and there is no unrelated heartbeat timer.

Actions use the package's .NET **source-generated** `[LoggerMessage]` methods and
human-readable console logging with UTC timestamps. Enqueue, simulate, send, reconcile, skip, cooldown, retry,
authentication, ownership, and fatal outcomes carry structured properties and
stable event IDs. Secrets and credential objects are not logged.
Optional observations use explicit descriptions such as "unbounded", "not yet
observed", or "not yet saved" instead of displaying `(null)` or inventing zero
values. HTTP outcomes include a cooldown clause only when a future deadline
exists. Available sequence numbers, timestamps, and durations remain typed
structured properties.

MaddyBot explicitly enables Information-level logs for
`idunno.Bluesky.BotSdk.ActionDispatcher` in `appsettings.json`. Outcome logs
include the action kind, source URI, remote URI when available, and reason.
MaddyBot still returns only replies unless you change its policy.
Developer-mode actions are logged as simulations, not actual remote effects.

The reader reports progress on its first consumed event, every 15 seconds while
consuming, and when moving between "catching up" and "near current time".
Each report includes the sequence, UTC event witnessed time, event-time lag, and
interval throughput. "Near current time" means within 30 seconds of the local
clock; it is not proof of the transport's archive/live phase. Quiet reads report
the last consumed event and time since receipt every 15 seconds, without claiming
the socket is connected. These logs do not drive liveness or advance checkpoints.

Each replay enumeration also has a `ReplayEnumerationId` in structured diagnostic
logs and a console-visible scope shared with the SDK. Its start records the loaded
SDK assembly/informational version, safe service authority, original filters and
bounds, and resume progress. Only
`idunno.AtProto.Jetstream.AtProtoJetstream` is enabled at **Debug** by default;
other categories keep their existing levels. The configured SimpleConsole sink
retains scopes and full exceptions, including inner exceptions and stack traces.
Configuration overrides or replacement sinks must preserve those settings.

Approximately every 30 seconds, a diagnostic summary reports cumulative consumed
events (including duplicates), relevant events committed (matches, deletion
effects, or malformed-post audits), interval throughput, in-memory versus durable
processed boundaries, and the last successfully saved checkpoint's archive/live
and block positions. It also reports checkpoint successes/failures and monotonic
count/total/maximum timings for event handling, database work, and checkpoint
attempts. Handling includes its database work; database timings include checkpoint
work, so these groups must not be added together. Read waits are separate, include
the pending read in their total/maximum, and exclude timed database callbacks.
A long read wait alone does not establish slow consumption. Event-time lag is
explicitly local UTC minus the last processed event's witnessed timestamp.

Cancellation and cleanup logs distinguish read-slice expiry, host shutdown and
existing recovery actions, awaiting an outstanding read, enumerator disposal,
and SDK disposal. Unexpected read failures are logged before cleanup, so a
secondary cleanup failure does not hide the initiating exception. Failure logs
retain correlation, phase and known processed/durable progress; propagation and
the host's `StopHost` policy are unchanged. A final summary is emitted on orderly
exit when possible, not on hard process termination or native crashes. These
diagnostics do not change the five-minute slice or alter replay, checkpoint
persistence or recovery policies.

`Bot:ReplayLiveBufferCapacity` defaults to **8,192 decoded events**, up from the
SDK default of 1,024. Observed catch-up overflows at 1,024 followed by stable live
consumption justify trying a larger burst buffer; 8,192 is a starting point,
not a proven optimum. It is independent of the WebSocket byte buffer and must
be positive. Increasing it costs memory and cannot fix sustained slow consumption.
Overflow still drains buffered events and recovers from consumed progress.
Compare catch-up overflow counts, event-time lag and memory after restarting.

## Docker and hosting

```powershell
$commit = git rev-parse HEAD
$ref = git symbolic-ref HEAD
docker build --platform linux/amd64 --build-arg GITHUB_ACTIONS=true --build-arg "GITHUB_SHA=$commit" --build-arg "GITHUB_REF=$ref" --tag maddybot:dev .
```

The image runs as the .NET non-root user on port 8080. Its Docker `HEALTHCHECK`
uses `/health/live`. **Docker marking a container unhealthy does not automatically
restart it**; arrange a supervisor when running locally.

The entry point is the Native AOT executable `./MaddyBot`, not `dotnet MaddyBot.dll`.
Keep its native debug symbols with the matching build when investigating crashes;
managed exception logs cannot capture hard termination or native faults.

Push the image with an immutable tag to a container registry supported by your
hosting provider. Configure a long-running container service or worker, rather
than a job that exits after handling one request. Populate the bot DID/handle and
secret environment values, and configure your hosting provider's liveness probe
to use `/health/live` on port 8080. Do not assume the provider honors the image's
Docker `HEALTHCHECK`; configure restart behavior explicitly.

Manage deployment configuration in your hosting provider's live app spec and
database settings. Creating resources may incur charges; the repository does
not provision them automatically.

Production configuration uses environment variables:
`Bot__AppPassword`, `Bot__ArchiveApiKey`, `Bot__CredentialEncryptionKey`,
`Bot__Did`, `Bot__Handle`, and `ConnectionStrings__Bot`.
`Bot__DeveloperMode=false` enables real actions; confirm the intended account and
policy before setting it. Array entries can use
`Bot__Replies__0` and `Bot__NeverReplyToDids__0`.

Use the database connection parameters and CA certificate supplied by your
hosting provider. Npgsql requires `SSL Mode=VerifyFull` outside Development.
`Bot__DatabaseCaCertificate` accepts the PEM CA certificate without needing a
persistent filesystem or embedding it in the image. Never disable certificate
verification to make deployment work.

Choose a database tier with backups, availability, capacity, and retention
appropriate for your bot. Verify pricing and support with your hosting provider
before deployment; a development-tier database does not necessarily offer
production-grade recovery.

MaddyBot stores durable replay state, queued actions, rate budgets, and encrypted
refresh credentials in PostgreSQL. Many hosting providers offer only ephemeral
container filesystems, so do not move that state into local files unless your
deployment guarantees persistent storage. SQLite is an option for local or
single-host deployments only when its database file lives on persistent local
storage.

### Manual production releases

The [Release workflow](.github/workflows/release.yml) updates the existing
DigitalOcean app `8c1c1576-3e22-4840-9def-f729526a3ad4` using images in
`registry.digitalocean.com/idunno-registry/maddybot`. It does not create an app,
database, user, or VPC, or replace the live app configuration with a checked-in spec.

Before the first release, create a GitHub environment named **`production`** in
repository Settings -> Environments:

| Setting | Value |
|---|---|
| Deployment branches | Only `main`. |
| Environment variable `DIGITALOCEAN_WORKER_NAME` | The exact existing worker component name from the live App Spec, not the app name. |
| Environment secret `DIGITALOCEAN_ACCESS_TOKEN` | A DigitalOcean token authorized to read/update the app, inspect its deployments, and generate read/write registry credentials. |
| Required reviewers | Configure a production approval if available for the repository's GitHub plan. |

Keep database credentials, the bot app password, archive key, and encryption key
in App Platform's existing runtime secrets; do not copy them into GitHub.
Restrict access to the DigitalOcean token and use an expiry/rotation policy.
The worker must already use the `maddybot` DOCR image, run one instance without
autoscaling, have liveness checks configured, and have automatic image-push
deployment disabled.

After merging the workflow, wait for **CI on the main push** to pass. Open
Actions -> Release -> Run workflow, select **main**, and run it. Other branches
are skipped. The workflow deploys the exact commit captured by that dispatch;
it does not silently switch to a newer main commit while running.

The workflow requires that commit's latest main-push CI run to have succeeded,
builds a Linux-x64 native image, and runs `--help` with networking disabled.
It pushes a unique `<commit>-<run-id>-<attempt>` tag using one-hour registry
credentials, then deploys the resulting digest rather than a mutable tag.
Production releases are serialized and do not cancel an active release.

The deployment script reads the live app spec and changes only the selected
worker's image tag/digest and registry selection, preserving its database,
VPC, encrypted secrets, liveness checks, and shutdown settings. It refuses an
already-running deployment. Avoid editing the app or starting other deployments
while releasing: the DigitalOcean spec update is not an atomic image-only patch.
No secret-bearing specs or API responses are printed or uploaded as artifacts.

Success requires the specific deployment to become `ACTIVE` within 20 minutes
and still be the active deployment with the expected worker image digest.
This checks App Platform's deployment result, not whether a real reply was
sent. Confirm replay progress and normal worker behavior in runtime logs.
The Actions summary records the new image and previous worker image selection.
Retain both images in the registry for rollback.

Failure or timeout fails the workflow; it does not reset durable state,
automatically roll back, rerun CI, or issue a second deployment update. Inspect
App Platform first, particularly after a timeout or lost API response: a
deployment can continue after the workflow stops waiting. An application rollback
must account for database-schema and stored-payload compatibility; never restore
an older database backup just to roll back an image.

Offline deployment-script checks use Python's standard library:

```powershell
python -B -m unittest discover -s scripts -p test_deploy_digitalocean.py
```

## Tests

The [CI workflow](.github/workflows/ci.yml) runs on pull requests and pushes to
`main`, and can also be started manually. It restores dependencies, builds
the Debug solution using full Git history for version calculation, checks
formatting, and runs the sample's full suite using xUnit v3 and Microsoft.Testing.Platform v2
without a database service. Skipped tests fail the coverage gate. Provider tests
are maintained upstream. A separate job builds the Linux x64 Native AOT Docker image
and runs `--help` with networking disabled; CI never authenticates a real bot
or publishes Bluesky actions.

Actions are pinned to immutable commit SHAs. Test results
and native build logs are retained as workflow artifacts for 14 days so the
original failure can be diagnosed before rerunning a job. The separate
[Test Results workflow](.github/workflows/test-results.yml) publishes the saved
TRX results as a check on the tested commit and a comment on pull requests,
including fork pull requests. It uses the original CI event to associate results
with the correct PR and never checks out or executes PR code with write permissions.
Missing or inconclusive results fail reporting rather than being presented as
passing tests.

[Dependabot](.github/dependabot.yml) checks NuGet packages, SHA-pinned GitHub
Actions, and Docker base images weekly and opens update pull requests. The
NuGet configuration includes the public MyGet prerelease feed and groups the
related idunno SDK packages into one update. Version updates in all three ecosystems have a
seven-day cooldown after publication, allowing time for newly published malicious
releases to be detected. This reduces risk but does not guarantee package safety;
Dependabot security updates bypass the cooldown, and ordinary restores do not
enforce it for newly resolved transitive dependencies. Review SDK replay compatibility and
update `THIRD-PARTY-NOTICES` for dependency changes before merging; updates are
not automatically merged.

Sample tests do not connect to a database, authenticate with a real account,
or post to Bluesky. Run the full suite with:

```powershell
dotnet test --solution MaddyBot.slnx
```

`global.json` selects Microsoft.Testing.Platform for .NET 10's `dotnet test`.
The test project references `xunit.v3.mtp-v2` and runs as an executable, without
the VSTest adapter or `Microsoft.NET.Test.Sdk`. Filter by class with:

```powershell
dotnet test --solution MaddyBot.slnx --filter-class MaddyBot.Tests.PolicyTests
```

CI uses xUnit's built-in TRX report extension under MTP. To generate the same
report locally and reject skips:

```powershell
dotnet test --solution MaddyBot.slnx --results-directory TestResults --report-xunit-trx --report-xunit-trx-filename tests.trx --fail-skips on
```

When the bot is running, use `--artifacts-path` with a separate directory so
builds/tests do not replace its binaries. Tests cover policy/configuration,
queued exclusions and uncertain replies through the public handler, service
registration, and process statistics. Provider, persistence/migration, replay,
serialization, and dispatcher suites belong to the upstream SDK project.
Set `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` to disable MTP telemetry; CI sets it.

## License

[MIT](LICENSE).
