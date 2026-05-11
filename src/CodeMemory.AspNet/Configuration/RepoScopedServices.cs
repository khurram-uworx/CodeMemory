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
            nameof(IStorageService) => root.GetKeyedService<IStorageService>(repoName)
                ?? root.GetService<IStorageService>(),
            nameof(ISemanticSearchService) => root.GetKeyedService<ISemanticSearchService>(repoName)
                ?? root.GetService<ISemanticSearchService>(),
            nameof(SymbolQueryService) => root.GetKeyedService<SymbolQueryService>(repoName)
                ?? root.GetService<SymbolQueryService>(),
            nameof(RelationshipQueryService) => root.GetKeyedService<RelationshipQueryService>(repoName)
                ?? root.GetService<RelationshipQueryService>(),
            nameof(IDependencyGraphService) => root.GetKeyedService<IDependencyGraphService>(repoName)
                ?? root.GetService<IDependencyGraphService>(),
            nameof(IArchitectureService) => root.GetKeyedService<IArchitectureService>(repoName)
                ?? root.GetService<IArchitectureService>(),
            nameof(IComponentClusteringService) => root.GetKeyedService<IComponentClusteringService>(repoName)
                ?? root.GetService<IComponentClusteringService>(),
            nameof(IGitHistoryService) => root.GetKeyedService<IGitHistoryService>(repoName)
                ?? root.GetService<IGitHistoryService>(),
            nameof(IEditContextService) => root.GetKeyedService<IEditContextService>(repoName)
                ?? root.GetService<IEditContextService>(),
            _ => root.GetService(serviceType),
        };
    }
}
