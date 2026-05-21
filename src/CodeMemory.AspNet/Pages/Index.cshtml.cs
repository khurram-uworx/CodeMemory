using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Registry.Models;
using CodeMemory.AspNet.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CodeMemory.AspNet.Pages;

public sealed class IndexModel : PageModel
{
    readonly RepoRegistryService registry;
    readonly CloneIndexService cloneIndex;

    public List<RegisteredRepo> Repos { get; private set; } = [];

    public IndexModel(RepoRegistryService registry, CloneIndexService cloneIndex)
    {
        this.registry = registry;
        this.cloneIndex = cloneIndex;
    }

    public async Task OnGetAsync()
    {
        Repos = await registry.ListAsync();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string name)
    {
        var repo = await registry.GetAsync(name);
        if (repo is null)
            return NotFound();

        await cloneIndex.DeleteRepoAsync(name);
        TempData["Message"] = $"Repo '{name}' deleted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReindexAsync(string name)
    {
        var repo = await registry.GetAsync(name);
        if (repo is null)
            return NotFound();

        var source = repo.GitUrl ?? repo.LocalPath;
        await cloneIndex.EnqueueRepoAsync(name, source, repo.Branch);
        TempData["Message"] = $"Re-index triggered for '{name}'.";
        return RedirectToPage();
    }
}
