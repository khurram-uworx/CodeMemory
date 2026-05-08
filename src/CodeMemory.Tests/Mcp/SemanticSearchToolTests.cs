using CodeMemory.Indexing.Search;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class SemanticSearchToolTests
{
    static async Task<JsonObject> sendToolsList(HttpClient client)
    {
        var json = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp")
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
        return JsonNode.Parse(jsonLine)!.AsObject();
    }

    static async Task<JsonObject> callTool(HttpClient client, string toolName, JsonObject args)
    {
        var callJson = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "2",
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = args
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp")
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
                    var mock = new MockSemanticSearchService();
                    s.AddSingleton<ISemanticSearchService>(mock);
                });
            });
        var client = factory.CreateClient();

        var body = await sendToolsList(client);

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
        var client = factory.CreateClient();

        var result = await callTool(client, "semantic_search",
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
        var client = factory.CreateClient();

        var result = await callTool(client, "semantic_search",
            new JsonObject { ["query"] = "anything", ["maxResults"] = 5 });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Is.EqualTo("[]"));
    }
}
