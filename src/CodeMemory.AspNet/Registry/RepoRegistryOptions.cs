namespace CodeMemory.AspNet.Registry;

public sealed record RepoRegistryOptions
{
    public const string SectionName = "RepoRegistry";

    public string CloneBasePath { get; init; } = "./cloned-repos";
    public int CloneTimeoutSeconds { get; init; } = 300;
}
