namespace CodeMemory.AspNet.Configuration;

public interface IRepoContextAccessor
{
    string? CurrentRepoName { get; set; }
    string? CurrentRepoRoot { get; set; }
}

public sealed class RepoContextAccessor : IRepoContextAccessor
{
    readonly AsyncLocal<string?> repoName = new();
    readonly AsyncLocal<string?> repoRoot = new();

    public string? CurrentRepoName
    {
        get => repoName.Value;
        set => repoName.Value = value;
    }

    public string? CurrentRepoRoot
    {
        get => repoRoot.Value;
        set => repoRoot.Value = value;
    }
}
