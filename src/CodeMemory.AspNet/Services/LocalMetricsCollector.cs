using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Models;
using CodeMemory.AspNet.Storage;
using CodeMemory.Diagnostics;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;

namespace CodeMemory.AspNet.Services;

sealed class LocalMetricsCollector : IDisposable
{
    readonly MeterListener listener = new();
    readonly IMetricsStore store;
    readonly LocalMetricsOptions options;
    bool disposed;

    public LocalMetricsCollector(IMetricsStore store, IOptions<LocalMetricsOptions> options)
    {
        this.store = store;
        this.options = options.Value;

        if (this.options.Enabled)
            Start();
    }

    public bool IsEnabled => options.Enabled;

    public RuntimeMetricsSnapshot? GetSnapshot(bool reset = false)
        => IsEnabled ? store.GetSnapshot(reset) : null;

    void Start()
    {
        listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == CodeMemoryMetrics.Meter.Name)
                listener.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        listener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        listener.Start();
    }

    void OnLongMeasurement(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (instrument.Meter.Name != CodeMemoryMetrics.Meter.Name)
            return;

        var tagList = tags.Length > 0 ? tags.ToArray() : null;

        if (instrument.GetType().GetGenericTypeDefinition() == typeof(Counter<>))
            store.RecordCounter(instrument.Name, measurement, tagList);
        else
            store.RecordHistogram(instrument.Name, measurement, tagList);
    }

    void OnDoubleMeasurement(
        Instrument instrument,
        double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (instrument.Meter.Name != CodeMemoryMetrics.Meter.Name)
            return;

        var tagList = tags.Length > 0 ? tags.ToArray() : null;
        store.RecordHistogram(instrument.Name, measurement, tagList);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        listener.Dispose();
    }
}
