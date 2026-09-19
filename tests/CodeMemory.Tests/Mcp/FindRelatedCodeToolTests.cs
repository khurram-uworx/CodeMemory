using CodeMemory.Indexing.Graph;
using CodeMemory.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Text.Json.Nodes;

namespace CodeMemory.Tests.Mcp;

public sealed class FindRelatedCodeToolTests : BaseToolTests
{
    [Test]
    public async Task ToolAppearsInDiscovery()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IDependencyGraphService>(MockServices.CreateDependencyGraphService());
                });
            });
        var client = factory.CreateClient();

        var body = await SendToolsList(client);

        var tools = body["result"]?["tools"]?.AsArray();
        Assert.That(tools, Is.Not.Null);
        var toolNames = tools!.Select(t => t!["name"]?.GetValue<string>()).ToList();
        Assert.That(toolNames, Does.Contain("find_related_code"));
    }

    [Test]
    public async Task FindRelatedCode_UnavailableService_ReturnsDiagnostic()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var result = await CallTool(client, "find_related_code",
            new JsonObject { ["symbolPath"] = "MyClass" });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        var obj = JsonNode.Parse(text!)!.AsObject();
        Assert.That(obj["results"]?.AsArray(), Has.Count.EqualTo(0));
        Assert.That(obj["message"]?.GetValue<string>(), Does.Contain("could not be resolved"));
    }

    [Test]
    public async Task FindRelatedCode_ReturnsResults_WithMockedService()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<IDependencyGraphService>(MockServices.CreateDependencyGraphService());
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "find_related_code",
            new JsonObject { ["symbolPath"] = "MyClass", ["relationType"] = "all" });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        Assert.That(text, Does.Contain("RelatedService"));
        Assert.That(text, Does.Contain("references"));
    }

    [Test]
    public async Task FindRelatedCode_UnknownSymbol_ReturnsSuggestionDiagnostic()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    var graph = Substitute.For<IDependencyGraphService>();
                    graph.FindRelatedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                         .Returns(new List<DependencyNode>());
                    s.AddSingleton<IDependencyGraphService>(graph);
                    ReplaceStorage(s, CreateStorageWithSuggestions());
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "find_related_code",
            new JsonObject { ["symbolPath"] = "getLikeMethod" });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        var obj = JsonNode.Parse(text!)!.AsObject();
        Assert.That(obj["results"]?.AsArray(), Has.Count.EqualTo(0));
        Assert.That(obj["message"]?.GetValue<string>(), Does.Contain("not found in index"));
        var suggestions = obj["suggestions"]?.AsArray();
        Assert.That(suggestions, Is.Not.Null);
        Assert.That(suggestions![0]?.GetValue<string>(), Does.Contain("getLikeMethod(string pattern)"));
    }

    [Test]
    public async Task FindRelatedCode_NoRelationsOfRequestedType_ReportsAvailableTypes()
    {
        var methodSymbol = new SymbolRecord
        {
            Id = "sym-1",
            Name = "getLikeMethod",
            Kind = "Method",
            FilePath = "/src/QueryUtils.cs",
            FullName = "Ns.Util.QueryUtils.getLikeMethod(string pattern)",
            LineStart = 10,
            LineEnd = 22
        };

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    var graph = Substitute.For<IDependencyGraphService>();
                    graph.FindRelatedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                         .Returns(new List<DependencyNode>());
                    s.AddSingleton<IDependencyGraphService>(graph);

                    var storage = Substitute.For<IStorageService>();
                    storage.GetSymbolByFullNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(methodSymbol);
                    storage.GetRelationshipsByTargetAsync("sym-1", Arg.Any<CancellationToken>())
                        .Returns([new RelationshipRecord { Id = "r1", SourceSymbolId = "caller", TargetSymbolId = "sym-1", RelationshipType = "Calls" }]);
                    storage.GetRelationshipsBySourceAsync("sym-1", Arg.Any<CancellationToken>())
                        .Returns([new RelationshipRecord { Id = "r2", SourceSymbolId = "sym-1", TargetSymbolId = "base", RelationshipType = "Inherits" }]);
                    s.AddSingleton<IStorageService>(storage);
                });
            });
        var client = factory.CreateClient();

        var result = await CallTool(client, "find_related_code",
            new JsonObject { ["symbolPath"] = "Ns.Util.QueryUtils.getLikeMethod", ["relationType"] = "references" });

        Assert.That(result["error"], Is.Null);
        var content = result["result"]?["content"]?.AsArray();
        Assert.That(content, Is.Not.Null);
        var text = content![0]!["text"]?.GetValue<string>();
        var obj = JsonNode.Parse(text!)!.AsObject();
        Assert.That(obj["results"]?.AsArray(), Has.Count.EqualTo(0));
        var message = obj["message"]?.GetValue<string>();
        Assert.That(message, Does.Contain("No 'references' relationships found"));
        Assert.That(message, Does.Contain("Calls, Inherits"));
        Assert.That(obj["matchedSymbol"]?["symbolName"]?.GetValue<string>(), Is.EqualTo("getLikeMethod"));
    }

    static void ReplaceStorage(IServiceCollection services, IStorageService storage)
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IStorageService));
        if (descriptor != null)
            services.Remove(descriptor);
        services.AddSingleton(storage);
    }

    static IStorageService CreateStorageWithSuggestions()
    {
        var storage = Substitute.For<IStorageService>();
        storage.GetSymbolByFullNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((SymbolRecord?)null);
        storage.SuggestSymbolsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([
                new SymbolRecord { Id = "s1", Name = "getLikeMethod", Kind = "Method", FilePath = "/src/QueryUtils.cs",
                    FullName = "Ns.Util.QueryUtils.getLikeMethod(string pattern)", LineStart = 10, LineEnd = 22 }
            ]);
        return storage;
    }
}