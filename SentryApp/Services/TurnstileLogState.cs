using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SentryApp.Services;

public sealed record DeviceMonitorSnapshot(
    string DeviceSerialNumber,
    string DeviceName,
    TurnstileLogEntry? Spotlight,
    IReadOnlyList<TurnstileQueueItem> Queue);

public sealed class TurnstileLogState : IDisposable
{
    private const int MaxEntriesPerLogType = 10;
    private static readonly TimeSpan FeedItemTtl = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceMonitorState> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly IConfiguration _configuration;
    private readonly ILogger<TurnstileLogState> _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly bool _diagnosticsEnabled;

    public event Action? Changed;

    // Retained for callers that need an aggregate view. Multi-device UI should use DeviceSnapshots.
    public TurnstileLogEntry? Spotlight
    {
        get
        {
            lock (_lock)
            {
                return _devices.Values
                    .Where(device => device.Spotlight is not null)
                    .OrderByDescending(device => device.Spotlight!.TimeLogStamp)
                    .Select(device => device.Spotlight)
                    .FirstOrDefault();
            }
        }
    }

    public IReadOnlyList<TurnstileQueueItem> QueueSnapshot
    {
        get
        {
            lock (_lock)
            {
                return _devices.Values
                    .SelectMany(device => device.Queue)
                    .OrderByDescending(item => item.Entry.TimeLogStamp)
                    .ThenByDescending(item => item.EnqueuedAt)
                    .ToList();
            }
        }
    }

    public IReadOnlyList<DeviceMonitorSnapshot> DeviceSnapshots
    {
        get
        {
            lock (_lock)
            {
                return _devices.Values
                    .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(device => device.SerialNumber, StringComparer.OrdinalIgnoreCase)
                    .Select(device => new DeviceMonitorSnapshot(
                        device.SerialNumber,
                        device.Name,
                        device.Spotlight,
                        device.Queue
                            .OrderByDescending(item => item.Entry.TimeLogStamp)
                            .ThenByDescending(item => item.EnqueuedAt)
                            .ToList()))
                    .ToList();
            }
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
        ArgumentNullException.ThrowIfNull(entry);

        var startProcessor = false;
        string deviceKey;

        lock (_lock)
        {
            deviceKey = NormalizeDeviceSerial(entry.DeviceSerialNumber);
            var device = GetOrCreateDeviceUnsafe(deviceKey, entry.DeviceName);

            if (device.QueuedEntryIds.Contains(entry.TimeLogId)
                || !device.SpotlightEntryIds.Add(entry.TimeLogId))
                return;

            device.SpotlightQueue.Enqueue(entry);
            if (!device.IsProcessingSpotlightQueue)
            {
                device.IsProcessingSpotlightQueue = true;
                startProcessor = true;
            }
        }

        if (startProcessor)
            _ = ProcessSpotlightQueueAsync(deviceKey);

        if (_diagnosticsEnabled)
            _logger.LogInformation("Turnstile flow: entry {EntryId} added to the {DeviceSerialNumber} spotlight queue.", entry.TimeLogId, deviceKey);
    }

    private async Task ProcessSpotlightQueueAsync(string deviceKey)
    {
        while (true)
        {
            TurnstileLogEntry entry;

            lock (_lock)
            {
                if (!_devices.TryGetValue(deviceKey, out var device) || device.SpotlightQueue.Count == 0)
                {
                    if (device is not null)
                    {
                        device.Spotlight = null;
                        device.IsProcessingSpotlightQueue = false;
                    }

                    return;
                }

                entry = device.SpotlightQueue.Dequeue();
                device.Spotlight = entry;
            }

            if (_diagnosticsEnabled)
                _logger.LogInformation("Turnstile flow: spotlight set for entry {EntryId} on {DeviceSerialNumber}.", entry.TimeLogId, deviceKey);

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
                if (!_devices.TryGetValue(deviceKey, out var device))
                    return;

                device.SpotlightEntryIds.Remove(entry.TimeLogId);

                if (device.Spotlight?.TimeLogId == entry.TimeLogId)
                    device.Spotlight = null;

                if (device.QueuedEntryIds.Add(entry.TimeLogId))
                {
                    device.Queue.Add(new TurnstileQueueItem
                    {
                        Entry = entry,
                        EnqueuedAt = DateTimeOffset.UtcNow
                    });

                    TrimQueue(device);
                    enqueued = true;
                }
            }

            if (enqueued)
                _ = ExpireFeedItemAsync(deviceKey, entry.TimeLogId, _disposeCts.Token);

            if (_diagnosticsEnabled && enqueued)
                _logger.LogInformation("Turnstile flow: entry {EntryId} moved to the {DeviceSerialNumber} feed queue.", entry.TimeLogId, deviceKey);

            Changed?.Invoke();
        }
    }

