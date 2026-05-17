using CodeMemory.Indexing.Graph;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class FindRelatedCodeToolTests : BaseToolTests
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
                });
            });
        var client = factory.CreateClient();

        var body = await SendToolsList(client);

        var tools = body["result"]?["tools"]?.AsArray();
        Assert.That(tools, Is.Not.Null);
        var toolNames = tools!.Select(t => t!["name"]?.GetValue<string>()).ToList();
        Assert.That(toolNames, Does.Contain("find_related_code"));
    }

    [Test]
    public async Task FindRelatedCode_ReturnsEmpty_WhenNoService()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var result = await CallTool(client, "find_related_code",
            new JsonObject { ["symbolPath"] = "MyClass" });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Is.EqualTo("[]"));
    }

    [Test]
    public async Task FindRelatedCode_ReturnsResults_WithMockedService()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IDependencyGraphService>(new MockDependencyGraphService());
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "find_related_code",
            new JsonObject { ["symbolPath"] = "MyClass", ["relationType"] = "all" });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("RelatedService"));
        Assert.That(text, Does.Contain("references"));
    }

    [Test]
    public async Task FindRelatedCode_UnknownSymbol_ReturnsEmpty()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IDependencyGraphService>(new MockDependencyGraphService());
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "find_related_code",
            new JsonObject { ["symbolPath"] = "NonExistent" });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Is.EqualTo("[]"));
    }
}
