using CodeMemory.Indexing.Graph;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class TraceDependencyToolTests
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
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

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
        var result = JsonNode.Parse(jsonLine)!.AsObject();

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

        var result = await callTool(client, "trace_dependency",
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

        var result = await callTool(client, "trace_dependency",
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

        var result = await callTool(client, "trace_dependency",
            new JsonObject { ["symbolPath"] = "NonExistent" });

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        Assert.That(text, Does.Not.Contain("NonExistent"));
    }
}
