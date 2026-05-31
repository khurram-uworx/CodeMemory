namespace CodeMemory.AspNet.Models;

public sealed record RepoMetricsSnapshot(
    DateTime CollectedAt,
    IReadOnlyList<InstrumentMetric> Instruments
);

public sealed record InstrumentMetric(
    string Name,
    string InstrumentType,
    IReadOnlyList<MetricValue> Values
);

public sealed record MetricValue(
    long? Count,
    double? Sum,
    double? Min,
    double? Max,
    double? Last,
    IReadOnlyList<MetricMeasurement>? Measurements,
    IReadOnlyList<KeyValuePair<string, string>>? Tags
);

public sealed record MetricMeasurement(
    double Value,
    DateTime Timestamp
);
