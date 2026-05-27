using CodeMemory.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace CodeMemory.Tests.ErrorHandling;

/// <summary>
/// Verifies every MCP tool's degraded/null-service behavior.
/// Coverage target: every guard clause in every tool method (see AGENTS.md §Error Handling).
///
/// Tools without null-service guards (always required DI):
///   - AdminTool (IStorageService, IServiceScopeFactory)
///   - McpTools.Ping (static IndexingState)
///   - AspNetMcpTools.Ping (IRepoContextAccessor)
/// </summary>
public sealed class McpToolErrorHandlingTests
{
    static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

    [Test]
    public async Task SemanticSearch_ReturnsEmpty_WhenSearchServiceNotRegistered()
    {
        var tool = new SemanticSearchTool(NullLogger<SemanticSearchTool>.Instance, EmptyServices());
        var result = await tool.SemanticSearchAsync("anything");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task ArchitectureOverview_ReturnsDefault_WhenArchitectureServiceNotRegistered()
    {
        var tool = new ArchitectureOverviewTool(NullLogger<ArchitectureOverviewTool>.Instance, EmptyServices());
        var result = await tool.GetArchitectureOverviewAsync();

        Assert.That(result.TotalFiles, Is.EqualTo(0));
        Assert.That(result.TotalSymbols, Is.EqualTo(0));
    }

    [Test]
    public async Task FindRelatedCode_ReturnsEmpty_WhenGraphServiceNotRegistered()
    {
        var tool = new FindRelatedCodeTool(NullLogger<FindRelatedCodeTool>.Instance, EmptyServices());
        var result = await tool.FindRelatedCodeAsync("TestClass");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task TraceDependency_ReturnsEmpty_WhenGraphServiceNotRegistered()
    {
        var tool = new TraceDependencyTool(NullLogger<TraceDependencyTool>.Instance, EmptyServices());
        var result = await tool.TraceDependencyAsync("TestClass");

        Assert.That(result.DependencyChain, Is.Empty);
        Assert.That(result.RelatedSymbols, Is.Empty);
    }

    [Test]
    public async Task GetSymbolHistory_ReturnsWarning_WhenGitHistoryServiceNotRegistered()
    {
        var tool = new GitHistoryTool(NullLogger<GitHistoryTool>.Instance, EmptyServices());
        var result = await tool.GetSymbolHistoryAsync("TestClass");

        Assert.That(result, Is.Not.Null);

        var json = JsonSerializer.Serialize(result);
        using var obj = JsonDocument.Parse(json);
        var root = obj.RootElement;
        Assert.That(root.GetProperty("warning").GetString(), Is.EqualTo("Git history service not available"));
    }

    [Test]
    public async Task GetHotspots_ReturnsEmpty_WhenGitHistoryServiceNotRegistered()
    {
        var tool = new GitHistoryTool(NullLogger<GitHistoryTool>.Instance, EmptyServices());
        var result = await tool.GetHotspotsAsync();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task EditContext_ReturnsMinimal_WhenEditContextServiceNotRegistered()
    {
        var tool = new EditContextTool(NullLogger<EditContextTool>.Instance, EmptyServices());
        var result = await tool.GetEditContextAsync("TestClass");

        Assert.That(result.Warnings, Is.Not.Null);
        Assert.That(result.Warnings, Does.Contain("Edit context service not available"));
    }

    [Test]
    public async Task ImpactAnalysis_ReturnsWarning_WhenGraphServiceNotRegistered()
    {
        var tool = new ImpactAnalysisTool(NullLogger<ImpactAnalysisTool>.Instance, EmptyServices());
        var result = await tool.ImpactAnalysisAsync("TestClass");

        Assert.That(result.Warning, Is.EqualTo("Dependency graph service not available"));
        Assert.That(result.DownstreamDependencies, Is.Empty);
        Assert.That(result.AffectedFiles, Is.Empty);
    }

    [Test]
    public async Task ComponentClusters_ReturnsEmpty_WhenClusteringServiceNotRegistered()
    {
        var tool = new ComponentClustersTool(NullLogger<ComponentClustersTool>.Instance, EmptyServices());
        var result = await tool.GetComponentClustersAsync();

        Assert.That(result, Is.Empty);
    }
}
