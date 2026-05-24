namespace FolderChangeTracker.Models;

public class AnalysisResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsInitialSnapshot { get; init; }
    public List<FileEntry> AllEntries { get; init; } = [];
    public List<FileEntry> Added { get; init; } = [];
    public List<FileEntry> Changed { get; init; } = [];
    public List<FileEntry> Removed { get; init; } = [];
}
