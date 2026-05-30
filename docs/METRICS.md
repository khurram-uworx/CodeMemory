# Metrics & Observability

CodeMemory exposes structured metrics through the `CodeMemory` `Meter`. OpenTelemetry can export those measurements to Prometheus, OTLP/Aspire, and Grafana; the ASP.NET host can also keep a bounded in-process snapshot for zero-infrastructure demos.

---

## Exposed Endpoints

| Endpoint | Port | Purpose |
|---|---|---|
| `/metrics` | 8080 | Prometheus scrape (OpenMetrics format) |
| OTLP gRPC (port 18889) | — | Aspire Dashboard via `OTEL_EXPORTER_OTLP_ENDPOINT` |
| Grafana UI | 3000 | Dashboard frontend |
| Prometheus UI | 9090 | Ad-hoc query interface |
| `/Repos/{name}/Metrics` | 4792 | Repository metrics page; includes local runtime metrics when enabled |

---

## Instrument Definitions

Defined in `src/CodeMemory/Diagnostics/CodeMemoryMetrics.cs` on the `CodeMemory` meter (version `1.0`).

| Instrument Name | Type | Unit | Description | Recording Location |
|---|---|---|---|---|
| `codememory.indexing.duration` | Histogram | ms | Full indexing pass per repo | `IndexingEngine.RunIndexingAsync()` — `IndexingEngine.cs:314` |
| `codememory.indexing.files_count` | Counter | — | Files indexed per repo | `IndexingEngine.RunIndexingAsync()` — `IndexingEngine.cs:315` |
| `codememory.indexing.symbols_count` | Counter | — | Symbols stored per repo | `IndexingEngine.RunIndexingAsync()` — `IndexingEngine.cs:316` |
| `codememory.git.clone.duration` | Histogram | ms | Git clone operations | `CloneIndexService.cs:100`, `IndexingHostedService.cs:103` |
| `codememory.search.query_duration` | Histogram | ms | Semantic search queries | `SemanticSearchService.cs:66` |
| `codememory.sql.query_duration` | Histogram | ms | Custom SQL queries | `SqlQueryService.cs:1960,1967,2222,2229` |
| `codememory.tools.invocations` | Counter | — | MCP tool invocations (tagged by `tool` & `host`) | `McpTools.cs:15`, `AdminTool.cs:31,65`, `AspNetMcpTools.cs:21` |

### Additional Metrics (Auto-Instrumentation)

OpenTelemetry SDK auto-instruments these via `.AddAspNetCoreInstrumentation()`, `.AddHttpClientInstrumentation()`, `.AddRuntimeInstrumentation()`:

| Instrument Family | Source |
|---|---|
| `http_server_request_duration_ms_*` | ASP.NET Core |
| `http_server_active_requests_count` | ASP.NET Core |
| `http_client_request_duration_ms_*` | HttpClient |
| `dotnet_gc_*` | .NET Runtime |
| `dotnet_thread_pool_*` | .NET Runtime |

---

## Flow: Data Path from Instrument to Dashboard

```
Application code
  │
├─► CodeMemoryMetrics.Meter instruments
│    └─► OpenTelemetry MeterProvider
│         ├─► OTLP Exporter ──► Aspire Dashboard (port 18888)
│         └─► Prometheus Exporter ──► /metrics endpoint (port 8080)
│                                    │
│                              Prometheus (port 9090)
│                              polls every 10s
│                                    │
│                              Grafana (port 3000)
│                              refreshes every 15s
│
└─► LocalMetricsCollector (optional MeterListener)
     ├─► /Repos/{name}/Metrics runtime section
     └─► get_metrics_snapshot MCP tool
```

Prometheus and the local collector can be enabled at the same time. They are independent listeners/exporters over the same measurements.

---

## Lifecycle: When Metrics Appear

### Indexing Metrics (Repo is added → indexing completes)

```
Repo added (UI at /Repos/Add or appsettings.json "Repositories")
  │
  CloneIndexService.EnqueueRepoAsync()
  │    └─ git clone
  │         └─► CloneDuration fires (visible immediately after clone)
  │
  IndexingEngine.RunIndexingAsync()
  │    ├─► FilesIndexed   ┐
  │    ├─► SymbolsStored  ┤ All fire at once when indexing completes
  │    └─► IndexingDuration ┘
  │
  IndexingState.MarkCompleted()
  │
  ▼
  Prometheus scrape picks up new metrics within 10s
  Grafana panel refreshes within 15s
```

### Query Metrics (Agent/user calls MCP tools)

```
Agent invokes semantic_search MCP tool
  │
  SemanticSearchService.SearchByTextAsync()
  │    └─► QueryDuration fires (visible immediately)
  │
  ▼
  Prometheus next scrape picks it up
```

### Tool Metrics (Any MCP tool invocation)

```
Agent calls any MCP tool
  │
  MCP tool entry point
  │    └─► ToolInvocations.Add(1, ...) fires
  │
  ▼
  Prometheus next scrape picks it up
```

---

## Running with Monitoring

```bash
docker compose up -d prometheus grafana
```

Then open:
- **Grafana**: http://localhost:3000 (admin / codememory)
- **Prometheus**: http://localhost:9090
- **Aspire Dashboard**: http://localhost:18888

The Grafana dashboard is auto-provisioned from `monitoring/grafana/dashboards/codememory.json` with 8 panels:

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

---

## Configuration Files

| File | Purpose |
|---|---|
| `monitoring/prometheus.yml` | Scrape config — targets `codememory:8080/metrics` every 10s |
| `monitoring/grafana/datasources/prometheus.yaml` | Auto-provisions Prometheus datasource in Grafana |
| `monitoring/grafana/dashboards/dashboards.yaml` | Dashboard provisioning config |
| `monitoring/grafana/dashboards/codememory.json` | Dashboard panel definitions |

### ASP.NET Observability Settings

```json
{
  "Observability": {
    "Prometheus": {
      "Enabled": true
    },
    "LocalMetrics": {
      "Enabled": false,
      "MaxSeries": 500,
      "HistogramWindowSize": 256
    }
  }
}
```

- `Prometheus:Enabled` defaults to `true`; when disabled, the Prometheus exporter and `/metrics` endpoint are not registered.
- `LocalMetrics:Enabled` defaults to `false`; when enabled, the ASP.NET dashboard shows runtime metrics and the `get_metrics_snapshot` MCP tool returns the same structured snapshot.
- `MaxSeries` bounds in-memory cardinality. Keep tags low-cardinality (`repo.name`, `tool`, `host`, provider-like values).
- `HistogramWindowSize` controls the rolling sample window used for local p95 calculations.

---

## Adding a New Metric

1. Add the instrument to `CodeMemoryMetrics` — follow existing patterns (`.CreateHistogram<double>(...)` or `.CreateCounter<long>(...)`)
2. The meter name `"CodeMemory"` is automatically picked up by OpenTelemetry's `MeterProvider` — no registration needed
3. If local metrics are enabled, the ASP.NET dashboard and `get_metrics_snapshot` pick it up automatically
4. Add a corresponding PromQL panel in `codememory.json` for the Grafana dashboard

Prometheus automatically exposes any new metric created via `CodeMemoryMetrics.Meter` without additional configuration.
