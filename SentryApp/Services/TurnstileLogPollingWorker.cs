using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;
using SentryApp.Data;
using SentryApp.Data.Query;

namespace SentryApp.Services;

public sealed class TurnstileLogPollingWorker : BackgroundService
{
    private readonly IDbContextFactory<AccessControlDbContext> _dbFactory;
    private readonly TurnstileLogState _state;
    private readonly IPhotoUrlBuilder _photoUrlBuilder;
    private readonly TurnstilePollingController _controller;
    private readonly PersonnelLookupService _personnelLookup;
    private readonly SmsModuleSender _smsSender;
    private readonly IConfiguration _config;
    private readonly ILogger<TurnstileLogPollingWorker> _logger;
    private readonly MonitoringDataLogWriter _dataLogWriter;
    private readonly bool _flowDiagnosticsEnabled;

    private int _intervalMs;
    private int _lookbackSecondsOnStart;
    private int _maxRowsPerPoll;
    private PeriodicTimer? _timer;

    private DateTimeOffset _sinceUtc;
    private readonly Dictionary<Guid, DateTimeOffset> _seen = new();

    private Guid _lastId;
    private DateTimeOffset _highWaterUtc;
    private Guid _highWaterId;
    private bool _isReplayScan;

    public TurnstileLogPollingWorker(
        IDbContextFactory<AccessControlDbContext> dbFactory,
        TurnstileLogState state,
        IPhotoUrlBuilder photoUrlBuilder,
        TurnstilePollingController controller,
        PersonnelLookupService personnelLookup,
        SmsModuleSender smsSender,
        MonitoringDataLogWriter dataLogWriter,
        IConfiguration config,
        ILogger<TurnstileLogPollingWorker> logger)
    {
        _dbFactory = dbFactory;
        _state = state;
        _photoUrlBuilder = photoUrlBuilder;
        _controller = controller;
        _personnelLookup = personnelLookup;
        _smsSender = smsSender;
        _dataLogWriter = dataLogWriter;
        _config = config;
        _logger = logger;
        _flowDiagnosticsEnabled = _config.GetValue("TurnstilePolling:FlowDiagnosticsEnabled", false);

        _intervalMs = config.GetValue("TurnstilePolling:IntervalsMs", config.GetValue("TurnstilePolling:IntervalMs", 500));
        _lookbackSecondsOnStart = config.GetValue("TurnstilePolling:LookbackSecondsOntart", config.GetValue("TurnstilePolling:LookbackSecondsOnStart", 3));
        _maxRowsPerPoll = config.GetValue("TurnstilePolling:MaxRowsPerPoll", 20);

        _lastId = Guid.Empty;
        _controller.StatusChanged += OnPollingStatusChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ResetCursor();

        while (!stoppingToken.IsCancellationRequested)
        {
            var newInterval = _config.GetValue("TurnstilePolling:IntervalsMs", _config.GetValue("TurnstilePolling:IntervalMs", 500));
            if (_timer is null || newInterval != _intervalMs)
            {
                _timer?.Dispose();
                _intervalMs = newInterval;
                _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_intervalMs));
            }

            if (_timer is null)
                break;

            if (!await _timer.WaitForNextTickAsync(stoppingToken))
                break;

