namespace CodeMemory.AspNet.Configuration;

public sealed class IndexingOptions
{
    public const string SectionName = "Indexing";

    public int RepoTimeoutMinutes { get; set; } = 30;

    public int RetryCount { get; set; } = 3;

    public int RetryBaseDelaySeconds { get; set; } = 2;
}
