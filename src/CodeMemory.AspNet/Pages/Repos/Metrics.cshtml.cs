using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace CodeMemory.AspNet.Pages.Repos;

public sealed class MetricsModel : PageModel
{
    readonly RepoRegistryService registry;
    readonly MetricsService metricsService;

    public Repositories? Repo { get; private set; }
    public string? NotFoundMessage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public RepoMetrics? Metrics { get; private set; }
    public bool LocalMetricsEnabled { get; }

    public MetricsModel(RepoRegistryService registry, MetricsService metricsService, IOptions<LocalMetricsOptions> localMetrics)
    {
        this.registry = registry;
        this.metricsService = metricsService;
        LocalMetricsEnabled = localMetrics.Value.Enabled;
    }

    public async Task<IActionResult> OnGetAsync(string name)
    {
        Repo = await registry.GetAsync(name);
        if (Repo is null)
        {
            NotFoundMessage = $"Repository '{name}' not found.";
            return Page();
        }

        try
        {
            Metrics = await metricsService.GetMetricsAsync(name);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        return Page();
    }
}
