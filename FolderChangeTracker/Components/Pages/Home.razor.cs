using FolderChangeTracker.Models;
using FolderChangeTracker.Services;
using Microsoft.AspNetCore.Components;

namespace FolderChangeTracker.Components.Pages;

public partial class Home
{
    [Inject]
    private IFolderAnalysisService AnalysisService { get; set; } = default!;

    private string path = string.Empty;
    private AnalysisResult? result;
    private bool isLoading;
    private string? unexpectedError;

    private async Task AnalyseAsync()
    {
        isLoading = true;
        result = null;
        unexpectedError = null;

        try
        {
            result = await AnalysisService.AnalyzeAsync(path);
        }
        catch (Exception)
        {
            unexpectedError = "An unexpected error occurred. Please try again.";
        }
        finally
        {
            isLoading = false;
        }
    }
}
