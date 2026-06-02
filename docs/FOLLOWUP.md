# Follow-Ups

Observations and known gaps discovered during implementation.

---

## Metrics Known Gaps

For background on the metrics pipeline, endpoints, dashboards, and configuration, see [`docs/METRICS.md`](METRICS.md).

### Gap A: Tag serialization format vulnerable to delimiter collision

**What's implemented:** `InMemoryMetricsStore.serializeTags()` joins tags as `key=value|key=value` (sorted by key). `deserializeTags()` splits on `|` then `=`. This is deterministic, human-readable, and round-trips correctly for current tag values.

**Assessment:** Works for current tags (`tool`, `host`, `repo`), but `|` or `=` in a tag value would break round-tripping. This is a latent fragility, not an active bug.

**Recommendation:** Switch to base64-encoded JSON or a content hash for tag serialization to safely handle arbitrary tag values.

## Two RepoRoot-Only IStorageService Consumers

`JsonGitMetricStore` and `AspNetSqlQueryTool` both inject `IStorageService` but only access `.RepoRoot` — they don't call any storage read/write/query methods. This is consistent with the existing DI pattern (14/16 services use `IStorageService` for more than just `RepoRoot`), so no abstraction change is warranted for just 2 consumers. Noted for awareness if the count grows.

### Gap B: Grafana datasource UID is implicit

**What's implemented:** Both Grafana dashboards reference the Prometheus datasource using `uid: "Prometheus"`, while `monitoring/grafana/datasources/prometheus.yaml` provisions the datasource by name only.

**Assessment:** Works in the current Docker Compose setup, and the existing runtime dashboard already uses the same convention. This is a provisioning hardening concern rather than an active dashboard failure.

**Recommendation:** Ask platform/observability reviewers whether to add an explicit `uid: Prometheus` to the datasource provisioning file, or to switch dashboards to name-based/default datasource references.
