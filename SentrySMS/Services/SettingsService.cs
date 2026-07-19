using System.Text.Json;
using System.Text.Json.Nodes;
using SentrySMS.Models;

namespace SentrySMS.Services;

public class SettingsService
{
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public SettingsService(IHostEnvironment hostEnvironment)
    {
        _settingsPath = Path.Combine(hostEnvironment.ContentRootPath, "appsettings.json");
    }

    public async Task SaveGsmSettingsAsync(GsmSettings settings, CancellationToken cancellationToken = default)
    {
        JsonNode root;

        if (File.Exists(_settingsPath))
        {
            var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken);
            root = JsonNode.Parse(json) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        root[GsmSettings.SectionName] = JsonSerializer.SerializeToNode(settings, _jsonOptions);

        var output = root.ToJsonString(_jsonOptions);
        await File.WriteAllTextAsync(_settingsPath, output, cancellationToken);
    }
}
