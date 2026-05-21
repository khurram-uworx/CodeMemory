using CodeMemory.Indexing.Search;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class SemanticSearchToolTests : BaseToolTests
{
    [Test]
    public async Task ToolAppearsInDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    var mock = new MockSemanticSearchService();
                    s.AddSingleton<ISemanticSearchService>(mock);
                });
            });
        var client = factory.CreateClient();

        var body = await SendToolsList(client);

        var tools = body["result"]?["tools"]?.AsArray();
        Assert.That(tools, Is.Not.Null);
        var toolNames = tools!.Select(t => t!["name"]?.GetValue<string>()).ToList();
        Assert.That(toolNames, Does.Contain("semantic_search"));
    }

    [Test]
    public async Task SemanticSearch_ReturnsResults_WithRegisteredService()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    var mock = new MockSemanticSearchService();
                    s.AddSingleton<ISemanticSearchService>(mock);
                });
            });
        await factory.RegisterRepoAsync();
        var client = factory.CreateClient();

        var result = await CallTool(client, "semantic_search",
            new JsonObject { ["query"] = "find database code", ["maxResults"] = 5 });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        Assert.That(content!.Count, Is.GreaterThan(0));

        var text = content[0]!["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("chunkId"));
        Assert.That(text, Does.Contain("DatabaseService"));
    }

    [Test]
    public async Task SemanticSearch_ReturnsEmpty_WhenNoService()
    {
        await using var factory = new WebApplicationFactory<Program>();
        await factory.RegisterRepoAsync();
        var client = factory.CreateClient();

        var result = await CallTool(client, "semantic_search",
            new JsonObject { ["query"] = "anything", ["maxResults"] = 5 });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Is.EqualTo("[]"));
    }
}
