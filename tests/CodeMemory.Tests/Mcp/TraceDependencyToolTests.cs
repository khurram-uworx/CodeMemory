using CodeMemory.Indexing;
using CodeMemory.Indexing.Graph;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class TraceDependencyToolTests : BaseToolTests
{
    [Test]
    public async Task ToolAppearsInDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var result = await SendToolsList(client);

        var tools = result["result"]?["tools"]?.AsArray();
        Assert.That(tools, Is.Not.Null);
        var toolNames = tools!.Select(t => t!["name"]?.GetValue<string>()).ToList();
        Assert.That(toolNames, Does.Contain("trace_dependency"));
    }

    [Test]
    public async Task TraceDependency_ReturnsEmpty_WhenNoService()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        while (!IndexingState.IsCompleted())
            await Task.Delay(100);

        var result = await CallTool(client, "trace_dependency",
            new JsonObject { ["symbolPath"] = "MyClass.MyMethod" });

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("dependencyChain"));
        Assert.That(text, Does.Contain("relatedSymbols"));
    }

    [Test]
    public async Task TraceDependency_ReturnsResults_WithMockedService()
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

        var result = await CallTool(client, "trace_dependency",
            new JsonObject
            {
                ["symbolPath"] = "MyClass.MyMethod",
                ["direction"] = "upstream",
                ["depth"] = 2,
                ["includeTests"] = true
            });

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("MyClass.MyMethod"));
        Assert.That(text, Does.Contain("MyOtherClass"));
        Assert.That(text, Does.Contain("TestCoverage"));
    }

    [Test]
    public async Task TraceDependency_UnknownSymbol_ReturnsEmpty()
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

        var result = await CallTool(client, "trace_dependency",
            new JsonObject { ["symbolPath"] = "NonExistent" });

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        Assert.That(text, Does.Not.Contain("NonExistent"));
    }
}
