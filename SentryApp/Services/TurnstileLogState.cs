using Microsoft.Extensions.Configuration;

namespace SentryApp.Services;

public sealed class TurnstileLogState : IDisposable
{
    private const int MaxQueueItems = 12;
    private readonly int _defaultHighlightDisplayDurationMs;

    private readonly object _lock = new();
    private readonly List<TurnstileQueueItem> _queue = new();
    private readonly IConfiguration _configuration;
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

    public TurnstileLogState(IConfiguration configuration)
    {
        _configuration = configuration;
        _defaultHighlightDisplayDurationMs = _configuration.GetValue("TurnstilePolling:HighlightDisplayDuration", 3000);
    }

    public void Push(TurnstileLogEntry entry)
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
                _queue.Add(new TurnstileQueueItem
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
            var delay = GetHighlightDisplayDuration();
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_lock)
        {
            if (Spotlight is not null)
            {
                _queue.Add(new TurnstileQueueItem
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
            _queue.RemoveAt(0);
    }

    public void Dispose()
    {
        _spotlightCts?.Cancel();
        _spotlightCts?.Dispose();
    }

    private TimeSpan GetHighlightDisplayDuration()
    {
        var highlightMs = _configuration.GetValue("TurnstilePolling:HighlightDisplayDuration", _defaultHighlightDisplayDurationMs);
        if (highlightMs < 1)
            highlightMs = 1;

        return TimeSpan.FromMilliseconds(highlightMs);
    }
}
