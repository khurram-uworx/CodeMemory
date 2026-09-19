# Issue #124 — SQL schema discoverability

## Priority
Medium

## Goal
Make the `sql_query` MCP surface self-discovering in both the stdio InMemoryVectorStore host and the ASP.NET relational backend, while preserving the existing schema-first unknown-column diagnostics.

## Problem
Issue #124 reports that agents had to guess SQL introspection syntax. The current tree already contains most of the requested behavior: the stdio tool description documents `PRAGMA`, `DESCRIBE`, and examples; `SchemaResources` exposes structured schema resources; and issue #122 added schema-validated unknown-column errors. The ASP.NET `sql_query` description still omits the supported `DESCRIBE TABLES` / `DESC <table>` commands, so the catalog is inconsistent across hosts.

## Scope
- Make schema-discovery commands and examples explicit in the ASP.NET `sql_query` tool description.
- Make the existing stdio schema resources discoverable from the stdio tool description.
- Add focused regression coverage for host catalog descriptions and schema-discovery responses.
- Reuse the existing `SchemaResources` and `TableSchemaProvider`; do not introduce a parallel schema API.
- Keep `PRAGMA table_info` documented only for the stdio InMemoryVectorStore host because the relational backend does not implement it.

## Constraints
- `sql_query` remains SELECT-only.
- Do not change SQL execution semantics or the existing unknown-column error contract.
- ASP.NET relational storage exposes `DESCRIBE TABLES` and `DESC <table>` only; `PRAGMA` is not supported there.
- Follow the repository's existing C# style and NUnit conventions.

## Acceptance criteria
- The ASP.NET `tools/list` description for `sql_query` names `DESCRIBE TABLES` and `DESC <table>` and includes a valid example.
- The stdio `sql_query` description points agents to the existing `codememory://schema/tables` and per-table schema resources.
- `DESCRIBE TABLES` and `DESC SymbolRecord` return structured rows in the ASP.NET tool path.
- The MCP resource catalog exposes `codememory://schema/tables` and the three table-specific schema resources.
- Existing schema-first unknown-column tests continue to pass and report valid columns.
- Targeted tests and the relevant build/typecheck pass.

## Blast radius
- `src/CodeMemory.AspNet/Tools/AspNetSqlQueryTool.cs` — catalog-facing documentation only.
- `src/CodeMemory.Mcp/Tools/SqlQueryTool.cs` — optional discovery-resource wording.
- `tests/CodeMemory.Tests/Mcp/AspNetSqlQueryToolTests.cs` and/or `tests/CodeMemory.Tests/Mcp/McpInfrastructureTests.cs` — focused coverage.
- No production SQL parser, validator, storage, or public model changes expected.

## Planned commits
1. `docs: make SQL schema discovery explicit` — host-accurate catalog descriptions and examples.
2. `test: cover SQL schema discovery surfaces` — catalog/resource and DESCRIBE response assertions.
3. `docs: remove TODO.md — plan executed` — remove this plan after G2 review.

## GitHub issues log
- [x] #124 — MCP SQL schema introspection documentation and discoverable examples (existing issue)
