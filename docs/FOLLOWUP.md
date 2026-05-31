# Metrics Follow-Ups

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

Auto-provisioned from `monitoring/grafana/dashboards/codememory.json` with 8 panels:

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

The `codememory.repo.*` gauge panels are not yet provisioned in the dashboard JSON (see [Gap C](#gap-c-repo-gauge-panels-missing-from-grafana-dashboard)).

### Configuration Files

| File | Purpose |
|---|---|
| `src/CodeMemory/Diagnostics/CodeMemoryMetrics.cs` | Instrument declarations on the `CodeMemory` meter |
| `src/CodeMemory.ServiceDefaults/Extensions.cs` | `ConfigureOpenTelemetry()` — MeterProvider, exporters |
| `src/CodeMemory.AspNet/Program.cs` | `AddServiceDefaults()`, `MapPrometheusScrapingEndpoint()` toggle |
| `monitoring/prometheus.yml` | Scrape config — targets `codememory:8080/metrics` |
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

### Tag key inconsistency (`repo` vs `repo.name`)

Runtime metrics tag repos with `repo.name` (e.g., `IndexingDuration`, `ToolInvocations`), while code-analysis metrics use `repo` (from `RepoMetricsRecorder`). The `RuntimeMetrics.cshtml` PageModel's `FilterByRepo` handles both keys, but this is a hidden convention not enforced by any contract. A new instrument could easily use the wrong tag key and silently not appear in repo-scoped dashboard views.

### Dual-writer pattern for repo metrics

`codememory.repo.*` metrics reach `IMetricsStore` through an explicit `RecordHistogram` call in `RepoMetricsRecorder.RecordAsync()`, NOT through the `MeterListener`. Adding a new code-analysis metric requires editing two separate code paths in `RepoMetricsRecorder`:
1. Register an `ObservableGauge` in the constructor (for OTel/Prometheus)
2. Add a `RecordHistogram` call in `RecordAsync()` (for the dashboard)

There is no compiler check or test enforcing both paths stay in sync.

### Two dashboard pages for code-analysis metrics

`Metrics.cshtml` queries the DB via `MetricsService` on every page load. `RuntimeMetrics.cshtml` reads from `IMetricsStore` (updated on reindex). Both display code-analysis metrics but through different mechanisms and update cadences. An indexing bug that causes `RecordAsync` to fail would produce stale data on `RuntimeMetrics` while `Metrics` remains current — operators need to understand both refresh models.

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

### Metric expiration / stale tag set cleanup

`InMemoryMetricsStore` has no mechanism to evict tag sets — entries accumulate for the process lifetime even if the associated repo is deleted. For long-running instances with many short-lived repos, this is a slow leak. Options:
- Add a `RemoveInstrumentTags(string instrumentName, string serializedTags)` method to `IMetricsStore` called from `RepoMetricsRecorder.RemoveRepo()`
- Add TTL-based expiry per tag set (configurable stale duration)
- Track last-accessed timestamp and prune on `GetSnapshot()`

### Consolidate the two dashboard pages

Currently `Metrics.cshtml` (DB-sourced) and `RuntimeMetrics.cshtml` (IMetricsStore-sourced) both display code-analysis metrics through different mechanisms. An operator must understand both refresh models. Options:
- Merge runtime metrics into `Metrics.cshtml` as a new card section fed from `IMetricsStore`
- Or make `RuntimeMetrics.cshtml` the single pane-of-glass by writing all code-analysis metrics through `IMetricsStore` and deprecating the DB-sourced page

---

## Metrics Known Gaps

### Gap A: ObservableGauge instruments not declared as static fields on `CodeMemoryMetrics`

**ADR says:** All instruments MUST be declared as `static readonly` fields on `CodeMemoryMetrics`, created eagerly in the static initializer. The instrument reference table says `codememory.repo.*` instruments are "Defined in `src/CodeMemory/Diagnostics/CodeMemoryMetrics.cs`".

**What's implemented:** The 8 `codememory.repo.*` ObservableGauge instruments are created dynamically in the `RepoMetricsRecorder` constructor (`RepoMetricsRecorder.cs:34–72`) via `CodeMemoryMetrics.Meter.CreateObservableGauge(...)`. They use the correct meter but are not visible as static fields.

**Assessment: ADR's approach is better.** Having all 13+ instruments declared in one file with names, units, and descriptions makes the complete surface discoverable at a glance. The current approach hides the repo gauges inside a service constructor — a developer adding a new gauge must know to look in `RepoMetricsRecorder` rather than `CodeMemoryMetrics`. The gauges already capture cache values via closures, so the declarations can be moved without changing the runtime behavior.

**Recommendation:**
1. Move all 8 `CreateObservableGauge` calls from `RepoMetricsRecorder` constructor into `CodeMemoryMetrics.cs` as `static readonly` fields
2. Change `RepoMetricsRecorder` to accept `Measurement<long>[]` factories or assign callbacks that reference the static fields
3. Update `CodeMemoryMetrics.cs` to include the meter name `"CodeMemory"` and version `"1.0"` as a constant if not already done

---

### Gap C: Repo gauge panels missing from Grafana dashboard

**ADR says:** The dashboard should have panels for `codememory.repo.*` gauges, but self-acknowledges they are "not yet provisioned."

**What's implemented:** 8 panels exist in `codememory.json` (indexing, tools, queries, runtime, HTTP). Zero panels for `codememory.repo.total_symbols`, `codememory.repo.total_files`, `codememory.repo.classes`, `codememory.repo.methods`, `codememory.repo.interfaces`, `codememory.repo.properties`, `codememory.repo.fields`, or `codememory.repo.total_relationships`.

**Assessment: Clear missing feature, no dispute.** The data is emitted as ObservableGauges and available in Prometheus — only the dashboard panels are missing.

**Recommendation:**
1. Add a Stat panel showing current per-repo totals (total_symbols, total_files, total_relationships)
2. Add a table or multi-stat panel showing per-repo breakdown by kind (classes, methods, interfaces, properties, fields)
3. Use `label_values(repo)` in the PromQL to support multi-repo deployments

---

### Gap F: Two unmerged dashboard pages for code-analysis metrics

**ADR says:** Does not prescribe dashboard page structure.

**What's implemented:** `Metrics.cshtml` (DB-sourced via `MetricsService`) shows symbol distribution, complexity, coupling. `RuntimeMetrics.cshtml` (IMetricsStore-sourced) shows runtime metrics + repo overview gauges. Both display overlapping code-analysis data through different mechanisms with different staleness characteristics. `RuntimeMetrics.cshtml` links to `Metrics.cshtml` (line 28), but `Metrics.cshtml` has no reciprocal link.

**Assessment: The split makes sense architecturally but UX is confusing.** DB-sourced metrics are authoritative (always reflect current index state). IMetricsStore-sourced metrics are faster but may lag on indexing failure. Having both is useful for operators, but the navigation should be bidirectional.

**Recommendation:**
1. Add a "Runtime Metrics" button to `Metrics.cshtml` linking to `RuntimeMetrics.cshtml`
2. Add a note on each page explaining the data source and refresh cadence
3. Future: consider merging runtime metrics into `Metrics.cshtml` as a new card section once the data sources are unified

---

### Gap H: Method complexity & coupling not exposed as OTel instruments

**ADR says:** Does not mandate instrumenting these — the decision was made during implementation.

**What's implemented:** `RepoMetrics` contains richer data (`AverageLinesPerMethod`, `MethodSizeHistogram`, coupling topology) that is deliberately not exposed as OTel instruments:

| Available Data | Record | Why Skipped |
|---|---|---|
| `Complexity.AverageLinesPerMethod` | `Histogram<double>` | Low signal-to-noise for infrastructure monitoring |
| `Complexity.MethodSizeHistogram` (buckets: 1–5, 6–10, …, 100+) | 6× `ObservableGauge<long>` | Typically queried ad-hoc via the metrics dashboard |
| `Coupling.MostCoupled` / `Coupling.MostImportant` | — | Per-symbol topology, not aggregate infra signal |
| `Coupling.RelationshipTypeDistribution` | — | High cardinality per repo; use dashboard instead |
| `TopFilesBySymbols` | — | Per-file detail better suited to dashboard than OTel |

**Assessment: Intentional, no action needed.** These are dashboard-domain concerns, not infrastructure-monitoring signals. If they become needed as OTel metrics in the future, add instruments to `CodeMemoryMetrics.cs` and record them in `RepoMetricsRecorder` alongside the overview stats.
