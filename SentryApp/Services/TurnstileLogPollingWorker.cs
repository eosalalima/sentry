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

    private int _intervalMs;
    private int _lookbackSecondsOnStart;
    private int _maxRowsPerPoll;
    private PeriodicTimer? _timer;

    private DateTimeOffset _sinceUtc;
    private readonly Dictionary<Guid, DateTimeOffset> _seen = new();

    private DateTimeOffset _lastStampUtc;
    private Guid _lastId;

    public TurnstileLogPollingWorker(
        IDbContextFactory<AccessControlDbContext> dbFactory,
        TurnstileLogState state,
        IPhotoUrlBuilder photoUrlBuilder,
        TurnstilePollingController controller,
        PersonnelLookupService personnelLookup,
        SmsModuleSender smsSender,
        IConfiguration config,
        ILogger<TurnstileLogPollingWorker> logger)
    {
        _dbFactory = dbFactory;
        _state = state;
        _photoUrlBuilder = photoUrlBuilder;
        _controller = controller;
        _personnelLookup = personnelLookup;
        _smsSender = smsSender;
        _config = config;
        _logger = logger;

        _intervalMs = config.GetValue("TurnstilePolling:IntervalsMs", config.GetValue("TurnstilePolling:IntervalMs", 500));
        _lookbackSecondsOnStart = config.GetValue("TurnstilePolling:LookbackSecondsOntart", config.GetValue("TurnstilePolling:LookbackSecondsOnStart", 3));
        _maxRowsPerPoll = config.GetValue("TurnstilePolling:MaxRowsPerPoll", 20);

        _lastStampUtc = DateTimeOffset.UtcNow.AddSeconds(-_lookbackSecondsOnStart);
        _lastId = Guid.Empty;
        _controller.StatusChanged += OnPollingStatusChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _sinceUtc = DateTimeOffset.UtcNow.AddSeconds(-_lookbackSecondsOnStart);

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
        // - We join PersonnelUnion (name/photo) and ZKDevices (device name)
        var hasPersonnelUnion = await HasPersonnelUnionAsync(db, ct);
        var sql = hasPersonnelUnion
            ? $@"
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
LEFT JOIN PersonnelUnion p
    ON p.AccessNumber = dl.AccessNumber
LEFT JOIN ZKDevices zk
    ON zk.IsDeleted = 0
   AND zk.SerialNumber = dl.DeviceSerialNumber
WHERE dl.IsDeleted = 0
  AND (
        dl.TimeLogStamp > {{0}}
     OR (dl.TimeLogStamp = {{0}} AND dl.Id > {{1}})
  )
ORDER BY dl.TimeLogStamp ASC, dl.Id ASC;";
            : $@"
SELECT TOP ({_maxRowsPerPoll})
    dl.Id                AS TimeLogId,
    dl.TimeLogStamp      AS TimeLogStamp,
    dl.LogType           AS LogType,
    dl.AccessNumber      AS AccessNumber,
    dl.DeviceSerialNumber AS DeviceSerialNumber,
    dl.VerifyMode        AS DeviceLogVerifyMode,
    dl.VerifyMode        AS TimeLogVerifyMode,

    NULL                 AS LastName,
    NULL                 AS FirstName,
    NULL                 AS PhotoId,

    dl.Event             AS Event,
    dl.EventAddress      AS EventAddress,
    zk.Name              AS DeviceName

FROM DeviceLogs dl
LEFT JOIN ZKDevices zk
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
            return;

        if (rows.Count > 0)
        {
            var last = rows[^1];
            _lastStampUtc = last.TimeLogStamp;
            _lastId = last.TimeLogId;
        }

        // advance watermark to max timestamp we saw (use >= in query + _seen to avoid missing same-timestamp rows)
        var maxStamp = rows.Max(r => r.TimeLogStamp);
        _sinceUtc = maxStamp;

        foreach (var row in rows)
        {
            if (_seen.ContainsKey(row.TimeLogId))
                continue;

            _seen[row.TimeLogId] = DateTimeOffset.UtcNow;

            var name = BuildName(row);
            var photoUrl = _photoUrlBuilder.Build(row.PhotoId);
            var smsStatusMessage = await SendEntrySmsAsync(row, ct);

            var entry = new TurnstileLogEntry
            {
                TimeLogId = row.TimeLogId,
                TimeLogStamp = row.TimeLogStamp,

                LogType = row.LogType,
                PhotoUrl = photoUrl,
                PersonnelName = name,
                AccessNumber = row.AccessNumber,

                DeviceSerialNumber = row.DeviceSerialNumber,
                DeviceName = row.DeviceName ?? row.DeviceSerialNumber,
                VerifyMode = row.DeviceLogVerifyMode ?? row.TimeLogVerifyMode,
                Event = row.Event,
                EventAddress = row.EventAddress,
                SmsStatusMessage = smsStatusMessage
            };

            _state.Push(entry);
        }
    }

    private async Task<string> SendEntrySmsAsync(TurnstileLogRow row, CancellationToken ct)
    {
        var mobileNumber = await _personnelLookup.GetMobileNumberAsync(row.AccessNumber, ct);
        if (string.IsNullOrWhiteSpace(mobileNumber))
        {
            return "SMS not sent: missing mobile number.";
        }

        var message = BuildSmsMessage(row);

        var result = _smsSender.TrySend(mobileNumber, message);
        if (!result.Success)
        {
            _logger.LogWarning("SMS send failed for {MobileNumber}: {Reason}", mobileNumber, result.Response);
            return $"SMS failed: {result.Response}";
        }

        return $"SMS sent to {mobileNumber}.";
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

    private static async Task<bool> HasPersonnelUnionAsync(AccessControlDbContext db, CancellationToken ct)
    {
        var results = await db.Database.SqlQueryRaw<int>(@"
SELECT 1
WHERE OBJECT_ID(N'dbo.PersonnelUnion', N'V') IS NOT NULL
   OR OBJECT_ID(N'dbo.PersonnelUnion', N'U') IS NOT NULL;").ToListAsync(ct);

        return results.Count > 0;
    }

    private const string DefaultSmsMessageFormat =
        "{PERSONNEL.LASTNAME}, {PERSONNEL.FIRSTNAME} has {IN or OUT} on {LOGDATE=DATEFORMAT:dd-MMM-yyyy} {LOGTIME=TIMEFORMAT:hh:mm tt} * Auto-generated SMS - do not reply";
    private const string DefaultDateFormat = "dd-MMM-yyyy";
    private const string DefaultTimeFormat = "hh:mm tt";

    private void CleanupSeen()
    {
        // keep "seen" only for a short window so the dictionary doesn't grow forever
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
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

        _sinceUtc = DateTimeOffset.UtcNow.AddSeconds(-_lookbackSecondsOnStart);
        _lastStampUtc = _sinceUtc;
        _lastId = Guid.Empty;
    }
}
