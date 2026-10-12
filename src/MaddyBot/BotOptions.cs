// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto;
using idunno.Bluesky;

namespace MaddyBot;

/// <summary>Adds sample-specific watch phrases and reply choices to the shared runtime configuration.</summary>
internal sealed class BotOptions : idunno.Bluesky.BotSdk.BotOptions
{
    /// <summary>Gets or sets the phrases used by this sample's normalized ordinal-ignore-case matching policy.</summary>
    public string[] WatchPhrases { get; set; } = [".NET Aspire"];
    /// <summary>Gets or sets the reply choices from which one complete durable reply is selected.</summary>
    public string[] Replies { get; set; } = ["It's just \"Aspire\" \U0001f92c"];
    /// <summary>Gets or sets the existing bot-owned curation list receiving matching-post authors.</summary>
    public string NaughtyStepListUri { get; set; } =
        "at://did:plc:4wxg4vpcqwk7p3f7cq4h4gla/app.bsky.graph.list/3mxovfc54kr2k";

    /// <inheritdoc/>
    public override void Validate()
    {
        base.Validate();

        AtUri listUri = new(NaughtyStepListUri);
        string[] listParts = listUri.ToString().Split('/');
        if (listParts.Length != 5 ||
            listParts[0] != "at:" ||
            listParts[2] != Did ||
            listParts[3] != "app.bsky.graph.list" ||
            string.IsNullOrWhiteSpace(listParts[4]))
        {
            throw new InvalidOperationException("Bot:NaughtyStepListUri must identify an app.bsky.graph.list record owned by Bot:Did.");
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
}