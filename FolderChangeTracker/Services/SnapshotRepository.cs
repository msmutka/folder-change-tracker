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

    public async Task<Snapshot?> LoadAsync(string trackedPath)
    {
        var filePath = GetFilePath(trackedPath);
        if (!File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<Snapshot>(json, JsonOptions);
    }

    public async Task SaveAsync(Snapshot snapshot)
    {
        var filePath = GetFilePath(snapshot.TrackedPath);
        var tempPath = filePath + ".tmp";

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, filePath, overwrite: true);
    }

    public Task DeleteAsync(string trackedPath)
    {
        var filePath = GetFilePath(trackedPath);
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }

    private string GetFilePath(string trackedPath)
    {
        var normalized = Path.GetFullPath(trackedPath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_dataDirectory, hash + ".json");
    }
}
