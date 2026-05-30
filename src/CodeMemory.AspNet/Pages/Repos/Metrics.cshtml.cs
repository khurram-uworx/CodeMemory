using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CodeMemory.AspNet.Pages.Repos;

public sealed class MetricsModel : PageModel
{
    readonly RepoRegistryService registry;
    readonly MetricsService metricsService;
    readonly ILocalMetricsCollector localMetrics;

    public Repositories? Repo { get; private set; }
    public string? NotFoundMessage { get; private set; }
    public string? RepositoryMetricsError { get; private set; }
    public RepoMetrics? Metrics { get; private set; }
    public LocalMetricsSnapshot? RuntimeMetrics { get; private set; }

    public MetricsModel(RepoRegistryService registry, MetricsService metricsService, ILocalMetricsCollector localMetrics)
    {
        this.registry = registry;
        this.metricsService = metricsService;
        this.localMetrics = localMetrics;
    }

    public async Task<IActionResult> OnGetAsync(string name)
    {
        Repo = await registry.GetAsync(name);
        if (Repo is null)
        {
            NotFoundMessage = $"Repository '{name}' not found.";
            return Page();
        }

        RuntimeMetrics = localMetrics.Enabled
            ? localMetrics.GetSnapshot(name)
            : null;

        try
        {
            Metrics = await metricsService.GetMetricsAsync(name);
        }
        catch (InvalidOperationException ex)
        {
            RepositoryMetricsError = ex.Message;
        }

        return Page();
    }
}
