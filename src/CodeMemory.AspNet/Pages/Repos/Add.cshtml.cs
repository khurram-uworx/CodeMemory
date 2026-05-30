using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace CodeMemory.AspNet.Pages.Repos;

public sealed class AddModel : PageModel
{
    public sealed class InputModel
    {
        [Required, MaxLength(200)]
        [Display(Name = "Repo Name")]
        public string Name { get; set; } = string.Empty;

        [Required, MaxLength(2000)]
        [Display(Name = "Source (Git URL or local path)")]
        public string Source { get; set; } = string.Empty;

        [MaxLength(200)]
        [Display(Name = "Branch (for URL repos only)")]
        public string? Branch { get; set; }
    }

    readonly RepoRegistryService registry;
    readonly CloneIndexService cloneIndex;
    readonly RepoRegistryOptions registryOptions;
    readonly RepositoryDashboardOptions dashboardOptions;

    public AddModel(RepoRegistryService registry, CloneIndexService cloneIndex,
        RepoRegistryOptions registryOptions, RepositoryDashboardOptions dashboardOptions)
        => (this.registry, this.cloneIndex, this.registryOptions, this.dashboardOptions) =
            (registry, cloneIndex, registryOptions, dashboardOptions);

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IActionResult OnGet()
        => dashboardOptions.AllowNewRepos ? Page() : RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        if (!dashboardOptions.AllowNewRepos)
            return RedirectToPage("/Index");

        if (!ModelState.IsValid)
            return Page();

        var existing = await registry.GetAsync(Input.Name);
        if (existing is not null)
        {
            ModelState.AddModelError("Input.Name", $"Repo name '{Input.Name}' is already registered.");
            return Page();
        }

        var isUrl = CloneIndexService.IsGitUrl(Input.Source);

        var repo = new Repositories
        {
            Name = Input.Name,
            GitUrl = isUrl ? Input.Source : null,
            Branch = isUrl ? Input.Branch : null,
            LocalPath = CloneIndexService.ResolveRepoPath(Input.Source, Input.Name, registryOptions),
            CloneStatus = isUrl ? "Pending" : "Cloned",
            IndexStatus = "Pending"
        };

        await registry.AddAsync(repo);

        await cloneIndex.EnqueueRepoAsync(repo.Name, Input.Source, Input.Branch);

        TempData["Message"] = $"Repo '{Input.Name}' registered.";
        return RedirectToPage("/Index");
    }
}
