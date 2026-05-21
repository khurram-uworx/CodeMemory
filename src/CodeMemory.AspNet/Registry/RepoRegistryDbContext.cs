using CodeMemory.AspNet.Registry.Models;
using Microsoft.EntityFrameworkCore;

namespace CodeMemory.AspNet.Registry;

public sealed class RepoRegistryDbContext : DbContext
{
    public RepoRegistryDbContext(DbContextOptions<RepoRegistryDbContext> options) : base(options)
    { }

    public DbSet<RegisteredRepo> RegisteredRepos
        => Set<RegisteredRepo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RegisteredRepo>(entity =>
        {
            entity.HasIndex(r => r.Name).IsUnique();
        });
    }
}
