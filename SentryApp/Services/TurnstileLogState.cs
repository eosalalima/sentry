using Microsoft.Extensions.Configuration;

namespace SentryApp.Services;

public sealed class TurnstileLogState : IDisposable
{
    private const string AllDevicesValue = "1";
    private const int MaxQueueItems = 12;
    private static readonly TimeSpan QueueRetention = TimeSpan.FromSeconds(10);
    private readonly int _defaultHighlightDisplayDurationMs;

    private readonly object _lock = new();
    private readonly List<TurnstileQueueItem> _queue = new();
    private readonly IConfiguration _configuration;
    private readonly HashSet<Guid> _pendingQueueEntries = new();
    private readonly HashSet<Guid> _pendingQueueRemovals = new();
    private readonly CancellationTokenSource _disposeCts = new();
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

    public TurnstileLogState(IConfiguration configuration)
    {
        _configuration = configuration;
        _defaultHighlightDisplayDurationMs = _configuration.GetValue("TurnstilePolling:HighlightDisplayDuration", 3000);
    }

    public void Push(TurnstileLogEntry entry)
    {
        string selectedDeviceSerialSnapshot;

        lock (_lock)
        {
            if (!ShouldAcceptEntry(entry))
                return;

            selectedDeviceSerialSnapshot = _selectedDeviceSerial;
            Spotlight = entry;

            if (_pendingQueueEntries.Add(entry.TimeLogId))
                _ = MoveEntryToQueueAfterDelayAsync(entry, selectedDeviceSerialSnapshot, _disposeCts.Token);
        }

        Changed?.Invoke();
    }

    private async Task MoveEntryToQueueAfterDelayAsync(
        TurnstileLogEntry entry,
        string selectedDeviceSerialSnapshot,
        CancellationToken ct)
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
            if (!ShouldAcceptEntry(entry, selectedDeviceSerialSnapshot))
            {
                if (Spotlight?.TimeLogId == entry.TimeLogId)
                    Spotlight = null;

                _pendingQueueEntries.Remove(entry.TimeLogId);
                return;
            }

            if (_queue.Any(item => item.Entry.TimeLogId == entry.TimeLogId))
            {
                _pendingQueueEntries.Remove(entry.TimeLogId);
                return;
            }

            var queueItem = new TurnstileQueueItem
            {
                Entry = entry,
                EnqueuedAt = DateTimeOffset.UtcNow
            };

            _queue.Add(queueItem);

            if (Spotlight?.TimeLogId == entry.TimeLogId)
                Spotlight = null;

            TrimQueue(selectedDeviceSerialSnapshot);
            _pendingQueueEntries.Remove(entry.TimeLogId);

            if (_pendingQueueRemovals.Add(entry.TimeLogId))
                _ = RemoveEntryFromQueueAfterDelayAsync(entry.TimeLogId, selectedDeviceSerialSnapshot, _disposeCts.Token);
        }

        Changed?.Invoke();
    }

    private async Task RemoveEntryFromQueueAfterDelayAsync(
        Guid entryId,
        string selectedDeviceSerialSnapshot,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(QueueRetention, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_lock)
        {
            var index = _queue.FindIndex(item => item.Entry.TimeLogId == entryId);
            if (index >= 0)
                _queue.RemoveAt(index);

            _pendingQueueRemovals.Remove(entryId);
            TrimQueue(selectedDeviceSerialSnapshot);
        }

        Changed?.Invoke();
    }

    private void TrimQueue(string? selectedDeviceSerial = null)
    {
        var effectiveSerial = string.IsNullOrWhiteSpace(selectedDeviceSerial)
            ? _selectedDeviceSerial
            : selectedDeviceSerial;

        while (CountForSerial(effectiveSerial) > MaxQueueItems)
        {
            if (effectiveSerial == AllDevicesValue)
            {
                RemoveQueueItemAt(0);
                continue;
            }

            var index = _queue.FindIndex(item =>
                string.Equals(item.Entry.DeviceSerialNumber, effectiveSerial, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                break;

            RemoveQueueItemAt(index);
        }
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
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
                    _pendingQueueRemovals.Remove(entryId);

                if (Spotlight is not null && !ShouldAcceptEntry(Spotlight))
                    Spotlight = null;
            }
        }

        Changed?.Invoke();
    }

    private TimeSpan GetHighlightDisplayDuration()
    {
        var highlightMs = _configuration.GetValue("TurnstilePolling:HighlightDisplayDuration", _defaultHighlightDisplayDurationMs);
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

    private int CountForSerial(string selectedDeviceSerial)
    {
        if (selectedDeviceSerial == AllDevicesValue)
            return _queue.Count;

        return _queue.Count(item =>
            string.Equals(item.Entry.DeviceSerialNumber, selectedDeviceSerial, StringComparison.OrdinalIgnoreCase));
    }

    private void RemoveQueueItemAt(int index)
    {
        var entryId = _queue[index].Entry.TimeLogId;
        _queue.RemoveAt(index);
        _pendingQueueRemovals.Remove(entryId);
    }
}
