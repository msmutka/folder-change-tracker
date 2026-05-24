namespace FolderChangeTracker.Models;

public class AnalysisResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsInitialSnapshot { get; init; }
    public bool SnapshotWasReset { get; init; }
    public IReadOnlyList<FileEntry> AllEntries { get; init; } = [];
    public IReadOnlyList<FileEntry> Added { get; init; } = [];
    public IReadOnlyList<FileEntry> Changed { get; init; } = [];
    public IReadOnlyList<FileEntry> Removed { get; init; } = [];
    public IReadOnlyList<string> UnreadableFiles { get; init; } = [];
}
