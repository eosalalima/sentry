using Microsoft.EntityFrameworkCore;
using SentryApp.Data;
using SentryApp.Data.Query;

namespace SentryApp.Services;

public sealed class TurnstileLogPollingWorker : BackgroundService
{
    private readonly IDbContextFactory<AccessControlDbContext> _dbFactory;
    private readonly TurnstileLogState _state;
    private readonly IPhotoUrlBuilder _photoUrlBuilder;
    private readonly ILogger<TurnstileLogPollingWorker> _logger;

    private readonly int _intervalMs;
    private readonly int _lookbackSecondsOnStart;
    private readonly int _maxRowsPerPoll;

    private DateTimeOffset _sinceUtc;
    private readonly Dictionary<Guid, DateTimeOffset> _seen = new();

    private DateTimeOffset _lastStampUtc;
    private Guid _lastId;

    public TurnstileLogPollingWorker(
        IDbContextFactory<AccessControlDbContext> dbFactory,
        TurnstileLogState state,
        IPhotoUrlBuilder photoUrlBuilder,
        IConfiguration config,
        ILogger<TurnstileLogPollingWorker> logger)
    {
        _dbFactory = dbFactory;
        _state = state;
        _photoUrlBuilder = photoUrlBuilder;
        _logger = logger;

        _intervalMs = config.GetValue("TurnstilePolling:IntervalMs", 500);
        _lookbackSecondsOnStart = config.GetValue("TurnstilePolling:LookbackSecondsOnStart", 3);
        _maxRowsPerPoll = config.GetValue("TurnstilePolling:MaxRowsPerPoll", 20);

        _lastStampUtc = DateTimeOffset.UtcNow.AddSeconds(-_lookbackSecondsOnStart);
        _lastId = Guid.Empty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _sinceUtc = DateTimeOffset.UtcNow.AddSeconds(-_lookbackSecondsOnStart);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_intervalMs));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PollOnceAsync(stoppingToken);
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

    private async Task PollOnceAsync(CancellationToken ct)
    {
        CleanupSeen();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // IMPORTANT:
        // - We poll DeviceLogs (per your requirement)
        // - We join Personnels (name/photo) and ZKDevices (device name)
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
LEFT JOIN Personnels p
    ON p.IsDeleted = 0
   AND p.AccessNumber = dl.AccessNumber
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

            var entry = new TurnstileLogEntry
            {
                TimeLogId = row.TimeLogId,
                TimeLogStamp = row.TimeLogStamp,

                LogType = row.LogType,
                PhotoUrl = photoUrl,
                PersonnelName = name,
                AccessNumber = row.AccessNumber,

                DeviceName = row.DeviceName ?? row.DeviceSerialNumber,
                VerifyMode = row.DeviceLogVerifyMode ?? row.TimeLogVerifyMode,
                Event = row.Event,
                EventAddress = row.EventAddress
            };

            _state.Push(entry);
        }
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

    private void CleanupSeen()
    {
        // keep "seen" only for a short window so the dictionary doesn't grow forever
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
        var oldKeys = _seen.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();
        foreach (var k in oldKeys)
            _seen.Remove(k);
    }
}
