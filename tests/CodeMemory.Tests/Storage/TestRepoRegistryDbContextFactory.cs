using CodeMemory.AspNet.Registry;
using Microsoft.EntityFrameworkCore;

namespace CodeMemory.Tests.Storage;

sealed class TestRepoRegistryDbContextFactory : IDbContextFactory<RepoRegistryDbContext>
{
    readonly DbContextOptions<RepoRegistryDbContext> options;

    public TestRepoRegistryDbContextFactory(DbContextOptions<RepoRegistryDbContext> options)
        => this.options = options;

    public RepoRegistryDbContext CreateDbContext()
        => new(options);
}
