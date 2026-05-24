using System.Collections.Concurrent;
using System.Security.Cryptography;
using FolderChangeTracker.Models;

namespace FolderChangeTracker.Services;

public class FolderAnalysisService : IFolderAnalysisService, IDisposable
{
    private const int MaxFiles = 100;
    private const long MaxFileSizeBytes = 50L * 1024 * 1024;
    private const int ScanParallelism = 4;

    private readonly ISnapshotRepository _repository;
    private readonly ILogger<FolderAnalysisService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public FolderAnalysisService(ISnapshotRepository repository, ILogger<FolderAnalysisService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<AnalysisResult> AnalyzeAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogDebug("Analysis rejected: path is empty");
            return Failure("Path cannot be empty.");
        }

        var trimmed = path.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            _logger.LogDebug("Analysis rejected: path {Path} is not absolute", trimmed);
            return Failure("Path must be an absolute path (e.g. C:\\Users\\...). Relative paths are not accepted.");
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(trimmed);
        }
        catch (Exception)
        {
            _logger.LogDebug("Analysis rejected: path {Path} is invalid", trimmed);
            return Failure("Path is invalid.");
        }

        if (File.Exists(normalizedPath))
        {
            _logger.LogDebug("Analysis rejected: path {Path} points to a file, not a folder", normalizedPath);
            return Failure("The specified path points to a file, not a folder.");
        }

        var semaphore = _locks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(normalizedPath))
            {
                _logger.LogWarning("Folder {Path} no longer exists, deleting snapshot", normalizedPath);
                await _repository.DeleteAsync(normalizedPath, ct);
                return Failure("Folder does not exist or is not accessible.");
            }
            return await AnalyzeLockedAsync(normalizedPath, ct);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<AnalysisResult> AnalyzeLockedAsync(string normalizedPath, CancellationToken ct)
    {
        string[] entryPaths;
        try
        {
            entryPaths = Directory.GetFileSystemEntries(normalizedPath, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Folder {Path} became inaccessible while enumerating entries", normalizedPath);
            return Failure($"Folder became inaccessible during analysis: {ex.Message}");
        }

        if (entryPaths.Length > MaxFiles)
        {
            _logger.LogWarning("Folder {Path} contains {EntryCount} entries, exceeds limit of {MaxFiles}", normalizedPath, entryPaths.Length, MaxFiles);
            return Failure($"Folder contains more than {MaxFiles} entries (files and subfolders). Analysis is limited to {MaxFiles} entries per folder.");
        }

        List<(string RelPath, bool IsDir, string? Hash)> scanned;
        List<string> unreadable;
        try
        {
            (scanned, unreadable) = await ScanAsync(normalizedPath, entryPaths, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Folder {Path} became inaccessible during file scan", normalizedPath);
            return Failure($"Folder became inaccessible during analysis: {ex.Message}");
        }

        var (previous, snapshotWasReset) = await _repository.LoadAsync(normalizedPath, ct);
        var previousByPath = previous?.Entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
                             ?? new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);

        var currentEntries = BuildEntries(scanned, previousByPath);
        var unreadableSet = new HashSet<string>(unreadable, StringComparer.OrdinalIgnoreCase);
        var carried = BuildCarried(previous, previousByPath, currentEntries, unreadableSet);

        var saveFailed = await TrySaveSnapshotAsync(normalizedPath, currentEntries, carried, ct);

        if (previous == null)
        {
            _logger.LogInformation("Initial snapshot captured for {Path}: {EntryCount} entries", normalizedPath, currentEntries.Count);
            return BuildInitialResult(currentEntries, unreadable, snapshotWasReset, saveFailed);
        }

        var result = BuildDiffResult(previous, currentEntries, previousByPath, unreadableSet, unreadable, saveFailed);
        _logger.LogInformation(
            "Diff analysis complete for {Path}: {Added} added, {Changed} changed, {Removed} removed",
            normalizedPath, result.Added.Count, result.Changed.Count, result.Removed.Count);
        return result;
    }

    private static List<FileEntry> BuildCarried(
        Snapshot? previous,
        Dictionary<string, FileEntry> previousByPath,
        List<FileEntry> currentEntries,
        HashSet<string> unreadableSet)
    {
        if (previous == null) return [];
        var currentPaths = new HashSet<string>(currentEntries.Select(e => e.RelativePath), StringComparer.OrdinalIgnoreCase);
        return previousByPath.Values
            .Where(e => unreadableSet.Contains(e.RelativePath) && !currentPaths.Contains(e.RelativePath))
            .ToList();
    }

    private async Task<bool> TrySaveSnapshotAsync(
        string normalizedPath,
        List<FileEntry> currentEntries,
        List<FileEntry> carried,
        CancellationToken ct)
    {
        try
        {
            await _repository.SaveAsync(new Snapshot
            {
                TrackedPath = normalizedPath,
                CapturedAt = DateTimeOffset.UtcNow,
                Entries = currentEntries.Concat(carried).ToList()
            }, ct);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to save snapshot for {Path}", normalizedPath);
            return true;
        }
    }

    private static InitialSnapshotResult BuildInitialResult(
        List<FileEntry> currentEntries,
        List<string> unreadable,
        bool snapshotWasReset,
        bool saveFailed) =>
        new()
        {
            AllEntries = currentEntries,
            UnreadableFiles = unreadable,
            SnapshotWasReset = snapshotWasReset,
            SnapshotSaveFailed = saveFailed
        };

    private static DiffResult BuildDiffResult(
        Snapshot previous,
        List<FileEntry> currentEntries,
        Dictionary<string, FileEntry> previousByPath,
        HashSet<string> unreadableSet,
        List<string> unreadable,
        bool saveFailed)
    {
        var currentByPath = currentEntries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);

        var added = currentEntries.Where(e => !previousByPath.ContainsKey(e.RelativePath)).ToList();
        var removed = previous.Entries
            .Where(e => !currentByPath.ContainsKey(e.RelativePath) && !unreadableSet.Contains(e.RelativePath))
            .ToList();
        var changed = currentEntries
            .Where(e => !e.IsDirectory && previousByPath.TryGetValue(e.RelativePath, out var prev) && prev.Hash != e.Hash)
            .ToList();

        return new DiffResult
        {
            SnapshotSaveFailed = saveFailed,
            Added = added,
            Changed = changed,
            Removed = removed,
            UnreadableFiles = unreadable
        };
    }

    private static async Task<(List<(string RelPath, bool IsDir, string? Hash)> Entries, List<string> Unreadable)> ScanAsync(
        string dirPath,
        string[] entryPaths,
        CancellationToken ct)
    {
        var entries = new ConcurrentBag<(string RelPath, bool IsDir, string? Hash)>();
        var unreadable = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            entryPaths,
            new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism, CancellationToken = ct },
            async (entryPath, loopCt) =>
            {
                var relativePath = Path.GetRelativePath(dirPath, entryPath);

                if (Directory.Exists(entryPath))
                {
                    entries.Add((relativePath, true, null));
                    return;
                }

                var (hash, isUnreadable) = await TryHashFileAsync(entryPath, loopCt);
                if (isUnreadable)
                    unreadable.Add(relativePath);
                else
                    entries.Add((relativePath, false, hash));
            });

        return (
            [..entries.OrderBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase)],
            [..unreadable.Order(StringComparer.OrdinalIgnoreCase)]
        );
    }

    private static async Task<(string? Hash, bool IsUnreadable)> TryHashFileAsync(string entryPath, CancellationToken ct)
    {
        FileInfo fileInfo;
        try
        {
            fileInfo = new FileInfo(entryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, true);
        }

        if (fileInfo.Length > MaxFileSizeBytes)
            return (null, true);

        try
        {
            await using var stream = File.OpenRead(entryPath);
            var hashBytes = await SHA256.HashDataAsync(stream, ct);
            return (Convert.ToHexString(hashBytes), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, true);
        }
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

    private static FailureResult Failure(string message) => new(message);

    public void Dispose()
    {
        foreach (var semaphore in _locks.Values)
            semaphore.Dispose();
        _locks.Clear();
    }
}
