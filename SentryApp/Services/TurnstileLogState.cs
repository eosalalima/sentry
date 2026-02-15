using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SentryApp.Services;

public sealed class TurnstileLogState : IDisposable
{
    private const string AllDevicesValue = "1";
    private const int MaxEntriesPerLogType = 10;
    private static readonly TimeSpan FeedItemTtl = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly List<TurnstileQueueItem> _queue = new();
    private readonly IConfiguration _configuration;
    private readonly ILogger<TurnstileLogState> _logger;
    private readonly HashSet<Guid> _queuedEntryIds = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _scheduledTransitions = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly bool _diagnosticsEnabled;

    private string _selectedDeviceSerial = AllDevicesValue;

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

    public TurnstileLogState(IConfiguration configuration, ILogger<TurnstileLogState> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _diagnosticsEnabled = _configuration.GetValue("TurnstilePolling:FlowDiagnosticsEnabled", false);
    }

    public void Push(TurnstileLogEntry entry)
    {
        string selectedDeviceSerialSnapshot;
        var shouldNotify = false;
        CancellationTokenSource? transitionCts = null;

        lock (_lock)
        {
            if (!ShouldAcceptEntry(entry))
                return;

            selectedDeviceSerialSnapshot = _selectedDeviceSerial;
            Spotlight = entry;
            shouldNotify = true;

            if (_scheduledTransitions.TryGetValue(entry.TimeLogId, out var existingTransition))
            {
                existingTransition.Cancel();
                existingTransition.Dispose();
            }

            transitionCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            _scheduledTransitions[entry.TimeLogId] = transitionCts;
        }

        if (transitionCts is not null)
            _ = MoveToFeedAfterDelayAsync(entry, selectedDeviceSerialSnapshot, transitionCts.Token);

        if (_diagnosticsEnabled)
            _logger.LogInformation("Turnstile flow: spotlight set for entry {EntryId}.", entry.TimeLogId);

        if (shouldNotify)
            Changed?.Invoke();
    }

