namespace CodeMemory.AspNet.Configuration;

public interface IRepoContextAccessor
{
    string? CurrentRepoName { get; }
    string? CurrentRepoRoot { get; }
}

public sealed class RepoContextAccessor : IRepoContextAccessor
{
    string? repoName;
    string? repoRoot;

    public string? CurrentRepoName
    {
        get => repoName;
        set => repoName = value;
    }

    public string? CurrentRepoRoot
    {
        get => repoRoot;
        set => repoRoot = value;
    }
}
