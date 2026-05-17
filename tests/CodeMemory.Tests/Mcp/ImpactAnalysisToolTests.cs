using CodeMemory.Indexing.Architecture;
using CodeMemory.Indexing.Graph;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class ImpactAnalysisToolTests : BaseToolTests
{
    [Test]
    public async Task ToolAppearsInDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IDependencyGraphService>(new MockDependencyGraphService());
                    s.AddSingleton<IArchitectureService>(new MockArchitectureService());
                });
            });
        var client = factory.CreateClient();

        var body = await SendToolsList(client);

        var tools = body["result"]?["tools"]?.AsArray();
        Assert.That(tools, Is.Not.Null);
        var toolNames = tools!.Select(t => t!["name"]?.GetValue<string>()).ToList();
        Assert.That(toolNames, Does.Contain("impact_analysis"));
    }

    [Test]
    public async Task ImpactAnalysis_ReturnsWarning_WhenNoGraphService()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    var descriptor = s.SingleOrDefault(d => d.ServiceType == typeof(IDependencyGraphService));
                    if (descriptor != null)
                        s.Remove(descriptor);
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "impact_analysis",
            new JsonObject { ["symbolPath"] = "MyClass" });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("warning"));
        Assert.That(text, Does.Contain("not available"));
    }

    [Test]
    public async Task ImpactAnalysis_ReturnsResults_WithMockedServices()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IDependencyGraphService>(new MockDependencyGraphService());
                    s.AddSingleton<IArchitectureService>(new MockArchitectureService());
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "impact_analysis",
            new JsonObject { ["symbolPath"] = "MyClass", ["depth"] = 2 });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("downstreamDependencies"));
        Assert.That(text, Does.Contain("MyClass"));
        Assert.That(text, Does.Contain("MyOtherClass"));
        Assert.That(text, Does.Contain("testFiles"));
    }

    [Test]
    public async Task ImpactAnalysis_UnknownSymbol_ReturnsEmptyDependencies()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IDependencyGraphService>(new MockDependencyGraphService());
                    s.AddSingleton<IArchitectureService>(new MockArchitectureService());
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "impact_analysis",
            new JsonObject { ["symbolPath"] = "NonExistent" });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        var obj = JsonNode.Parse(text!)!.AsObject();
        Assert.That(obj["downstreamDependencies"]?.AsArray(), Has.Count.EqualTo(0));
        Assert.That(obj["affectedFiles"]?.AsArray(), Has.Count.EqualTo(0));
    }
}
