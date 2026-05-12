using CodeMemory.Indexing.Git;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class GitHistoryToolTests : BaseToolTests
{
    [Test]
    public async Task ToolAppearsInDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IGitHistoryService>(new MockGitHistoryService());
                });
            });
        var client = factory.CreateClient();
        var result = await SendToolsList(client);

        var tools = result["result"]?["tools"]?.AsArray();
        var toolNames = tools!.Select(t => t!["name"]?.GetValue<string>()).ToList();
        Assert.That(toolNames, Does.Contain("get_symbol_history"));
        Assert.That(toolNames, Does.Contain("get_hotspots"));
    }

    [Test]
    public async Task GetSymbolHistory_ReturnsWarning_WhenNoService()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    var descriptor = s.SingleOrDefault(d => d.ServiceType == typeof(IGitHistoryService));
                    if (descriptor != null)
                        s.Remove(descriptor);
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "get_symbol_history",
            new JsonObject { ["symbolPath"] = "MyClass" });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null.And.Not.Empty);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("warning"));
    }

    [Test]
    public async Task GetSymbolHistory_ReturnsHistory_WithMockedService()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IGitHistoryService>(new MockGitHistoryService());
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "get_symbol_history",
            new JsonObject { ["symbolPath"] = "MyClass.MyMethod", ["maxCommits"] = 5 });

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var obj = JsonNode.Parse(text!)!.AsObject();
        Assert.That(obj["totalCommits"]?.GetValue<int>(), Is.EqualTo(3));
        Assert.That(obj["uniqueAuthors"]?.GetValue<int>(), Is.EqualTo(1));
        Assert.That(obj["recentCommits"]?.AsArray(), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetHotspots_ReturnsEmpty_WhenNoService()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    var descriptor = s.SingleOrDefault(d => d.ServiceType == typeof(IGitHistoryService));
                    if (descriptor != null)
                        s.Remove(descriptor);
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "get_hotspots",
            new JsonObject { ["top"] = 5 });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null.And.Not.Empty);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("[]"));
    }

    [Test]
    public async Task GetHotspots_ReturnsResults_WithMockedService()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IGitHistoryService>(new MockGitHistoryService());
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "get_hotspots",
            new JsonObject { ["top"] = 10, ["maxCommits"] = 100 });

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var arr = JsonNode.Parse(text!)!.AsArray();
        Assert.That(arr.Count, Is.EqualTo(2));
        Assert.That(arr[0]?["filePath"]?.GetValue<string>(), Does.Contain("Service.cs"));
    }
}
