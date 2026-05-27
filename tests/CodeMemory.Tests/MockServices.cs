using CodeMemory.Indexing.Architecture;
using CodeMemory.Indexing.Git;
using CodeMemory.Indexing.Graph;
using CodeMemory.Indexing.Search;
using CodeMemory.Storage;
using NSubstitute;

namespace CodeMemory.Tests;

static class MockServices
{
    public static IStorageService CreateStorageService()
    {
        var storage = Substitute.For<IStorageService>();
        storage.RepoRoot.Returns(Environment.CurrentDirectory);

        var myClassId = "myclass-guid";
        var symbol = new SymbolRecord
        {
            Id = myClassId,
            Name = "MyClass",
            Kind = "Class",
            FilePath = "/src/MyClass.cs",
            FullName = "MyClass",
            LineStart = 1,
            LineEnd = 50
        };
        storage.GetSymbolByFullNameAsync("MyClass", Arg.Any<CancellationToken>()).Returns(symbol);
        storage.GetSymbolAsync(myClassId, Arg.Any<CancellationToken>()).Returns(symbol);
        storage.GetChunksBySymbolAsync(myClassId, Arg.Any<CancellationToken>()).Returns([
            new ChunkRecord
            {
                Id = "c1", SymbolId = myClassId, FilePath = "/src/MyClass.cs",
                Content = "public class MyClass { }", Language = "CSharp",
                LineStart = 1, LineEnd = 10
            }
        ]);
        return storage;
    }

    public static IDependencyGraphService CreateDependencyGraphService()
    {
        var graph = Substitute.For<IDependencyGraphService>();
        graph.TraceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(args =>
             {
                 var symbolPath = args.ArgAt<string>(0);
                 var direction = args.ArgAt<string>(1);
                 if (symbolPath == "NonExistent")
                     return new List<DependencyNode>();
                 return new List<DependencyNode>
                 {
                     new(symbolPath, "/src/MyClass.cs", "Method", "10-30", "self"),
                     new("MyOtherClass", "/src/Other.cs", "Class", "1-50", direction == "upstream" ? "imports" : "references")
                 };
             });
        graph.FindRelatedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(args =>
             {
                 if (args.ArgAt<string>(0) == "NonExistent")
                     return new List<DependencyNode>();
                 return new List<DependencyNode> { new("RelatedService", "/src/Related.cs", "Class", "1-20", "references") };
             });
        graph.FindTestCoverageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(args =>
             {
                 if (args.ArgAt<string>(0) == "NonExistent")
                     return new List<string>();
                 return new List<string> { "/tests/MyClassTest.cs" };
             });
        return graph;
    }

    public static IDependencyGraphService CreateGraphService()
    {
        var graph = Substitute.For<IDependencyGraphService>();
        graph.TraceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns([new DependencyNode("MyOtherClass", "/src/Other.cs", "Class", "1-30", "references")]);
        graph.FindRelatedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns([]);
        graph.FindTestCoverageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(["/tests/MyClassTest.cs"]);
        return graph;
    }

    public static ISemanticSearchService CreateSemanticSearchService()
    {
        var search = Substitute.For<ISemanticSearchService>();
        search.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
              .Returns([
                  new ScoredChunk
                  {
                      Chunk = new ChunkRecord
                      {
                          Id = "chunk1",
                          SymbolId = "DatabaseService",
                          FilePath = "/src/DatabaseService.cs",
                          Content = "public class DatabaseService { }",
                          Language = "CSharp",
                          LineStart = 1,
                          LineEnd = 10
                      },
                      Score = 0.95
                  }
              ]);
        return search;
    }

    public static IArchitectureService CreateArchitectureService()
    {
        var arch = Substitute.For<IArchitectureService>();
        arch.GetOverviewAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ArchitectureOverview(
                [new ComponentInfo("src", 5, 20), new ComponentInfo("tests", 3, 2)],
                new Dictionary<string, int> { ["C#"] = 8, ["JavaScript"] = 2 },
                10, 42
            ));
        return arch;
    }

    public static IComponentClusteringService CreateClusteringService()
    {
        var cluster = Substitute.For<IComponentClusteringService>();
        cluster.GetClustersAsync(Arg.Any<double>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns([
                   new ComponentCluster("src+tests", ["src", "tests"], 0.75),
                   new ComponentCluster("lib", ["lib"], 1.0)
               ]);
        return cluster;
    }

    public static IGitHistoryService CreateGitHistoryService()
    {
        var git = Substitute.For<IGitHistoryService>();
        git.GetSymbolHistoryAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
           .Returns(new SymbolHistoryResult(
               "MyClass", "/src/MyClass.cs", 3, 1,
               "2024-01-01", "2024-03-15",
               [
                   new CommitInfo("abc123", "testuser", "2024-03-15", "Fix bug"),
                   new CommitInfo("def456", "testuser", "2024-02-01", "Add feature"),
               ]));
        git.GetHotspotsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
           .Returns([
               new HotspotInfo("/src/Service.cs", 15, 3, "2024-03-15"),
               new HotspotInfo("/src/Controller.cs", 8, 2, "2024-03-10"),
           ]);
        return git;
    }
}