    private async Task MoveToFeedAfterDelayAsync(
        TurnstileLogEntry entry,
        string selectedDeviceSerialSnapshot,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(GetHighlightDisplayDuration(), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var shouldNotify = false;
        var enqueued = false;

        lock (_lock)
        {
            if (_scheduledTransitions.Remove(entry.TimeLogId, out var scheduledTransition))
                scheduledTransition.Dispose();

            if (!ShouldAcceptEntry(entry, selectedDeviceSerialSnapshot))
            {
                if (Spotlight?.TimeLogId == entry.TimeLogId)
                {
                    Spotlight = null;
                    shouldNotify = true;
                }
            }
            else
            {
                if (Spotlight?.TimeLogId == entry.TimeLogId)
                {
                    Spotlight = null;
                    shouldNotify = true;
                }

                if (!_queuedEntryIds.Contains(entry.TimeLogId))
                {
                    _queue.Add(new TurnstileQueueItem
                    {
                        Entry = entry,
                        EnqueuedAt = DateTimeOffset.UtcNow
                    });

                    _queuedEntryIds.Add(entry.TimeLogId);
                    TrimQueue();
                    enqueued = true;
                    shouldNotify = true;
                }
                else
                {
                    var index = _queue.FindIndex(item => item.Entry.TimeLogId == entry.TimeLogId);
                    if (index >= 0)
                    {
                        _queue[index] = new TurnstileQueueItem
                        {
                            Entry = entry,
                            EnqueuedAt = DateTimeOffset.UtcNow
                        };

                        shouldNotify = true;
                    }
                }
            }
        }

        if (enqueued)
            _ = ExpireFeedItemAsync(entry.TimeLogId, ct);

        if (_diagnosticsEnabled)
            _logger.LogInformation("Turnstile flow: entry {EntryId} moved to feed queue.", entry.TimeLogId);

        if (shouldNotify)
            Changed?.Invoke();
    }

    private async Task ExpireFeedItemAsync(Guid entryId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(FeedItemTtl, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var removed = false;

        lock (_lock)
        {
            var removedCount = _queue.RemoveAll(item => item.Entry.TimeLogId == entryId);
            if (removedCount > 0)
            {
                _queuedEntryIds.Remove(entryId);
                removed = true;
            }
        }

        if (!removed)
            return;

        if (_diagnosticsEnabled)
            _logger.LogInformation("Turnstile flow: entry {EntryId} expired from feed queue.", entryId);

        Changed?.Invoke();
    }

    private void TrimQueue()
    {
        TrimQueueForType(IsInLogType);
        TrimQueueForType(IsOutOrBreakOutLogType);
    }

    public void Dispose()
    {
        _disposeCts.Cancel();

        lock (_lock)
        {
            foreach (var transition in _scheduledTransitions.Values)
                transition.Dispose();

            _scheduledTransitions.Clear();
        }

        _disposeCts.Dispose();
    }

    public void UpdateSelectedDeviceSerial(string? selectedDeviceSerial)
    {
        var normalized = string.IsNullOrWhiteSpace(selectedDeviceSerial) ? AllDevicesValue : selectedDeviceSerial;

        lock (_lock)
        {
            if (string.Equals(_selectedDeviceSerial, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _selectedDeviceSerial = normalized;

            if (_selectedDeviceSerial != AllDevicesValue)
            {
                var removedItems = _queue
                    .Where(item => !ShouldAcceptEntry(item.Entry))
                    .Select(item => item.Entry.TimeLogId)
                    .ToList();

                _queue.RemoveAll(item => !ShouldAcceptEntry(item.Entry));

                foreach (var entryId in removedItems)
                    _queuedEntryIds.Remove(entryId);

                if (Spotlight is not null && !ShouldAcceptEntry(Spotlight))
                    Spotlight = null;
            }
        }

        Changed?.Invoke();
    }

    private TimeSpan GetHighlightDisplayDuration()
    {
        var highlightMs = _configuration.GetValue<int?>("TurnstilePolling:HighlightDisplayDuration")
            ?? _configuration.GetValue<int?>("TurnstilePolling:HighlightDispayDuration")
            ?? _configuration.GetValue<int?>("HighlightDispayDuration")
            ?? _configuration.GetValue<int?>("HighlightDisplayDuration")
            ?? 3000;

        if (highlightMs < 1)
            highlightMs = 1;

        return TimeSpan.FromMilliseconds(highlightMs);
    }

    private bool ShouldAcceptEntry(TurnstileLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_selectedDeviceSerial) || _selectedDeviceSerial == AllDevicesValue)
            return true;

        return string.Equals(entry.DeviceSerialNumber, _selectedDeviceSerial, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldAcceptEntry(TurnstileLogEntry entry, string selectedDeviceSerial)
    {
        if (string.IsNullOrWhiteSpace(selectedDeviceSerial) || selectedDeviceSerial == AllDevicesValue)
            return true;

        return string.Equals(entry.DeviceSerialNumber, selectedDeviceSerial, StringComparison.OrdinalIgnoreCase);
    }

    private void TrimQueueForType(Func<TurnstileLogEntry, bool> typePredicate)
    {
        while (CountForType(typePredicate) > MaxEntriesPerLogType)
        {
            var index = _queue.FindIndex(item => typePredicate(item.Entry));
            if (index < 0)
                break;

            _queuedEntryIds.Remove(_queue[index].Entry.TimeLogId);
            _queue.RemoveAt(index);
        }
    }

    private int CountForType(Func<TurnstileLogEntry, bool> typePredicate)
        => _queue.Count(item => typePredicate(item.Entry));

    private static bool IsInLogType(TurnstileLogEntry entry)
        => string.Equals(entry.LogType?.Trim(), "IN", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutOrBreakOutLogType(TurnstileLogEntry entry)
    {
        var normalized = NormalizeLogType(entry.LogType);
        return normalized is "OUT" or "BREAKOUT";
    }

    private static string NormalizeLogType(string? logType)
    {
        if (string.IsNullOrWhiteSpace(logType))
            return string.Empty;

        return logType
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }
}
