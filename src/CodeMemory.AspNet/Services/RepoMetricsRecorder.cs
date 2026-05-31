using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Storage;
using CodeMemory.Diagnostics;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CodeMemory.AspNet.Services;

public sealed class RepoMetricsRecorder
{
    readonly MetricsService metricsService;
    readonly IServiceRegistry registry;
    readonly IMetricsStore? metricsStore;
    readonly ILogger<RepoMetricsRecorder> logger;
    readonly LocalMetricsOptions options;
    readonly ConcurrentDictionary<string, RepoMetrics> cache = new(StringComparer.OrdinalIgnoreCase);

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

        CodeMemoryMetrics.Meter.CreateObservableGauge<long>(
            "codememory.repo.total_symbols",
            observeValues: () => cache.Select(kvp => gauge(kvp.Value.Overview.TotalSymbols, kvp.Key)),
            description: "Total number of symbols per repo");

        CodeMemoryMetrics.Meter.CreateObservableGauge<long>(
            "codememory.repo.total_files",
            observeValues: () => cache.Select(kvp => gauge(kvp.Value.Overview.TotalFiles, kvp.Key)),
            description: "Total number of indexed files per repo");

        CodeMemoryMetrics.Meter.CreateObservableGauge<long>(
            "codememory.repo.classes",
            observeValues: () => cache.Select(kvp => gauge(kvp.Value.Overview.Classes, kvp.Key)),
            description: "Number of class symbols per repo");

        CodeMemoryMetrics.Meter.CreateObservableGauge<long>(
            "codememory.repo.methods",
            observeValues: () => cache.Select(kvp => gauge(kvp.Value.Overview.Methods, kvp.Key)),
            description: "Number of method symbols per repo");

        CodeMemoryMetrics.Meter.CreateObservableGauge<long>(
            "codememory.repo.interfaces",
            observeValues: () => cache.Select(kvp => gauge(kvp.Value.Overview.Interfaces, kvp.Key)),
            description: "Number of interface symbols per repo");

        CodeMemoryMetrics.Meter.CreateObservableGauge<long>(
            "codememory.repo.properties",
            observeValues: () => cache.Select(kvp => gauge(kvp.Value.Overview.Properties, kvp.Key)),
            description: "Number of property symbols per repo");

        CodeMemoryMetrics.Meter.CreateObservableGauge<long>(
            "codememory.repo.fields",
            observeValues: () => cache.Select(kvp => gauge(kvp.Value.Overview.Fields, kvp.Key)),
            description: "Number of field symbols per repo");

        CodeMemoryMetrics.Meter.CreateObservableGauge<long>(
            "codememory.repo.total_relationships",
            observeValues: () => cache.Select(kvp => gauge(kvp.Value.Overview.TotalRelationships, kvp.Key)),
            description: "Total number of symbol relationships per repo");
    }

    static Measurement<long> gauge(long value, string repo)
        => new(value, new KeyValuePair<string, object?>(CodeMemoryMetrics.Tags.Repo, repo));

    void recordToStore(string name, int value, IReadOnlyList<KeyValuePair<string, object?>> tags)
        => metricsStore!.RecordGauge(name, value, tags);

    static readonly string[] RepoInstruments =
    [
        "codememory.repo.total_symbols",
        "codememory.repo.total_files",
        "codememory.repo.classes",
        "codememory.repo.methods",
        "codememory.repo.interfaces",
        "codememory.repo.properties",
        "codememory.repo.fields",
        "codememory.repo.total_relationships",
    ];

    public void RemoveRepo(string repoName)
    {
        cache.TryRemove(repoName, out _);

        if (!options.Enabled || metricsStore is null)
            return;

        var tags = new[] { new KeyValuePair<string, object?>(CodeMemoryMetrics.Tags.Repo, repoName) };
        foreach (var instrument in RepoInstruments)
            metricsStore.RemoveInstrumentTags(instrument, tags);
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
            recordToStore("codememory.repo.total_symbols", o.TotalSymbols, tags);
            recordToStore("codememory.repo.total_files", o.TotalFiles, tags);
            recordToStore("codememory.repo.classes", o.Classes, tags);
            recordToStore("codememory.repo.methods", o.Methods, tags);
            recordToStore("codememory.repo.interfaces", o.Interfaces, tags);
            recordToStore("codememory.repo.properties", o.Properties, tags);
            recordToStore("codememory.repo.fields", o.Fields, tags);
            recordToStore("codememory.repo.total_relationships", o.TotalRelationships, tags);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record repo metrics for '{Repo}'", repoName);
        }
    }
}
