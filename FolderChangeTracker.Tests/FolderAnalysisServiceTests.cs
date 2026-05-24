using FolderChangeTracker.Models;
using FolderChangeTracker.Services;
using Moq;

namespace FolderChangeTracker.Tests;

public class FolderAnalysisServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<ISnapshotRepository> _repository;
    private readonly FolderAnalysisService _sut;

    public FolderAnalysisServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);

        _repository = new Mock<ISnapshotRepository>();
        _repository.Setup(r => r.SaveAsync(It.IsAny<Snapshot>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _repository.Setup(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _sut = new FolderAnalysisService(_repository.Object);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

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
        var result = await _sut.AnalyzeAsync(path);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.ErrorMessage!);
    }

    [Fact]
    public async Task AnalyzeAsync_NonExistentPath_ReturnsFailureAndDeletesSnapshot()
    {
        var result = await _sut.AnalyzeAsync(@"C:\this\path\does\not\exist\abc123xyz");

        Assert.False(result.IsSuccess);
        _repository.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task AnalyzeAsync_PathIsFile_ReturnsFailure()
    {
        CreateFile("somefile.txt");

        var result = await _sut.AnalyzeAsync(Path.Combine(_tempDir, "somefile.txt"));

        Assert.False(result.IsSuccess);
        Assert.Contains("file", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // --- initial snapshot ---

    [Fact]
    public async Task AnalyzeAsync_NoPreviousSnapshot_AllEntriesContainsAllScannedItems()
    {
        CreateFile("file1.txt");
        CreateFile("file2.txt");
        CreateSubdir("subdir");
        SetupNoPreviousSnapshot();

        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.True(result.IsInitialSnapshot);
        Assert.Equal(3, result.AllEntries.Count);
        Assert.Contains(result.AllEntries, e => e.RelativePath == "file1.txt" && !e.IsDirectory);
        Assert.Contains(result.AllEntries, e => e.RelativePath == "file2.txt" && !e.IsDirectory);
        Assert.Contains(result.AllEntries, e => e.RelativePath == "subdir" && e.IsDirectory);
    }

    [Fact]
    public async Task AnalyzeAsync_FileInSubdirectory_RelativePathIsCorrect()
    {
        CreateSubdir("sub");
        System.IO.File.WriteAllText(Path.Combine(_tempDir, "sub", "nested.txt"), "content");
        SetupNoPreviousSnapshot();

        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.Contains(result.AllEntries, e => e.RelativePath == Path.Combine("sub", "nested.txt") && !e.IsDirectory);
    }

    [Fact]
    public async Task AnalyzeAsync_NoPreviousSnapshot_NewFilesHaveVersionOne()
    {
        CreateFile("file.txt");
        SetupNoPreviousSnapshot();

        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.Equal(1, result.AllEntries.Single(e => !e.IsDirectory).Version);
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
        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsInitialSnapshot);
        Assert.Empty(result.Added);
        Assert.Empty(result.Changed);
        Assert.Empty(result.Removed);
        Assert.Empty(result.AllEntries);
    }

    // --- diff: additions ---

    [Fact]
    public async Task AnalyzeAsync_FileAdded_AppearsInAdded()
    {
        SetupPreviousSnapshot();
        CreateFile("new.txt");

        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.Single(result.Added);
        Assert.Equal("new.txt", result.Added[0].RelativePath);
        Assert.Empty(result.Changed);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public async Task AnalyzeAsync_DirectoryAdded_AppearsInAdded()
    {
        SetupPreviousSnapshot();
        CreateSubdir("newdir");

        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.Single(result.Added);
        Assert.Equal("newdir", result.Added[0].RelativePath);
        Assert.True(result.Added[0].IsDirectory);
    }

    // --- diff: removals ---

    [Fact]
    public async Task AnalyzeAsync_FileRemoved_AppearsInRemoved()
    {
        SetupPreviousSnapshot(Entry("gone.txt", "any_hash"));

        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.Single(result.Removed);
        Assert.Equal("gone.txt", result.Removed[0].RelativePath);
        Assert.Empty(result.Added);
        Assert.Empty(result.Changed);
    }

    [Fact]
    public async Task AnalyzeAsync_DirectoryRemoved_AppearsInRemoved()
    {
        SetupPreviousSnapshot(DirEntry("olddir"));

        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.Single(result.Removed);
        Assert.Equal("olddir", result.Removed[0].RelativePath);
        Assert.True(result.Removed[0].IsDirectory);
    }

    // --- diff: modifications ---

    [Fact]
    public async Task AnalyzeAsync_FileContentChanged_AppearsInChanged()
    {
        CreateFile("file.txt", "current content");
        SetupPreviousSnapshot(Entry("file.txt", "STALE_HASH_THAT_WONT_MATCH", version: 1));

        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.Single(result.Changed);
        Assert.Equal("file.txt", result.Changed[0].RelativePath);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public async Task AnalyzeAsync_FileContentChanged_VersionIsIncremented()
    {
        CreateFile("file.txt", "current content");
        SetupPreviousSnapshot(Entry("file.txt", "STALE_HASH_THAT_WONT_MATCH", version: 3));

        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.Equal(4, result.Changed[0].Version);
    }

    [Fact]
    public async Task AnalyzeAsync_DirectoryNotInChanged_EvenWhenPresentInBothSnapshots()
    {
        CreateSubdir("subdir");
        SetupPreviousSnapshot(DirEntry("subdir"));

        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.Empty(result.Changed);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
    }

    // --- unreadable files ---

    [Fact]
    public async Task AnalyzeAsync_UnreadableFile_AppearsInUnreadableFiles()
    {
        var filePath = Path.Combine(_tempDir, "locked.txt");
        System.IO.File.WriteAllText(filePath, "content");
        SetupNoPreviousSnapshot();

        using var lockedStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.True(result.IsSuccess);
        Assert.Contains("locked.txt", result.UnreadableFiles);
    }

    [Fact]
    public async Task AnalyzeAsync_UnreadableFileInDiff_DoesNotAppearInRemoved()
    {
        var filePath = Path.Combine(_tempDir, "locked.txt");
        System.IO.File.WriteAllText(filePath, "content");
        SetupPreviousSnapshot(Entry("locked.txt", "some_hash"));

        using var lockedStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await _sut.AnalyzeAsync(_tempDir);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Removed);
        Assert.Contains("locked.txt", result.UnreadableFiles);
    }
}
