## Overview

`maddyBot` is a C# 13 / .NET 10 sample showing how to build a reliable Bluesky
watcher and reply bot with `idunno.AtProto` and `idunno.Bluesky`. It reads Jetstream
archive/live replay and stores progress and reply work in PostgreSQL through
Npgsql and parameterized SQL. There is no Entity Framework.

`src\idunno.Bluesky.BotSdk` is the reusable durable-action runtime. It supports
replies, likes, reposts, bookmarks, author membership in curation lists, and
DI-registered custom action handlers. MaddyBot remains a reply-only example.

These instructions are adapted from `blowdart/idunno.Bluesky`'s
`version/v8.0.0` branch. SDK-specific endpoint generation, package publishing,
multi-target testing and public API baseline rules do not apply to this app.

## Build, test, and formatting

* Use the SDK pinned in `global.json` and `MaddyBot.slnx`.
* Restore: `dotnet restore MaddyBot.slnx`.
* Build: `dotnet build MaddyBot.slnx -c Debug --no-incremental`.
* Tests use **xUnit v2 and VSTest**, not Microsoft.Testing.Platform or xUnit v3.
* Run tests: `dotnet test MaddyBot.slnx`.
* Filter tests with VSTest syntax, for example
  `dotnet test MaddyBot.slnx --filter FullyQualifiedName~PolicyTests`.
* PostgreSQL tests require `MADDYBOT_TEST_CONNECTION`; use a disposable database
  and verify tests actually ran rather than accepting skips as database coverage.
  Never alter a running user's database, schema, checkpoint or container to test changes.
* Warnings are errors. Check formatting with
  `dotnet format MaddyBot.slnx --verify-no-changes --severity info --no-restore`.
  Use the whitespace formatter for formatting-only fixes; do not blindly apply
  analyzer fixes that could change cancellation or cleanup behavior.
* When the bot is running, use a separate `--artifacts-path` for builds/tests so
  validation does not replace its binaries. Do not stop or restart it without permission.
* Native AOT is enabled. Validate publish/dependency changes with the Docker
  Linux-x64 native build, not just a managed build. Windows native publish needs
  C++ build tools; do not assume they are missing or claim untested platform support.

## Architecture and reliability

The flow is Jetstream -> `ReaderWorker` -> PostgreSQL `reply_work` -> `SenderWorker`
-> Bluesky. `OwnershipWorker` initializes storage and renews the database lease
that selects the active process during overlapping deployments.

* Preserve sequential replay consumption. Commit event effects before advancing
  progress. Ignored events can have their progress coalesced with checkpoint saves.
* Persist the **complete** `SnapshotCheckpoint` and original request configuration.
  Do not substitute a scalar cursor, assume portability across hosts, or save ahead
  of durable handling. Sequence numbers are not timestamps or event counts.
* Replay can repeat events. Preserve transactional batch enqueue/progress,
  source-URI/action-kind/action-key uniqueness, and the processed-through boundary
  used after completed rows are pruned. One source may require several actions.
* Persist each fixed repository key, complete record and creation time before sending.
  Reconcile an uncertain remote write at that known URI before retrying. Never
  generate a fresh key merely because a response was lost. The database and PDS
  cannot share a transaction; do not claim absolute exactly-once delivery.
* Service-managed bookmarks have no caller-selected record key. Reconcile every
  page before retrying the unchanged saved URI/CID; do not claim fixed-key guarantees.
* Author-list actions require an existing bot-owned curation list. Do not silently
  add moderation, mute, block, subscription, or list-creation behavior.
* Preserve the existing normal-create, self/excluded-DID, backfill, deletion and
  reply-restriction policies. Malformed-data skipping is explicitly permitted and
  audited as data loss; do not broaden it silently.
* `--lookback` is an explicit forward-only history skip, not routine recovery.
  Retain queued/uncertain work and preserve request filters when applying it.
* Keep durable rate accounting, cooldowns and retry scheduling across restarts.
  Check `AtProtoHttpResult<T>.Succeeded` and server errors; do not infer success.
* Use `AddAtProtoHttpClient` or SDK defaults with SSRF protections. A delegating
  handler may add accounting but must not replace the protected primary transport.
* Restore encrypted refresh credentials before creating a new session. Never log
  keys, tokens, passwords, credential objects or secret-bearing connection strings.
* Health probes inspect memory and actual worker deadlines, not unrelated heartbeat
  timers or time since the last reply. Keep dependency recovery separate from liveness.
* Log exceptions as exception objects so stacks and inner exceptions survive.
  Preserve reader correlation scopes, bounded aggregate logging, explicit cancellation
  reasons, and cancel -> await outstanding read -> dispose cleanup ordering.
* Do not change processing/recovery behavior, buffers or retry policy solely to
  improve diagnostics. Distinguish observed reconnect causes from hypotheses.

## Customization and sample documentation

* Configuration-only changes belong in `Bot:WatchPhrases`, `Bot:Replies` and
  `Bot:NeverReplyToDids` in `src\MaddyBot\appsettings.json`.
* Richer detection and reply construction belong in the marked sections of
  `ReplyPolicy.Create()`. Keep remote writes out of the reader/policy.
* `IBotPolicy` returns complete `BotAction` batches without remote writes.
  `ActionDispatcher` uses versioned `IBotActionHandler` implementations to perform
  actions and reconcile uncertain writes. Preserve queued payload-version support.
* Keep the README customization guide and code signposts synchronized. Remind
  people cloning the sample to change `httpUserAgent` in `Program.cs`.
* Explain the flow and decisions in plain language. Define terms such as lease
  and outbox before relying on them. Comment important decisions, not basic syntax.
