namespace FolderChangeTracker.Models;

public class FileEntry
{
    public string RelativePath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public string? Hash { get; init; }
    public int? Version { get; init; }
}
