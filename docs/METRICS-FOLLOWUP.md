# Metrics Follow-Up

Discovered during the zero-infrastructure local metrics collector implementation.

---

## Observations

### IL code path detection
Inside `LocalMetricsCollector.OnLongMeasurement`, we detect whether an instrument is a `Counter<long>` or `Histogram<long>` via `instrument.GetType().GetGenericTypeDefinition()`. This is a reflection call per measurement on the hot path. If we ever add `Histogram<long>` instruments, this becomes part of the data path. For now, only `Counter<long>` exists — but worth revisiting if perf matters.

Alternative: register two `MeterListener` instances (one for counters, one for histograms) and filter by instrument name pattern. But that's more complex for little gain at current traffic levels.

### Tag serialization format
`InMemoryMetricsStore` serializes tags as `key=value|key=value` (sorted). This is deterministic and human-readable, but:
- The `|` character could theoretically appear in a tag value (unlikely for `tool`/`host`/`repo.name`, but worth noting)
- Switching to base64-encoded JSON or a hash would be safer for arbitrary tag values
- The deserialization round-trips correctly but should be hardened if tags ever contain `|` or `=`

### Histogram <long> gap
`CodeMemoryMetrics` currently has no `Histogram<long>` instruments, but our `MeterListener` callback `OnLongMeasurement` handles them. If one is ever added, it would be correctly routed. The `GetGenericTypeDefinition()` branch already handles this.

### MeterListener delivery latency
`MeterListener` delivers measurements on an arbitrary thread-pool thread. Tests use `SpinWait.SpinUntil` with a 2-second timeout. In production, delivery is sub-millisecond, but the async nature means `GetSnapshot()` called immediately after `Add()` may miss the latest value. This is inherent to the MeterListener API and acceptable for a demo/observability tool.

---

## Future Considerations

### SqliteMetricsStore
The `IMetricsStore` interface is designed for a `SqliteMetricsStore` implementation:

```csharp
public sealed class SqliteMetricsStore(IConfiguration config) : IMetricsStore
{
    // Tables:
    //   CounterMetrics (InstrumentName, TagKey, Value, LastUpdated)
    //   HistogramMetrics (InstrumentName, TagKey, Count, Sum, Min, Max, Last, LastUpdated)
    //   HistogramMeasurements (InstrumentName, TagKey, Value, Timestamp)  -- optional ring

    public void RecordCounter(string name, long value, ...) { /* UPSERT */ }
    public void RecordHistogram(string name, double value, ...) { /* INSERT + UPDATE aggregates */ }
    public RuntimeMetricsSnapshot GetSnapshot(bool reset) { /* SELECT */ }
}
```

Benefits:
- Metrics survive process restarts
- Can power a historical dashboard (not just current snapshot)
- Shared across host instances (if using same DB)

Tradeoff: adds SQLite dependency, write amplification from per-measurement INSERTs. Mitigation: batch writes or buffer in memory and flush.

### Metrics Dashboard Page
The existing `Metrics.cshtml` still shows only code-analysis metrics (symbol counts, complexity, coupling). To add runtime metrics:

1. Add a new card section to `Metrics.cshtml` for "Runtime Metrics"
2. Wire it to the `LocalMetricsCollector` via a new API endpoint or direct DI in the PageModel
3. Show: tool invocation counts, query durations, indexing stats in a summary table
4. If ring buffer is enabled, show sparklines using Chart.js (already in the page)

### Tag cardinality monitoring
When `MaxUniqueTagCombinations` is exceeded, the warning is logged but there's no observable signal for the operator. Consider:
- Exposing a `dropped_tag_sets_total` counter (could be a separate Counter instrument on the CodeMemory meter)
- Surfacing the drop count in the snapshot itself

### Rate calculation
The current snapshot shows cumulative counter values. For dashboards, showing rate (operations/second) is more useful. This requires tracking timestamps between snapshots. Options:
- Add `LastSnapshotTime` to the store and compute deltas in `GetSnapshot()`
- Keep a sliding window of `(value, timestamp)` per counter

Given the demo focus, cumulative values are sufficient. Rate computation is a UI concern.

### Version tracking
The snapshot doesn't include a version or schema field. If we later change the `RuntimeMetricsSnapshot` schema, consumers (both the MCP tool and future dashboard) need to handle backward compatibility. Consider adding a `SchemaVersion` field.

### Prometheus toggle on service defaults
The `Prometheus:Enabled` config only toggles the HTTP endpoint (`MapPrometheusScrapingEndpoint()`). The `AddPrometheusExporter()` in `ServiceDefaults/Extensions.cs` still registers the exporter. This is harmless (no observable cost) but slightly unclean. If desired, we could thread the config into `ConfigureOpenTelemetry` to conditionally skip the exporter registration entirely.
