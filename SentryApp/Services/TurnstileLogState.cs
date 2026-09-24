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
    private readonly Queue<TurnstileLogEntry> _spotlightQueue = new();
    private readonly IConfiguration _configuration;
    private readonly ILogger<TurnstileLogState> _logger;
    private readonly HashSet<Guid> _queuedEntryIds = new();
    private readonly HashSet<Guid> _spotlightEntryIds = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly bool _diagnosticsEnabled;

    private string _selectedDeviceSerial = AllDevicesValue;
    private bool _isProcessingSpotlightQueue;

    public event Action? Changed;

    public TurnstileLogEntry? Spotlight { get; private set; }

    public IReadOnlyList<TurnstileQueueItem> QueueSnapshot
    {
        get
        {
            lock (_lock)
                return _queue
                    .OrderByDescending(item => item.Entry.TimeLogStamp)
                    .ThenByDescending(item => item.EnqueuedAt)
                    .ToList();
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
        var startProcessor = false;

        lock (_lock)
        {
            if (!ShouldAcceptEntry(entry)
                || _queuedEntryIds.Contains(entry.TimeLogId)
                || !_spotlightEntryIds.Add(entry.TimeLogId))
                return;

            _spotlightQueue.Enqueue(entry);
            if (!_isProcessingSpotlightQueue)
            {
                _isProcessingSpotlightQueue = true;
                startProcessor = true;
            }
        }

        if (startProcessor)
            _ = ProcessSpotlightQueueAsync();

        if (_diagnosticsEnabled)
            _logger.LogInformation("Turnstile flow: entry {EntryId} added to the spotlight queue.", entry.TimeLogId);
    }

    private async Task ProcessSpotlightQueueAsync()
    {
        while (true)
        {
            TurnstileLogEntry entry;

            lock (_lock)
            {
                do
                {
                    if (_spotlightQueue.Count == 0)
                    {
                        Spotlight = null;
                        _isProcessingSpotlightQueue = false;
                        return;
                    }

                    entry = _spotlightQueue.Dequeue();
                    if (!ShouldAcceptEntry(entry))
                        _spotlightEntryIds.Remove(entry.TimeLogId);
                }
                while (!ShouldAcceptEntry(entry));

                Spotlight = entry;
            }

            if (_diagnosticsEnabled)
                _logger.LogInformation("Turnstile flow: spotlight set for entry {EntryId}.", entry.TimeLogId);

            Changed?.Invoke();

            try
            {
                await Task.Delay(GetHighlightDisplayDuration(), _disposeCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var enqueued = false;
            lock (_lock)
            {
                _spotlightEntryIds.Remove(entry.TimeLogId);

                if (Spotlight?.TimeLogId == entry.TimeLogId)
                    Spotlight = null;

                if (ShouldAcceptEntry(entry) && _queuedEntryIds.Add(entry.TimeLogId))
                {
                    _queue.Add(new TurnstileQueueItem
                    {
                        Entry = entry,
                        EnqueuedAt = DateTimeOffset.UtcNow
                    });

                    TrimQueue();
                    enqueued = true;
                }
            }

            if (enqueued)
                _ = ExpireFeedItemAsync(entry.TimeLogId, _disposeCts.Token);

            if (_diagnosticsEnabled && enqueued)
                _logger.LogInformation("Turnstile flow: entry {EntryId} moved to feed queue.", entry.TimeLogId);

            Changed?.Invoke();
        }
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
            _spotlightQueue.Clear();
            _spotlightEntryIds.Clear();
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

            if (_spotlightQueue.Count > 0)
            {
                var retainedEntries = _spotlightQueue.Where(ShouldAcceptEntry).ToList();
                _spotlightQueue.Clear();
                foreach (var entry in retainedEntries)
                    _spotlightQueue.Enqueue(entry);

                _spotlightEntryIds.Clear();
                foreach (var entry in retainedEntries)
                    _spotlightEntryIds.Add(entry.TimeLogId);

                if (Spotlight is not null)
                    _spotlightEntryIds.Add(Spotlight.TimeLogId);
            }

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
    {
        var normalized = NormalizeLogType(entry.LogType);
        return normalized.Contains("IN", StringComparison.Ordinal);
    }

    private static bool IsOutOrBreakOutLogType(TurnstileLogEntry entry)
    {
        var normalized = NormalizeLogType(entry.LogType);
        return normalized.Contains("OUT", StringComparison.Ordinal)
            || normalized.Contains("BREAK", StringComparison.Ordinal);
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
