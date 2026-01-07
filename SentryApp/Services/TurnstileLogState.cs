namespace SentryApp.Services;

public sealed class TurnstileLogState : IDisposable
{
    private const int MaxQueueItems = 24;

    private readonly object _lock = new();
    private readonly List<TurnstileQueueItem> _queue = new();
    private CancellationTokenSource? _spotlightCts;

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

    public TurnstileLogState()
    {
    }

    public void Enqueue(TurnstileLogEntry entry)
    {
        lock (_lock)
        {
            // cancel previous delayed move
            _spotlightCts?.Cancel();
            _spotlightCts?.Dispose();
            _spotlightCts = new CancellationTokenSource();

            // if there was a previous spotlight, move it immediately to queue
            if (Spotlight is not null)
            {
                _queue.Insert(0, new TurnstileQueueItem
                {
                    Entry = Spotlight
                });

                TrimQueue();
            }

            Spotlight = entry;

            _ = MoveSpotlightToQueueAfterDelayAsync(_spotlightCts.Token);
        }

        Changed?.Invoke();
    }

    private async Task MoveSpotlightToQueueAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_lock)
        {
            if (Spotlight is not null)
            {
                _queue.Insert(0, new TurnstileQueueItem
                {
                    Entry = Spotlight
                });
                Spotlight = null;

                TrimQueue();
            }
        }

        Changed?.Invoke();
    }

    private void TrimQueue()
    {
        while (_queue.Count > MaxQueueItems)
            _queue.RemoveAt(_queue.Count - 1);
    }

    public void Dispose()
    {
        _spotlightCts?.Cancel();
        _spotlightCts?.Dispose();
    }
}
