using CodeMemory.AspNet.Registry;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CodeMemory.AspNet.Pages;

public sealed class RepositoryDashboardModel : PageModel
{
    readonly RepoRegistryService registry;
    readonly RepoRegistryOptions registryOptions;

    public List<RepoRow> Repos { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int IndexedCount { get; private set; }
    public int InProgressCount { get; private set; }
    public int FailedCount { get; private set; }
    public bool CanAddRepo => registryOptions.EnableManualRepoAdd;

    public RepositoryDashboardModel(RepoRegistryService registry, RepoRegistryOptions registryOptions)
        => (this.registry, this.registryOptions) = (registry, registryOptions);

    public async Task OnGetAsync()
    {
        var all = await registry.ListAsync();

        TotalCount = all.Count;
        IndexedCount = all.Count(r => r.IndexStatus == "Indexed");
        InProgressCount = all.Count(r => r.CloneStatus == "Cloning" || r.IndexStatus == "Indexing");
        FailedCount = all.Count(r => r.CloneStatus == "Failed" || r.IndexStatus == "Failed");

        Repos = all.Select(r => new RepoRow
        {
            Name = r.Name,
            GitUrl = r.GitUrl,
            LocalPath = r.LocalPath,
            LastIndexedAt = r.LastIndexedAt,
            ErrorMessage = r.ErrorMessage,
            Status = ResolveStatus(r),
            BadgeClass = ResolveBadgeClass(r)
        }).ToList();
    }

    static string ResolveStatus(Repositories r) => (r.CloneStatus, r.IndexStatus) switch
    {
        ("Failed", _) or (_, "Failed") => "Failed",
        ("Pending", _) => "Pending",
        ("Cloning", _) => "Cloning",
        ("Cloned", "Pending") => "Pending Clone",
        (_, "Indexing") => "Indexing",
        (_, "Indexed") => "Indexed",
        (_, _) => r.CloneStatus
    };

    static string ResolveBadgeClass(Repositories r) => ResolveStatus(r) switch
    {
        "Indexed" => "bg-success",
        "Failed" => "bg-danger",
        "Cloning" or "Indexing" or "Pending" => "bg-warning text-dark",
        _ => "bg-secondary"
    };

    public sealed record RepoRow
    {
        public required string Name { get; init; }
        public string? GitUrl { get; init; }
        public string LocalPath { get; init; } = "";
        public DateTime? LastIndexedAt { get; init; }
        public string? ErrorMessage { get; init; }
        public required string Status { get; init; }
        public required string BadgeClass { get; init; }
    }
}
