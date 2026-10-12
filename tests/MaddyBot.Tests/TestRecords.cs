// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using idunno.AtProto;
using idunno.AtProto.Jetstream;
using idunno.AtProto.Repo;
using idunno.Bluesky;
using idunno.Bluesky.Feed;

namespace MaddyBot.Tests;

internal static class TestRecords
{
    public static ActionHandlerRegistry Handlers() => new(
        [new ReplyActionHandler(), new LikeActionHandler(), new RepostActionHandler(), new BookmarkActionHandler(), new ListItemActionHandler()]);

    public const string BotDid = "did:plc:ec72yg6n2sydzjvtovvdlxrk";
    public const string AuthorDid = "did:plc:7e6kocyzb77xkncplrkoojej";
    public const string CidValue = "bafyreigwnxqttkhzha2ig4io6wwht3qiugtor4ruglceyfdbnyq53a55fe";
    public static readonly DateTimeOffset Start = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    public static BotOptions Options() => new()
    {
        Did = BotDid,
        NaughtyStepListUri = $"at://{BotDid}/app.bsky.graph.list/listkey",
        ArchiveApiKey = "not-a-real-key",
        CredentialEncryptionKey = Convert.ToBase64String(new byte[32])
    };

    public static JetstreamCommitEvent Event(string text = ".NET Aspire", long sequence = 11, ReplyReferences? reply = null) => new()
    {
        Did = new Did(AuthorDid),
        TimeStamp = Start.ToUnixTimeMilliseconds() * 1000,
        WitnessedAt = Start,
        Sequence = sequence,
        Kind = JetStreamEventKind.Commit,
        Commit = new JetstreamCommit
        {
            Operation = JetstreamCommitOperation.Create,
            Collection = new Nsid("app.bsky.feed.post"),
            Rev = "3mjfhkjsshs2q",
            RKey = new RecordKey("3mjfhkjsshs2q"),
            Cid = new Cid(CidValue),
            Record = JsonSerializer.SerializeToElement(new Post(text, Start.AddDays(-30), reply: reply),
                BlueskyJsonSerializerOptions.Options)
        }
    };

    public static BotAction Work(BotOptions? options = null) =>
        ReplyPolicy.Create(Event(), Start, options ?? Options())
        ?? throw new InvalidOperationException("Test event did not match.");

    public static Post Post(BotAction work) =>
        JsonSerializer.Deserialize<Post>(work.RecordJson, BlueskyJsonSerializerOptions.Options)!;

    public static PostView View(AtUri uri, bool restricted = false)
    {
        string json = JsonSerializer.Serialize(new
        {
            uri = uri.ToString(),
            cid = CidValue,
            author = new { did = AuthorDid, handle = "author.test" },
            record = JsonSerializer.SerializeToElement(new Post(".NET Aspire", Start), BlueskyJsonSerializerOptions.Options),
            indexedAt = Start,
            viewer = new { replyDisabled = restricted }
        });

        return JsonSerializer.Deserialize<PostView>(json, BlueskyJsonSerializerOptions.Options)!;
    }
}