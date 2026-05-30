namespace CodeMemory.AspNet.Services;

public sealed class LocalMetricsOptions
{
    public const string SectionName = "Observability:LocalMetrics";

    public bool Enabled { get; set; }

    public int MaxSeries { get; set; } = 500;

    public int HistogramWindowSize { get; set; } = 256;
}
