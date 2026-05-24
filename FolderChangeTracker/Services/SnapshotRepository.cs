using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderChangeTracker.Models;

namespace FolderChangeTracker.Services;

public class SnapshotRepository : ISnapshotRepository
{
    private readonly string _dataDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SnapshotRepository(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var dataPath = configuration["Storage:DataPath"] ?? "data";
        _dataDirectory = Path.Combine(environment.ContentRootPath, dataPath);
        Directory.CreateDirectory(_dataDirectory);
    }

    public async Task<(Snapshot? Snapshot, bool WasReset)> LoadAsync(string trackedPath, CancellationToken ct = default)
    {
        var filePath = GetFilePath(trackedPath);
        if (!File.Exists(filePath))
            return (null, false);

        var json = await File.ReadAllTextAsync(filePath, ct);
        try
        {
            return (JsonSerializer.Deserialize<Snapshot>(json, JsonOptions), false);
        }
        catch (JsonException)
        {
            try { File.Delete(filePath); } catch { }
            return (null, true);
        }
    }

    public async Task SaveAsync(Snapshot snapshot, CancellationToken ct = default)
    {
        var filePath = GetFilePath(snapshot.TrackedPath);
        var tempPath = filePath + ".tmp";

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        try
        {
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public Task DeleteAsync(string trackedPath, CancellationToken ct = default)
    {
        var filePath = GetFilePath(trackedPath);
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private string GetFilePath(string trackedPath)
    {
        var normalized = Path.GetFullPath(trackedPath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_dataDirectory, hash + ".json");
    }
}
