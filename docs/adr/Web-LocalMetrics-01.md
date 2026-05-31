# ADR-Web-LocalMetrics-01: InMemoryMetricsStore — Zero-Infrastructure Repo Metrics

---

## Context

The AspNet host has a Razor Pages metrics dashboard (`Metrics.cshtml`) that displays per-repo code-analysis metrics (symbol counts, complexity, coupling). The `MetricsService` computes these from the relational database. However, there is no mechanism to show **runtime metrics** (tool invocation counts, query durations, indexing stats) on the same dashboard without requiring a Prometheus scraper to be running.

The requirements are:
1. The dashboard must work out-of-the-box — no external infrastructure required
2. Runtime metrics must survive for the lifetime of the process (in-memory is acceptable)
3. The solution must coexist with the OTel/Prometheus pipeline — not replace it
4. Must be toggleable — operators who rely on Prometheus should be able to disable the local path

The existing OTel pipeline (see ADR `Observability-01`) exports to Prometheus and OTLP, but Prometheus requires a scraper. In local `dotnet run` or demo scenarios, there is no scraper.

---

## Decision

**Introduce an `InMemoryMetricsStore` (via `IMetricsStore`) that collects runtime and code-analysis metrics from two sources: (1) a `MeterListener`-based `LocalMetricsCollector` capturing push-based Counter/Histogram instruments, and (2) direct `RecordHistogram` calls from `RepoMetricsRecorder` when repo metrics are computed after indexing. Exposed via `IMetricsStore` for the Razor Pages dashboard. Disabled by default; toggled via `LocalMetrics:Enabled` in `appsettings.json`.**

### Architecture

```
Application code
  │
  ├─► Push-based instruments (Counter/Histogram)   ← runtime metrics
  │    │  (ToolInvocations.Add, QueryDuration.Record, etc.)
  │    │
  │    ├─► OpenTelemetry MeterProvider        (existing OTel path)
  │    │    ├─► OTLP Exporter
  │    │    └─► Prometheus Exporter
  │    │
  │    └─► LocalMetricsCollector (MeterListener)   ← captures push-based only
  │         └─► InMemoryMetricsStore (IMetricsStore)
  │              │
  │              ├─► RepoMetricsSnapshot ──► RepoMetrics.cshtml
  │              └─► (future) MCP tool
  │
  ├─► Pull-based instruments (ObservableGauge)      ← code-analysis metrics
  │    │  (codememory.repo.total_symbols, etc.)
  │    │
  │    └─► OpenTelemetry MeterProvider        (existing OTel path)
  │         ├─► OTLP Exporter
  │         └─► Prometheus Exporter
  │
  └─► RepoMetricsRecorder.RecordAsync()              ← direct push on reindex
       └─► InMemoryMetricsStore (IMetricsStore)      ← via RecordHistogram
            └─► (same store as MeterListener above)
```

`LocalMetricsCollector` subscribes to all `CodeMemory` meter instruments but **skips** `ObservableGauge<long>` in its `OnLongMeasurement` callback — the gauge values reach the store exclusively through the explicit `RepoMetricsRecorder.RecordAsync()` write, avoiding a double-feed from the 5-second `RecordObservableInstruments()` timer.

### Components

| Component | Responsibility | Location |
|---|---|---|
| `LocalMetricsCollector` | Registers a `MeterListener` for the `CodeMemory` meter; routes `Counter<long>` to `RecordCounter()`, non-gauge instruments to `RecordHistogram()`; skips `ObservableGauge<long>` | `CodeMemory.AspNet.Services` |
| `RepoMetricsRecorder` | On indexing completion, fetches code-analysis metrics from DB, caches them for ObservableGauge callbacks, and writes them into `IMetricsStore` via `RecordHistogram` | `CodeMemory.AspNet.Services` |
| `InMemoryMetricsStore` | `IMetricsStore` implementation — `ConcurrentDictionary` of instrument states, each with per-tag-set accumulators | `CodeMemory.AspNet.Services` |
| `IMetricsStore` | Interface — `RecordCounter()`, `RecordHistogram()`, `GetSnapshot()` | `CodeMemory.AspNet.Storage` |
| `LocalMetricsOptions` | Config — `Enabled` (default `false`), `MaxUniqueTagCombinations`, `MaxMeasurementsPerTagSet` | `CodeMemory.AspNet.Configuration` |
| `RepoMetricsSnapshot` | Snapshot model — `CollectedAt`, `Instruments`, each with `MetricValue[]` | `CodeMemory.AspNet.Models` |

### Instrument Type Detection

`LocalMetricsCollector.OnLongMeasurement` differentiates between instrument types using pattern matching:
- `Counter<long>` → `store.RecordCounter()`
- `ObservableGauge<long>` → **skipped** (repo metrics reach the store via the explicit `RepoMetricsRecorder.RecordAsync()` write instead)
- All other `long` instruments (e.g., `Histogram<long>`) → `store.RecordHistogram()`

The `is Counter<long>` pattern was initially implemented via `GetGenericTypeDefinition()` but was replaced with the `is` operator to avoid a reflection call on the measurement hot path (see `docs/FOLLOWUP.md` §LocalMetricsCollector per-fix notes).

