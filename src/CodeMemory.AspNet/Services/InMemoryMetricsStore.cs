using CodeMemory.AspNet.Configuration;
using CodeMemory.AspNet.Models;
using CodeMemory.AspNet.Storage;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace CodeMemory.AspNet.Services;

sealed class InMemoryMetricsStore : IMetricsStore
{
    readonly ConcurrentDictionary<string, InstrumentState> instruments = new(StringComparer.Ordinal);
    readonly LocalMetricsOptions options;
    readonly ILogger<InMemoryMetricsStore>? logger;

    public InMemoryMetricsStore(IOptions<LocalMetricsOptions> options, ILogger<InMemoryMetricsStore>? logger = null)
    {
        this.options = options.Value;
        this.logger = logger;
    }

    public void RecordCounter(string instrumentName, long value, IReadOnlyList<KeyValuePair<string, object?>>? tags)
    {
        var state = instruments.GetOrAdd(instrumentName, _ => new InstrumentState(true, options, logger));
        state.RecordCounter(value, SerializeTags(tags));
    }

    public void RecordHistogram(string instrumentName, double value, IReadOnlyList<KeyValuePair<string, object?>>? tags)
    {
        var state = instruments.GetOrAdd(instrumentName, _ => new InstrumentState(false, options, logger));
        state.RecordHistogram(value, SerializeTags(tags));
    }

    public void RecordGauge(string instrumentName, double value, IReadOnlyList<KeyValuePair<string, object?>>? tags)
    {
        var state = instruments.GetOrAdd(instrumentName, _ => new InstrumentState(isCounter: false, isGauge: true, options, logger));
        state.RecordGauge(value, SerializeTags(tags));
    }

    public void RemoveInstrumentTags(string instrumentName, IReadOnlyList<KeyValuePair<string, object?>>? tags)
    {
        if (instruments.TryGetValue(instrumentName, out var state))
            state.RemoveTagSet(SerializeTags(tags));
    }

    public RepoMetricsSnapshot GetSnapshot(bool reset = false)
    {
        var snapshotTime = DateTime.UtcNow;
        var instrumentMetrics = new List<InstrumentMetric>(instruments.Count);

        foreach (var (name, state) in instruments)
        {
            var values = state.ReadAndReset(reset);
            instrumentMetrics.Add(new InstrumentMetric(
                name,
                state.IsCounter ? "counter" : state.IsGauge ? "gauge" : "histogram",
                values));
        }

        return new RepoMetricsSnapshot(snapshotTime, instrumentMetrics);
    }

    static string SerializeTags(IReadOnlyList<KeyValuePair<string, object?>>? tags)
    {
        if (tags is null || tags.Count == 0)
            return "";

        var sorted = tags
            .Select(kvp => $"{kvp.Key}={kvp.Value?.ToString() ?? ""}")
            .OrderBy(x => x, StringComparer.Ordinal);

        return string.Join("|", sorted);
    }

    sealed class InstrumentState
    {
        public bool IsCounter { get; }
        public bool IsGauge { get; }
        readonly ConcurrentDictionary<string, TagSetState> tagSets = new(StringComparer.Ordinal);
        readonly LocalMetricsOptions options;
        readonly ILogger? logger;
        long tagComboCount;

        public InstrumentState(bool isCounter, LocalMetricsOptions options, ILogger? logger)
            : this(isCounter, false, options, logger) { }

        public InstrumentState(bool isCounter, bool isGauge, LocalMetricsOptions options, ILogger? logger)
        {
            IsCounter = isCounter;
            IsGauge = isGauge;
            this.options = options;
            this.logger = logger;
        }

        public void RecordCounter(long value, string tagKey)
        {
            var state = getOrAddTagSet(tagKey);
            if (state is null) return;

            var useRing = options.MaxMeasurementsPerTagSet > 0;

            if (useRing)
            {
                lock (state.Lock)
                {
                    state.CounterValue += value;
                    state.Measurements ??= [];
                    state.Measurements.Enqueue(new MetricMeasurement(value, DateTime.UtcNow));
                    while (state.Measurements.Count > options.MaxMeasurementsPerTagSet)
                        state.Measurements.TryDequeue(out _);
                }
            }
            else
            {
                Interlocked.Add(ref state.CounterValue, value);
            }
        }

        public void RecordGauge(double value, string tagKey)
        {
            var state = getOrAddTagSet(tagKey);
            if (state is null) return;

            lock (state.Lock)
            {
                state.Last = value;

                if (options.MaxMeasurementsPerTagSet > 0)
                {
                    state.Measurements ??= [];
                    state.Measurements.Enqueue(new MetricMeasurement(value, DateTime.UtcNow));
                    while (state.Measurements.Count > options.MaxMeasurementsPerTagSet)
                        state.Measurements.TryDequeue(out _);
                }
            }
        }

