# CodeMemory — Container

> Local-first repository intelligence engine exposed via the Model Context Protocol (MCP).
> Build a persistent semantic memory layer over any codebase.

## Quick Start

```bash
docker run -p 4792:8080 ghcr.io/khurram-uworx/codememory
```

Open http://localhost:4792/ — the Repository Dashboard.

## Environment Variables

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_URLS` | `http://+:8080` | Listening address/port |
| `Storage__Provider` | `sqlite` | Storage backend: `sqlite`, `inmemory`, `pgvector`, `sqlserver` |
| `Embedding__Provider` | `ngram` | Embedding backend: `ngram`, `onnx`, `ollama` |
| `ConnectionStrings__Sqlite` | `Data Source=/data/codememory.db` | SQLite connection string |
| `ConnectionStrings__Postgres` | — | PostgreSQL/pgvector connection string |
| `RebuildIndex__Cron` | — | Cron expression for scheduled re-indexing |
| `Logging__LogLevel__Default` | `Information` | ASP.NET Core log level |
| `ASPNETCORE_ENVIRONMENT` | `Production` | .NET environment; `Production` loads only `appsettings.json` |
| `LocalMetrics__Enabled` | `false` | Enable in-process runtime metrics on the dashboard |
| `Prometheus__Enabled` | `true` | Enable Prometheus `/metrics` scrape endpoint |
| `RepoRegistry__EnableDemoMode` | `false` | Lock repos to pre-configured list (disable add/delete) |
| `Repositories__{name}` | — | Seed a repo at startup. `{name}` = repo label, value = Git URL or local path |

Persistent storage requires mounting a volume:

```bash
docker run -v codememory-data:/data -p 4792:8080 ghcr.io/khurram-uworx/codememory
```

## Demo Deployment (Azure / Cloud)

Minimal one-liner for a quick demo with pre-seeded repos:

```bash
docker run -p 4792:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ASPNETCORE_URLS=http://+:8080 \
  -e LocalMetrics__Enabled=true \
  -e Prometheus__Enabled=false \
  -e RepoRegistry__EnableDemoMode=true \
  -e Storage__Provider=sqlite \
  -e Repositories__codememory=https://github.com/khurram-uworx/codememory \
  -e Repositories__memori=https://github.com/khurram-uworx/memori \
  ghcr.io/khurram-uworx/codememory
```

| Setting | Why |
|---|---|
| `Production` | Avoids loading development-only config (local paths, debug settings) |
| `LocalMetrics__Enabled=true` | See runtime metrics on the dashboard without Prometheus |
| `Prometheus__Enabled=false` | Cleaner — no unused `/metrics` endpoint in a simple demo |
| `RepoRegistry__EnableDemoMode=true` | Read-only — repos are pre-seeded, no add/delete UI |
| `Repositories__*` | Seeds repos at startup. The key after `Repositories__` becomes the repo name in the dashboard |

## Ports

| Port | Purpose |
|---|---|
| `8080` | HTTP — Repository Dashboard + MCP endpoints |

## docker-compose

See [docker-compose.yml](https://github.com/khurram-uworx/CodeMemory/blob/main/docker-compose.yml) for a full stack with PostgreSQL (pgvector), Prometheus (port 9090), Grafana (port 3000, admin/codememory) with auto-provisioned dashboards, and an Aspire Dashboard (port 18888) for OTLP telemetry.

## GitHub Package

**Package:** `ghcr.io/khurram-uworx/codememory`

## License

MIT
