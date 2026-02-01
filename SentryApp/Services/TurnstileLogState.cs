using Microsoft.Extensions.Configuration;

namespace SentryApp.Services;

public sealed class TurnstileLogState : IDisposable
{
    private const string AllDevicesValue = "1";
    private const int MaxQueueItems = 12;
    private readonly int _defaultHighlightDisplayDurationMs;

    private readonly object _lock = new();
    private readonly List<TurnstileQueueItem> _queue = new();
    private readonly IConfiguration _configuration;
    private readonly HashSet<Guid> _pendingQueueEntries = new();
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
        lock (_lock)
        {
            if (!ShouldAcceptEntry(entry))
                return;

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
            if (!ShouldAcceptEntry(entry))
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

    private void AddToQueue(TurnstileLogEntry entry)
    {
        _queue.Add(new TurnstileQueueItem
        {
            Entry = entry
        });

        TrimQueue();
    }

    private void TrimQueue()
    {
        var selectedDeviceSerial = _selectedDeviceSerial;

        while (CountForSerial(selectedDeviceSerial) > MaxQueueItems)
        {
            if (selectedDeviceSerial == AllDevicesValue)
            {
                _queue.RemoveAt(0);
                continue;
            }

            var index = _queue.FindIndex(item =>
                string.Equals(item.Entry.DeviceSerialNumber, selectedDeviceSerial, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                break;

            _queue.RemoveAt(index);
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
                _queue.RemoveAll(item => !ShouldAcceptEntry(item.Entry));

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

    private int CountForSerial(string selectedDeviceSerial)
    {
        if (selectedDeviceSerial == AllDevicesValue)
            return _queue.Count;

        return _queue.Count(item =>
            string.Equals(item.Entry.DeviceSerialNumber, selectedDeviceSerial, StringComparison.OrdinalIgnoreCase));
    }
}
