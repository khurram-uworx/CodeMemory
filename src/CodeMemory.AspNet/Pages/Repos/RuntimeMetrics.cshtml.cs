using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Models;
using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Storage;
using CodeMemory.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace CodeMemory.AspNet.Pages.Repos;

public sealed class RuntimeMetricsModel : PageModel
{
    readonly RepoRegistryService registry;
    readonly IMetricsStore metricsStore;
    readonly LocalMetricsOptions options;

    public Repositories? Repo { get; private set; }
    public string? NotFoundMessage { get; private set; }
    public bool IsEnabled { get; private set; }
    public RuntimeMetricsSnapshot? AllSnapshot { get; private set; }
    public RuntimeMetricsSnapshot? RepoSnapshot { get; private set; }

    public RuntimeMetricsModel(
        RepoRegistryService registry,
        IMetricsStore metricsStore,
        IOptions<LocalMetricsOptions> options)
    {
        this.registry = registry;
        this.metricsStore = metricsStore;
        this.options = options.Value;
    }

    public async Task<IActionResult> OnGetAsync(string name)
    {
        Repo = await registry.GetAsync(name);
        if (Repo is null)
        {
            NotFoundMessage = $"Repository '{name}' not found.";
            return Page();
        }

        if (options.Enabled)
        {
            IsEnabled = true;
            AllSnapshot = metricsStore.GetSnapshot();
            RepoSnapshot = FilterByRepo(AllSnapshot, name);
        }

        return Page();
    }

    static RuntimeMetricsSnapshot? FilterByRepo(RuntimeMetricsSnapshot snapshot, string repoName)
    {
        var filtered = new List<InstrumentMetric>(snapshot.Instruments.Count);

        foreach (var inst in snapshot.Instruments)
        {
            var matchingValues = inst.Values
                .Where(v => HasRepoTag(v, repoName))
                .ToList();

            if (matchingValues.Count > 0)
                filtered.Add(new InstrumentMetric(inst.Name, inst.InstrumentType, matchingValues));
        }

        return filtered.Count > 0
            ? new RuntimeMetricsSnapshot(snapshot.CollectedAt, filtered)
            : null;
    }

    static bool HasRepoTag(MetricValue value, string repoName)
    {
        if (value.Tags is null || value.Tags.Count == 0)
            return false;

        return value.Tags.Any(t =>
            string.Equals(t.Key, CodeMemoryMetrics.Tags.Repo, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Value, repoName, StringComparison.OrdinalIgnoreCase));
    }
}
