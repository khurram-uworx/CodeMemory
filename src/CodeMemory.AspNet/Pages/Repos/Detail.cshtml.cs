using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Services;
using CodeMemory.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CodeMemory.AspNet.Pages.Repos;

public sealed class DetailModel : PageModel
{
    readonly RepoRegistryService registry;
    readonly IDbContextFactory<RepoRegistryDbContext> dbFactory;

    public Repositories? Repo { get; private set; }
    public string? NotFoundMessage { get; private set; }
    public List<ComponentRow> Components { get; private set; } = [];

    public sealed record ComponentRow(
        string BuildFilePath,
        string ComponentName,
        string ComponentKind,
        string ComponentType,
        int FileCount
    );

    public DetailModel(RepoRegistryService registry, IDbContextFactory<RepoRegistryDbContext> dbFactory)
        => (this.registry, this.dbFactory) = (registry, dbFactory);

    public async Task<IActionResult> OnGetAsync(string name)
    {
        Repo = await registry.GetAsync(name);
        if (Repo is null)
        {
            NotFoundMessage = $"Repository '{name}' not found.";
            return Page();
        }

        await LoadComponentsAsync(Repo.Id);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(string name, string buildFilePath, string componentKind, string componentType)
    {
        Repo = await registry.GetAsync(name);
        if (Repo is null)
        {
            NotFoundMessage = $"Repository '{name}' not found.";
            return Page();
        }

        if (!Enum.TryParse<ComponentKind>(componentKind, ignoreCase: true, out _))
        {
            ModelState.AddModelError(string.Empty, $"Invalid component kind '{componentKind}'");
            await LoadComponentsAsync(Repo.Id);
            return Page();
        }

        if (!Enum.TryParse<ComponentType>(componentType, ignoreCase: true, out _))
        {
            ModelState.AddModelError(string.Empty, $"Invalid component type '{componentType}'");
            await LoadComponentsAsync(Repo.Id);
            return Page();
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var entity = await db.Components
            .FirstOrDefaultAsync(c => c.RepositoryId == Repo.Id && c.BuildFilePath == buildFilePath);

        if (entity is null || entity.IsDeleted)
        {
            ModelState.AddModelError(string.Empty, $"Component '{buildFilePath}' not found");
            await LoadComponentsAsync(Repo.Id);
            return Page();
        }

        entity.ComponentKindString = componentKind;
        entity.ComponentTypeString = componentType;
        await db.SaveChangesAsync();

        TempData["Message"] = $"Component '{buildFilePath}' updated.";
        return RedirectToPage(new { name });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string name, string buildFilePath)
    {
        Repo = await registry.GetAsync(name);
        if (Repo is null)
        {
            NotFoundMessage = $"Repository '{name}' not found.";
            return Page();
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var entity = await db.Components
            .FirstOrDefaultAsync(c => c.RepositoryId == Repo.Id && c.BuildFilePath == buildFilePath);

        if (entity is null || entity.IsDeleted)
        {
            ModelState.AddModelError(string.Empty, $"Component '{buildFilePath}' not found");
            await LoadComponentsAsync(Repo.Id);
            return Page();
        }

        entity.IsDeleted = true;
        entity.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        TempData["Message"] = $"Component '{buildFilePath}' deleted.";
        return RedirectToPage(new { name });
    }

    async Task LoadComponentsAsync(int repoId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entities = await db.Components
            .AsNoTracking()
            .Where(c => c.RepositoryId == repoId && !c.IsDeleted)
            .OrderBy(c => c.BuildFilePath)
            .ToListAsync();

        Components = entities
            .Select(e => new ComponentRow(
                e.BuildFilePath,
                e.ComponentName,
                e.ComponentKindString,
                e.ComponentTypeString,
                e.FileCount
            ))
            .ToList();
    }
}
