namespace CodeMemory.AspNet.Scheduling;

public sealed class RebuildOptions
{
    public string? Cron { get; set; }

    public int GitPullTimeoutSeconds { get; set; } = 120;
}