    private async Task ExpireFeedItemAsync(string deviceKey, Guid entryId, CancellationToken ct)
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
            if (!_devices.TryGetValue(deviceKey, out var device))
                return;

            if (device.Queue.RemoveAll(item => item.Entry.TimeLogId == entryId) > 0)
            {
                device.QueuedEntryIds.Remove(entryId);
                removed = true;
            }
        }

        if (!removed)
            return;

        if (_diagnosticsEnabled)
            _logger.LogInformation("Turnstile flow: entry {EntryId} expired from the {DeviceSerialNumber} feed queue.", entryId, deviceKey);

        Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposeCts.Cancel();

        lock (_lock)
        {
            foreach (var device in _devices.Values)
            {
                device.SpotlightQueue.Clear();
                device.SpotlightEntryIds.Clear();
            }
        }

        _disposeCts.Dispose();
    }

    private DeviceMonitorState GetOrCreateDeviceUnsafe(string deviceKey, string? deviceName)
    {
        if (_devices.TryGetValue(deviceKey, out var device))
        {
            device.UpdateName(deviceName);
            return device;
        }

        device = new DeviceMonitorState(deviceKey, string.IsNullOrWhiteSpace(deviceName) ? deviceKey : deviceName);
        _devices.Add(deviceKey, device);
        return device;
    }

    private TimeSpan GetHighlightDisplayDuration()
    {
        var highlightMs = _configuration.GetValue<int?>("TurnstilePolling:HighlightDisplayDuration")
            ?? _configuration.GetValue<int?>("TurnstilePolling:HighlightDispayDuration")
            ?? _configuration.GetValue<int?>("HighlightDispayDuration")
            ?? _configuration.GetValue<int?>("HighlightDisplayDuration")
            ?? 3000;

        return TimeSpan.FromMilliseconds(Math.Max(1, highlightMs));
    }

    private static string NormalizeDeviceSerial(string? serialNumber) =>
        string.IsNullOrWhiteSpace(serialNumber) ? "Unknown device" : serialNumber.Trim();

    private static void TrimQueue(DeviceMonitorState device)
    {
        TrimQueueForType(device, IsInLogType);
        TrimQueueForType(device, IsOutOrBreakOutLogType);
    }

    private static void TrimQueueForType(DeviceMonitorState device, Func<TurnstileLogEntry, bool> typePredicate)
    {
        while (device.Queue.Count(item => typePredicate(item.Entry)) > MaxEntriesPerLogType)
        {
            var index = device.Queue.FindIndex(item => typePredicate(item.Entry));
            if (index < 0)
                break;

            device.QueuedEntryIds.Remove(device.Queue[index].Entry.TimeLogId);
            device.Queue.RemoveAt(index);
        }
    }

    private static bool IsInLogType(TurnstileLogEntry entry) => NormalizeLogType(entry.LogType).Contains("IN", StringComparison.Ordinal);

    private static bool IsOutOrBreakOutLogType(TurnstileLogEntry entry)
    {
        var normalized = NormalizeLogType(entry.LogType);
        return normalized.Contains("OUT", StringComparison.Ordinal)
            || normalized.Contains("BREAK", StringComparison.Ordinal);
    }

    private static string NormalizeLogType(string? logType) => string.IsNullOrWhiteSpace(logType)
        ? string.Empty
        : logType.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private sealed class DeviceMonitorState
    {
        public DeviceMonitorState(string serialNumber, string name)
        {
            SerialNumber = serialNumber;
            Name = name;
        }

        public string SerialNumber { get; }
        public string Name { get; private set; }
        public TurnstileLogEntry? Spotlight { get; set; }
        public Queue<TurnstileLogEntry> SpotlightQueue { get; } = new();
        public List<TurnstileQueueItem> Queue { get; } = new();
        public HashSet<Guid> QueuedEntryIds { get; } = new();
        public HashSet<Guid> SpotlightEntryIds { get; } = new();
        public bool IsProcessingSpotlightQueue { get; set; }

        public void UpdateName(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                Name = name;
        }
    }
}
