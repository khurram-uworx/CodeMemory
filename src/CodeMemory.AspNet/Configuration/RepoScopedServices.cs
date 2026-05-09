using CodeMemory.Indexing.Architecture;
using CodeMemory.Indexing.Git;
using CodeMemory.Indexing.Graph;
using CodeMemory.Indexing.Search;
using CodeMemory.Mcp.Services;
using CodeMemory.Services.Query;
using CodeMemory.Storage;

namespace CodeMemory.AspNet.Configuration;

public sealed class RepoScopedServices : IServiceProvider
{
    readonly IServiceProvider root;
    readonly string repoName;

    public RepoScopedServices(IServiceProvider root, string repoName)
    {
        this.root = root;
        this.repoName = repoName;
    }

    public object? GetService(Type serviceType)
    {
        return serviceType.Name switch
        {
            nameof(IStorageService) => root.GetKeyedService<IStorageService>(repoName),
            nameof(ISemanticSearchService) => root.GetKeyedService<ISemanticSearchService>(repoName),
            nameof(SymbolQueryService) => root.GetKeyedService<SymbolQueryService>(repoName),
            nameof(RelationshipQueryService) => root.GetKeyedService<RelationshipQueryService>(repoName),
            nameof(IDependencyGraphService) => root.GetKeyedService<IDependencyGraphService>(repoName),
            nameof(IArchitectureService) => root.GetKeyedService<IArchitectureService>(repoName),
            nameof(IComponentClusteringService) => root.GetKeyedService<IComponentClusteringService>(repoName),
            nameof(IGitHistoryService) => root.GetKeyedService<IGitHistoryService>(repoName),
            nameof(IEditContextService) => root.GetKeyedService<IEditContextService>(repoName),
            _ => null,
        };
    }
}
