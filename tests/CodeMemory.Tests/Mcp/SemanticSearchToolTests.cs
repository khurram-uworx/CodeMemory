using CodeMemory.Indexing.Search;
using CodeMemory.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class SemanticSearchToolTests : BaseToolTests
{
    [Test]
    public async Task ToolAppearsInDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<ISemanticSearchService>(MockServices.CreateSemanticSearchService());
                });
            });
        var client = factory.CreateClient();

        var body = await SendToolsList(client);

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
                    s.AddSingleton<ISemanticSearchService>(MockServices.CreateSemanticSearchService());
                });
            });
        await factory.RegisterRepoAsync();
        var client = factory.CreateClient();

        var result = await CallTool(client, "semantic_search",
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
        await factory.RegisterRepoAsync();
        var client = factory.CreateClient();

        var result = await CallTool(client, "semantic_search",
            new JsonObject { ["query"] = "anything", ["maxResults"] = 5 });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Is.EqualTo("[]"));
    }

    [Test]
    public async Task SemanticSearch_CodeOnlyTrue_ExcludesDocumentationChunks()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<ISemanticSearchService>(CreateMixedSearchService());
                });
            });
        await factory.RegisterRepoAsync();
        var client = factory.CreateClient();

        var result = await CallTool(client, "semantic_search",
            new JsonObject { ["query"] = "database docs", ["maxResults"] = 5, ["codeOnly"] = true });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();

        var chunkIds = JsonNode.Parse(text!)!.AsArray().Select(n => n!["chunkId"]?.GetValue<string>()).ToList();
        Assert.That(chunkIds, Is.EquivalentTo(["chunk-code"]));
    }

    [Test]
    public async Task SemanticSearch_CodeOnlyFalse_IncludesDocumentationChunks()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<ISemanticSearchService>(CreateMixedSearchService());
                });
            });
        await factory.RegisterRepoAsync();
        var client = factory.CreateClient();

        var result = await CallTool(client, "semantic_search",
            new JsonObject { ["query"] = "database docs", ["maxResults"] = 5 });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();

        var chunkIds = JsonNode.Parse(text!)!.AsArray().Select(n => n!["chunkId"]?.GetValue<string>()).ToList();
        Assert.That(chunkIds, Is.EquivalentTo(["chunk-code", "chunk-doc"]));
    }

    static ISemanticSearchService CreateMixedSearchService()
    {
        var search = Substitute.For<ISemanticSearchService>();
        search.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
              .Returns([
                  new ScoredChunk
                  {
                      Chunk = new ChunkRecord
                      {
                          Id = "chunk-code",
                          SymbolId = "DatabaseService",
                          FilePath = "/src/DatabaseService.cs",
                          Content = "public class DatabaseService { }",
                          Language = "CSharp",
                          LineStart = 1,
                          LineEnd = 10
                      },
                      Score = 0.9
                  },
                  new ScoredChunk
                  {
                      Chunk = new ChunkRecord
                      {
                          Id = "chunk-doc",
                          SymbolId = "ReadmeDoc",
                          FilePath = "/docs/README.md",
                          Content = "Documentation about the database service",
                          Language = "Text",
                          LineStart = 1,
                          LineEnd = 30
                      },
                      Score = 0.8
                  }
              ]);
        return search;
    }
}
