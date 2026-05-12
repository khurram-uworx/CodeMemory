using CodeMemory.Indexing.Architecture;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class ArchitectureOverviewToolTests : BaseToolTests
{
    [Test]
    public async Task ToolAppearsInDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var result = await SendToolsList(client);

        var tools = result["result"]?["tools"]?.AsArray();
        var toolNames = tools!.Select(t => t!["name"]?.GetValue<string>()).ToList();
        Assert.That(toolNames, Does.Contain("get_architecture_overview"));
    }

    [Test]
    public async Task ReturnsDefault_WhenNoService()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var result = await CallTool(client, "get_architecture_overview",
            new JsonObject());

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("totalFiles"));
        Assert.That(text, Does.Contain("totalSymbols"));
    }

    [Test]
    public async Task ReturnsData_WithMockedService()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IArchitectureService>(new MockArchitectureService());
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "get_architecture_overview",
            new JsonObject());

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var obj = JsonNode.Parse(text!)!.AsObject();
        Assert.That(obj["totalFiles"]?.GetValue<int>(), Is.EqualTo(10));
        Assert.That(obj["totalSymbols"]?.GetValue<int>(), Is.EqualTo(42));
        Assert.That(text, Does.Contain("C#"));
    }

    sealed class MockArchitectureService : IArchitectureService
    {
        public Task<ArchitectureOverview> GetOverviewAsync(string? path = null, CancellationToken ct = default)
        {
            return Task.FromResult(new ArchitectureOverview(
                [new ComponentInfo("src", 5, 20), new ComponentInfo("tests", 3, 2)],
                new Dictionary<string, int> { ["C#"] = 8, ["JavaScript"] = 2 },
                10, 42
            ));
        }
    }
}
