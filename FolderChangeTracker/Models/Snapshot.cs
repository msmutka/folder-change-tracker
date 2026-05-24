namespace FolderChangeTracker.Models;

public class Snapshot
{
    public string TrackedPath { get; init; } = string.Empty;
    public DateTimeOffset CapturedAt { get; init; }
    public List<FileEntry> Entries { get; init; } = [];
}
