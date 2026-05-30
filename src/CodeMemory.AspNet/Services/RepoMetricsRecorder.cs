using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Storage;
using CodeMemory.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CodeMemory.AspNet.Services;

public sealed class RepoMetricsRecorder
{
    readonly MetricsService metricsService;
    readonly IServiceRegistry registry;
    readonly ILogger<RepoMetricsRecorder> logger;
    readonly ConcurrentDictionary<string, RepoMetrics> cache = new(StringComparer.OrdinalIgnoreCase);

    public RepoMetricsRecorder(
        MetricsService metricsService,
        IServiceRegistry registry,
        ILogger<RepoMetricsRecorder> logger)
    {
        this.metricsService = metricsService;
        this.registry = registry;
        this.logger = logger;

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
        => new(value, new KeyValuePair<string, object?>("repo", repo));

    public void RemoveRepo(string repoName)
        => cache.TryRemove(repoName, out _);

    public async Task RecordAsync(string repoName)
    {
        try
        {
            var storage = registry.GetStorage(repoName);
            if (storage is not HybridStorageService)
                return;

            var metrics = await metricsService.GetMetricsAsync(repoName);
            cache[repoName] = metrics;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record repo metrics for '{Repo}'", repoName);
        }
    }
}
