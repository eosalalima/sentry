using System.Text.Json;

namespace SentryApp.Services;

public sealed class MonitoringDataLogWriter
{
    public const string DefaultGeneratedFileName = "gendata.log";
    public const string DefaultPolledFileName = "polled.log";

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<MonitoringDataLogWriter> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public MonitoringDataLogWriter(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<MonitoringDataLogWriter> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public Task LogGeneratedAsync(object details, CancellationToken cancellationToken) =>
        WriteAsync("Generated", "DataLogging:Generated", DefaultGeneratedFileName, details, cancellationToken);

    public Task LogPolledAsync(object details, CancellationToken cancellationToken) =>
        WriteAsync("Polled", "DataLogging:Polled", DefaultPolledFileName, details, cancellationToken);

    private async Task WriteAsync(
        string logType,
        string configurationSection,
        string defaultFileName,
        object details,
        CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue($"{configurationSection}:Enabled", false))
            return;

        var configuredName = _configuration.GetValue<string>($"{configurationSection}:FileName");
        var fileName = NormalizeFileName(configuredName, defaultFileName);
        var path = Path.Combine(_environment.ContentRootPath, fileName);
        var line = JsonSerializer.Serialize(new
        {
            Timestamp = DateTimeOffset.Now,
            Type = logType,
            Details = details
        });

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Unable to append to the {LogType} data log {LogFile}.", logType, path);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static string NormalizeFileName(string? configuredName, string defaultFileName)
    {
        var fileName = Path.GetFileName(configuredName?.Trim());
        return string.IsNullOrWhiteSpace(fileName) ? defaultFileName : fileName;
    }
}
