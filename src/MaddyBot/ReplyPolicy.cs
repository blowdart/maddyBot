// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

using idunno.AtProto;
using idunno.AtProto.Jetstream;
using idunno.AtProto.Repo;
using idunno.Bluesky;
using idunno.Bluesky.BotSdk;

namespace MaddyBot;

// This is the main customization point: decide whether a post matches and, if so, build
// the reply and author-list actions to save. Start with Bot:WatchPhrases and Bot:Replies in appsettings.json;
// change the marked sections below when configuration alone cannot express your bot's rules.
// Return null for no reaction, or a saved reply. The adapter adds the matching author-list action.
// Do not publish anything here: the reader must save the work before sending can begin.
/// <summary>Selects sample-specific matching posts and constructs complete replies without performing remote writes.</summary>
internal static partial class ReplyPolicy
{
    /// <summary>Provides the generated separator matcher shared by post and configured-phrase normalization.</summary>
    /// <returns>The culture-independent matcher for spaces, tabs, underscores, and hyphens.</returns>
    [GeneratedRegex("[ \\t_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex PhraseSeparators();

    // CUSTOMIZE NORMALIZATION: these separator and .NET/dotnet rules belong to this sample's
    // matching policy, not the SDK. Adjust them alongside your detection rules if needed.
    // Normalize only the separators explicitly accepted by policy. Using \s or removing all
    // punctuation would incorrectly match across newlines or sentences such as ".NET. Aspire".
    // Both posts and configured phrases use the same alias/separator rules.
    /// <summary>Normalizes only this sample's accepted aliases and separators before ordinal comparison.</summary>
    /// <param name="text">The post text or configured watch phrase.</param>
    /// <returns>The matching text with dotnet aliases and accepted separators normalized.</returns>
    private static string NormalizePhrase(string text) =>
        PhraseSeparators().Replace(text.Replace("dotnet", ".NET", StringComparison.OrdinalIgnoreCase), " ");

    /// <summary>Creates saved reply intent for an eligible create event matching a configured phrase.</summary>
    /// <param name="item">The next sequential replay event.</param>
    /// <param name="startedAt">The durable first-run eligibility cutoff.</param>
    /// <param name="options">The sample's exclusions, watch phrases, and reply choices.</param>
    /// <returns>The complete fixed-key reply, or <see langword="null"/> when no reply is needed.</returns>
    /// <exception cref="InvalidDataException">Required v2 event identity or timing is missing.</exception>
    /// <exception cref="JsonException">The source post is malformed.</exception>
    public static BotAction? Create(
        JetstreamEvent item,
        DateTimeOffset startedAt,
        BotOptions options) =>
        Create(item, startedAt, options, out _);

    /// <summary>Creates saved reply intent and returns its source reference for sibling actions.</summary>
    /// <param name="item">The next sequential replay event.</param>
    /// <param name="startedAt">The durable first-run eligibility cutoff.</param>
    /// <param name="options">The sample's exclusions, watch phrases, and reply choices.</param>
    /// <param name="source">When a reply is returned, contains its source post's URI and CID; otherwise, <see langword="null"/>.</param>
    /// <returns>The complete fixed-key reply, or <see langword="null"/> when no reply is needed.</returns>
    /// <exception cref="InvalidDataException">Required v2 event identity or timing is missing.</exception>
    /// <exception cref="JsonException">The source post is malformed.</exception>
    internal static BotAction? Create(
        JetstreamEvent item,
        DateTimeOffset startedAt,
        BotOptions options,
        out StrongReference? source)
    {
        source = null;

        // Updates and sync backfills are not new posts. Eligibility is checked before decoding
        // post content, so excluded authors and irrelevant operations take the cheap path.
        if (item is not JetstreamCommitEvent { IsSyncBackfill: false, Commit.Operation: JetstreamCommitOperation.Create } commit ||
            commit.Commit.Collection.ToString() != "app.bsky.feed.post" ||
            options.Excludes(commit.Did.ToString()))
        {
            return null;
        }

        // Use the service's witnessed time, not the author's createdAt. Backdated content is
        // still eligible when first witnessed after our start; replayed pre-start content is not.
        DateTimeOffset witnessedAt = item.WitnessedAt
            ?? throw new InvalidDataException("A v2 commit has no witnessed timestamp.");

        if (witnessedAt < startedAt)
        {
            return null;
        }

        JsonElement record = commit.Commit.Record ?? throw new JsonException("Create event has no record.");
        Post post = record.Deserialize(PostMetadata)
            ?? throw new JsonException("Create event has a null post.");

        // CUSTOMIZE DETECTION: replace this phrase check for more complex matching rules.
        // The source Post is available here. Keep the eligibility checks above unless you
        // deliberately want a different policy for old posts, updates, backfills or exclusions.
        string text = NormalizePhrase(post.Text ?? "");

        if (!options.WatchPhrases.Any(phrase =>
            text.Contains(NormalizePhrase(phrase), StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        long sequence = item.Sequence ?? throw new InvalidDataException("A v2 event has no sequence.");
        AtUri sourceUri = new($"at://{commit.Did}/{commit.Commit.Collection}/{commit.Commit.RKey}");
        Cid cid = commit.Commit.Cid ?? throw new InvalidDataException("Create event has no CID.");

        // Parent identifies the exact source version. A reply to a reply keeps the original
        // thread root; using the immediate parent as root would create incorrect thread references.
        StrongReference parent = new(sourceUri, cid);

        // CUSTOMIZE REPLY: build your reaction here, for example deriving text from the source
        // Post instead of choosing from Bot:Replies. We save this complete reply once, so retries
        // send the same thing. Keep the parent/root references if your action is still a reply.
        Post reply = new(
            text: options.Replies[RandomNumberGenerator.GetInt32(options.Replies.Length)],
            createdAt: DateTimeOffset.UtcNow,
            reply: new ReplyReferences(parent, post.Reply?.Root ?? parent),
            tags: ["bot", "\U0001f916"]);

        // Post records require a TID, not merely a syntactically valid generic record key.
        // Pick random text, createdAt, and a record key once. After the reader commits this work,
        // retries must reuse all three so a lost response can be reconciled at a known URI.
        source = parent;

        return BotActions.Reply(
            parent,
            sequence,
            reply);
    }

    private static JsonTypeInfo<Post> PostMetadata { get; } =
        (JsonTypeInfo<Post>)BlueskyJsonSerializerOptions.Default.GetTypeInfo(typeof(Post));
}

/// <summary>Adapts the sample's reply and curation-list actions to the SDK's atomic action-batch contract.</summary>
/// <param name="options">The configured sample matching and reply rules.</param>
internal sealed class MaddyReplyPolicy(BotOptions options) : IBotPolicy
{
    /// <inheritdoc/>
    public IReadOnlyList<BotAction> Create(
        JetstreamEvent item,
        DateTimeOffset startedAt)
    {
        BotAction? reply = ReplyPolicy.Create(item, startedAt, options, out StrongReference? source);

        if (reply is null)
        {
            return [];
        }

        StrongReference parent = source
            ?? throw new InvalidOperationException("A saved reply has no source reference.");

        return
        [
            reply,
            BotActions.AddAuthorToList(
                parent,
                reply.SourceSequence,
                new AtUri(options.NaughtyStepListUri))
        ];
    }
}