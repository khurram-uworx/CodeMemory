using CodeMemory.Indexing.Architecture;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class ComponentClustersToolTests
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
        await using var factory = new WebApplicationFactory<Program>();
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
        Assert.That(toolNames, Does.Contain("get_component_clusters"));
    }

    [Test]
    public async Task ReturnsEmpty_WhenNoService()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var result = await callTool(client, "get_component_clusters",
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

        var result = await callTool(client, "get_component_clusters",
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
