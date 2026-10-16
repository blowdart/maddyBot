// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto;
using idunno.AtProto.Repo;
using idunno.Bluesky;
using idunno.Bluesky.Feed;

using Xunit;

namespace MaddyBot.Tests;

/// <summary>Checks sample-owned queued exclusions while exercising the packaged reply handler.</summary>
public sealed class ReplyHandlerTests
{
    /// <summary>Rejects queued excluded replies without creating a record, including at final execution.</summary>
    /// <param name="uncertain">Whether the original write outcome needs reconciliation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcludesQueuedReplies(bool uncertain)
    {
        BotOptions options = TestRecords.Options();
        BotAction work = TestRecords.Work(options) with { Status = uncertain ? "uncertain" : "pending" };
        options.NeverReplyToDids = [TestRecords.AuthorDid];
        ExcludingReplyActionHandler handler = new(options);
        FakeRemote remote = new();
        BotActionResult? result = await handler.ReconcileAsync(work, remote, CancellationToken.None);
        Assert.False(result!.Succeeded);
        Assert.False((await handler.ExecuteAsync(work, remote, CancellationToken.None)).Succeeded);
        Assert.Equal(uncertain ? 1 : 0, remote.Lookups);
        Assert.Equal(0, remote.Creates);
        Assert.Equal(0, remote.TargetReads);
    }

    /// <summary>Recognizes a previously accepted reply even after its author is excluded.</summary>
    [Fact]
    public async Task ReconcilesBeforeRejectingExcludedAuthor()
    {
        BotOptions options = TestRecords.Options();
        BotAction work = TestRecords.Work(options) with { Status = "uncertain" };
        options.NeverReplyToDids = [TestRecords.AuthorDid];
        FakeRemote remote = new() { Existing = work.RecordJson };
        BotActionResult? result = await new ExcludingReplyActionHandler(options).ReconcileAsync(work, remote, CancellationToken.None);
        Assert.True(result!.Succeeded);
        Assert.Equal(1, remote.Lookups);
        Assert.Equal(0, remote.Creates);
    }

    /// <summary>Does not hide reconciliation failures or overwrite mismatched remote records.</summary>
    [Fact]
    public async Task PreservesMismatchFailure()
    {
        BotOptions options = TestRecords.Options();
        BotAction work = TestRecords.Work(options) with { Status = "uncertain" };
        options.NeverReplyToDids = [TestRecords.AuthorDid];
        FakeRemote remote = new() { Existing = TestRecords.Work().RecordJson.Replace("Aspire", "different", StringComparison.Ordinal) };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ExcludingReplyActionHandler(options).ReconcileAsync(work, remote, CancellationToken.None));
        Assert.Equal(0, remote.Creates);
    }

    /// <summary>Delegates eligible replies to the packaged handler with their unchanged payload and fixed key.</summary>
    [Fact]
    public async Task SendsEligibleReply()
    {
        BotAction work = TestRecords.Work();
        ExcludingReplyActionHandler handler = new(TestRecords.Options());
        FakeRemote remote = new();
        handler.Validate(work);
        Assert.Null(await handler.ReconcileAsync(work, remote, CancellationToken.None));
        Assert.True((await handler.ExecuteAsync(work, remote, CancellationToken.None)).Succeeded);
        Assert.Equal(1, remote.Creates);
        Assert.Same(work, remote.Created);
    }

    /// <summary>Provides deterministic reads and records writes without a real Bluesky connection.</summary>
    private sealed class FakeRemote : IBotActionRemote
    {
        /// <summary>Gets the complete JSON returned for an existing fixed-key record.</summary>
        public string? Existing { get; init; }
        /// <summary>Gets the number of fixed-key lookups.</summary>
        public int Lookups { get; private set; }
        /// <summary>Gets the number of remote create calls.</summary>
        public int Creates { get; private set; }
        /// <summary>Gets the number of parent/root preflight reads.</summary>
        public int TargetReads { get; private set; }
        /// <summary>Gets the unchanged action supplied for creation.</summary>
        public BotAction? Created { get; private set; }
        /// <summary>Gets the unchanged action supplied for reconciliation.</summary>
        public BotAction? LookedUp { get; private set; }
        /// <inheritdoc/>
        public BlueskyAgent Agent => throw new NotSupportedException();
        /// <inheritdoc/>
        public string BotDid { get; } = TestRecords.BotDid;

        /// <inheritdoc/>
        public Task<string?> FindRecordAsync(BotAction action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Lookups++;
            LookedUp = action;

            return Task.FromResult(Existing);
        }

        /// <inheritdoc/>
        public Task<BotActionResult> CreateRecordAsync(BotAction action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Creates++;
            Created = action;

            return Task.FromResult(new BotActionResult(true, $"at://{BotDid}/app.bsky.feed.post/{action.RecordKey}", "created"));
        }

        /// <inheritdoc/>
        public Task<PostView> TargetAsync(AtUri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TargetReads++;

            return Task.FromResult(TestRecords.View(uri));
        }
        /// <inheritdoc/>
        public Task<bool> HasBookmarkAsync(AtUri uri, CancellationToken cancellationToken) => throw new NotSupportedException();
        /// <inheritdoc/>
        public Task CreateBookmarkAsync(StrongReference subject, CancellationToken cancellationToken) => throw new NotSupportedException();
        /// <inheritdoc/>
        public Task<string?> FindListMemberAsync(AtUri listUri, Did author, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}