            try
            {
                if (_controller.IsActive)
                {
                    _lookbackSecondsOnStart = _config.GetValue("TurnstilePolling:LookbackSecondsOntart", _config.GetValue("TurnstilePolling:LookbackSecondsOnStart", _lookbackSecondsOnStart));
                    _maxRowsPerPoll = _config.GetValue("TurnstilePolling:MaxRowsPerPoll", _maxRowsPerPoll);
                    await PollOnceAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Turnstile polling error.");
            }
        }
    }

    public override void Dispose()
    {
        _controller.StatusChanged -= OnPollingStatusChanged;
        _timer?.Dispose();
        base.Dispose();
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        CleanupSeen();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // IMPORTANT:
        // - We poll DeviceLogs (per your requirement)
        // - We join AccessControl.Personnels (name) and ZKDevices (device name)
        var sql = $@"
SELECT TOP ({_maxRowsPerPoll})
    dl.Id                AS TimeLogId,
    dl.TimeLogStamp      AS TimeLogStamp,
    dl.LogType           AS LogType,
    dl.AccessNumber      AS AccessNumber,
    dl.DeviceSerialNumber AS DeviceSerialNumber,
    dl.VerifyMode        AS DeviceLogVerifyMode,
    dl.VerifyMode        AS TimeLogVerifyMode,

    p.LastName           AS LastName,
    p.FirstName          AS FirstName,
    p.PhotoId            AS PhotoId,

    dl.Event             AS Event,
    dl.EventAddress      AS EventAddress,
    zk.Name              AS DeviceName

FROM DeviceLogs dl
LEFT JOIN [dbo].[Personnels] p
    ON p.AccessNumber = dl.AccessNumber
   AND p.IsDeleted = 0
LEFT JOIN [dbo].[ZKDevices] zk
    ON zk.IsDeleted = 0
   AND zk.SerialNumber = dl.DeviceSerialNumber
WHERE dl.IsDeleted = 0
  AND (
        dl.TimeLogStamp > {{0}}
     OR (dl.TimeLogStamp = {{0}} AND dl.Id > {{1}})
  )
ORDER BY dl.TimeLogStamp ASC, dl.Id ASC;";

        var rows = await db.TurnstileLogRows
            .FromSqlRaw(sql, _sinceUtc, _lastId)
            .AsNoTracking()
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            if (_isReplayScan)
            {
                // The high-water row may have been deleted while the replay was in
                // progress. Resume from the last acknowledged position rather than
                // remaining stuck in replay mode.
                _sinceUtc = _highWaterUtc;
                _lastId = _highWaterId;
                _isReplayScan = false;
            }
            else
            {
                // Re-scan the lookback window after catching up. A timestamp cursor
                // alone misses rows that are committed late with a timestamp behind
                // the cursor (or the same timestamp and a lower GUID).
                _sinceUtc = _highWaterUtc.AddSeconds(-_lookbackSecondsOnStart);
                _lastId = Guid.Empty;
                _isReplayScan = true;
            }

            return;
        }

        foreach (var row in rows)
        {
            if (_seen.ContainsKey(row.TimeLogId))
            {
                if (_flowDiagnosticsEnabled)
                    _logger.LogInformation("Turnstile flow: duplicate row {EntryId} ignored in poll cycle.", row.TimeLogId);

                AdvanceCursor(row);
                FinishReplayAtHighWater(row);
                continue;
            }

            var name = BuildName(row);
            var photoUrl = _photoUrlBuilder.Build(row.PhotoId);
            var personnel = await _personnelLookup.GetPersonnelAsync(row.AccessNumber, ct);
            var smsStatusMessage = SendEntrySms(row, personnel?.MobileNumber);

            var entry = new TurnstileLogEntry
            {
                TimeLogId = row.TimeLogId,
                TimeLogStamp = row.TimeLogStamp,

                LogType = row.LogType,
                PhotoUrl = photoUrl,
                PersonnelName = name,
                Classification = personnel?.Classification,
                AccessNumber = row.AccessNumber,

                DeviceSerialNumber = row.DeviceSerialNumber,
                DeviceName = row.DeviceName ?? row.DeviceSerialNumber,
                VerifyMode = row.DeviceLogVerifyMode ?? row.TimeLogVerifyMode,
                Event = row.Event,
                EventAddress = row.EventAddress,
                SmsStatusMessage = smsStatusMessage
            };

            if (_flowDiagnosticsEnabled)
                _logger.LogInformation("Turnstile flow: new entry {EntryId} detected and pushed to spotlight.", entry.TimeLogId);

            _state.Push(entry);
            await _dataLogWriter.LogPolledAsync(entry, ct);

            // Only acknowledge a row after all processing has completed. Advancing the
            // cursor before this point caused transient SMS/processing failures to drop
            // the entire fetched batch permanently.
            _seen[row.TimeLogId] = DateTimeOffset.UtcNow;
            AdvanceCursor(row);

            if (!_isReplayScan)
            {
                _highWaterUtc = row.TimeLogStamp;
                _highWaterId = row.TimeLogId;
            }

            FinishReplayAtHighWater(row);
        }
    }

    private void AdvanceCursor(TurnstileLogRow row)
    {
        _sinceUtc = row.TimeLogStamp;
        _lastId = row.TimeLogId;
    }

    private void FinishReplayAtHighWater(TurnstileLogRow row)
    {
        if (_isReplayScan
            && row.TimeLogStamp == _highWaterUtc
            && row.TimeLogId == _highWaterId)
        {
            _isReplayScan = false;
        }
    }

    private string SendEntrySms(TurnstileLogRow row, string? mobileNumber)
    {
        var isLiveMode = _config.GetValue("IsLiveMode", true);
        var recipient = SmsRecipientResolver.Resolve(
            isLiveMode,
            mobileNumber,
            _config.GetValue<string>("SmsModule:DemoRecipientNumber"));

        if (recipient is null)
        {
            return isLiveMode
                ? "SMS not sent: missing mobile number."
                : "SMS not sent: demo recipient number is not configured.";
        }

        var message = BuildSmsMessage(row);

        var result = _smsSender.TrySend(recipient, message);
        if (!result.Success)
        {
            _logger.LogWarning("SMS send failed for {MobileNumber}: {Reason}", recipient, result.Response);
            return $"SMS failed: {result.Response}";
        }

        return $"SMS sent to {recipient}.";
    }

    private static string BuildName(TurnstileLogRow row)
    {
        var last = (row.LastName ?? "").Trim();
        var first = (row.FirstName ?? "").Trim();

        if (string.IsNullOrWhiteSpace(last) && string.IsNullOrWhiteSpace(first))
            return "UNKNOWN";

        if (string.IsNullOrWhiteSpace(last))
            return first;

        if (string.IsNullOrWhiteSpace(first))
            return last;

        return $"{last}, {first}";
    }

    private string BuildSmsMessage(TurnstileLogRow row)
    {
        var template = _config.GetValue("SmsModule:MessageFormat", DefaultSmsMessageFormat);
        if (string.IsNullOrWhiteSpace(template))
            template = DefaultSmsMessageFormat;

        var localTime = row.TimeLogStamp.ToLocalTime();
        var lastName = (row.LastName ?? string.Empty).Trim();
        var firstName = (row.FirstName ?? string.Empty).Trim();
        var inOut = ResolveInOut(row.LogType);

        var message = template
            .Replace("{PERSONNEL.LASTNAME}", lastName, StringComparison.OrdinalIgnoreCase)
            .Replace("{PERSONNEL.FIRSTNAME}", firstName, StringComparison.OrdinalIgnoreCase)
            .Replace("{IN or OUT}", inOut, StringComparison.OrdinalIgnoreCase)
            .Replace("{LOGDATE}", localTime.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{LOGTIME}", localTime.ToString("HH:mm:ss"), StringComparison.OrdinalIgnoreCase);

        message = ReplaceDateTimeToken(message, "LOGDATE", localTime, DefaultDateFormat);
        message = ReplaceDateTimeToken(message, "LOGTIME", localTime, DefaultTimeFormat);

        return message;
    }

    private static string ReplaceDateTimeToken(string message, string token, DateTimeOffset localTime, string fallbackFormat)
    {
        var pattern = $@"\{{{token}=DATEFORMAT:(?<format>[^}}]+)\}}";
        message = Regex.Replace(message, pattern, match =>
        {
            var format = match.Groups["format"].Value;
            if (string.IsNullOrWhiteSpace(format))
                format = fallbackFormat;
            return localTime.ToString(format);
        }, RegexOptions.IgnoreCase);

        pattern = $@"\{{{token}=TIMEFORMAT:(?<format>[^}}]+)\}}";
        message = Regex.Replace(message, pattern, match =>
        {
            var format = match.Groups["format"].Value;
            if (string.IsNullOrWhiteSpace(format))
                format = fallbackFormat;
            return localTime.ToString(format);
        }, RegexOptions.IgnoreCase);

        return message;
    }

    private static string ResolveInOut(string? logType)
    {
        var normalized = (logType ?? string.Empty).Trim();
        if (normalized.Contains("OUT", StringComparison.OrdinalIgnoreCase))
            return "OUT";
        if (normalized.Contains("IN", StringComparison.OrdinalIgnoreCase))
            return "IN";
        return "IN/OUT";
    }

    private const string DefaultSmsMessageFormat =
        "{PERSONNEL.LASTNAME}, {PERSONNEL.FIRSTNAME} has {IN or OUT} on {LOGDATE=DATEFORMAT:dd-MMM-yyyy} {LOGTIME=TIMEFORMAT:hh:mm tt} * Auto-generated SMS - do not reply";
    private const string DefaultDateFormat = "dd-MMM-yyyy";
    private const string DefaultTimeFormat = "hh:mm tt";

    private void CleanupSeen()
    {
        // Keep IDs for at least the complete replay window. Otherwise a long
        // lookback would cause acknowledged rows to be emitted again.
        var retention = TimeSpan.FromSeconds(Math.Max(_lookbackSecondsOnStart, 60));
        var cutoff = DateTimeOffset.UtcNow.Subtract(retention);
        var oldKeys = _seen.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
        foreach (var k in oldKeys)
            _seen.Remove(k);
    }

    private void OnPollingStatusChanged(bool isActive)
    {
        if (!isActive)
            return;

        _lookbackSecondsOnStart = _config.GetValue("TurnstilePolling:LookbackSecondsOntart", _config.GetValue("TurnstilePolling:LookbackSecondsOnStart", _lookbackSecondsOnStart));
        _maxRowsPerPoll = _config.GetValue("TurnstilePolling:MaxRowsPerPoll", _maxRowsPerPoll);

        ResetCursor();
    }

    private void ResetCursor()
    {
        _sinceUtc = DateTimeOffset.UtcNow.AddSeconds(-_lookbackSecondsOnStart);
        _lastId = Guid.Empty;
        _highWaterUtc = _sinceUtc;
        _highWaterId = Guid.Empty;
        _isReplayScan = false;
    }
}
