using FolderChangeTracker.Models;

namespace FolderChangeTracker.Services;

public interface ISnapshotRepository
{
    Task<(Snapshot? Snapshot, bool WasReset)> LoadAsync(string trackedPath, CancellationToken ct = default);
    Task SaveAsync(Snapshot snapshot, CancellationToken ct = default);
    Task DeleteAsync(string trackedPath, CancellationToken ct = default);
}
