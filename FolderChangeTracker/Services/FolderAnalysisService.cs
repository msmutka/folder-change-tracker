using System.Collections.Concurrent;
using System.Security.Cryptography;
using FolderChangeTracker.Models;

namespace FolderChangeTracker.Services;

public class FolderAnalysisService : IFolderAnalysisService
{
    private const int MaxFiles = 100;
    private const long MaxFileSizeBytes = 50L * 1024 * 1024;

    private readonly ISnapshotRepository _repository;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public FolderAnalysisService(ISnapshotRepository repository)
    {
        _repository = repository;
    }

    public async Task<AnalysisResult> AnalyzeAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failure("Path cannot be empty.");

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path.Trim());
        }
        catch (Exception)
        {
            return Failure("Path is invalid.");
        }

        if (!Directory.Exists(normalizedPath))
        {
            if (File.Exists(normalizedPath))
                return Failure("The specified path points to a file, not a folder.");

            await _repository.DeleteAsync(normalizedPath);
            return Failure("Folder does not exist or is not accessible.");
        }

        var semaphore = _locks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            return await AnalyzeLockedAsync(normalizedPath);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<AnalysisResult> AnalyzeLockedAsync(string normalizedPath)
    {
        string[] entryPaths;
        try
        {
            entryPaths = Directory.GetFileSystemEntries(normalizedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure($"Folder became inaccessible during analysis: {ex.Message}");
        }

        var fileCount = entryPaths.Count(e => !Directory.Exists(e));
        if (fileCount > MaxFiles)
            return Failure($"Folder contains more than {MaxFiles} files. Analysis is limited to {MaxFiles} files per folder.");

        List<(string RelPath, bool IsDir, string? Hash)> scanned;
        List<string> unreadable;
        try
        {
            (scanned, unreadable) = await ScanAsync(normalizedPath, entryPaths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure($"Folder became inaccessible during analysis: {ex.Message}");
        }

        var previous = await _repository.LoadAsync(normalizedPath);
        var previousByPath = previous?.Entries.ToDictionary(e => e.RelativePath)
                             ?? new Dictionary<string, FileEntry>();

        var currentEntries = BuildEntries(scanned, previousByPath);

        await _repository.SaveAsync(new Snapshot
        {
            TrackedPath = normalizedPath,
            CapturedAt = DateTimeOffset.UtcNow,
            Entries = currentEntries
        });

        if (previous == null)
        {
            return new AnalysisResult
            {
                IsSuccess = true,
                IsInitialSnapshot = true,
                AllEntries = currentEntries,
                UnreadableFiles = unreadable
            };
        }

        var currentByPath = currentEntries.ToDictionary(e => e.RelativePath);

        var added = currentEntries.Where(e => !previousByPath.ContainsKey(e.RelativePath)).ToList();
        var removed = previous.Entries.Where(e => !currentByPath.ContainsKey(e.RelativePath)).ToList();
        var changed = currentEntries
            .Where(e => !e.IsDirectory
                        && previousByPath.TryGetValue(e.RelativePath, out var prev)
                        && prev.Hash != e.Hash)
            .ToList();

        return new AnalysisResult
        {
            IsSuccess = true,
            Added = added,
            Changed = changed,
            Removed = removed,
            UnreadableFiles = unreadable
        };
    }

    private static async Task<(List<(string RelPath, bool IsDir, string? Hash)> Entries, List<string> Unreadable)> ScanAsync(string dirPath, string[] entryPaths)
    {
        var entries = new List<(string, bool, string?)>();
        var unreadable = new List<string>();

        foreach (var entryPath in entryPaths)
        {
            var relativePath = Path.GetRelativePath(dirPath, entryPath);

            if (Directory.Exists(entryPath))
            {
                entries.Add((relativePath, true, null));
                continue;
            }

            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(entryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(relativePath);
                continue;
            }

            if (fileInfo.Length > MaxFileSizeBytes)
            {
                unreadable.Add(relativePath);
                continue;
            }

            try
            {
                await using var stream = File.OpenRead(entryPath);
                var hashBytes = await SHA256.HashDataAsync(stream);
                entries.Add((relativePath, false, Convert.ToHexString(hashBytes)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(relativePath);
            }
        }

        return (entries, unreadable);
    }

    private static List<FileEntry> BuildEntries(
        List<(string RelPath, bool IsDir, string? Hash)> scanned,
        Dictionary<string, FileEntry> previousByPath)
    {
        var result = new List<FileEntry>(scanned.Count);
        foreach (var (relPath, isDir, hash) in scanned)
        {
            int? version = null;
            if (!isDir)
            {
                if (previousByPath.TryGetValue(relPath, out var prev))
                    version = prev.Hash == hash ? prev.Version : (prev.Version ?? 0) + 1;
                else
                    version = 1;
            }
            result.Add(new FileEntry { RelativePath = relPath, IsDirectory = isDir, Hash = hash, Version = version });
        }
        return result;
    }

    private static AnalysisResult Failure(string message) =>
        new() { IsSuccess = false, ErrorMessage = message };
}
