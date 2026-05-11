using CodeMemory.Indexing.Graph;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class FindRelatedCodeToolTests
{
    static async Task<JsonObject> sendToolsList(HttpClient client)
    {
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
                b.UseSetting("Repositories:test", ".");
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IDependencyGraphService>(new MockDependencyGraphService());
                    var hd = s.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                        && d.ImplementationType?.Name == "IndexingHostedService");
                    if (hd != null) s.Remove(hd);
                });
            });
        var client = factory.CreateClient();

        var body = await sendToolsList(client);

        var tools = body["result"]?["tools"]?.AsArray();
        Assert.That(tools, Is.Not.Null);
        var toolNames = tools!.Select(t => t!["name"]?.GetValue<string>()).ToList();
        Assert.That(toolNames, Does.Contain("find_related_code"));
    }

    [Test]
    public async Task FindRelatedCode_ReturnsEmpty_WhenNoService()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Repositories:test", ".");
                b.ConfigureServices(s =>
                {
                    var hd = s.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                        && d.ImplementationType?.Name == "IndexingHostedService");
                    if (hd != null) s.Remove(hd);
                });
            });
        var client = factory.CreateClient();

        var result = await callTool(client, "find_related_code",
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
                b.UseSetting("Repositories:test", ".");
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IDependencyGraphService>(new MockDependencyGraphService());
                    var hd = s.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                        && d.ImplementationType?.Name == "IndexingHostedService");
                    if (hd != null) s.Remove(hd);
                });
            });
        var client = factory.CreateClient();

        var result = await callTool(client, "find_related_code",
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
                b.UseSetting("Repositories:test", ".");
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IDependencyGraphService>(new MockDependencyGraphService());
                    var hd = s.SingleOrDefault(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                        && d.ImplementationType?.Name == "IndexingHostedService");
                    if (hd != null) s.Remove(hd);
                });
            });
        var client = factory.CreateClient();

        var result = await callTool(client, "find_related_code",
            new JsonObject { ["symbolPath"] = "NonExistent" });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Is.EqualTo("[]"));
    }
}
