using Microsoft.Extensions.Configuration;

namespace SentryApp.Services;

public sealed class TurnstileLogState : IDisposable
{
    private const int MaxQueueItems = 12;
    private readonly int _defaultHighlightDisplayDurationMs;

    private readonly object _lock = new();
    private readonly List<TurnstileQueueItem> _queue = new();
    private readonly IConfiguration _configuration;
    private readonly HashSet<Guid> _pendingQueueEntries = new();
    private readonly CancellationTokenSource _disposeCts = new();

    public event Action? Changed;

    public TurnstileLogEntry? Spotlight { get; private set; }

    public IReadOnlyList<TurnstileQueueItem> QueueSnapshot
    {
        get
        {
            lock (_lock)
                return _queue.ToList();
        }
    }

    public TurnstileLogState(IConfiguration configuration)
    {
        _configuration = configuration;
        _defaultHighlightDisplayDurationMs = _configuration.GetValue("TurnstilePolling:HighlightDisplayDuration", 3000);
    }

    public void Push(TurnstileLogEntry entry)
    {
        lock (_lock)
        {
            Spotlight = entry;

            if (_pendingQueueEntries.Add(entry.TimeLogId))
                _ = MoveEntryToQueueAfterDelayAsync(entry, _disposeCts.Token);
        }

        Changed?.Invoke();
    }

    private async Task MoveEntryToQueueAfterDelayAsync(TurnstileLogEntry entry, CancellationToken ct)
    {
        try
        {
            var delay = GetHighlightDisplayDuration();
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_lock)
        {
            if (_queue.Any(item => item.Entry.TimeLogId == entry.TimeLogId))
            {
                _pendingQueueEntries.Remove(entry.TimeLogId);
                return;
            }

            _queue.Add(new TurnstileQueueItem
            {
                Entry = entry
            });

            if (Spotlight?.TimeLogId == entry.TimeLogId)
                Spotlight = null;

            TrimQueue();
            _pendingQueueEntries.Remove(entry.TimeLogId);
        }

        Changed?.Invoke();
    }

    private void TrimQueue()
    {
        while (_queue.Count > MaxQueueItems)
            _queue.RemoveAt(0);
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private TimeSpan GetHighlightDisplayDuration()
    {
        var highlightMs = _configuration.GetValue("TurnstilePolling:HighlightDisplayDuration", _defaultHighlightDisplayDurationMs);
        if (highlightMs < 1)
            highlightMs = 1;

        return TimeSpan.FromMilliseconds(highlightMs);
    }
}
