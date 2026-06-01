# Metrics Reference

Operational reference for the OpenTelemetry + Prometheus metrics pipeline. Design decisions are documented in the ADRs (`docs/adr/Observability-01.md`, `docs/adr/Web-LocalMetrics-01.md`).

## Exposed Endpoints

| Endpoint | Port | Purpose |
|---|---|---|
| `/metrics` | 8080 | Prometheus scrape (OpenMetrics format) |
| OTLP gRPC (port 18889) | — | Aspire Dashboard via `OTEL_EXPORTER_OTLP_ENDPOINT` |
| Grafana UI | 3000 | Dashboard frontend |
| Prometheus UI | 9090 | Ad-hoc query interface |

## Running with Monitoring

```bash
docker compose up -d prometheus grafana
```

Then open:
- **Grafana**: http://localhost:3000 (admin / codememory)
- **Prometheus**: http://localhost:9090
- **Aspire Dashboard**: http://localhost:18888

## Grafana Dashboard Panels

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

A separate dashboard (`codememory-repo-gauges.json`) provides per-repo gauge panels — select a repo from the dropdown to inspect symbol composition, scale, and density.

## Configuration Files

| File | Purpose |
|---|---|
| `src/CodeMemory/Diagnostics/CodeMemoryMetrics.cs` | Instrument declarations on the `CodeMemory` meter |
| `src/CodeMemory.ServiceDefaults/Extensions.cs` | `ConfigureOpenTelemetry()` — MeterProvider, exporters |
| `src/CodeMemory.AspNet/Program.cs` | `AddServiceDefaults()`, `MapPrometheusScrapingEndpoint()` toggle |
| `monitoring/prometheus.yml` | Scrape config — targets `codememory:8080/metrics` |
| `monitoring/grafana/datasources/prometheus.yaml` | Auto-provisions Prometheus datasource |
| `monitoring/grafana/dashboards/dashboards.yaml` | Dashboard provisioning config |
| `monitoring/grafana/dashboards/codememory.json` | Dashboard: runtime panels |
| `monitoring/grafana/dashboards/codememory-repo-gauges.json` | Dashboard: per-repo gauge panels |

## Future Considerations

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

The snapshot doesn't include a version or schema field. If we later change the `RepoMetricsSnapshot` schema, consumers (both the MCP tool and future dashboard) need to handle backward compatibility. Consider adding a `SchemaVersion` field.

### Prometheus toggle on service defaults

The `Prometheus:Enabled` config only toggles the HTTP endpoint (`MapPrometheusScrapingEndpoint()`). The `AddPrometheusExporter()` in `ServiceDefaults/Extensions.cs` still registers the exporter. This is harmless (no observable cost) but slightly unclean. If desired, we could thread the config into `ConfigureOpenTelemetry` to conditionally skip the exporter registration entirely.

### Metric expiration / stale tag set cleanup

`InMemoryMetricsStore` has no mechanism to evict tag sets — entries accumulate for the process lifetime even if the associated repo is deleted. For long-running instances with many short-lived repos, this is a slow leak. Options:
- Add a `RemoveInstrumentTags(string instrumentName, string serializedTags)` method to `IMetricsStore` called from `RepoMetricsRecorder.RemoveRepo()`
- Add TTL-based expiry per tag set (configurable stale duration)
- Track last-accessed timestamp and prune on `GetSnapshot()`
