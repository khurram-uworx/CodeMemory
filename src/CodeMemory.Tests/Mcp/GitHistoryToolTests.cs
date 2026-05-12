using CodeMemory.Indexing.Git;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class GitHistoryToolTests
{
    static async Task<JsonObject> callTool(HttpClient client, string toolName, JsonObject args)
    {
        var callJson = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "1",
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = args
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp/test")
        {
            Content = new StringContent(callJson.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var jsonLine = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.StartsWith("data: "))
            ?.Substring("data: ".Length) ?? body;
        return JsonNode.Parse(jsonLine)!.AsObject();
    }

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

        var json = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp/test")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var jsonLine = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.StartsWith("data: "))
            ?.Substring("data: ".Length) ?? body;
        var result = JsonNode.Parse(jsonLine)!.AsObject();

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

        var result = await callTool(client, "get_symbol_history",
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

        var result = await callTool(client, "get_symbol_history",
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

        var result = await callTool(client, "get_hotspots",
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

        var result = await callTool(client, "get_hotspots",
            new JsonObject { ["top"] = 10, ["maxCommits"] = 100 });

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var arr = JsonNode.Parse(text!)!.AsArray();
        Assert.That(arr.Count, Is.EqualTo(2));
        Assert.That(arr[0]?["filePath"]?.GetValue<string>(), Does.Contain("Service.cs"));
    }
}