`OnDoubleMeasurement` routes all double measurements (from `Histogram<double>` instruments) to `store.RecordHistogram()` — no type differentiation needed since the `CodeMemory` meter has no `ObservableGauge<double>` instruments.

### Configuration

```json
"LocalMetrics": {
    "Enabled": false,
    "MaxUniqueTagCombinations": 50,
    "MaxMeasurementsPerTagSet": 0
}
```

| Field | Default | Description |
|---|---|---|
| `Enabled` | `false` | Enables the MeterListener and store |
| `MaxUniqueTagCombinations` | `50` | Hard cap on unique tag sets per instrument to prevent unbounded memory growth |
| `MaxMeasurementsPerTagSet` | `0` | Ring buffer size. `0` = no ring buffer (aggregates only). `>0` enables raw measurement capture for sparklines |

When `MaxUniqueTagCombinations` is exceeded, the excess measurements are dropped and a warning is logged once.

---

## Rationale

### 1. MeterListener is the designed-in observer API

`System.Diagnostics.Metrics.MeterListener` is the official .NET API for subscribing to `Meter` instrument measurements without going through the OpenTelemetry SDK. It gives us:
- Filtering by meter name (`CodeMemory`)
- Typed callbacks for `long` and `double` measurements
- Zero dependencies — it's in the BCL

Using `MeterListener` means we piggyback on the same instrument calls that feed the OTel pipeline, rather than adding a separate recording path.

### 2. In-memory store is sufficient for the dashboard use case

The Razor Pages dashboard shows current values — cumulative counter totals, histogram aggregates (count/sum/min/max/last), and optionally recent raw measurements for sparklines. There is no requirement for:
- Persistence across restarts
- Historical querying
- Sharing across host instances

A `ConcurrentDictionary`-backed store satisfies all current requirements with zero setup cost. The `IMetricsStore` interface is designed so a future `SqliteMetricsStore` can be swapped in (see `docs/FOLLOWUP.md` §Future Considerations).

### 3. Disabled by default to avoid redundant bookkeeping

In production deployments with Prometheus, the local store is unnecessary — all metrics are already available via `/metrics`. Enabling it by default would add:
- Memory overhead for duplicate storage
- CPU overhead for the MeterListener callbacks
- Configuration surface that operators don't need

Operators who want the local dashboard runtime metrics or who run without Prometheus can set `LocalMetrics:Enabled: true`.

### 4. Tag serialization was kept simple

Tags are serialized as `key=value|key=value` (sorted). This is deterministic and human-readable. The `|` and `=` characters are unambiguous in practice because tag values come from controlled sources (tool names, repo names, host names) that never contain those characters. The deserialization round-trips correctly via split on `|` and first `=`.

---

## Consequences

### Positive

- **Zero-infrastructure dashboard** — runtime metrics work without Prometheus, Grafana, or any external service
- **No dependency on OTel SDK internals** — the MeterListener API is stable and in the BCL
- **IMetricsStore abstraction** — future backends (SQLite, Redis) can be swapped in without changing consumers
- **Composable with OTel path** — push-based Counter/Histogram instruments feed both consumers. ObservableGauge instruments serve only the OTel path, with repo metrics reaching the local store via a separate explicit write
- **Ring buffer support** — optional per-tag-set raw measurement capture for sparklines

### Negative

- **Memory-bound** — the store accumulates counter values and histogram aggregates for the lifetime of the process. For long-running instances with many unique tag combinations (e.g., many distinct tool+repo combinations), memory grows linearly. The `MaxUniqueTagCombinations` cap prevents unbounded growth.
- **No process restart survival** — a restart resets all metrics to zero. The `IMetricsStore` interface supports a `SqliteMetricsStore` alternative for persistence, but it is not yet implemented.
- **Enabled by operator, not default** — new users won't see runtime metrics on the dashboard unless they read the docs and flip the config. This is an acceptable tradeoff for production safety.

### Performance

The `MeterListener` callback runs on an arbitrary thread-pool thread. Delivery is generally sub-millisecond. For the current traffic level (sporadic tool invocations, occasional queries), the overhead is negligible. Testing uses `SpinWait.SpinUntil` with a 2-second timeout to account for the async delivery nature.

### Compliance

- New push-based instrument types (Counter/Histogram) added to `CodeMemoryMetrics` are automatically picked up by `LocalMetricsCollector` — no registration needed. New ObservableGauge instruments must be explicitly written to `IMetricsStore` if they should appear on the dashboard
- `IMetricsStore` implementations MUST handle both `RecordCounter` and `RecordHistogram` even if the current store only has counter instruments
- The `LocalMetrics:Enabled` check MUST happen at startup, not per-measurement — the `MeterListener` is either started or not

---

## Alternatives Considered

| Alternative | Rejected because |
|---|---|
| **Always-on MeterListener** | Adds unnecessary overhead in production deployments that use Prometheus |
| **Prometheus-only dashboard** | Requires a Prometheus scraper to be running — defeats the zero-infrastructure goal |
| **Poll CodeMemoryMetrics.Meter directly** | The `Meter` API does not expose current instrument values — only `MeterListener` callbacks deliver measurements |
| **Separate recording calls (duplicate instrumentation)** | Would require every `Add()`/`Record()` call to be duplicated — fragile and error-prone |
| **SQLite store from day one** | Over-engineered for the current use case; adds EF Core dependency; can be added later via `IMetricsStore` |
