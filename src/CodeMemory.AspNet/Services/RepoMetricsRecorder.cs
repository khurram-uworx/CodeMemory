using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Storage;
using CodeMemory.Diagnostics;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CodeMemory.AspNet.Services;

sealed record RepoInstrument(string Name, string Description, Func<OverviewStats, int> Selector);

public sealed class RepoMetricsRecorder
{
    readonly MetricsService metricsService;
    readonly IServiceRegistry registry;
    readonly IMetricsStore? metricsStore;
    readonly ILogger<RepoMetricsRecorder> logger;
    readonly LocalMetricsOptions options;
    readonly ConcurrentDictionary<string, RepoMetrics> cache = new(StringComparer.OrdinalIgnoreCase);

    static readonly RepoInstrument[] RepoInstruments =
    [
        new("codememory.repo.total_symbols", "Total number of symbols per repo", o => o.TotalSymbols),
        new("codememory.repo.total_files", "Total number of indexed files per repo", o => o.TotalFiles),
        new("codememory.repo.classes", "Number of class symbols per repo", o => o.Classes),
        new("codememory.repo.methods", "Number of method symbols per repo", o => o.Methods),
        new("codememory.repo.interfaces", "Number of interface symbols per repo", o => o.Interfaces),
        new("codememory.repo.properties", "Number of property symbols per repo", o => o.Properties),
        new("codememory.repo.fields", "Number of field symbols per repo", o => o.Fields),
        new("codememory.repo.total_relationships", "Total number of symbol relationships per repo", o => o.TotalRelationships),
    ];

    public RepoMetricsRecorder(
        MetricsService metricsService,
        IServiceRegistry registry,
        IOptions<LocalMetricsOptions> localMetricsOptions,
        ILogger<RepoMetricsRecorder> logger) : this(metricsService, registry, null, localMetricsOptions, logger)
    { }

    public RepoMetricsRecorder(
        MetricsService metricsService,
        IServiceRegistry registry,
        IMetricsStore? metricsStore,
        IOptions<LocalMetricsOptions> localMetricsOptions,
        ILogger<RepoMetricsRecorder> logger)
    {
        this.metricsService = metricsService;
        this.registry = registry;
        this.metricsStore = metricsStore;
        this.logger = logger;
        options = localMetricsOptions.Value;

        foreach (var inst in RepoInstruments)
        {
            CodeMemoryMetrics.Meter.CreateObservableGauge<long>(
                inst.Name,
                observeValues: () => cache.Select(kvp => gauge(inst.Selector(kvp.Value.Overview), kvp.Key)),
                description: inst.Description);
        }
    }

    static Measurement<long> gauge(long value, string repo)
        => new(value, new KeyValuePair<string, object?>(CodeMemoryMetrics.Tags.Repo, repo));

    void recordToStore(string name, int value, IReadOnlyList<KeyValuePair<string, object?>> tags)
        => metricsStore!.RecordGauge(name, value, tags);

    public void RemoveRepo(string repoName)
    {
        cache.TryRemove(repoName, out _);

        if (!options.Enabled || metricsStore is null)
            return;

        var tags = new[] { new KeyValuePair<string, object?>(CodeMemoryMetrics.Tags.Repo, repoName) };
        foreach (var inst in RepoInstruments)
            metricsStore.RemoveInstrumentTags(inst.Name, tags);
    }

    public async Task RecordAsync(string repoName)
    {
        try
        {
            var storage = registry.GetStorage(repoName);
            if (storage is not HybridStorageService)
                return;

            var metrics = await metricsService.GetMetricsAsync(repoName);
            cache[repoName] = metrics;

            if (!options.Enabled || metricsStore is null)
                return;

            var tags = new[] { new KeyValuePair<string, object?>(CodeMemoryMetrics.Tags.Repo, repoName) };
            var o = metrics.Overview;
            foreach (var inst in RepoInstruments)
                recordToStore(inst.Name, inst.Selector(o), tags);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record repo metrics for '{Repo}'", repoName);
        }
    }
}
