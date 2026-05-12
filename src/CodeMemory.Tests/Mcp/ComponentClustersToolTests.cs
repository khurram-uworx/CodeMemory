using CodeMemory.Indexing.Architecture;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class ComponentClustersToolTests : BaseToolTests
{
    [Test]
    public async Task ToolAppearsInDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var result = await SendToolsList(client);

        var tools = result["result"]?["tools"]?.AsArray();
        var toolNames = tools!.Select(t => t!["name"]?.GetValue<string>()).ToList();
        Assert.That(toolNames, Does.Contain("get_component_clusters"));
    }

    [Test]
    public async Task ReturnsEmpty_WhenNoService()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var result = await CallTool(client, "get_component_clusters",
            new JsonObject { ["threshold"] = 0.3 });

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("[]"));
    }

    [Test]
    public async Task ReturnsClusters_WithMockedService()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IComponentClusteringService>(new MockClusteringService());
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "get_component_clusters",
            new JsonObject { ["threshold"] = 0.3 });

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var arr = JsonNode.Parse(text!)!.AsArray();
        Assert.That(arr.Count, Is.EqualTo(2));
        var first = arr[0]!.AsObject();
        Assert.That(first["name"]?.GetValue<string>(), Does.Contain("src"));
        Assert.That(first["name"]?.GetValue<string>(), Does.Contain("tests"));
        Assert.That(first["cohesionScore"]?.GetValue<double>(), Is.EqualTo(0.75));
    }
}
