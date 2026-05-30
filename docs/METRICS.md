# Metrics & Observability

CodeMemory exposes structured metrics via OpenTelemetry, simultaneously shipped to an OTLP endpoint (Aspire Dashboard) and polled by Prometheus for Grafana visualization.

---

## Exposed Endpoints

| Endpoint | Port | Purpose |
|---|---|---|
| `/metrics` | 8080 | Prometheus scrape (OpenMetrics format) |
| OTLP gRPC (port 18889) | — | Aspire Dashboard via `OTEL_EXPORTER_OTLP_ENDPOINT` |
| Grafana UI | 3000 | Dashboard frontend |
| Prometheus UI | 9090 | Ad-hoc query interface |

---

## Instrument Definitions

Defined in `src/CodeMemory/Diagnostics/CodeMemoryMetrics.cs` on the `CodeMemory` meter (version `1.0`).

| Instrument Name | Type | Unit | Description | Recording Location |
|---|---|---|---|---|---|
| `codememory.indexing.duration` | Histogram | ms | Full indexing pass per repo | `IndexingEngine.RunIndexingAsync()` — `IndexingEngine.cs:314` |
| `codememory.indexing.files_count` | Counter | — | Files indexed per repo | `IndexingEngine.RunIndexingAsync()` — `IndexingEngine.cs:315` |
| `codememory.indexing.symbols_count` | Counter | — | Symbols stored per repo | `IndexingEngine.RunIndexingAsync()` — `IndexingEngine.cs:316` |
| `codememory.git.clone.duration` | Histogram | ms | Git clone operations | `CloneIndexService.cs:100`, `IndexingHostedService.cs:103` |
| `codememory.search.query_duration` | Histogram | ms | Semantic search queries | `SemanticSearchService.cs:66` |
| `codememory.sql.query_duration` | Histogram | ms | Custom SQL queries | `SqlQueryService.cs:1960,1967,2222,2229` |
| `codememory.tools.invocations` | Counter | — | MCP tool invocations (tagged by `tool` & `host`) | `McpTools.cs:15`, `AdminTool.cs:31,65`, `AspNetMcpTools.cs:21` |
| `codememory.repo.total_symbols` | ObservableGauge | — | Total symbols per repo (tagged by `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.total_files` | ObservableGauge | — | Indexed files per repo (tagged by `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.classes` | ObservableGauge | — | Class symbols per repo (tagged by `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.methods` | ObservableGauge | — | Method symbols per repo (tagged by `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.interfaces` | ObservableGauge | — | Interface symbols per repo (tagged by `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.properties` | ObservableGauge | — | Property symbols per repo (tagged by `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.fields` | ObservableGauge | — | Field symbols per repo (tagged by `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |
| `codememory.repo.total_relationships` | ObservableGauge | — | Symbol relationships per repo (tagged by `repo`) | `RepoMetricsRecorder` after `MarkCompleted()` |

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
  └─► Runtime/HTTP instruments
       └─► (same path)
```

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
  │    ├─► FilesIndexed      ┐
  │    ├─► SymbolsStored     ┤ All fire at once when indexing completes
  │    └─► IndexingDuration  ┘
  │
  IndexingState.MarkCompleted()
  │
  RepoMetricsRecorder.RecordAsync()
  │    └─ hybrid storage? ──no──► skip silently
  │    └─ yes ──► MetricsService.GetMetricsAsync()
  │                 └─► OverviewStats computed from relational DB
  │                 └─► cache[repoName] = RepoMetrics
  │
  ▼
  Prometheus scrape picks up:
    • Event-based: indexing.duration, files_count, symbols_count (10s latency)
    • State-based: repo.total_symbols, .total_files, .classes, .methods,
                   .interfaces, .properties, .fields, .total_relationships
                   (ObservableGauge — read on next scrape, always up-to-date)
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

The Grafana dashboard is auto-provisioned from `monitoring/grafana/dashboards/codememory.json` with 10 panels:

| Panel | Metrics |
|---|---|
| Indexing Duration | `codememory_indexing_duration_*` — avg & max per-repo |
| Files & Symbols | `codememory_indexing_files_count_total`, `codememory_indexing_symbols_count_total` |
| Repo Overview Stats | `codememory_repo_total_symbols`, `codememory_repo_classes`, `codememory_repo_methods`, `codememory_repo_interfaces`, `codememory_repo_properties`, `codememory_repo_fields` — per-repo gauges |
| Files & Relationships | `codememory_repo_total_files`, `codememory_repo_total_relationships` — per-repo gauges |
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

---

## Adding a New Metric

1. Add the instrument to `CodeMemoryMetrics` — follow existing patterns (`.CreateHistogram<double>(...)` or `.CreateCounter<long>(...)`)
2. The meter name `"CodeMemory"` is automatically picked up by OpenTelemetry's `MeterProvider` — no registration needed
3. Add a corresponding PromQL panel in `codememory.json`

Prometheus automatically exposes any new metric created via `CodeMemoryMetrics.Meter` without additional configuration.

---

## Known Gaps

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
