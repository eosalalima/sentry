using System.Text.Json;

namespace SentryApp.Services;

public sealed class PictureRetrievalLogWriter
{
    public const string FileName = "picimage.log";

    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<PictureRetrievalLogWriter> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PictureRetrievalLogWriter(
        IWebHostEnvironment environment,
        ILogger<PictureRetrievalLogWriter> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public async Task LogAsync(
        string? personnelNo,
        string fileName,
        bool photoFound,
        CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(new
        {
            Timestamp = DateTimeOffset.Now,
            PersonnelNo = string.IsNullOrWhiteSpace(personnelNo) ? null : personnelNo,
            FileName = fileName,
            PhotoFound = photoFound
        });
        var path = Path.Combine(_environment.ContentRootPath, FileName);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Unable to append picture retrieval to {LogFile}.", path);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
