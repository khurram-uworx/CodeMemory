using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Services;
using CodeMemory.Indexing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CodeMemory.AspNet.Pages;

public sealed class IndexModel : PageModel
{
    readonly RepoRegistryService registry;
    readonly CloneIndexService cloneIndex;
    readonly NotificationService notifications;

    public List<RegisteredRepo> Repos { get; private set; } = [];
    public Dictionary<string, double?> Progress { get; private set; } = [];

    public IndexModel(RepoRegistryService registry, CloneIndexService cloneIndex, NotificationService notifications)
        => (this.registry, this.cloneIndex, this.notifications) = (registry, cloneIndex, notifications);

    public async Task OnGetAsync()
    {
        Repos = await registry.ListAsync();
        Progress = Repos.ToDictionary(r => r.Name, r => IndexingState.GetProgress(r.Name));

        if (Repos.Count == 0)
            notifications.PublishInfo("No repositories registered. Click \"Add Repo\" to get started.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(string name)
    {
        var repo = await registry.GetAsync(name);
        if (repo is null) return NotFound();

        await cloneIndex.DeleteRepoAsync(name);

        notifications.PublishInfo($"Repo '{name}' deleted.");

        var remaining = await registry.ListAsync();
        if (remaining.Count == 0)
            notifications.PublishInfo("No repositories registered. Click \"Add Repo\" to get started.");

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReindexAsync(string name)
    {
        var repo = await registry.GetAsync(name);
        if (repo is null) return NotFound();

        var source = repo.GitUrl ?? repo.LocalPath;
        await cloneIndex.EnqueueRepoAsync(name, source, repo.Branch);

        notifications.PublishInfo($"Re-index triggered for '{name}'.");
        return RedirectToPage();
    }
}
