# ADR-Observability-01: OpenTelemetry & Prometheus as the Metrics Pipeline

---

## Context

CodeMemory needs structured runtime observability — indexing duration, tool invocation counts, query latencies, repository state — surfaced to operators via dashboards and alerting. The requirements span two hosts (`CodeMemory.Mcp` STDIO and `CodeMemory.AspNet` HTTP) and must work in three deployment modes:

| Mode | Toil level | Metrics consumer |
|---|---|---|---|
| Local `dotnet run` (AspNet host) | Zero-infrastructure | Aspire Dashboard (OTLP) |
| Docker Compose (`docker compose up`) | One command | Prometheus + Grafana |
| Production / Azure Container Apps | Managed | OTLP endpoint |

The instrumentation must be built-in, not bolted on — every deployed instance emits metrics without config.

**Exception — MCP STDIO host:** The `CodeMemory.Mcp` host (STDIO transport, single-repo CLI) is intentionally excluded from the OTel pipeline. It has no HTTP endpoint to serve `/metrics`, no Prometheus scraper to poll it, and no dashboard to refresh. Adding OTel infrastructure would add ~200ms startup cost for zero benefit. `CodeMemoryMetrics` instruments are still called from MCP tools for correctness — they produce no output when no `MeterProvider` is registered. See `docs/FOLLOWUP.md` (Gap D) for the rationale.

Additionally, the AspNet host needs a zero-infrastructure local metrics path for the Razor Pages dashboard (`RuntimeMetrics.cshtml`) to show both **runtime metrics** (tool invocations, query duration) and **code-analysis metrics** (symbol counts, file counts per repo) without requiring a Prometheus scraper.

---

## Decision

**Use `System.Diagnostics.Metrics` (`Meter` / `Counter` / `Histogram` / `ObservableGauge`) as the sole instrument surface, wired into the OpenTelemetry SDK via `ConfigureOpenTelemetry()` in `CodeMemory.ServiceDefaults`, with dual export: OTLP (for Aspire) and Prometheus (for Grafana).**

### Architecture

```
Application code
  │
  ├─► CodeMemoryMetrics.Meter (System.Diagnostics.Metrics)
  │    └─► OpenTelemetry MeterProvider
  │         ├─► OTLP Exporter ──► Aspire Dashboard (port 18888)
  │         └─► Prometheus Exporter ──► /metrics (port 8080)
  │                                    │
   │                              Prometheus (port 9090)
   │                              (scrapes /metrics)
   │                                    │
   │                              Grafana (port 3000)
   │                              (dashboards refresh on interval)
   │
  └─► Auto-instrumentation (AspNetCore, HttpClient, Runtime)
       └─► (same MeterProvider, same exporters)
```

The `CodeMemory` meter (version `1.0`) is registered in `ConfigureOpenTelemetry` via `.AddMeter("CodeMemory")`. All custom instruments live on this single meter.

---

### Instrument Type Semantics

| Instrument | Use Case | Examples |
|---|---|---|
| `Counter<long>` | Cumulative events that only increase | Tool invocations |
| `Histogram<T>` | Distribution of measurements | Durations, file counts, symbol counts |
| `ObservableGauge<long>` | Point-in-time state, read on scrape | Per-repo symbol counts, file counts |

### Instrument Reference

Defined in `src/CodeMemory/Diagnostics/CodeMemoryMetrics.cs`.