* Add related documentation files to suitable solution folders in `MaddyBot.slnx`,
  preserving existing projects and solution items. There is no SDK documentation
  `toc.yml`, public API baseline or package changelog to maintain in this app.

## Serialization, dependencies and configuration

* Reuse SDK-generated type metadata via `BotJson`; use `BotJsonContext` for
  application-owned types. Do not use reflection-based JSON overloads in the app.
* Preserve stored request/checkpoint/credential JSON and public health response
  shapes. Add compatibility tests when changing serialization.
* Configuration binding is source-generated. Preserve AOT/trimming compatibility;
  do not suppress warnings as a substitute for a correct implementation.
* Versions are centralized in `Directory.Packages.props`. Verify an exact SDK
  prerelease exists in the public MyGet feed configured in `nuget.config` before
  updating; do not guess versions or assume a PR commit is published.
* Nerdbank.GitVersioning is scoped to the MaddyBot project. Keep its package
  reference out of the reusable SDK project; version and release intent belong
  in the root `version.json` and do not require Git tags.
* Restore dependencies after version changes. Review replay compatibility and
  SDK-specific generation-mismatch classification on upgrades.
* Keep `THIRD-PARTY-NOTICES` up to date whenever dependencies are added, removed
  or upgraded, including transitive, build and test dependencies. Verify licenses,
  copyright attribution and bundled third-party notices against the actual
  packages and authoritative upstream sources; retain required license texts.
  Keep component/license columns aligned and do not include dependency versions.
* Respect `.editorconfig`, `.gitattributes` and existing configuration. Avoid
  unrelated infrastructure changes or replacing user-authored solution items.

## Text file encoding and line endings

Create new text files as UTF-8 without a byte-order mark (BOM). Use CRLF in Windows
working trees, while respecting Git's line-ending normalization and `.gitattributes`.
The default `text=auto` stores normalized LF in Git; `core.autocrlf=true` checks
those files out as CRLF on Windows. LF in a committed file or GitHub diff is
therefore expected and is not a CRLF violation.

Do not raise review findings solely because normalized text files contain LF in
Git, or add `-text` exceptions to bypass normalization. Preserve existing explicit
`.gitattributes` exceptions.

When checking line endings locally, use `git ls-files --eol` and
`git check-attr text eol` to distinguish indexed content from working-tree content.

## C# style

* Use C# 13, not preview features. Follow `.editorconfig`, file-scoped namespaces,
  single-line `using` directives and Allman braces.
* Separate logical sections with blank lines and put the final `return` on its own line.
* When an argument or parameter list spans multiple lines, put each argument or
  parameter on its own line, including constructors and primary constructors.
  Do not group several on a continuation line.
* Prefer pattern matching, switch expressions, `nameof`, `?.`, and
  `ObjectDisposedException.ThrowIf` where appropriate.
* Prefer primary constructors and positional internal records where they fit;
  keep explicit constructors when validation or accessibility requires them.
* Nullable reference types are enabled. Use `is null` / `is not null`; respect
  annotations and avoid unnecessary casts or null checks.
* Document public APIs using [docs.instructions.md](instructions/docs.instructions.md).
  Use approachable implementation comments for reliability-critical internal code.

## Testing and checkin conventions

* Follow nearby xUnit v2 tests and fake remote/logging patterns; no real Bluesky writes.
* Do not add `// Arrange` / `// Act` / `// Assert` comments.
* Prefer theories for similar cases. Add matching examples to `PolicyTests`,
  stored-format cases to `BotJsonTests`, diagnostics cases to `ReplayDiagnosticsTests`,
  and shared durable processing/recovery cases to `RelationalProviderTests`.
  Keep SQLite-only connection rules in `SqliteProviderTests`, and durable action
  sending/reconciliation cases in `RelationalProviderActionTests`.
* Before committing code, require a clean non-incremental Debug solution build
  and full test suite, including actual isolated PostgreSQL coverage. Filtered
  tests and incremental builds are iteration tools, not the final commit gate.
* Commit only when requested, use a feature branch, preserve unrelated changes,
  and use short imperative commit messages.
* All commits must be signed. If signing requires interaction or fails, stop and
  report it; do not change signing configuration, retry without authorization,
  delegate a retry, or commit unsigned.

## GitHub Actions failed-test protocol

1. Do not rerun or dispatch a failed workflow until its original failure is diagnosed
   or the user explicitly authorizes a rerun.
2. Preserve run/job IDs, attempt, SHA and failing checks. Retrieve
   `gh run view <run-id> --log-failed`, check annotations/summaries with `gh api`,
   and list/download artifacts, especially test results and diagnostics.
3. Record failing tests, target frameworks, projects, exceptions and stacks before recovery.
4. If the run is still active, wait for it to finalize; do not rerun because one job failed.
5. A rerun tests flakiness, not a fix. Preserve evidence first and report missing
   original failure details explicitly rather than inferring test identity from counts.
6. Merge only after the original failure is diagnosed or explicitly accepted as
   unresolvable infrastructure/flakiness, final required checks pass, and required
   CodeQL/Copilot review conditions are satisfied.

Never use `gh run rerun --failed` as the first response to failed tests.

## Copilot reviews

Request review with `gh pr edit <number> --repo <owner/repo> --add-reviewer '@copilot'`,
then verify `gh pr view <number> --repo <owner/repo> --json reviewRequests`.
Do not claim success unless Copilot is present; report unavailable/unverified
requests and use the reviewer UI where appropriate. `gh pr review` submits a
review as the authenticated user; it does not request Copilot.

When checking findings, read all review bodies and inline threads, including
previously missed findings and older unaddressed issues. Verify against the
current head; resolved/outdated thread status does not establish a code fix.
