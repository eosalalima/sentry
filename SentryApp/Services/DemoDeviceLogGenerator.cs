using Microsoft.EntityFrameworkCore;
using SentryApp.Data;

namespace SentryApp.Services;

public sealed class DemoDeviceLogGenerator : BackgroundService
{
    private static readonly string[] LogTypes = ["IN", "OUT", "BREAK OUT"];

    private readonly IDbContextFactory<AccessControlDbContext> _dbFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<DemoDeviceLogGenerator> _logger;
    private readonly Random _random = new();

    public DemoDeviceLogGenerator(
        IDbContextFactory<AccessControlDbContext> dbFactory,
        IConfiguration config,
        ILogger<DemoDeviceLogGenerator> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (IsLiveModeEnabled())
        {
            _logger.LogInformation("Demo device log generator is disabled while live mode is active.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delaySeconds = _random.Next(1, 11);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);

                if (IsLiveModeEnabled())
                    continue;

                await GenerateLogAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate demo device log entry.");
            }
        }
    }

    private bool IsLiveModeEnabled() => _config.GetValue("IsLiveMode", true);

    private async Task GenerateLogAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var accessNumber = await PickRandomAsync(db.Personnels.Where(p => !p.IsDeleted).Select(p => p.AccessNumber), ct);
        var deviceSerial = await PickRandomAsync(db.ZkDevices.Where(d => !d.IsDeleted).Select(d => d.SerialNumber), ct);

        if (string.IsNullOrWhiteSpace(accessNumber) || string.IsNullOrWhiteSpace(deviceSerial))
        {
            _logger.LogWarning("Skipping demo device log generation because required data was unavailable (accessNumber: {AccessNumber}, deviceSerial: {DeviceSerial}).", accessNumber, deviceSerial);
            return;
        }

        var now = DateTimeOffset.Now;
        var recordDate = DateTime.Now.Date;
        var logType = LogTypes[_random.Next(LogTypes.Length)];

        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO DeviceLogs
    (Id, DataCreated, IsDeleted, RecordDate, TimeLogStamp, AccessNumber, DeviceSerialNumber, CardNo, SiteCode, LinkId, Event, EventAddress, LogType, VerifyMode, [Index], HasMask, Temperature, IsNotified)
VALUES
    ({Guid.NewGuid()}, {now}, 0, {recordDate}, {now}, {accessNumber}, {deviceSerial}, {"TEST"}, { (string?)null }, { (int?)null }, {"20"}, {"1"}, {logType}, {"200"}, 0, { (bool?)null }, { (float?)null }, { (bool?)null });", ct);
    }

    private async Task<string?> PickRandomAsync(IQueryable<string> query, CancellationToken ct)
    {
        var values = await query.ToListAsync(ct);
        if (values.Count == 0)
            return null;

        var index = _random.Next(values.Count);
        return values[index];
    }
}
