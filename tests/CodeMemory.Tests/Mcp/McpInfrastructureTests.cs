using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class McpInfrastructureTests : BaseToolTests
{
    [Test]
    public async Task HealthEndpoint_RespondsOk()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.That(body?["service"]?.GetValue<string>(), Does.StartWith("CodeMemory"));
    }

    [Test]
    public async Task McpEndpoint_RespondsToPost()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var json = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, McpUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await client.SendAsync(request);

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK),
            $"Response body: {responseBody}");

        // MCP Streamable HTTP may return SSE format: parse the data line
        var jsonLine = responseBody
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(l => l.StartsWith("data: "))
            ?.Substring("data: ".Length)
            ?? responseBody;

        var body = JsonNode.Parse(jsonLine)?.AsObject();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!["jsonrpc"]?.GetValue<string>(), Is.EqualTo("2.0"));
        Assert.That(body["id"]?.GetValue<int>(), Is.EqualTo(1));

        var tools = body["result"]?["tools"]?.AsArray();
        Assert.That(tools, Is.Not.Null);
        var toolNames = tools!.Select(t => t!["name"]?.GetValue<string>()).ToList();
        Assert.That(toolNames, Does.Contain("ping"));
    }

    [Test]
    public async Task McpEndpoint_ReturnsCorsHeaders()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var json = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, McpUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("Origin", "https://example.com");

        var response = await client.SendAsync(request);

        Assert.That(response.Headers.Contains("Access-Control-Allow-Origin"), Is.True);
    }

    [Test]
    public async Task Ping_ReturnsAspNetVersion()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var result = await CallTool(client, "ping", new JsonObject());
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Is.Not.Null);

        var body = JsonNode.Parse(text)!.AsObject();
        Assert.That(body["status"]?.GetValue<string>(), Is.EqualTo("ok"));

        // Per-repo awareness — proves AspNetMcpTools.Ping replaced McpTools.Ping
        Assert.That(body.ContainsKey("repo"), Is.True);
        Assert.That(body["repo"]?.GetValue<string>(), Is.EqualTo("codememory"));

        //if (body["indexingCompleted"]?.GetValue<bool>() == true)
        //{
        //    Assert.That(body["host"]?.GetValue<string>(), Is.EqualTo("aspnet"));
        //    Assert.That(body["transport"]?.GetValue<string>(), Is.EqualTo("streamable-http"));
        //}
    }
}
