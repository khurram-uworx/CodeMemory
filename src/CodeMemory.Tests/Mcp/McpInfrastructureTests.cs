using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class McpInfrastructureTests
{
    [Test]
    public async Task HealthEndpoint_RespondsOk()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.That(body?["status"]?.GetValue<string>(), Is.EqualTo("healthy"));
    }

    [Test]
    public async Task McpEndpoint_RespondsToPost()
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
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp/test")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("Origin", "https://example.com");

        var response = await client.SendAsync(request);

        Assert.That(response.Headers.Contains("Access-Control-Allow-Origin"), Is.True);
    }
}
