using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderChangeTracker.Models;
using Microsoft.Extensions.Options;

namespace FolderChangeTracker.Services;

public class SnapshotRepository : ISnapshotRepository
{
    private readonly string _dataDirectory;
    private readonly ILogger<SnapshotRepository> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SnapshotRepository(IOptions<StorageOptions> options, IWebHostEnvironment environment, ILogger<SnapshotRepository> logger)
    {
        _dataDirectory = Path.Combine(environment.ContentRootPath, options.Value.DataPath);
        _logger = logger;
        Directory.CreateDirectory(_dataDirectory);
    }

    public async Task<(Snapshot? Snapshot, bool WasReset)> LoadAsync(string trackedPath, CancellationToken ct = default)
    {
        var filePath = GetFilePath(trackedPath);
        if (!File.Exists(filePath))
            return (null, false);

        try
        {
            await using var stream = File.OpenRead(filePath);
            var snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(stream, JsonOptions, ct);
            return (snapshot, false);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Snapshot file for {TrackedPath} is corrupted, resetting", trackedPath);
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
            {
                File.Delete(filePath);
                _logger.LogInformation("Deleted snapshot for {TrackedPath}", trackedPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete snapshot for {TrackedPath}", trackedPath);
        }
        return Task.CompletedTask;
    }

    private string GetFilePath(string trackedPath)
    {
        var normalized = OperatingSystem.IsWindows()
            ? Path.GetFullPath(trackedPath).ToLowerInvariant()
            : Path.GetFullPath(trackedPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_dataDirectory, hash + ".json");
    }
}
