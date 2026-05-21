using System.ComponentModel.DataAnnotations;
using CodeMemory.AspNet.Registry;
using CodeMemory.AspNet.Registry.Models;
using CodeMemory.AspNet.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CodeMemory.AspNet.Pages.Repos;

public sealed class AddModel : PageModel
{
    readonly RepoRegistryService registry;
    readonly CloneIndexService cloneIndex;

    public AddModel(RepoRegistryService registry, CloneIndexService cloneIndex)
    {
        this.registry = registry;
        this.cloneIndex = cloneIndex;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var existing = await registry.GetAsync(Input.Name);
        if (existing is not null)
        {
            ModelState.AddModelError("Input.Name", $"Repo name '{Input.Name}' is already registered.");
            return Page();
        }

        var isUrl = Input.Source.Contains("://");
        var cloneBasePath = GetCloneBasePath();

        var repo = new RegisteredRepo
        {
            Name = Input.Name,
            GitUrl = isUrl ? Input.Source : null,
            Branch = isUrl ? Input.Branch : null,
            LocalPath = isUrl
                ? Path.GetFullPath(Path.Combine(cloneBasePath, Input.Name))
                : Path.GetFullPath(Input.Source),
            CloneStatus = isUrl ? "Pending" : "Cloned",
            IndexStatus = "Pending"
        };

        await registry.AddAsync(repo);

        await cloneIndex.EnqueueRepoAsync(repo.Name, Input.Source, Input.Branch);

        TempData["Message"] = $"Repo '{Input.Name}' registered.";
        return RedirectToPage("/Index");
    }

    string GetCloneBasePath()
    {
        var config = (IConfiguration)HttpContext.RequestServices.GetRequiredService(typeof(IConfiguration));
        return config.GetSection("RepoRegistry:CloneBasePath")?.Value
            ?? Path.Combine(Environment.CurrentDirectory, "cloned-repos");
    }

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
}
