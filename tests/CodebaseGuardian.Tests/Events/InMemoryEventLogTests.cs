using System.Text.Json.Nodes;
using Mcp.Events;
using Microsoft.Extensions.Time.Testing;

namespace CodebaseGuardian.Tests.Events;

public class InMemoryEventLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));

    private static EventDefinition Def(string name, Func<JsonObject?, JsonObject, bool>? matches = null) => new()
    {
        Name = name,
        Description = name,
        PayloadSchema = new JsonObject { ["type"] = "object" },
        Matches = matches,
    };

    private InMemoryEventLog NewLog(int capacity = 100, TimeSpan? retention = null)
    {
        var options = new EventsOptions { Capacity = capacity };
        if (retention is { } r) options.Retention = r;
        options.Define(Def("repo.pushed", (args, data) => (string?)args?["branch"] == (string?)data["branch"]));
        options.Define(Def("repo.tagged"));
        options.Define(Def("scan.done"));
        return new InMemoryEventLog(options, _time);
    }

    private static JsonObject D(string? branch = null) => new() { ["branch"] = branch ?? "main" };

    private static EventQuery Q(string? cursor, int max = 100, IReadOnlyCollection<string>? names = null,
        JsonObject? args = null, TimeSpan? maxAge = null) => new(names, args, cursor, maxAge, max);

    private static async Task<EventEnvelope> Pub(InMemoryEventLog log, string name = "scan.done", JsonObject? data = null,
        string? id = null, DateTimeOffset? ts = null) =>
        await log.PublishAsync(name, data ?? D(), id, ts, Ct);

    [Fact]
    public async Task NullCursorReturnsNothingAndHead_ThenReplaysInOrder()
    {
        var log = NewLog();
        await Pub(log); // pre-existing event must not be replayed on "start from now"
        var first = log.Read(Q(null));
        Assert.Empty(first.Events);
        Assert.Equal(log.HeadCursor, first.Cursor);
        Assert.False(first.Truncated);
        Assert.False(first.HasMore);

        var a = await Pub(log, data: D("a"));
        var b = await Pub(log, data: D("b"));
        var second = log.Read(Q(first.Cursor));
        Assert.Equal([a.EventId, b.EventId], second.Events.Select(e => e.EventId));
        Assert.Equal([2L, 3L], second.Events.Select(e => e.Sequence));
        Assert.False(second.HasMore);
        Assert.False(second.Truncated);
        Assert.Equal(log.HeadCursor, second.Cursor);
        Assert.Empty(log.Read(Q(second.Cursor)).Events);
    }

    [Fact]
    public void EmptyLogHeadAndOldestCursorsAreSequenceZero()
    {
        var log = NewLog();
        Assert.Equal(0, log.GetSequence(log.HeadCursor));
        Assert.Equal(0, log.GetSequence(log.OldestCursor));
    }

    [Fact]
    public async Task GeneratedEventIdUsesEpochPrefixAndSequence()
    {
        var log = NewLog();
        var e = await Pub(log);
        Assert.Matches(@"^evt_[0-9a-f]{8}_1$", e.EventId);
        Assert.Equal(1, e.Sequence);
        Assert.Equal(_time.GetUtcNow(), e.Timestamp);
        Assert.Equal(log.CursorAfter(e), log.HeadCursor);
    }

    [Fact]
    public async Task PagingByMaxEventsHasNoDuplicatesOrGaps()
    {
        var log = NewLog();
        var start = log.Read(Q(null)).Cursor;
        for (var i = 0; i < 5; i++) await Pub(log, data: D($"b{i}"));

        var r1 = log.Read(Q(start, 2));
        var r2 = log.Read(Q(r1.Cursor, 2));
        var r3 = log.Read(Q(r2.Cursor, 2));
        Assert.Equal([true, true, false], new[] { r1.HasMore, r2.HasMore, r3.HasMore });
        Assert.Equal([1L, 2L], r1.Events.Select(e => e.Sequence));
        Assert.Equal([3L, 4L], r2.Events.Select(e => e.Sequence));
        Assert.Equal([5L], r3.Events.Select(e => e.Sequence));
        Assert.Equal(2, log.GetSequence(r1.Cursor));
        Assert.Equal(log.HeadCursor, r3.Cursor);
    }

    [Fact]
    public async Task NameFilterAdvancesCursorPastOtherEvents()
    {
        var log = NewLog();
        var start = log.Read(Q(null)).Cursor;
        await Pub(log, "scan.done");
        await Pub(log, "scan.done");

        var r = log.Read(Q(start, names: ["repo.tagged"]));
        Assert.Empty(r.Events);
        Assert.False(r.HasMore);
        Assert.Equal(log.HeadCursor, r.Cursor);

        var again = log.Read(Q(r.Cursor, names: ["repo.tagged"]));
        Assert.Empty(again.Events);
        Assert.Equal(r.Cursor, again.Cursor);

        var tagged = await Pub(log, "repo.tagged");
        Assert.Equal([tagged.EventId], log.Read(Q(r.Cursor, names: ["repo.tagged"])).Events.Select(e => e.EventId));
    }

    [Fact]
    public async Task PagingWithFilterCountsOnlyMatches()
    {
        var log = NewLog();
        var start = log.Read(Q(null)).Cursor;
        await Pub(log, "scan.done");
        var t1 = await Pub(log, "repo.tagged");
        await Pub(log, "scan.done");
        var t2 = await Pub(log, "repo.tagged");
        var r1 = log.Read(Q(start, 1, ["repo.tagged"]));
        Assert.Equal([t1.EventId], r1.Events.Select(e => e.EventId));
        Assert.True(r1.HasMore);
        var r2 = log.Read(Q(r1.Cursor, 1, ["repo.tagged"]));
        Assert.Equal([t2.EventId], r2.Events.Select(e => e.EventId));
        Assert.False(r2.HasMore);
    }

    [Fact]
    public async Task MatchesFilterUsesArgumentsAgainstData()
    {
        var log = NewLog();
        var start = log.Read(Q(null)).Cursor;
        await Pub(log, "repo.pushed", D("dev"));
        var hit = await Pub(log, "repo.pushed", D("main"));
        var r = log.Read(Q(start, names: ["repo.pushed"], args: D("main")));
        Assert.Equal([hit.EventId], r.Events.Select(e => e.EventId));
    }

    [Fact]
    public async Task MatchesIsIgnoredWhenSeveralNamesRequested()
    {
        var log = NewLog();
        var start = log.Read(Q(null)).Cursor;
        await Pub(log, "repo.pushed", D("dev"));
        await Pub(log, "repo.pushed", D("main"));
        var r = log.Read(Q(start, names: ["repo.pushed", "repo.tagged"], args: D("main")));
        Assert.Equal(2, r.Events.Count);
    }

    [Fact]
    public async Task CapacityEvictionTruncatesStaleCursor()
    {
        var log = NewLog(capacity: 3);
        for (var i = 0; i < 5; i++) await Pub(log);
        var stale = EventCursor.Encode(EpochOf(log), 1);
        var r = log.Read(Q(stale));
        Assert.True(r.Truncated);
        Assert.Equal([3L, 4L, 5L], r.Events.Select(e => e.Sequence));
        Assert.Equal(2, log.GetSequence(log.OldestCursor));
    }

    [Fact]
    public async Task CursorExactlyAtOldestMinusOneIsNotTruncated()
    {
        var log = NewLog(capacity: 3);
        for (var i = 0; i < 5; i++) await Pub(log);
        var r = log.Read(Q(log.OldestCursor));
        Assert.False(r.Truncated);
        Assert.Equal([3L, 4L, 5L], r.Events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task CursorFromAnotherLogIsTruncatedAndReadsFromOldest()
    {
        var other = NewLog();
        await Pub(other);
        var foreign = other.HeadCursor;

        var log = NewLog();
        await Pub(log);
        await Pub(log);
        var r = log.Read(Q(foreign));
        Assert.True(r.Truncated);
        Assert.Equal([1L, 2L], r.Events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task CursorBeyondHeadOfSameEpochIsAGap()
    {
        var log = NewLog();
        await Pub(log);
        var forged = EventCursor.Encode(EpochOf(log), 99);
        var r = log.Read(Q(forged));
        Assert.Empty(r.Events);
        Assert.True(r.Truncated);
        Assert.False(r.HasMore);
        Assert.Equal(log.HeadCursor, r.Cursor);
    }

    [Fact]
    public async Task SuppliedIdWithGeneratedPrefixIsRejected()
    {
        var log = NewLog();
        var generated = (await Pub(log)).EventId;
        var prefix = generated[..^1];
        await Assert.ThrowsAsync<ArgumentException>(async () => await Pub(log, id: prefix + "7"));
    }

    [Fact]
    public async Task MutatingPredicateCannotChangeStoredEvents()
    {
        var options = new EventsOptions();
        options.Define(Def("repo.pushed", (args, data) => { data["branch"] = "evil"; args!["branch"] = "evil"; return true; }));
        var log = new InMemoryEventLog(options, _time);
        var cursor = log.HeadCursor;
        await log.PublishAsync("repo.pushed", D("orig"), null, null, Ct);
        var arguments = D("a");
        log.Read(Q(cursor, names: ["repo.pushed"], args: arguments));
        Assert.Equal("a", (string?)arguments["branch"]);
        Assert.Equal("orig", (string?)log.Read(Q(cursor)).Events[0].Data["branch"]);
    }

    [Fact]
    public async Task WaitAboveHeadIsNotCompletedByPublishThatDoesNotPassIt()
    {
        var log = NewLog();
        var wait = log.WaitForEventsAfterAsync(5, Ct);
        await Pub(log);
        await Task.Yield();
        Assert.False(wait.IsCompleted);
        for (var i = 0; i < 4; i++) await Pub(log);
        Assert.False(wait.IsCompleted);
        await Pub(log);
        await wait.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public void TimeProviderDefaultsToSystem()
    {
        var log = new InMemoryEventLog(new EventsOptions());
        Assert.Equal(0, log.GetSequence(log.HeadCursor));
    }

    [Fact]
    public async Task MaxAgeSkipsOldEventsAndTruncates()
    {
        var log = NewLog();
        var cursor = log.HeadCursor;
        var t0 = _time.GetUtcNow();
        await Pub(log, ts: t0);
        _time.Advance(TimeSpan.FromMinutes(10));
        var second = await Pub(log, ts: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(1));

        var r = log.Read(Q(cursor, maxAge: TimeSpan.FromMinutes(5)));
        Assert.Equal([second.EventId], r.Events.Select(e => e.EventId));
        Assert.True(r.Truncated);
    }

    [Fact]
    public async Task MaxAgeThatSkipsNothingIsNotTruncated()
    {
        var log = NewLog();
        var cursor = log.HeadCursor;
        await Pub(log);
        var r = log.Read(Q(cursor, maxAge: TimeSpan.FromMinutes(5)));
        Assert.Single(r.Events);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task RetentionDropsExpiredEventsOnReadAndAppend()
    {
        var log = NewLog(retention: TimeSpan.FromHours(1));
        var cursor = log.HeadCursor;
        await Pub(log);
        _time.Advance(TimeSpan.FromHours(2));
        var r = log.Read(Q(cursor));
        Assert.Empty(r.Events);
        Assert.True(r.Truncated); // the event the cursor had not seen is gone
        Assert.Equal(1, log.GetSequence(log.OldestCursor));
        var fresh = await Pub(log);
        Assert.Equal(2, fresh.Sequence);
        Assert.Equal([fresh.EventId], log.Read(Q(log.OldestCursor)).Events.Select(e => e.EventId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("djE6eDox")] // v1:x:1
    public void MalformedCursorThrows(string cursor)
    {
        var log = NewLog();
        Assert.Throws<InvalidCursorException>(() => log.Read(Q(cursor)));
        Assert.Throws<InvalidCursorException>(() => log.GetSequence(cursor));
    }

    [Fact]
    public async Task CustomEventIdIsUsed_DuplicateReturnsExistingWithoutAppending()
    {
        var log = NewLog();
        var cursor = log.HeadCursor;
        var a = await Pub(log, id: "gh-delivery_1", data: D("first"));
        var dup = await Pub(log, id: "gh-delivery_1", data: D("second"));
        Assert.Equal("gh-delivery_1", a.EventId);
        Assert.Equal(a.Sequence, dup.Sequence);
        Assert.Equal("first", (string?)dup.Data["branch"]);
        Assert.Single(log.Read(Q(cursor)).Events);
        Assert.Equal(1, log.GetSequence(log.HeadCursor));
    }

    [Fact]
    public async Task EvictedEventIdCanBeReused()
    {
        var log = NewLog(capacity: 1);
        await Pub(log, id: "x");
        await Pub(log, id: "y");
        var again = await Pub(log, id: "x");
        Assert.Equal(3, again.Sequence);
    }

    [Theory]
    [InlineData("has.dot")]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("bad/char")]
    [InlineData("trailing-newline\n")]
    public async Task InvalidEventIdThrows(string id)
    {
        var log = NewLog();
        await Assert.ThrowsAsync<ArgumentException>(async () => await Pub(log, id: id));
    }

    [Fact]
    public async Task OverlongEventIdThrows()
    {
        var log = NewLog();
        await Assert.ThrowsAsync<ArgumentException>(async () => await Pub(log, id: new string('a', 129)));
        await Pub(log, id: new string('a', 128));
    }

    [Fact]
    public async Task UnknownEventNameThrows()
    {
        var log = NewLog();
        await Assert.ThrowsAsync<ArgumentException>(async () => await Pub(log, "nope.unknown"));
    }

    [Fact]
    public async Task NewestFirstReturnsNewestNAscending()
    {
        var log = NewLog();
        var start = log.Read(Q(null)).Cursor;
        for (var i = 0; i < 5; i++) await Pub(log, data: D($"b{i}"));
        var r = log.Read(Q(start, 2) with { NewestFirst = true });
        Assert.Equal([4L, 5L], r.Events.Select(e => e.Sequence));
        Assert.False(r.HasMore);
        Assert.Equal(log.HeadCursor, r.Cursor);
    }

    [Fact]
    public async Task DataIsClonedOnPublishAndOnRead()
    {
        var log = NewLog();
        var cursor = log.HeadCursor;
        var data = D("orig");
        var published = await Pub(log, data: data);
        data["branch"] = "mutated-source";
        published.Data["branch"] = "mutated-returned";
        var read = log.Read(Q(cursor)).Events[0];
        Assert.Equal("orig", (string?)read.Data["branch"]);
        read.Data["branch"] = "mutated-read";
        Assert.Equal("orig", (string?)log.Read(Q(cursor)).Events[0].Data["branch"]);
    }

    [Fact]
    public async Task WaitCompletesImmediatelyWhenEventsAlreadyExist()
    {
        var log = NewLog();
        await Pub(log);
        await log.WaitForEventsAfterAsync(0, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task WaitCompletesAfterPublish()
    {
        var log = NewLog();
        var wait = log.WaitForEventsAfterAsync(0, Ct);
        Assert.False(wait.IsCompleted);
        await Pub(log);
        await wait.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var wait2 = log.WaitForEventsAfterAsync(1, Ct);
        Assert.False(wait2.IsCompleted);
        await Pub(log);
        await wait2.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task DuplicatePublishDoesNotWakeWaiters()
    {
        var log = NewLog();
        await Pub(log, id: "a");
        var wait = log.WaitForEventsAfterAsync(1, Ct);
        await Pub(log, id: "a");
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task WaitIsCancellable()
    {
        var log = NewLog();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var wait = log.WaitForEventsAfterAsync(0, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task ConcurrentPublishesGetUniqueContiguousSequences()
    {
        var log = NewLog(capacity: 10_000);
        var cursor = log.HeadCursor;
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => Pub(log), Ct)));
        var r = log.Read(Q(cursor, 1000));
        Assert.Equal(Enumerable.Range(1, 200).Select(i => (long)i), r.Events.Select(e => e.Sequence));
    }

    [Fact]
    public async Task Seek_with_no_cursor_is_the_head_and_CursorAt_round_trips()
    {
        var log = NewLog();
        await Pub(log);
        await Pub(log);

        Assert.Equal((2L, false), log.Seek(null, null));
        Assert.Equal(log.HeadCursor, log.CursorAt(2));
        Assert.Equal(1, log.GetSequence(log.CursorAt(1)));
    }

    [Fact]
    public async Task Seek_follows_the_Read_truncation_rules()
    {
        var log = NewLog(capacity: 2);
        var start = log.HeadCursor;
        for (var i = 0; i < 4; i++) await Pub(log);

        Assert.Equal((2L, true), log.Seek(start, null));                                         // evicted: oldest retained
        Assert.Equal((3L, false), log.Seek(log.CursorAt(3), null));                              // in window
        Assert.Equal((4L, true), log.Seek(log.CursorAt(99), null));                              // ahead of the head: a gap
        Assert.Equal((2L, true), log.Seek(EventCursor.Encode(Guid.NewGuid(), 4), null));         // foreign epoch
        Assert.Throws<InvalidCursorException>(() => log.Seek("not-a-cursor", null));
    }

    [Fact]
    public async Task Seek_skips_events_older_than_maxAge_and_reports_the_gap()
    {
        var log = NewLog();
        var start = log.HeadCursor;
        await Pub(log, ts: _time.GetUtcNow() - TimeSpan.FromMinutes(10));
        await Pub(log, ts: _time.GetUtcNow() - TimeSpan.FromMinutes(9));
        await Pub(log, ts: _time.GetUtcNow() - TimeSpan.FromSeconds(5));

        Assert.Equal((2L, true), log.Seek(start, TimeSpan.FromMinutes(1)));
        Assert.Equal((0L, false), log.Seek(start, TimeSpan.FromHours(1)));
        Assert.Equal((3L, false), log.Seek(null, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Seek_with_out_of_order_timestamps_never_passes_an_event_Read_would_return()
    {
        var log = NewLog();
        var start = log.HeadCursor;
        await Pub(log, ts: _time.GetUtcNow() - TimeSpan.FromMinutes(10));   // too old
        await Pub(log, ts: _time.GetUtcNow() - TimeSpan.FromSeconds(5));    // fresh
        await Pub(log, ts: _time.GetUtcNow() - TimeSpan.FromMinutes(10));   // too old, but after a fresh one
        var age = TimeSpan.FromMinutes(1);

        var seek = log.Seek(start, age);

        Assert.Equal((1L, true), seek);
        Assert.Equal(2L, log.Read(Q(start, maxAge: age)).Events[0].Sequence);
    }

    private static Guid EpochOf(InMemoryEventLog log) => EventCursor.Decode(log.HeadCursor).Epoch;

    [Fact]
    public void DefineRejectsInvalidAndDuplicateNames()
    {
        var o = new EventsOptions();
        o.Define(Def("a.b"));
        Assert.Throws<ArgumentException>(() => o.Define(Def("a.b")));
        foreach (var bad in new[] { "nodot", "Upper.case", "a.", ".a", "a.b\n", "1a.b", "a..b" })
            Assert.Throws<ArgumentException>(() => o.Define(Def(bad)));
    }
}
