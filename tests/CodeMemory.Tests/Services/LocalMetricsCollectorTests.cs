using CodeMemory.AspNet.Services;
using CodeMemory.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeMemory.Tests.Services;

public sealed class LocalMetricsCollectorTests
{
    [Test]
    public async Task GetSnapshot_Disabled_ReturnsEmptyDisabledSnapshot()
    {
        using var collector = new LocalMetricsCollector(
            Options.Create(new LocalMetricsOptions { Enabled = false }),
            NullLogger<LocalMetricsCollector>.Instance);

        await collector.StartAsync(CancellationToken.None);

        var snapshot = collector.GetSnapshot();

        Assert.That(snapshot.Enabled, Is.False);
        Assert.That(snapshot.Series, Is.Empty);
    }

    [Test]
    public async Task GetSnapshot_WithRepoFilter_ReturnsOnlyMatchingRepoSeries()
    {
        using var collector = new LocalMetricsCollector(
            Options.Create(new LocalMetricsOptions { Enabled = true, MaxSeries = 20 }),
            NullLogger<LocalMetricsCollector>.Instance);

        await collector.StartAsync(CancellationToken.None);
        var repoName = $"repo-{Guid.NewGuid():N}";
        var otherRepo = $"repo-{Guid.NewGuid():N}";

        using (CodeMemoryMetrics.BeginRepoScope(repoName))
        {
            CodeMemoryMetrics.AddToolInvocation("ping", "aspnet");
            CodeMemoryMetrics.RecordSearchQueryDuration(12);
        }

        using (CodeMemoryMetrics.BeginRepoScope(otherRepo))
        {
            CodeMemoryMetrics.AddToolInvocation("ping", "aspnet");
        }

        var snapshot = collector.GetSnapshot(repoName);

        Assert.That(snapshot.Enabled, Is.True);
        Assert.That(snapshot.Series, Is.Not.Empty);
        Assert.That(snapshot.Series.All(s =>
            s.Tags.TryGetValue("repo.name", out var value) && value == repoName), Is.True);
        Assert.That(snapshot.Series.Any(s => s.Name == "codememory.tools.invocations"), Is.True);
        Assert.That(snapshot.Series.Any(s => s.Name == "codememory.search.query_duration"), Is.True);
    }

    [Test]
    public async Task GetSnapshot_WhenSeriesLimitReached_DropsNewSeries()
    {
        using var collector = new LocalMetricsCollector(
            Options.Create(new LocalMetricsOptions { Enabled = true, MaxSeries = 1 }),
            NullLogger<LocalMetricsCollector>.Instance);

        await collector.StartAsync(CancellationToken.None);

        using (CodeMemoryMetrics.BeginRepoScope($"repo-{Guid.NewGuid():N}"))
            CodeMemoryMetrics.AddToolInvocation("ping", "aspnet");

        using (CodeMemoryMetrics.BeginRepoScope($"repo-{Guid.NewGuid():N}"))
            CodeMemoryMetrics.AddToolInvocation("get_metrics_snapshot", "aspnet");

        var snapshot = collector.GetSnapshot();

        Assert.That(snapshot.SeriesCount, Is.EqualTo(1));
        Assert.That(snapshot.Series, Has.Count.EqualTo(1));
    }
}
