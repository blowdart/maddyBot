// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.Bluesky;

namespace MaddyBot;

/// <summary>Adds sample-specific watch phrases and reply choices to the shared runtime configuration.</summary>
internal sealed class BotOptions : idunno.Bluesky.BotSdk.BotOptions
{
    /// <summary>Gets or sets the phrases used by this sample's normalized ordinal-ignore-case matching policy.</summary>
    public string[] WatchPhrases { get; set; } = [".NET Aspire"];
    /// <summary>Gets or sets the reply choices from which one complete durable reply is selected.</summary>
    public string[] Replies { get; set; } = ["It's just \"Aspire\" \U0001f92c"];
    /// <summary>Gets or sets author DIDs that this sample must not reply to.</summary>
    public string[] NeverReplyToDids { get; set; } = [];
    /// <summary>Gets or sets the trusted PostgreSQL root CA certificate in PEM format.</summary>
    public string? DatabaseCaCertificate { get; set; }

    /// <inheritdoc/>
    public override void Validate()
    {
        base.Validate();

        if (NeverReplyToDids is null || NeverReplyToDids.Any(did => !idunno.AtProto.Did.TryParse(did, out _)))
        {
            throw new InvalidOperationException("Every Bot:NeverReplyToDids entry must be a valid DID.");
        }

        if (WatchPhrases is null || WatchPhrases.Length == 0 || WatchPhrases.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Bot:WatchPhrases must contain at least one nonblank phrase and no blank entries.");
        }

        if (Replies is null || Replies.Length == 0)
        {
            throw new InvalidOperationException("Bot:Replies must contain at least one reply.");
        }

        foreach (string reply in Replies)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reply, nameof(Replies));
            _ = new Post(reply);
        }
    }

    /// <summary>Determines whether an author is the bot itself or an explicitly excluded author.</summary>
    /// <param name="did">The author DID to check.</param>
    /// <returns>Whether the author must be excluded from replies.</returns>
    public bool Excludes(string did) =>
        string.Equals(did, Did, StringComparison.Ordinal) || NeverReplyToDids.Contains(did, StringComparer.Ordinal);
}