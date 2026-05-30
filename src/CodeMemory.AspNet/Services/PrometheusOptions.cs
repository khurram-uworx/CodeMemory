namespace CodeMemory.AspNet.Services;

public sealed class PrometheusOptions
{
    public const string SectionName = "Observability:Prometheus";

    public bool Enabled { get; set; } = true;
}
