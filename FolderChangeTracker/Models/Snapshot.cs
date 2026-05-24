namespace FolderChangeTracker.Models;

public record Snapshot
{
    public string TrackedPath { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }
    public List<FileEntry> Entries { get; init; } = [];
}
