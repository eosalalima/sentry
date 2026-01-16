using Microsoft.Extensions.Configuration;

namespace SentryApp.Services;

public sealed class TurnstileLogState : IDisposable
{
    private const int MaxQueueItems = 12;
    private const int DefaultHighlightDisplayDurationMs = 3000;
    private const string AllDevicesValue = "1";

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
    }

    public void Push(TurnstileLogEntry entry)
    {
        lock (_lock)
        {
            // cancel previous delayed move
            _spotlightCts?.Cancel();
            _spotlightCts?.Dispose();
            _spotlightCts = new CancellationTokenSource();

            if (Spotlight is not null)
                AddToQueue(Spotlight);

            Spotlight = entry;

            _ = ClearSpotlightAfterDelayAsync(entry, _spotlightCts.Token);
        }

        Changed?.Invoke();
    }

    private async Task ClearSpotlightAfterDelayAsync(TurnstileLogEntry entry, CancellationToken ct)
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
            if (!ReferenceEquals(Spotlight, entry))
                return;

            Spotlight = null;
            AddToQueue(entry);
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
        var selectedDeviceSerial = GetSelectedDeviceSerial();

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
        _spotlightCts?.Cancel();
        _spotlightCts?.Dispose();
    }

    private TimeSpan GetHighlightDisplayDuration()
    {
        var highlightMs = _configuration.GetValue("TurnstilePolling:HighlightDisplayDuration", DefaultHighlightDisplayDurationMs);
        if (highlightMs < 1)
            highlightMs = 1;

        return TimeSpan.FromMilliseconds(highlightMs);
    }

    private string GetSelectedDeviceSerial()
    {
        var selectedDeviceSerial = _configuration.GetValue<string>("SelectedDeviceSerial");
        return string.IsNullOrWhiteSpace(selectedDeviceSerial) ? AllDevicesValue : selectedDeviceSerial;
    }

    private int CountForSerial(string selectedDeviceSerial)
    {
        if (selectedDeviceSerial == AllDevicesValue)
            return _queue.Count;

        return _queue.Count(item =>
            string.Equals(item.Entry.DeviceSerialNumber, selectedDeviceSerial, StringComparison.OrdinalIgnoreCase));
    }
}
