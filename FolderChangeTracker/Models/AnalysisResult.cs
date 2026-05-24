namespace FolderChangeTracker.Models;

public abstract record AnalysisResult;

public sealed record FailureResult(string ErrorMessage) : AnalysisResult;

public sealed record InitialSnapshotResult : AnalysisResult
{
    public IReadOnlyList<FileEntry> AllEntries { get; init; } = [];
    public IReadOnlyList<string> UnreadableFiles { get; init; } = [];
    public bool SnapshotWasReset { get; init; }
    public bool SnapshotSaveFailed { get; init; }
}

public sealed record DiffResult : AnalysisResult
{
    public IReadOnlyList<FileEntry> Added { get; init; } = [];
    public IReadOnlyList<FileEntry> Changed { get; init; } = [];
    public IReadOnlyList<FileEntry> Removed { get; init; } = [];
    public IReadOnlyList<string> UnreadableFiles { get; init; } = [];
    public bool SnapshotSaveFailed { get; init; }
}
