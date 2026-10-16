// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.Bluesky.BotSdk;

namespace MaddyBot;

/// <summary>Retains this sample's queued-author exclusions while delegating reply recovery to the packaged SDK.</summary>
/// <param name="options">The sample's startup exclusions.</param>
internal sealed class ExcludingReplyActionHandler(BotOptions options) : IBotActionHandler
{
    private readonly ReplyActionHandler _reply = new();

    /// <inheritdoc/>
    public string Kind => _reply.Kind;

    /// <inheritdoc/>
    public int PayloadVersion => _reply.PayloadVersion;

    /// <inheritdoc/>
    public void Validate(BotAction action) => _reply.Validate(action);

    /// <inheritdoc/>
    public async Task<BotActionResult?> ReconcileAsync(
        BotAction action,
        IBotActionRemote remote,
        CancellationToken cancellationToken)
    {
        bool excluded = options.Excludes(action.SourceDid);
        if (excluded && action.Status != "uncertain")
        {
            return new(false, null, "excluded author");
        }

        // The SDK checks an uncertain fixed-key write before TargetDeleted. Use that
        // no-retry path for excluded work without persisting a false deletion flag or
        // reading parent/root posts that we no longer intend to reply to.
        BotActionResult? result = await _reply.ReconcileAsync(
            excluded ? action with { TargetDeleted = true } : action,
            remote,
            cancellationToken);

        return result ?? (excluded ? new(false, null, "excluded author") : null);
    }

    /// <inheritdoc/>
    public Task<BotActionResult> ExecuteAsync(
        BotAction action,
        IBotActionRemote remote,
        CancellationToken cancellationToken) =>
        options.Excludes(action.SourceDid)
            ? Task.FromResult(new BotActionResult(false, null, "excluded author"))
            : _reply.ExecuteAsync(action, remote, cancellationToken);
}