// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Net;

using idunno.Bluesky.BotSdk.DataProviders;

namespace idunno.Bluesky.BotSdk;

/// <summary>Resolves persisted action kinds and payload versions to explicitly registered handlers.</summary>
/// <param name="handlers">The built-in and application handlers from DI; duplicate kind/version pairs are rejected.</param>
internal sealed class ActionHandlerRegistry(IEnumerable<IBotActionHandler> handlers)
{
    private readonly Dictionary<(string Kind, int Version), IBotActionHandler> _handlers = CreateLookup(handlers);

    /// <summary>Builds an exact format lookup and reports conflicting registrations with their persisted identity.</summary>
    /// <param name="handlers">The handlers explicitly registered in DI.</param>
    /// <returns>The unique kind/version lookup.</returns>
    private static Dictionary<(string Kind, int Version), IBotActionHandler> CreateLookup(IEnumerable<IBotActionHandler> handlers)
    {
        Dictionary<(string Kind, int Version), IBotActionHandler> result = [];
        foreach (IBotActionHandler handler in handlers)
        {
            if (string.IsNullOrWhiteSpace(handler.Kind) || handler.Kind.Length > 128 || handler.PayloadVersion <= 0)
            {
                throw new InvalidOperationException("Action handlers require a nonempty bounded kind and positive payload version.");
            }
            if (!result.TryAdd((handler.Kind, handler.PayloadVersion), handler))
            {
                throw new InvalidOperationException(
                    $"Multiple action handlers are registered for '{handler.Kind}' payload version {handler.PayloadVersion}. Register exactly one handler per format.");
            }
        }

        return result;
    }

    /// <summary>Finds an exact saved format match without silently discarding unsupported queued work.</summary>
    /// <param name="action">The saved intent identifying its kind and version.</param>
    /// <returns>The handler supporting that exact persisted format.</returns>
    public IBotActionHandler Get(BotAction action) =>
        _handlers.TryGetValue((action.ActionKind, action.PayloadVersion), out IBotActionHandler? handler)
            ? handler
            : throw new InvalidOperationException(
                $"No action handler supports '{action.ActionKind}' payload version {action.PayloadVersion}. Queued work is retained.");

    /// <summary>Rejects invalid newly created intents before ingestion advances progress.</summary>
    /// <param name="action">The untouched pending intent returned by the policy.</param>
    public void Validate(BotAction action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action.ActionKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(action.ActionKey);
        if (action.ActionKind.Length > 128 || action.ActionKey.Length > 128 ||
            action.Status != "pending" || action.Attempts != 0 || action.TargetDeleted)
        {
            throw new InvalidOperationException("New actions must have bounded identities and untouched pending delivery state.");
        }
        Get(action).Validate(action);
    }
}

/// <summary>Coordinates validation, simulation, reconciliation, fenced admission, and durable action outcomes.</summary>
/// <param name="database">The provider holding complete intent and delivery state.</param>
/// <param name="options">The mode and current exclusion settings.</param>
/// <param name="logger">The structured action outcome logger.</param>
/// <param name="statistics">The per-process counter updated only after a newly committed completion.</param>
/// <param name="handlers">The exact kind/version handler registry.</param>
internal sealed class ActionDispatcher(
    IDatabaseProvider database,
    BotOptions options,
    ILogger<ActionDispatcher> logger,
    ActionStatistics statistics,
    ActionHandlerRegistry handlers)
{
    /// <summary>Executes one saved intent without regenerating its payload or assuming an uncertain write failed.</summary>
    /// <param name="action">The complete durable action loaded from storage.</param>
    /// <param name="remote">The authenticated transport, or <see langword="null"/> in developer mode.</param>
    /// <param name="cancellationToken">The active ownership/shutdown token.</param>
    public async Task SendAsync(
        BotAction action,
        IBotActionRemote? remote,
        CancellationToken cancellationToken)
    {
        IBotActionHandler handler = handlers.Get(action);
        handler.Validate(action);
        if (options.DeveloperMode)
        {
            if (options.Excludes(action.SourceDid) || action.TargetDeleted)
            {
                await FinishAsync(action, new BotActionResult(false, null, "excluded DID or deleted target"), cancellationToken);
            }
            else
            {
                int completed = await database.FinishAsync(
                    action,
                    "simulated",
                    null,
                    "developer mode; no remote write",
                    cancellationToken);
                if (completed == 1)
                {
                    statistics.RecordCompletion();
                    BotLog.WouldAct(logger, action.ActionKind, action.SourceUri, action.ActionKey);
                }
            }

            return;
        }

        ArgumentNullException.ThrowIfNull(remote);
        // Reconcile uncertain effects before applying new exclusions or deletion flags.
        // Pending excluded work must not contact remote services.
        if (action.Status != "uncertain" && (options.Excludes(action.SourceDid) || action.TargetDeleted))
        {
            await FinishAsync(action, new BotActionResult(false, null, "excluded DID or deleted target"), cancellationToken);

            return;
        }
        BotActionResult? reconciled = await handler.ReconcileAsync(action, remote, cancellationToken);
        if (reconciled is not null)
        {
            await FinishAsync(action, reconciled, cancellationToken);

            return;
        }
        if (options.Excludes(action.SourceDid) || action.TargetDeleted)
        {
            await FinishAsync(action, new BotActionResult(false, null, "excluded DID or deleted target"), cancellationToken);

            return;
        }

        // Persist uncertainty and recheck target deletion after remote preflight reads.
        if (await database.MarkAttemptAsync(action, cancellationToken) != 1)
        {
            await FinishAsync(action, new BotActionResult(false, null, "target deleted while preparing action"), cancellationToken);

            return;
        }
        BotActionResult result = await handler.ExecuteAsync(action, remote, cancellationToken);
        await FinishAsync(action, result, cancellationToken);
    }

    /// <summary>Commits a confirmed terminal outcome before updating counters or logging successful completion.</summary>
    /// <param name="action">The durable action identity.</param>
    /// <param name="result">The confirmed effect or terminal rejection.</param>
    /// <param name="cancellationToken">The active ownership/shutdown token.</param>
    private async Task FinishAsync(
        BotAction action,
        BotActionResult result,
        CancellationToken cancellationToken)
    {
        string status = result.Succeeded ? "sent" : "not-sent";
        int completed = await database.FinishAsync(
            action,
            status,
            result.RemoteUri,
            result.Reason,
            cancellationToken);
        if (completed == 1)
        {
            if (result.Succeeded)
            {
                statistics.RecordCompletion();
            }
            BotLog.Outcome(logger, action.ActionKind, action.ActionKey, action.SourceUri, status, result.RemoteUri, result.Reason);
        }
    }
}

/// <summary>Preserves an unsuccessful SDK response for explicit sender retry or fail-fast classification.</summary>
/// <param name="status">The observed HTTP status.</param>
/// <param name="error">The server's protocol error, or <see langword="null"/>.</param>
/// <param name="operation">The operation being attempted, without secrets.</param>
/// <param name="retryMissing"><see langword="true"/> when AppView indexing lag makes a missing target retryable.</param>
internal sealed class RemoteFailureException(
    HttpStatusCode status,
    string? error,
    string operation,
    bool retryMissing = false)
    : Exception($"HTTP {(int)status} ({error}) while {operation}.")
{
    public HttpStatusCode Status { get; } = status;
    public bool Retryable { get; } = status is HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or HttpStatusCode.Conflict ||
        (int)status >= 500 || error is "RecordAlreadyExists" or "InvalidSwap" ||
        (retryMissing && status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
}