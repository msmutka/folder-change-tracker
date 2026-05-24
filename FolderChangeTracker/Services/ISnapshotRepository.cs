using FolderChangeTracker.Models;

namespace FolderChangeTracker.Services;

public interface ISnapshotRepository
{
    Task<(Snapshot? Snapshot, bool WasReset)> LoadAsync(string trackedPath);
    Task SaveAsync(Snapshot snapshot);
    Task DeleteAsync(string trackedPath);
}
