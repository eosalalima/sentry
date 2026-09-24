using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SentryApp.Services;

namespace SentryApp.Tests;

public class TurnstileLogStateTests
{
    [Fact]
    public async Task Push_MovesToQueueAfterHighlight_AndExpiresAfterTtl()
    {
        using var state = CreateState(highlightMs: 50);
        var entry = CreateEntry(Guid.NewGuid(), "IN");

        state.Push(entry);

        Assert.NotNull(state.Spotlight);
        Assert.Empty(state.QueueSnapshot);

        await EventuallyAsync(() => state.QueueSnapshot.Any(item => item.Entry.TimeLogId == entry.TimeLogId), TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(state.QueueSnapshot, item => item.Entry.TimeLogId == entry.TimeLogId && item.EnqueuedAt == default);

        await EventuallyAsync(() => !state.QueueSnapshot.Any(item => item.Entry.TimeLogId == entry.TimeLogId), TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task Push_EnforcesFifoCapacityPerLogType()
    {
        using var state = CreateState(highlightMs: 1);
        var ids = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToList();

        foreach (var id in ids)
            state.Push(CreateEntry(id, "IN"));

        await EventuallyAsync(() => state.QueueSnapshot.Count(item => item.Entry.LogType == "IN") == 10, TimeSpan.FromSeconds(2));

        var inEntries = state.QueueSnapshot.Where(item => item.Entry.LogType == "IN").Select(item => item.Entry.TimeLogId).ToList();
        Assert.DoesNotContain(ids[0], inEntries);
        Assert.Contains(ids[^1], inEntries);
    }

    [Fact]
    public async Task QueueSnapshot_OrdersLatestRecordsFirst()
    {
        using var state = CreateState(highlightMs: 1);
        var older = CreateEntry(Guid.NewGuid(), "IN", DateTimeOffset.UtcNow.AddMinutes(-1));
        var latest = CreateEntry(Guid.NewGuid(), "IN", DateTimeOffset.UtcNow);

        // Push out of timestamp order to ensure the snapshot order comes from the
        // device log timestamp rather than task scheduling or insertion order.
        state.Push(latest);
        state.Push(older);

        await EventuallyAsync(() => state.QueueSnapshot.Count == 2, TimeSpan.FromSeconds(2));

        Assert.Collection(
            state.QueueSnapshot,
            item => Assert.Equal(latest.TimeLogId, item.Entry.TimeLogId),
            item => Assert.Equal(older.TimeLogId, item.Entry.TimeLogId));
    }

    [Fact]
    public async Task Push_QueuesBurstSoEveryRecordIsShownInSpotlight()
    {
        using var state = CreateState(highlightMs: 75);
        var entries = Enumerable.Range(0, 3)
            .Select(_ => CreateEntry(Guid.NewGuid(), "IN"))
            .ToList();
        var displayedEntryIds = new List<Guid>();
        var displayedEntryIdsLock = new object();

        state.Changed += CaptureSpotlight;

        foreach (var entry in entries)
            state.Push(entry);

        await EventuallyAsync(() =>
        {
            lock (displayedEntryIdsLock)
                return displayedEntryIds.Count == entries.Count;
        }, TimeSpan.FromSeconds(2));

        lock (displayedEntryIdsLock)
            Assert.Equal(entries.Select(entry => entry.TimeLogId), displayedEntryIds);

        void CaptureSpotlight()
        {
            var spotlight = state.Spotlight;
            if (spotlight is null)
                return;

            lock (displayedEntryIdsLock)
            {
                if (!displayedEntryIds.Contains(spotlight.TimeLogId))
                    displayedEntryIds.Add(spotlight.TimeLogId);
            }
        }
    }

    private static TurnstileLogState CreateState(int highlightMs)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TurnstilePolling:HighlightDisplayDuration"] = highlightMs.ToString()
            })
            .Build();

        return new TurnstileLogState(cfg, NullLogger<TurnstileLogState>.Instance);
    }

    private static TurnstileLogEntry CreateEntry(
        Guid id,
        string logType,
        DateTimeOffset? timeLogStamp = null) => new()
    {
        TimeLogId = id,
        TimeLogStamp = timeLogStamp ?? DateTimeOffset.UtcNow,
        LogType = logType,
        PersonnelName = "Test"
    };

    private static async Task EventuallyAsync(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            if (condition())
                return;

            await Task.Delay(25);
        }

        Assert.True(false, "Condition was not met within timeout.");
    }
}
