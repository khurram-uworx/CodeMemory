using CodeMemory.Indexing.Graph;
using CodeMemory.Mcp.Services;
using CodeMemory.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class EditContextToolTests : BaseToolTests
{
    [Test]
    public async Task ToolAppearsInDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var result = await SendToolsList(client);

        var tools = result["result"]?["tools"]?.AsArray();
        var toolNames = tools!.Select(t => t!["name"]?.GetValue<string>()).ToList();
        Assert.That(toolNames, Does.Contain("get_edit_context"));
    }

    [Test]
    public async Task GetEditContext_ReturnsPartial_WhenNoServices()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var result = await CallTool(client, "get_edit_context",
            new JsonObject { ["symbolPath"] = "MyClass" });

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("MyClass"));
    }

    [Test]
    public async Task GetEditContext_ReturnsFullContext_WithMocks()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IStorageService>(new MockStorageService());
                    s.AddSingleton<IDependencyGraphService>(new MockGraphService());
                    s.AddSingleton<IEditContextService>(sp =>
                    {
                        var logger = sp.GetRequiredService<ILogger<EditContextService>>();
                        return new EditContextService(sp);
                    });
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "get_edit_context",
            new JsonObject
            {
                ["symbolPath"] = "MyClass",
                ["includeDependencies"] = true,
                ["depth"] = 2,
                ["includeSourceCode"] = true
            });

        Assert.That(result["error"], Is.Null);
        var text = result["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("MyClass"));
        Assert.That(text, Does.Contain("/src/MyClass.cs"));
        Assert.That(text, Does.Contain("sourceCode"));
        Assert.That(text, Does.Contain("dependencies"));
        Assert.That(text, Does.Contain("MyOtherClass"));
        Assert.That(text, Does.Contain("tests"));
    }
}
