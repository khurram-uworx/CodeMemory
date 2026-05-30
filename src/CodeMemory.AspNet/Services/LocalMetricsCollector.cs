using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CodeMemory.AspNet.Services;

public sealed record LocalMetricsSnapshot(
    bool Enabled,
    DateTimeOffset StartedAt,
    DateTimeOffset CapturedAt,
    int SeriesCount,
    int MaxSeries,
    IReadOnlyList<LocalMetricSeriesSnapshot> Series);

public sealed record LocalMetricSeriesSnapshot(
    string Name,
    string Kind,
    string? Unit,
    string? Description,
    IReadOnlyDictionary<string, string> Tags,
    long Count,
    double Sum,
    double? Min,
    double? Max,
    double? Average,
    double? P95);

public interface ILocalMetricsCollector
{
    bool Enabled { get; }

    LocalMetricsSnapshot GetSnapshot(string? repoName = null, string? metricName = null, bool includeSeries = true);
}

public sealed class LocalMetricsCollector : IHostedService, IDisposable, ILocalMetricsCollector
{
    readonly LocalMetricsOptions options;
    readonly ILogger<LocalMetricsCollector> logger;
    readonly ConcurrentDictionary<string, MetricSeries> series = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, InstrumentInfo> instruments = new(StringComparer.Ordinal);
    readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    MeterListener? listener;

    public LocalMetricsCollector(IOptions<LocalMetricsOptions> options, ILogger<LocalMetricsCollector> logger)
    {
        this.options = options.Value;
        this.logger = logger;
    }

    public bool Enabled => options.Enabled;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Local metrics collector disabled");
            return Task.CompletedTask;
        }

        listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (!string.Equals(instrument.Meter.Name, "CodeMemory", StringComparison.Ordinal))
                return;

            instruments[instrument.Name] = new InstrumentInfo(
                instrument.Name,
                getKind(instrument),
                instrument.Unit,
                instrument.Description);
            meterListener.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            recordMeasurement(instrument, measurement, tags));
        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, state) =>
            recordMeasurement(instrument, measurement, tags));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            recordMeasurement(instrument, measurement, tags));
        listener.SetMeasurementEventCallback<float>((instrument, measurement, tags, state) =>
            recordMeasurement(instrument, measurement, tags));

        listener.Start();
        logger.LogInformation("Local metrics collector enabled for meter {MeterName}", "CodeMemory");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        listener?.Dispose();
        listener = null;
        return Task.CompletedTask;
    }

    public LocalMetricsSnapshot GetSnapshot(string? repoName = null, string? metricName = null, bool includeSeries = true)
    {
        var rows = includeSeries
            ? series.Values
                .Select(s => s.ToSnapshot())
                .Where(s => string.IsNullOrWhiteSpace(repoName)
                    || s.Tags.TryGetValue("repo.name", out var value)
                    && string.Equals(value, repoName, StringComparison.OrdinalIgnoreCase))
                .Where(s => string.IsNullOrWhiteSpace(metricName)
                    || string.Equals(s.Name, metricName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ThenBy(s => formatTags(s.Tags), StringComparer.Ordinal)
                .ToList()
            : [];

        return new LocalMetricsSnapshot(
            options.Enabled,
            startedAt,
            DateTimeOffset.UtcNow,
            series.Count,
            Math.Max(1, options.MaxSeries),
            rows);
    }

    void recordMeasurement<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        var value = Convert.ToDouble(measurement);
        var info = instruments.GetOrAdd(instrument.Name, _ => new InstrumentInfo(
            instrument.Name,
            getKind(instrument),
            instrument.Unit,
            instrument.Description));
        var normalizedTags = normalizeTags(tags);
        var key = $"{instrument.Name}|{formatTags(normalizedTags)}";

        if (!series.TryGetValue(key, out var metricSeries))
        {
            if (series.Count >= Math.Max(1, options.MaxSeries))
                return;

            metricSeries = new MetricSeries(info, normalizedTags, Math.Max(1, options.HistogramWindowSize));
            metricSeries = series.GetOrAdd(key, metricSeries);
        }

        metricSeries.Record(value);
    }

    static string getKind(Instrument instrument)
    {
        var typeName = instrument.GetType().Name;
        if (typeName.StartsWith("Counter", StringComparison.Ordinal))
            return "counter";
        if (typeName.StartsWith("Histogram", StringComparison.Ordinal))
            return "histogram";
        if (typeName.StartsWith("Observable", StringComparison.Ordinal))
            return "observable";
        return "unknown";
    }

    static IReadOnlyDictionary<string, string> normalizeTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag.Key))
                continue;

            result[tag.Key] = tag.Value?.ToString() ?? "";
        }

        return result;
    }

    static string formatTags(IReadOnlyDictionary<string, string> tags)
        => string.Join("|", tags.Select(kv => $"{kv.Key}={kv.Value}"));

    public void Dispose()
        => listener?.Dispose();

    sealed record InstrumentInfo(string Name, string Kind, string? Unit, string? Description);

    sealed class MetricSeries
    {
        readonly object gate = new();
        readonly InstrumentInfo info;
        readonly IReadOnlyDictionary<string, string> tags;
        readonly double[] window;
        long count;
        double sum;
        double min = double.NaN;
        double max = double.NaN;
        int nextWindowIndex;
        int windowCount;

        public MetricSeries(InstrumentInfo info, IReadOnlyDictionary<string, string> tags, int windowSize)
        {
            this.info = info;
            this.tags = tags;
            window = new double[windowSize];
        }

        public void Record(double value)
        {
            lock (gate)
            {
                count++;
                sum += value;
                min = double.IsNaN(min) ? value : Math.Min(min, value);
                max = double.IsNaN(max) ? value : Math.Max(max, value);
                window[nextWindowIndex] = value;
                nextWindowIndex = (nextWindowIndex + 1) % window.Length;
                windowCount = Math.Min(window.Length, windowCount + 1);
            }
        }

        public LocalMetricSeriesSnapshot ToSnapshot()
        {
            lock (gate)
            {
                var values = window.Take(windowCount).Order().ToArray();
                double? p95 = values.Length == 0
                    ? null
                    : values[Math.Clamp((int)Math.Ceiling(values.Length * 0.95) - 1, 0, values.Length - 1)];

                return new LocalMetricSeriesSnapshot(
                    info.Name,
                    info.Kind,
                    info.Unit,
                    info.Description,
                    tags,
                    count,
                    sum,
                    double.IsNaN(min) ? null : min,
                    double.IsNaN(max) ? null : max,
                    count == 0 ? null : sum / count,
                    p95);
            }
        }
    }
}
