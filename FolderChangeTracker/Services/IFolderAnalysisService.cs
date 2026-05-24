using FolderChangeTracker.Models;

namespace FolderChangeTracker.Services;

public interface IFolderAnalysisService
{
    Task<AnalysisResult> AnalyzeAsync(string path);
}
