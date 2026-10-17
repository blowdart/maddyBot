# maddyBot

[![GitHub License](https://img.shields.io/github/license/blowdart/maddyBot)](https://github.com/blowdart/maddyBot/blob/main/LICENSE)
[![Last Commit](https://img.shields.io/github/last-commit/blowdart/maddyBot)](https://github.com/blowdart/maddyBot/commits/main/)

A small Bluesky bot with strong feelings about naming things. It watches
Jetstream v2 for posts mentioning `.NET Aspire` and replies:

> It's just "Aspire" 🤬

You can change the phrases and replies without touching C#. Matching ignores
case and accepts `.NET` or `dotnet`, with spaces, tabs, hyphens, or underscores
between words. The [matching details](#watch-phrases-replies-and-exclusions)
explain exactly what counts.

It's written in C# 13 / .NET 10 and built on the published
**`idunno.Bluesky.BotSdk`** NuGet package. The SDK handles reading, recovery,
and sending; this repository is the sample app that decides what to reply to.
Before sending a reply, it saves the work in PostgreSQL so a restart won't
forget it. That saved queue is called an **outbox**. Storage comes from
**`idunno.Bluesky.BotSdk.PostgreSQL`**, using Npgsql and parameterized SQL,
without Entity Framework.

**New here?** Follow [Local development](#local-development) for a safe, no-write
run of the sample, then [Make this sample your own](#make-this-sample-your-own).
For a step-by-step SDK host, all five built-in actions, and custom actions, see
the [upstream Bot SDK guide](https://bluesky.idunno.dev/docs/bots/durableBotSdk.html).

## Bot SDK and sample

Think of the SDK as the plumbing. It reads archived and live events, saves
progress, queues actions, manages authentication and rate limits, and recovers
after interruptions. It also provides diagnostics and health checks. MaddyBot
supplies the phrase matching, reply text, and tags.

| Package | Role in this sample |
|---|---|
| `idunno.Bluesky.BotSdk` | Hosted workers, durable actions, authentication, replay recovery, diagnostics, and health probes. |
| `idunno.Bluesky.BotSdk.PostgreSQL` | PostgreSQL storage, schema initialization/migration, and the host-owned connection pool. |
| `idunno.AtProto` and `idunno.Bluesky` | Protocol and Bluesky types used by the application and its policy. |

Here's where to look in `src\MaddyBot`:

| File or type | What it does |
|---|---|
| `BotOptions` | Adds watch phrases, reply choices, author exclusions, and database CA settings to the SDK options. |
| `MaddyReplyPolicy` | Implements `IBotPolicy` to choose replies. |
| `Program.cs` | Loads configuration, handles command-line options, and hosts the status and health pages. |
| `BotRegistration.cs` | Wires everything together, selects PostgreSQL storage, and wraps the reply handler so queued work respects author exclusions. |

If you find a bug or need a feature in the runtime or storage provider,
[open an issue in the SDK repository](https://github.com/blowdart/idunno.Bluesky/issues).
If you'd like to contribute a fix, fork that repository and open a pull request
linked to the issue, rather than copying the SDK's internals into this app.

A policy returns `IReadOnlyList<BotAction>`: `[]` means "nothing to do";
otherwise, it returns the actions to save, built with `BotActions`. The reader
saves the whole batch and its progress in one database transaction. The sender
then performs each action separately—Bluesky doesn't apply them as one transaction.
Keep remote writes out of the policy. Choose the content, creation time, and
thread references here, then let retries reuse the saved content and record key.

`AddBlueskyBot` automatically registers handlers for **replies, likes, reposts,
private bookmarks, and adding the source author to an existing bot-owned curation
list**. Application-defined actions use `BotActions.Create<TPayload>` and a
singleton `IBotActionHandler`; see the [upstream guide](https://bluesky.idunno.dev/docs/bots/customBotActions.html).
MaddyBot intentionally creates only a reply for each eligible match. For a new
host, alternative storage providers, or custom provider contracts, follow the
[upstream SDK guide](https://bluesky.idunno.dev/docs/bots/durableBotSdk.html).

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

For bigger changes, start here. You'll find matching customization comments
in the code:

| What you want to change | Where to change it |
|---|---|
| Phrases, reply texts, or authors to ignore | `Bot:WatchPhrases`, `Bot:Replies`, and `Bot:NeverReplyToDids` in `src\MaddyBot\appsettings.json`. See [Watch phrases, replies, and exclusions](#watch-phrases-replies-and-exclusions) for configuration overrides. |
| How a post counts as a match | The **CUSTOMIZE DETECTION** block in `src\MaddyBot\ReplyPolicy.cs`, inside `ReplyPolicy.Create()`. `NormalizePhrase()` defines the accepted separators and the `.NET`/`dotnet` alias; change those rules if they do not suit your bot. |
| How to build a reply after a match, such as choosing text from the source post | The **CUSTOMIZE REPLY** block in `ReplyPolicy.Create()`, where `Post reply` is constructed. This runs before the reply is saved, not on each send attempt. |
| Add likes, reposts, bookmarks, or author-list membership | Adapt the policy to return more `BotAction` intents. See the [upstream SDK guide](https://bluesky.idunno.dev/docs/bots/durableBotSdk.html). No dispatcher changes are needed for built-in actions. |
| Add an application-defined remote action | Implement `IBotActionHandler` and use `BotActions.Create<TPayload>`; see [Custom actions](https://bluesky.idunno.dev/docs/bots/customBotActions.html). Runtime sending and recovery remain package-owned. |
| The HTTP application identity | The **CUSTOMIZE HTTP IDENTITY** comment in `src\MaddyBot\Program.cs`, above `httpUserAgent`. |

The basic flow is **Jetstream -> ReaderWorker -> saved actions in a database ->
SenderWorker -> Bluesky**. `OwnershipWorker` decides which running copy may work.
The workers share one `IDatabaseProvider`, registered through dependency
injection (DI); this sample uses the packaged PostgreSQL provider.
`Program.cs` explains how the three workers fit together. The important rule:
save each action before trying to send it, and don't change its record key,
content, creation time, or thread references when retrying.

### Adapt the sample to return several actions

Right now, `MaddyReplyPolicy.Evaluate()` returns either no actions or one reply.
To add another built-in action, use the reply helper overload that also returns
the source reference, then include both actions in the returned collection.
For a standalone multi-action policy, see the
[SDK example](https://github.com/blowdart/idunno.Bluesky/tree/version/v8.0.0/samples/Samples.BotSdk).

Keep the existing eligibility checks when adapting the sample: normal creates,
the post collection, no sync backfills, no self/excluded authors, and the
witnessed start time. Replies keep the source post's `Reply.Root`, or use the
parent for a top-level post. The SDK already handles sending and recovery, so
you don't need to write your own remote create call. Actions can finish at
different times; if one is rejected, earlier successful actions stay completed.

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

You'll find package versions in `Directory.Packages.props` and feed settings
in `nuget.config`. The directly referenced idunno
packages are pinned to stable `8.0.0` from nuget.org.
The application references the core BotSdk and PostgreSQL provider packages.
There is no SQLite dependency or separate ASP.NET Core BotSdk package.
The committed `nuget.config` uses nuget.org for all dependencies.
Dependabot uses the same source.
This repository does not maintain per-project package lock files.

To upgrade, edit `Directory.Packages.props` and keep the directly referenced
idunno packages on the same version. Check that version is published first.
Before deploying, check the upstream notes for checkpoint compatibility,
database migrations, and changes to how the SDK recognizes archive-generation
mismatches.

### Versioning

MaddyBot uses [Nerdbank.GitVersioning](https://github.com/dotnet/Nerdbank.GitVersioning)
to stamp its assembly and executable with version information derived from Git.
It's a build dependency, not part of the BotSdk runtime.
`version.json` sets the version to `1.0`; you don't need Git tags. CI fetches full
history so version height and commit identity are calculated for the build.

Change `version.json` when you want a new version, rather than overriding it
during a release build. To start the next prerelease line, commit `"version":
"1.1-alpha"`. Builds from `main` are configured as public releases; other refs
include a commit identifier so their versions remain distinct. The informational
version includes the Git commit ID. Managed builds in a Git working tree mark
uncommitted changes `dirty`; the Docker build stamps the supplied commit ID
without copying Git metadata into its context.

### Native AOT

Publishing turns the app into a self-contained native executable using
Native AOT (`PublishAot`). Normal `dotnet run`, builds, and tests still use .NET.
The Dockerfile builds the native version for **Linux x64** and puts it in a
small `runtime-deps` image; the finished container doesn't need a .NET runtime.
Use Docker's Linux-container mode:

```powershell
$commit = git rev-parse HEAD
$ref = git symbolic-ref HEAD
docker build --platform linux/amd64 --build-arg GITHUB_ACTIONS=true --build-arg "GITHUB_SHA=$commit" --build-arg "GITHUB_REF=$ref" --tag maddybot:dev .
```

The Docker build does not copy `.git` into its context or image. Pass the commit
and ref as build arguments so the native executable retains its commit identity.
Because the build context has no Git history, the Docker build cannot calculate
version height; CI's full-history managed build does.

The SDK supplies generated JSON metadata for credentials, health responses,
posts, replay requests, and checkpoints. Stored property names and health
response shapes stay the same; there's no database reset for AOT.
Use `BlueskyJsonSerializerOptions` for SDK types, as the sample does.
Copying internal JSON helpers or using reflection-based serializer overloads
can break native publishing.

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
from `blowdart/idunno.Bluesky`. See the [signers guide](.ssh-allowed-signers.md)
for per-clone verification setup.

## Local development

This sample uses **PostgreSQL 16** for local development. The SDK also has a
**MySQL 8.4+** storage provider available.

If you change the PostgreSQL version, update your deployment settings and
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

The SDK also has a MySQL 8.4+ storage provider. To create a local MySQL
container for a project configured to use that provider, run:

```powershell
$connectionString = .\scripts\create-mysql.ps1 -HostType Docker
```

This creates a MySQL 8.4 container on `127.0.0.1:33060` with the named volume
`maddybot-mysql84`, and prints a connection string containing a generated
password. It requires TLS for MySQL 8.4 password authentication. Treat the
connection string as a secret. The MaddyBot sample itself is currently wired
to PostgreSQL; creating this container alone does not switch the sample to MySQL.

Next, add your bot's settings. Configuration comes from `appsettings.json`,
.NET user secrets in Development, and then environment variables (last wins).
The project already has a user-secrets ID:

```powershell
dotnet user-secrets set --project src\MaddyBot "Bot:Did" "did:plc:<your-bot-did>"
dotnet user-secrets set --project src\MaddyBot "Bot:ArchiveApiKey" "<jetstream-archive-api-key>"
dotnet user-secrets set --project src\MaddyBot "Bot:DeveloperMode" "true"
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8080'
dotnet run --project src\MaddyBot --no-launch-profile -- --doctor
dotnet run --project src\MaddyBot --no-launch-profile
```

Replace the placeholder with your bot's full DID: its stable `did:plc:...` or
`did:web:...` identifier, not its `@handle`. Get the archive key from
[bsky.network](https://bsky.network/account#api-keys-section-heading).
User secrets are not encrypted; they are for local development, not production.
`--no-launch-profile` prevents local launch settings from overriding the HTTP
address above. The optional `--doctor` run checks your settings and database connection.
It doesn't create tables, take ownership, or start workers.

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

Ready to let it reply for real? Stop the development run, check your phrases and
exclusions, and **double-check the account you're about to use**. Create a
different database for the real run and point `ConnectionStrings:Bot` at it.
Don't reuse the developer-mode database.

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

Here's a minimal set of phrases, replies, and authors to ignore. Put these
arrays in `src\MaddyBot\appsettings.json`, or override individual entries with
user secrets or environment variables:

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

Include at least one reply, and make sure each is a valid Bluesky post.
The bot picks one at random, with equal odds, **once, when saving the work**.
It saves the complete record, creation time, and record key together. Changing
the list later won't change queued replies or pick new text on a retry.

Include at least one nonblank watch phrase. A post gets one queued reply even
if several phrases match.

Matching uses `StringComparison.OrdinalIgnoreCase` after applying these rules
to both the post and the watch phrase:

| Example | Matches `.NET Aspire`? |
|---|---|
| `.NET Aspire`, `.net aspire`, `dotnet aspire` | Yes. Case doesn't matter, and `dotnet` is an alias for `.NET`. |
| `.NET  Aspire`, `.NET-Aspire`, `.NET_Aspire` | Yes. Runs of ordinary spaces, tabs, ASCII hyphens, and underscores become one space. A real tab between the words works too. |
| `NET Aspire` | No. The leading dot is still required unless you use `dotnet`. |
| `.NET. Aspire`, `.NET! Aspire`, `dotnet. Aspire` | No. Other punctuation isn't removed. |
| `.NET` and `Aspire` on separate lines | No. Matching doesn't cross line breaks. |

For example, `My code is written in .NET. Aspire works for me` won't trigger a
reply. These rules only affect matching, not the original post or reply text.
Restart to pick up changed settings; the bot won't revisit posts it has already
processed.

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

The aim is simple: resume where we left off, and don't forget saved replies.
Here are the rules the sample and SDK use to do that.

| Policy | Behavior |
|---|---|
| First run | Durably record the first owned run's UTC eligibility time before accessing Jetstream. Inspect archive segment metadata to select a positive sequence boundary covering that time; never replay from sequence zero. |
| Restart | Restore the original service, request bounds/filters, complete `SnapshotCheckpoint`, and application processed-through boundary. |
| Eligibility | Normal create events only. Ignore updates, sync-backfill assertions, own posts, and excluded DIDs. Use the server's witnessed time for initial eligibility, not an author's untrusted `createdAt`. |
| Historical work | No age limit or backlog expiry for eligible posts since the recorded start. Downtime can create a large queue. |
| Malformed data | **Explicitly authorized data loss:** persist an audit entry, log a warning, and skip malformed individual archive records, malformed blocks, and invalid post records. The reply policy reports missing posts, JSON decoding errors, and SDK constructor argument-validation failures as `MalformedEventException`; configuration and programming errors must not use this skip signal. Valid records in an undecodable block can also be lost. Unsafe framing, truncated transport, and other non-skippable SDK errors are not blindly skipped. |
| Generation mismatch | Re-enumerate from the latest durable checkpoint with the unchanged request, at most five times per process, with cancellable 30-second waits. Other invalid-data errors are not treated as compaction races. |
| Deleted/restricted targets | Observe source deletes in the reader; reconcile uncertain sends first, then record `not-sent` for deleted targets or server-confirmed reply restrictions. AppView absence alone is retried because indexing can lag. Known target-rejection errors are recorded as terminal outcomes. |
| Unknown remote outcome | Retain `uncertain` work and use its kind's reconciliation handler before retrying. Replies, likes, and reposts use fixed keys; list membership uses a fixed list/author key. Bookmarks use exhaustive service scans, not a caller-selected remote key. Never generate replacement identities on retry. |

Replies point back to the source post's URI/CID as their parent. For a reply
within a thread, they keep the source's `Reply.Root`; for a top-level post, the
parent is also the root. They come from the account in `Bot:Did` / `Bot:Handle`.
If those settings identify a different account than the login, authentication
fails when the sender needs to process queued work. An idle startup by itself
doesn't verify the account.

The reader transaction inserts all actions for a matching event and advances the processed-through
boundary together. Repeated events at or below that boundary are consumed without
re-enqueueing, so the SDK can still advance its checkpoint. Checkpoint callbacks
return only after bounded database writes have committed; all earlier emitted
events have already been durably handled.

Posts that don't match and need no saved changes don't each need a database
transaction. Instead, the reader saves their progress and the complete SDK
checkpoint together at most once per second. Matches, deletes, and malformed
record audits still commit before the next event is read. A crash may cause
ignored events to be read again, but won't lose an action already queued.

`ReplayAsync()` handles archive-to-live handoff, transient live disconnections,
expired live cursors, archive quotas, and validated range resumes. Recovery is
limited by archive availability and compaction: physically deleted historical
records may no longer be recoverable. Missing initial archive coverage fails
explicitly rather than inventing a newer starting point.

Terminal action rows and skip audits are retained for seven days by default.
Action rows are pruned only if the processed-through boundary covers their source
sequence. Pending/uncertain work is never pruned; rate reservations live for the
full rolling day. `Bot:CompletedRetentionDays` changes the audit retention.

**Before restoring an older database backup or rewinding, compare its saved
work with the bot's remote repository.** A backup can be missing record keys
and receipts for actions already completed, so ordinary duplicate protection
isn't enough for an arbitrary rewind. Actions are identified by source URI,
action kind, and a stable action key. The database and Bluesky can't share a
transaction: saved keys and checking uncertain writes reduce duplicates, but
don't guarantee exactly-once delivery, especially for service-managed bookmarks.

### Explicit forward-only catch-up override

If you deliberately want to leave older, unprocessed history behind, stop the
bot and start it with a cutoff:

```powershell
dotnet run --project src\MaddyBot --no-launch-profile -- --lookback 30m
```

Use a whole number followed by `s`, `m`, `h`, or `d`; `0s` means the time you run
the command. **This skips history and loses potential replies. It isn't normal
recovery or a fix for a slow reader.**
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

A database **lease** gives one running copy temporary permission to read and
send. If two deployments overlap, only the owner does the work.
Every database-changing operation checks ownership in its transaction.
An outbound request needs at least 35 seconds left on the lease before it can
start. Leases last 90 seconds and are checked/renewed every 10 seconds.
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

The sender saves a rate-budget reservation **before** each HTTP request.
That includes reads, identity lookups, retries, login, and token refresh—not
just replies. Creating a record reserves three write points even if the request
fails or times out. The defaults leave some room below Bluesky's limits:

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

The sender reuses one agent. It refreshes credentials before use when needed,
rather than letting background refresh race with sending.
`CredentialsUpdatedAsync` saves refreshed credentials using AES-256-GCM,
bound to the configured DID. Keep the 32-byte encryption key outside the database
and source control, reuse it across deployments, and back it up. Changing it
requires an intentional authentication migration or reset. Access tokens aren't
saved.

## Health and logs

Open `/` for a quick status page. Browsers requesting `text/html` receive an HTML
page that keeps UTC timestamps and adds the browser's local date, 24-hour time,
and time zone alongside them using JavaScript. With JavaScript disabled, UTC remains
visible. Command-line clients receive the original plain text; request
`Accept: text/plain` explicitly when needed. The page starts with the action count:
`<count> action(s) completed since <UTC date>`. The count resets on each process
start and includes newly confirmed actions (including reconciled writes) or
developer-mode simulations, not skipped targets or failed attempts.

Below that, you'll see whether the reader is catching up or within 30 seconds
of current time, how far behind it is, and the last event's witnessed time.
The page also shows when progress was last observed. Until the first event is
handled in an ownership period, it says progress is unknown.

**Healthy doesn't mean caught up.** Observations older than 30 seconds are
marked as not recent; waiting or reconnecting doesn't refresh them. Clock
differences are shown explicitly too. This is handled progress, not necessarily
the latest saved checkpoint, and it doesn't prove the socket is connected.
Reading the page doesn't query PostgreSQL.

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
The sample's `MaddyReplyPolicy` warning event `InvalidSourcePost` (ID 1) includes
the source record's AT URI and exception when post deserialization fails; the SDK
still owns the durable skip audit and replay progress.
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

If you need to dig deeper, a diagnostic summary appears about every 30 seconds.
It reports cumulative consumed
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
uses `/health/live`. One easy-to-miss detail: **Docker won't automatically restart
a container just because it's unhealthy.** Set up a supervisor for local use.

The entry point is the Native AOT executable `./MaddyBot`, not `dotnet MaddyBot.dll`.
Keep its native debug symbols with the matching build when investigating crashes;
managed exception logs cannot capture hard termination or native faults.

To host it, push the image with an immutable tag to a registry your provider
supports. Choose a long-running container service or worker, not a one-shot job.
Add the bot account settings and secrets, then point the provider's liveness
probe at `/health/live` on port 8080. Check its restart settings too; providers
don't necessarily use the image's Docker `HEALTHCHECK`.

Manage deployment configuration in your hosting provider's live app spec and
database settings. Creating resources may incur charges; the repository does
not provision them automatically.

The GitHub Actions **Release** workflow updates the existing DigitalOcean web
service, not a worker component. In the GitHub `release` environment, set
`DIGITALOCEAN_SERVICE_NAME` to the exact component name (`maddybot` for this
deployment). The workflow retains app ID `8c1c1576-3e22-4840-9def-f729526a3ad4`;
change `APP_ID` in the workflow if deploying to a different app.
Configure one service instance without autoscaling, HTTP port 8080, and a
`liveness_health_check` using `/health/live` on that port. The separate
`health_check` controls readiness and does not replace the restart probe.
Disable automatic registry-push deployments. Release pins the image digest,
preserves the rest of the live app spec (including routes, secrets, and database
settings), and waits for that exact deployment to become active.

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

Pick a database tier with the backups, availability, space, and retention your
bot needs. Check current pricing and support with your provider—a development
tier may be fine to get started, but may not offer the recovery guarantees you
want for production.

MaddyBot stores durable replay state, queued actions, rate budgets, and encrypted
refresh credentials in PostgreSQL. Many hosting providers offer only ephemeral
container filesystems, so do not move that state into local files unless your
deployment guarantees persistent storage. SQLite is an option for local or
single-host deployments only when its database file lives on persistent local
storage.

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
NuGet configuration uses nuget.org and groups the
related idunno SDK packages into one update. Version updates in all three ecosystems have a
seven-day cooldown after publication, allowing time for newly published malicious
releases to be detected. This reduces risk but does not guarantee package safety;
Dependabot security updates bypass the cooldown, and ordinary restores do not
enforce it for newly resolved transitive dependencies. Review SDK replay compatibility and
update `THIRD-PARTY-NOTICES` for dependency changes before merging; updates are
not automatically merged.

You can run the sample tests without a database or a Bluesky account.
They won't post anything. Run the full suite with:

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

If the bot is running, give builds/tests a separate `--artifacts-path` so they
don't replace its binaries. Tests cover policy/configuration,
queued exclusions and uncertain replies through the public handler, service
registration, and process statistics. Provider, persistence/migration, replay,
serialization, and dispatcher suites belong to the upstream SDK project.
Set `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` to disable MTP telemetry; CI sets it.

## License

[MIT](LICENSE).
