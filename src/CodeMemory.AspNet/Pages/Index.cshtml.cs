using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Services;
using CodeMemory.Indexing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CodeMemory.AspNet.Pages;

public sealed class IndexModel : PageModel
{
    readonly RepoRegistryService registry;
    readonly CloneIndexService cloneIndex;
    readonly NotificationService notifications;
    readonly IConfiguration configuration;
    readonly RepositoryDashboardOptions dashboardOptions;

    public List<Repositories> Repos { get; private set; } = [];
    public Dictionary<string, double?> Progress { get; private set; } = [];
    public bool ShowMetricsLink { get; private set; }
    public bool AllowNewRepos { get; private set; }

    public IndexModel(RepoRegistryService registry, CloneIndexService cloneIndex,
        NotificationService notifications, IConfiguration configuration,
        RepositoryDashboardOptions dashboardOptions)
        => (this.registry, this.cloneIndex, this.notifications, this.configuration, this.dashboardOptions) =
            (registry, cloneIndex, notifications, configuration, dashboardOptions);

    public async Task OnGetAsync()
    {
        Repos = await registry.ListAsync();
        Progress = Repos.ToDictionary(r => r.Name, r => IndexingState.GetProgress(r.Name));
        var provider = configuration.GetValue<string>("Storage:Provider") ?? "inmemory";
        var localMetricsEnabled = configuration.GetValue<bool>("Observability:LocalMetrics:Enabled");
        ShowMetricsLink = localMetricsEnabled
            || !string.Equals(provider, "inmemory", StringComparison.OrdinalIgnoreCase);

        AllowNewRepos = dashboardOptions.AllowNewRepos;

        if (Repos.Count == 0 && AllowNewRepos)
            notifications.PublishInfo("No repositories registered. Click \"Add Repo\" to get started.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(string name)
    {
        var repo = await registry.GetAsync(name);
        if (repo is null) return NotFound();

        await cloneIndex.DeleteRepoAsync(name);

        notifications.PublishInfo($"Repo '{name}' deleted.");

        var remaining = await registry.ListAsync();
        if (remaining.Count == 0)
        {
            var msg = AllowNewRepos
                ? "No repositories registered. Click \"Add Repo\" to get started."
                : "No repositories registered.";
            notifications.PublishInfo(msg);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReindexAsync(string name)
    {
        var repo = await registry.GetAsync(name);
        if (repo is null) return NotFound();

        var source = repo.GitUrl ?? repo.LocalPath;
        await cloneIndex.EnqueueRepoAsync(name, source, repo.Branch);

        notifications.PublishInfo($"Re-index triggered for '{name}'.");
        return RedirectToPage();
    }
}
