namespace CodeMemory.AspNet.Configuration;

public sealed class LocalMetricsOptions
{
    public const string SectionName = "LocalMetrics";

    public bool Enabled { get; init; } = false;
    public int MaxUniqueTagCombinations { get; init; } = 50;
    public int MaxMeasurementsPerTagSet { get; init; } = 0;
}
