using FolderChangeTracker.Models;
using FolderChangeTracker.Services;
using Microsoft.AspNetCore.Components;

namespace FolderChangeTracker.Components.Pages;

public partial class Home : IAsyncDisposable
{
    [Inject]
    private IFolderAnalysisService AnalysisService { get; set; } = default!;

    private string path = string.Empty;
    private AnalysisResult? result;
    private bool isLoading;
    private bool wasCancelled;
    private string? unexpectedError;
    private CancellationTokenSource? cts;

    private async Task AnalyseAsync()
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();

        isLoading = true;
        wasCancelled = false;
        result = null;
        unexpectedError = null;

        try
        {
            result = await AnalysisService.AnalyzeAsync(path, cts.Token);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
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

    private void Cancel()
    {
        cts?.Cancel();
    }

    public ValueTask DisposeAsync()
    {
        cts?.Cancel();
        cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