| Instrument Name | Type | Unit | Description | Recording Location |
|---|---|---|---|---|
| `codememory.indexing.duration` | Histogram | ms | Full indexing pass per repo | `IndexingEngine.RunIndexingAsync()` |
| `codememory.indexing.files_count` | Histogram | — | Files indexed per repo | `IndexingEngine.RunIndexingAsync()` |
| `codememory.indexing.symbols_count` | Histogram | — | Symbols stored per repo | `IndexingEngine.RunIndexingAsync()` |
| `codememory.git.clone.duration` | Histogram | ms | Git clone operations | `CloneIndexService`, `IndexingHostedService` |
| `codememory.search.query_duration` | Histogram | ms | Semantic search queries | `SemanticSearchService` |
| `codememory.sql.query_duration` | Histogram | ms | Custom SQL queries | `SqlQueryService` |
| `codememory.tools.invocations` | Counter | — | MCP tool invocations (tagged `tool`, `host`) | `McpTools`, `AdminTool`, `AspNetMcpTools` |
| `codememory.repo.total_symbols` | ObservableGauge | — | Total symbols per repo (tagged `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.total_files` | ObservableGauge | — | Indexed files per repo (tagged `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.classes` | ObservableGauge | — | Class symbols per repo (tagged `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.methods` | ObservableGauge | — | Method symbols per repo (tagged `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.interfaces` | ObservableGauge | — | Interface symbols per repo (tagged `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.properties` | ObservableGauge | — | Property symbols per repo (tagged `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.fields` | ObservableGauge | — | Field symbols per repo (tagged `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.total_relationships` | ObservableGauge | — | Symbol relationships per repo (tagged `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |

### Auto-Instrumented Metrics

OpenTelemetry SDK auto-instruments these via `.AddAspNetCoreInstrumentation()`, `.AddHttpClientInstrumentation()`, `.AddRuntimeInstrumentation()`:

| Instrument Family | Source |
|---|---|
| `http_server_request_duration_ms_*` | ASP.NET Core |
| `http_server_active_requests_count` | ASP.NET Core |
| `http_client_request_duration_ms_*` | HttpClient |
| `dotnet_gc_*` | .NET Runtime |
| `dotnet_thread_pool_*` | .NET Runtime |

---

## Rationale

### 1. `System.Diagnostics.Metrics` is the native .NET instrumentation API

It requires no external packages for instrument creation — `Meter`, `Counter<T>`, `Histogram<T>`, `ObservableGauge<T>` are part of `System.Diagnostics.DiagnosticSource`. The same `Meter` object is consumed by the OpenTelemetry SDK's `MeterProvider`, which handles export formatting and batching. This avoids coupling to any specific APM vendor.

### 2. Dual export covers all deployment modes

- **OTLP** (OpenTelemetry Protocol) is the standard wire format for telemetry — any OpenTelemetry Collector or Aspire Dashboard can consume it.
- **Prometheus** (OpenMetrics text format) is the de-facto standard for pull-based scraping in the Docker/Kubernetes ecosystem.
- Both exporters are registered in `ConfigureOpenTelemetry()` in `CodeMemory.ServiceDefaults/Extensions.cs` — every host that calls `AddServiceDefaults()` gets both automatically.

### 3. Prometheus was chosen over push-based backends

| Backend | Access | Scrape | Setup cost |
|---|---|---|---|
| **Prometheus** | Pull | Polls `/metrics` | `docker compose up prometheus grafana` |
| Azure Monitor | Push | OTLP/HTTP | Requires Azure subscription + Application Insights |
| Datadog | Push | Agent-based | Requires agent + API key |
| Grafana Cloud | Push | OTLP gRPC | Requires Grafana Cloud account |

Key consideration: CodeMemory runs in environments where Prometheus is the shared infrastructure (GitHub Actions CI, dev machines, edge deployments). The pull model means exporters don't need to know where to send data — they just expose `/metrics` and the infrastructure picks it up.

OTLP is available as a side path when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, so managed environments (Azure Container Apps, Grafana Cloud) can route to their backend of choice.

### 4. ObservableGauge for state vs Counter/Histogram for events

Repo overview stats (symbol counts, file counts) are **state** — point-in-time snapshots that change only on re-index. `ObservableGauge` with a cached-value callback is the correct instrument for the OTel/Prometheus path:
- The value is read on each scrape via a callback that returns the latest cached `RepoMetrics`
- No accumulation artifacts — on re-index the value is atomically replaced
- Tagged by `repo` name, supporting multi-repo deployments

These same values are also pushed into the local `IMetricsStore` by `RepoMetricsRecorder.RecordAsync()` after each re-index, using `RecordHistogram` — one measurement per re-index, captured with ring-buffer support for the Razor Pages dashboard (see ADR `Web-LocalMetrics-01`).

The `LocalMetricsCollector` (MeterListener) explicitly skips `ObservableGauge<long>` instruments to prevent the 5-second `RecordObservableInstruments()` timer from duplicating these measurements into the local store — the dashboard gets them only from the explicit `RecordAsync` write.

Indexing/query/tool metrics are **events** — each occurrence is a discrete observation. `Histogram` captures the distribution (count, sum, min, max). `Counter` captures cumulative totals. These flow through the `LocalMetricsCollector` MeterListener to `IMetricsStore` automatically.

### 5. The `CodeMemoryMetrics` static class is the single registration point

All instruments are declared as `static readonly` fields on `CodeMemoryMetrics`, created eagerly in the static initializer via `Meter.Create*()`. OpenTelemetry discovers them via `.AddMeter("CodeMemory")` — no per-instrument registration needed. This means:
- New instruments are automatically exported — just add a field and record data
- Instrument names, descriptions, and units are self-documenting at the point of declaration
- The meter name `"CodeMemory"` is a well-known contract consumed by `MeterProvider`

---

## Consequences

### Positive

- **Zero-config metrics** — any deployed host exposes `/metrics` immediately
- **Portable** — same instruments work in local dev, Docker Compose, and production
- **Vendor-neutral** — OTel + Prometheus is the industry standard; no vendor lock-in
- **Extensible** — new instruments require only a field in `CodeMemoryMetrics.cs` and a recording call
- **Auto-instrumentation included** — HTTP request duration, GC, thread pool for free

### Negative

- **Prometheus pull model requires a scraper** — the `/metrics` endpoint is useless without a Prometheus server polling it. This is fine in Docker Compose but must be explicitly set up in production.
- **OTLP exporter is gated on env var** — `OTEL_EXPORTER_OTLP_ENDPOINT` must be set. If forgotten, the Aspire Dashboard receives no data. This is an operational concern, not a code concern.
- **ObservableGauge callbacks are synchronous** — the callback runs on the MeterProvider's collection thread. Heavy computation in a callback would block the scrape. The `RepoMetricsRecorder` handles this by pre-computing and caching results; the callback only reads from a `ConcurrentDictionary`.
- **No ring buffer for histograms** — the OpenTelemetry SDK's Prometheus exporter exposes `_bucket`, `_sum`, and `_count` series. Raw measurement histograms must be queried via the local metrics store (see ADR `Web-LocalMetrics-01`).

### Dashboard

The Grafana dashboard is auto-provisioned from `monitoring/grafana/dashboards/codememory.json` with 9 panels:

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

### How to Add a New Metric

1. Add the instrument to `CodeMemoryMetrics` — e.g. `Meter.CreateHistogram<double>(...)`
2. Record data at the instrumentation point — `MyMetric.Record(value, tags)`
3. Optionally add a PromQL panel in `codememory.json`

No registration needed — `AddMeter("CodeMemory")` picks it up automatically.

---

## Configuration Files

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

## Compliance

- All custom instruments MUST be declared on `CodeMemoryMetrics.Meter` — no separate meters.
- Instrument names MUST follow the `codememory.<domain>.<name>` convention (dot-separated, lowercase).
- Tag keys MUST use lowercase dot-separated names (`tool`, `host`, `repo`).
- New host projects MUST call `builder.AddServiceDefaults()` in `Program.cs`.
  **Exception:** The MCP STDIO host (`CodeMemory.Mcp`) is exempt — see Context §Exception above.
- The `Prometheus:Enabled` config toggle in `appsettings.json` controls the HTTP scrape endpoint; the Prometheus exporter registration in `ConfigureOpenTelemetry()` is always enabled.

---

## Alternatives Considered

| Alternative | Rejected because |
|---|---|
| **App.Metrics** (third-party library) | Adds external dependency; .NET 8+ built-in Meter API is sufficient and standard |
| **Prometheus-net** (third-party library) | Same as above — `System.Diagnostics.Metrics` + OTel SDK is the recommended path |
| **Push-only (StatsD, DogStatsD)** | Requires destination config in every host; pull model fits CodeMemory's ephemeral indexing hosts better |
| **Expose Prometheus only (no OTel)** | OTLP is needed for Aspire Dashboard and managed environments; dual export is low-cost since both share the same MeterProvider |
