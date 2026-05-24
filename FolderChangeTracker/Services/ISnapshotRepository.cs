using FolderChangeTracker.Models;

namespace FolderChangeTracker.Services;

public interface ISnapshotRepository
{
    Task<Snapshot?> LoadAsync(string trackedPath);
    Task SaveAsync(Snapshot snapshot);
    Task DeleteAsync(string trackedPath);
}
