using System.Text.Json;
using System.Text.Json.Nodes;

namespace ICOGenerator.Services.Settings;

/// <summary>
/// Reads and writes appsettings.json in the content root. The host loads the file
/// with reloadOnChange, so most saved values take effect without a restart.
/// </summary>
public class AppSettingsFileStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    public AppSettingsFileStore(IHostEnvironment environment)
    {
        _filePath = Path.Combine(environment.ContentRootPath, "appsettings.json");
    }

    public async Task<JsonObject> ReadAsync()
    {
        if (!File.Exists(_filePath))
            return new JsonObject();

        await using var stream = File.OpenRead(_filePath);
        return await JsonNode.ParseAsync(stream) as JsonObject ?? new JsonObject();
    }

    public Task WriteAsync(JsonObject root) =>
        File.WriteAllTextAsync(_filePath, root.ToJsonString(WriteOptions));
}
