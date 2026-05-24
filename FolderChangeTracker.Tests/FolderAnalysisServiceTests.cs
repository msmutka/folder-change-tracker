using FolderChangeTracker.Models;
using FolderChangeTracker.Services;
using Moq;

namespace FolderChangeTracker.Tests;

public class FolderAnalysisServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<ISnapshotRepository> _repository;
    private readonly IFolderAnalysisService _sut;

    public FolderAnalysisServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _repository = new Mock<ISnapshotRepository>();
        _repository.Setup(r => r.SaveAsync(It.IsAny<Snapshot>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _repository.Setup(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _sut = new FolderAnalysisService(_repository.Object);
    }

    public void Dispose()
    {
        (_sut as IDisposable)?.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    // --- helpers ---

    private void CreateFile(string name, string content = "default content")
        => System.IO.File.WriteAllText(Path.Combine(_tempDir, name), content);

    private void CreateSubdir(string name)
        => Directory.CreateDirectory(Path.Combine(_tempDir, name));

    private void SetupNoPreviousSnapshot()
        => _repository.Setup(r => r.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((Snapshot?)null, false));

    private void SetupPreviousSnapshot(params FileEntry[] entries)
        => _repository.Setup(r => r.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new Snapshot { TrackedPath = _tempDir, CapturedAt = DateTimeOffset.UtcNow, Entries = [..entries] }, false));

    private static FileEntry Entry(string relPath, string hash, int version = 1)
        => new() { RelativePath = relPath, IsDirectory = false, Hash = hash, Version = version };

    private static FileEntry DirEntry(string relPath)
        => new() { RelativePath = relPath, IsDirectory = true };

    // --- path validation ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzeAsync_EmptyOrWhitespacePath_ReturnsFailure(string path)
    {
        var failure = Assert.IsType<FailureResult>(await _sut.AnalyzeAsync(path));

        Assert.NotEmpty(failure.ErrorMessage);
    }

    [Fact]
    public async Task AnalyzeAsync_NonExistentPath_ReturnsFailureAndDeletesSnapshot()
    {
        Assert.IsType<FailureResult>(await _sut.AnalyzeAsync(@"C:\this\path\does\not\exist\abc123xyz"));

        _repository.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnalyzeAsync_PathIsFile_ReturnsFailure()
    {
        CreateFile("somefile.txt");

        var failure = Assert.IsType<FailureResult>(await _sut.AnalyzeAsync(Path.Combine(_tempDir, "somefile.txt")));

        Assert.Contains("file", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("relative\\path")]
    [InlineData("../parent")]
    [InlineData(".")]
    public async Task AnalyzeAsync_RelativePath_ReturnsFailure(string path)
    {
        var failure = Assert.IsType<FailureResult>(await _sut.AnalyzeAsync(path));

        Assert.Contains("absolute", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // --- initial snapshot ---

    [Fact]
    public async Task AnalyzeAsync_NoPreviousSnapshot_AllEntriesContainsAllScannedItems()
    {
        CreateFile("file1.txt");
        CreateFile("file2.txt");
        CreateSubdir("subdir");
        SetupNoPreviousSnapshot();

        var initial = Assert.IsType<InitialSnapshotResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Equal(3, initial.AllEntries.Count);
        Assert.Contains(initial.AllEntries, e => e.RelativePath == "file1.txt" && !e.IsDirectory);
        Assert.Contains(initial.AllEntries, e => e.RelativePath == "file2.txt" && !e.IsDirectory);
        Assert.Contains(initial.AllEntries, e => e.RelativePath == "subdir" && e.IsDirectory);
    }

    [Fact]
    public async Task AnalyzeAsync_FileInSubdirectory_RelativePathIsCorrect()
    {
        CreateSubdir("sub");
        System.IO.File.WriteAllText(Path.Combine(_tempDir, "sub", "nested.txt"), "content");
        SetupNoPreviousSnapshot();

        var initial = Assert.IsType<InitialSnapshotResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Contains(initial.AllEntries, e => e.RelativePath == Path.Combine("sub", "nested.txt") && !e.IsDirectory);
    }

    [Fact]
    public async Task AnalyzeAsync_NoPreviousSnapshot_NewFilesHaveVersionOne()
    {
        CreateFile("file.txt");
        SetupNoPreviousSnapshot();

        var initial = Assert.IsType<InitialSnapshotResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Equal(1, initial.AllEntries.Single(e => !e.IsDirectory).Version);
    }

    // --- diff: no changes ---

    [Fact]
    public async Task AnalyzeAsync_NoChanges_AllDiffListsAreEmpty()
    {
        CreateFile("file.txt");

        (Snapshot? snapshot, bool wasReset) saved = (null, false);
        _repository.Setup(r => r.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(saved));
        _repository.Setup(r => r.SaveAsync(It.IsAny<Snapshot>(), It.IsAny<CancellationToken>()))
            .Callback<Snapshot, CancellationToken>((s, _) => saved = (s, false))
            .Returns(Task.CompletedTask);

        await _sut.AnalyzeAsync(_tempDir);
        var diff = Assert.IsType<DiffResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Changed);
        Assert.Empty(diff.Removed);
    }

    // --- diff: additions ---

    [Fact]
    public async Task AnalyzeAsync_FileAdded_AppearsInAdded()
    {
        SetupPreviousSnapshot();
        CreateFile("new.txt");

        var diff = Assert.IsType<DiffResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Single(diff.Added);
        Assert.Equal("new.txt", diff.Added[0].RelativePath);
        Assert.Empty(diff.Changed);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public async Task AnalyzeAsync_DirectoryAdded_AppearsInAdded()
    {
        SetupPreviousSnapshot();
        CreateSubdir("newdir");

        var diff = Assert.IsType<DiffResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Single(diff.Added);
        Assert.Equal("newdir", diff.Added[0].RelativePath);
        Assert.True(diff.Added[0].IsDirectory);
    }

    // --- diff: removals ---

    [Fact]
    public async Task AnalyzeAsync_FileRemoved_AppearsInRemoved()
    {
        SetupPreviousSnapshot(Entry("gone.txt", "any_hash"));

        var diff = Assert.IsType<DiffResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Single(diff.Removed);
        Assert.Equal("gone.txt", diff.Removed[0].RelativePath);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public async Task AnalyzeAsync_DirectoryRemoved_AppearsInRemoved()
    {
        SetupPreviousSnapshot(DirEntry("olddir"));

        var diff = Assert.IsType<DiffResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Single(diff.Removed);
        Assert.Equal("olddir", diff.Removed[0].RelativePath);
        Assert.True(diff.Removed[0].IsDirectory);
    }

    // --- diff: modifications ---

    [Fact]
    public async Task AnalyzeAsync_FileContentChanged_AppearsInChanged()
    {
        CreateFile("file.txt", "current content");
        SetupPreviousSnapshot(Entry("file.txt", "STALE_HASH_THAT_WONT_MATCH", version: 1));

        var diff = Assert.IsType<DiffResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Single(diff.Changed);
        Assert.Equal("file.txt", diff.Changed[0].RelativePath);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public async Task AnalyzeAsync_FileContentChanged_VersionIsIncremented()
    {
        CreateFile("file.txt", "current content");
        SetupPreviousSnapshot(Entry("file.txt", "STALE_HASH_THAT_WONT_MATCH", version: 3));

        var diff = Assert.IsType<DiffResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Equal(4, diff.Changed[0].Version);
    }

    [Fact]
    public async Task AnalyzeAsync_DirectoryNotInChanged_EvenWhenPresentInBothSnapshots()
    {
        CreateSubdir("subdir");
        SetupPreviousSnapshot(DirEntry("subdir"));

        var diff = Assert.IsType<DiffResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Empty(diff.Changed);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    // --- unreadable files ---

    [Fact]
    public async Task AnalyzeAsync_UnreadableFile_AppearsInUnreadableFiles()
    {
        var filePath = Path.Combine(_tempDir, "locked.txt");
        System.IO.File.WriteAllText(filePath, "content");
        SetupNoPreviousSnapshot();

        using var lockedStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var initial = Assert.IsType<InitialSnapshotResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Contains("locked.txt", initial.UnreadableFiles);
    }

    [Fact]
    public async Task AnalyzeAsync_UnreadableFileInDiff_DoesNotAppearInRemoved()
    {
        var filePath = Path.Combine(_tempDir, "locked.txt");
        System.IO.File.WriteAllText(filePath, "content");
        SetupPreviousSnapshot(Entry("locked.txt", "some_hash"));

        using var lockedStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var diff = Assert.IsType<DiffResult>(await _sut.AnalyzeAsync(_tempDir));

        Assert.Empty(diff.Removed);
        Assert.Contains("locked.txt", diff.UnreadableFiles);
    }
}