        public void RecordHistogram(double value, string tagKey)
        {
            var state = getOrAddTagSet(tagKey);
            if (state is null) return;

            lock (state.Lock)
            {
                state.Count++;
                state.Sum += value;
                if (value < state.Min) state.Min = value;
                if (value > state.Max) state.Max = value;
                state.Last = value;

                if (options.MaxMeasurementsPerTagSet > 0)
                {
                    state.Measurements ??= [];
                    state.Measurements.Enqueue(new MetricMeasurement(value, DateTime.UtcNow));
                    while (state.Measurements.Count > options.MaxMeasurementsPerTagSet)
                        state.Measurements.TryDequeue(out _);
                }
            }
        }

        public void RemoveTagSet(string tagKey)
        {
            if (tagSets.TryRemove(tagKey, out _))
                Interlocked.Decrement(ref tagComboCount);
        }

        public List<MetricValue> ReadAndReset(bool reset)
        {
            var result = new List<MetricValue>(tagSets.Count);
            var useRing = options.MaxMeasurementsPerTagSet > 0;

            foreach (var (tagKey, state) in tagSets)
            {
                lock (state.Lock)
                {
                    if (IsGauge)
                    {
                        result.Add(new MetricValue(
                            Count: null, Sum: null, Min: null, Max: null,
                            Last: state.Last,
                            Measurements: useRing ? state.Measurements?.ToList() : null,
                            Tags: DeserializeTags(tagKey)));

                        if (reset)
                        {
                            state.Last = 0;
                            if (useRing) state.Measurements = null;
                        }
                    }
                    else if (IsCounter)
                    {
                        var count = state.CounterValue;
                        result.Add(new MetricValue(
                            Count: count,
                            Sum: null, Min: null, Max: null, Last: null,
                            Measurements: useRing ? state.Measurements?.ToList() : null,
                            Tags: DeserializeTags(tagKey)));

                        if (reset)
                        {
                            state.CounterValue = 0;
                            if (useRing) state.Measurements = null;
                        }
                    }
                    else
                    {
                        result.Add(new MetricValue(
                            Count: state.Count,
                            Sum: state.Sum,
                            Min: state.Count > 0 ? state.Min : null,
                            Max: state.Count > 0 ? state.Max : null,
                            Last: state.Count > 0 ? state.Last : null,
                            Measurements: useRing ? state.Measurements?.ToList() : null,
                            Tags: DeserializeTags(tagKey)));

                        if (reset)
                        {
                            state.Count = 0;
                            state.Sum = 0;
                            state.Min = double.MaxValue;
                            state.Max = double.MinValue;
                            state.Last = 0;
                            state.Measurements = null;
                        }
                    }
                }
            }

            return result;
        }

        TagSetState? getOrAddTagSet(string tagKey)
        {
            if (tagSets.TryGetValue(tagKey, out var existing))
                return existing;

            if (Interlocked.Read(ref tagComboCount) >= options.MaxUniqueTagCombinations)
            {
                logger?.LogWarning(
                    "MaxUniqueTagCombinations ({Limit}) reached for instrument. Dropping tag set: {TagKey}",
                    options.MaxUniqueTagCombinations, tagKey);
                return null;
            }

            var newState = new TagSetState(IsCounter);
            if (tagSets.TryAdd(tagKey, newState))
            {
                Interlocked.Increment(ref tagComboCount);
                return newState;
            }

            return tagSets.TryGetValue(tagKey, out existing) ? existing : null;
        }

        static IReadOnlyList<KeyValuePair<string, string>>? DeserializeTags(string tagKey)
        {
            if (string.IsNullOrEmpty(tagKey))
                return null;

            return tagKey
                .Split('|')
                .Select(p =>
                {
                    var eq = p.IndexOf('=');
                    return eq >= 0
                        ? new KeyValuePair<string, string>(p[..eq], p[(eq + 1)..])
                        : new KeyValuePair<string, string>(p, "");
                })
                .ToList();
        }
    }

    sealed class TagSetState
    {
        public long CounterValue;
        public long Count;
        public double Sum;
        public double Min = double.MaxValue;
        public double Max = double.MinValue;
        public double Last;
        public Queue<MetricMeasurement>? Measurements;
        public readonly object Lock = new();

        public TagSetState(bool isCounter)
        {
            if (isCounter)
                CounterValue = 0;
        }
    }
}
