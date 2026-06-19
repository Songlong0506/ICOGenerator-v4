using System.Text.Json;
using System.Text.Json.Nodes;

namespace ICOGenerator.Services.Settings;

/// <summary>
/// Reads and writes appsettings.json in the content root. The host loads it with reloadOnChange, so most saved values take effect without a restart.
/// </summary>
public class AppSettingsFileStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // The store is a singleton, so this serializes every write process-wide.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

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

    public async Task WriteAsync(JsonObject root)
    {
        await _writeLock.WaitAsync();
        try
        {
            await WriteUnlockedAsync(root);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Atomically read-modify-write under the lock so two concurrent saves can't each read the same
    /// snapshot and have the later writer silently clobber the earlier one's changes (lost update).
    /// </summary>
    public async Task UpdateAsync(Action<JsonObject> mutate)
    {
        await _writeLock.WaitAsync();
        try
        {
            // ReadAsync does not take the lock, so calling it here is safe (SemaphoreSlim is non-reentrant).
            var root = await ReadAsync();
            mutate(root);
            await WriteUnlockedAsync(root);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteUnlockedAsync(JsonObject root)
    {
        var json = root.ToJsonString(WriteOptions);
        // Write to a temp file then atomically replace, so a crash or concurrent write can't leave appsettings.json half-written and break the next reloadOnChange reload or app start.
        var tempPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }
}
