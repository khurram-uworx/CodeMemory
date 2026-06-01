# Follow-Ups

Observations and known gaps discovered during implementation.

---

## Metrics Known Gaps

For background on the metrics pipeline, endpoints, dashboards, and configuration, see [`docs/METRICS.md`](METRICS.md).

### Gap A: Tag serialization format vulnerable to delimiter collision

**What's implemented:** `InMemoryMetricsStore.serializeTags()` joins tags as `key=value|key=value` (sorted by key). `deserializeTags()` splits on `|` then `=`. This is deterministic, human-readable, and round-trips correctly for current tag values.

**Assessment:** Works for current tags (`tool`, `host`, `repo`), but `|` or `=` in a tag value would break round-tripping. This is a latent fragility, not an active bug.

**Recommendation:** Switch to base64-encoded JSON or a content hash for tag serialization to safely handle arbitrary tag values.

### Gap B: Grafana datasource UID is implicit

**What's implemented:** Both Grafana dashboards reference the Prometheus datasource using `uid: "Prometheus"`, while `monitoring/grafana/datasources/prometheus.yaml` provisions the datasource by name only.

**Assessment:** Works in the current Docker Compose setup, and the existing runtime dashboard already uses the same convention. This is a provisioning hardening concern rather than an active dashboard failure.

**Recommendation:** Ask platform/observability reviewers whether to add an explicit `uid: Prometheus` to the datasource provisioning file, or to switch dashboards to name-based/default datasource references.
