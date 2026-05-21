using CodeMemory.AspNet.Registry.Models;
using Microsoft.EntityFrameworkCore;

namespace CodeMemory.AspNet.Registry;

public sealed class RepoRegistryService
{
    readonly IDbContextFactory<RepoRegistryDbContext> contextFactory;

    public RepoRegistryService(IDbContextFactory<RepoRegistryDbContext> contextFactory)
        => this.contextFactory = contextFactory;

    public async Task<List<RegisteredRepo>> ListAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        return await db.RegisteredRepos.OrderBy(r => r.Name).ToListAsync();
    }

    public async Task<RegisteredRepo?> GetAsync(string name)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        return await db.RegisteredRepos.FirstOrDefaultAsync(r => r.Name == name);
    }

    public async Task<RegisteredRepo> AddAsync(RegisteredRepo repo)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        db.RegisteredRepos.Add(repo);
        await db.SaveChangesAsync();

        return repo;
    }

    public async Task UpdateCloneStatusAsync(string name, string status, string? localPath = null, string? errorMessage = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var repo = await db.RegisteredRepos.FirstOrDefaultAsync(r => r.Name == name);
        if (repo is null) return;

        repo.CloneStatus = status;
        if (localPath is not null) repo.LocalPath = localPath;
        if (errorMessage is not null) repo.ErrorMessage = errorMessage;

        await db.SaveChangesAsync();
    }

    public async Task UpdateIndexStatusAsync(string name, string status, string? errorMessage = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var repo = await db.RegisteredRepos.FirstOrDefaultAsync(r => r.Name == name);
        if (repo is null) return;

        repo.IndexStatus = status;
        if (status == "Indexed") repo.LastIndexedAt = DateTime.UtcNow;
        if (errorMessage is not null) repo.ErrorMessage = errorMessage;

        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(string name)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var repo = await db.RegisteredRepos.FirstOrDefaultAsync(r => r.Name == name);
        if (repo is null) return;

        db.RegisteredRepos.Remove(repo);

        await db.SaveChangesAsync();
    }
}
