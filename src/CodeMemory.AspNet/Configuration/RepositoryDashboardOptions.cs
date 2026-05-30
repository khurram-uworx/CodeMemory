namespace CodeMemory.AspNet.Configuration;

public sealed record RepositoryDashboardOptions
{
    public const string SectionName = "RepositoryDashboard";

    public bool AllowNewRepos { get; init; } = true;
}
