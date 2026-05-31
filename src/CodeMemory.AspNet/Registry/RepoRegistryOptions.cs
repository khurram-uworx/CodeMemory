namespace CodeMemory.AspNet.Registry;

public sealed record RepoRegistryOptions
{
    public const string SectionName = "RepoRegistry";

    public string CloneBasePath { get; init; } = "./cloned-repos";
    public int GitCommandTimeoutSeconds { get; init; } = 300;
    public bool EnableDemoMode { get; init; } = false;
    public string? RebuildCron { get; init; }
    public int RebuildPollIntervalSeconds { get; init; } = 120;
}
