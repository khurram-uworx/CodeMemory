# CodeMemory Follow-Up

Observations, future considerations, and known gaps discovered during implementation.

---

## Metrics Reference

Operational reference for the OpenTelemetry + Prometheus metrics pipeline. Design decisions are documented in the ADRs (`docs/adr/Observability-01.md`, `docs/adr/Web-LocalMetrics-01.md`).

### Exposed Endpoints

| Endpoint | Port | Purpose |
|---|---|---|
| `/metrics` | 8080 | Prometheus scrape (OpenMetrics format) |
| OTLP gRPC (port 18889) | — | Aspire Dashboard via `OTEL_EXPORTER_OTLP_ENDPOINT` |
| Grafana UI | 3000 | Dashboard frontend |
| Prometheus UI | 9090 | Ad-hoc query interface |

### Running with Monitoring

```bash
docker compose up -d prometheus grafana
```

Then open:
- **Grafana**: http://localhost:3000 (admin / codememory)
- **Prometheus**: http://localhost:9090
- **Aspire Dashboard**: http://localhost:18888

### Grafana Dashboard Panels

Auto-provisioned from `monitoring/grafana/dashboards/codememory.json` with 9 panels:

| Panel | Metrics |
|---|---|
| Indexing Duration | `codememory_indexing_duration_*` — avg & max per-repo |
| Files & Symbols | `codememory_indexing_files_count_total`, `codememory_indexing_symbols_count_total` |
| Tool Invocations | `codememory_tools_invocations_total` — stacked by tool name |
| Search Query Duration | `codememory_search_query_duration_*` — avg & max |
| Clone Duration | `codememory_git_clone_duration_*` — avg & max |
| SQL Query Duration | `codememory_sql_query_duration_*` — avg & max |
| Runtime (GC & ThreadPool) | `dotnet_gc_collections_total`, `dotnet_thread_pool_queue_length` |
| HTTP Request Rate & Duration | `http_server_request_duration_ms_*`, `http_server_active_requests_count` |

The `codememory.repo.*` gauge panels are not yet provisioned in the dashboard JSON.

### Configuration Files

| File | Purpose |
|---|---|
| `src/CodeMemory/Diagnostics/CodeMemoryMetrics.cs` | Instrument declarations on the `CodeMemory` meter |
| `src/CodeMemory.ServiceDefaults/Extensions.cs` | `ConfigureOpenTelemetry()` — MeterProvider, exporters |
| `src/CodeMemory.AspNet/Program.cs` | `AddServiceDefaults()`, `MapPrometheusScrapingEndpoint()` toggle |
| `monitoring/prometheus.yml` | Scrape config — targets `codememory:8080/metrics` every 10s |
| `monitoring/grafana/datasources/prometheus.yaml` | Auto-provisions Prometheus datasource |
| `monitoring/grafana/dashboards/dashboards.yaml` | Dashboard provisioning config |
| `monitoring/grafana/dashboards/codememory.json` | Dashboard panel definitions |

---

## Observations

### Tag serialization format

`InMemoryMetricsStore` serializes tags as `key=value|key=value` (sorted). This is deterministic and human-readable, but:

- The `|` character could theoretically appear in a tag value (unlikely for `tool`/`host`/`repo.name`, but worth noting)
- Switching to base64-encoded JSON or a hash would be safer for arbitrary tag values
- The deserialization round-trips correctly but should be hardened if tags ever contain `|` or `=`

### Histogram `<long>` gap

`CodeMemoryMetrics` currently has no `Histogram<long>` instruments, but our `MeterListener` callback `OnLongMeasurement` handles them via the `is Counter<long>` pattern — the else branch routes them to `RecordHistogram`. Routing is correct; the gap is purely that no `long`-valued histogram instruments exist yet.

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

---

## Metrics Known Gaps

### Gap 1: Method complexity & coupling not exposed as OTel instruments

`RepoMetrics` contains rich data beyond `OverviewStats` that is not currently instrumented:

| Available Data | Record | Why Skipped |
|---|---|---|
| `Complexity.AverageLinesPerMethod` | `Histogram<double>` | Low signal-to-noise for infrastructure monitoring |
| `Complexity.MethodSizeHistogram` (buckets: 1–5, 6–10, …, 100+) | 6× `ObservableGauge<long>` | Typically queried ad-hoc via the metrics dashboard |
| `Coupling.MostCoupled` / `Coupling.MostImportant` | — | Per-symbol topology, not aggregate infra signal |
| `Coupling.RelationshipTypeDistribution` | — | High cardinality per repo; use dashboard instead |
| `TopFilesBySymbols` | — | Per-file detail better suited to dashboard than OTel |

If these are needed as OTel metrics in the future, add instruments to `CodeMemoryMetrics.cs` and record them in `RepoMetricsRecorder` alongside the overview stats.

### Gap 2: MCP host (`CodeMemory.Mcp`) does not emit repo metrics

The `CodeMemory.Mcp` host (STDIO transport) has its own indexing flow in `Program.cs` that calls `IndexingState.MarkCompleted()` but does not reference `RepoMetricsRecorder` or `MetricsService` (which lives in `CodeMemory.AspNet` and requires `HybridStorageService`).

**Impact:** Repos indexed via the MCP host will have correct `IndexingState` but zero `codememory.repo.*` gauge data until a subsequent AspNet-hosted re-index populates the cache.

**To close this gap in the future:**
- Extract `RepoMetrics` records and `MetricsService` query logic into `CodeMemory` (core library)
- Move `RepoMetricsRecorder` (or an equivalent) into the shared layer
- Hook into `Program.cs` in `CodeMemory.Mcp` after the existing `IndexingState.MarkCompleted()` call at lines 166 and 191
- The MCP host uses `StorageService` (in-memory) by default — the instrumentation must gracefully no-op when storage is not hybrid
