namespace CodeMemory.AspNet.Registry;

public sealed record RepoRegistryOptions
{
    public const string SectionName = "RepoRegistry";

    public string CloneBasePath { get; init; } = "./cloned-repos";
    public int GitCommandTimeoutSeconds { get; init; } = 300;
    public bool EnableManualRepoAdd { get; init; } = true;
    public string? RebuildCron { get; init; }
}
