namespace FolderChangeTracker.Models;

public record Snapshot
{
    public string TrackedPath { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }
    public IReadOnlyList<FileEntry> Entries { get; init; } = [];
}
