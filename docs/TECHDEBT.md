# Technical Debt

Items identified by AI agent analysis of the codebase — patterns that add friction, hide bugs, or waste tokens.

---

## 1. Three Error-Handling Patterns for MCP Tools

| Return Type | Pattern | Where Used |
|---|---|---|
| `string` | `try/catch` → `JsonSerializer.Serialize(new { status="error", message })` | `AdminTool`, `McpTools.Ping` |
| Typed record | Null-service guard → `[]` / sentinel with `Warning` field | All `CodeMemory` library tools |
| `IDictionary<string, object?>` | `fail()` helper → `{ success: false, error }` | `AspNetSqlQueryTool` |

Writing a new tool requires deciding which pattern fits the return type. The typed-record pattern is strictly better — the MCP SDK handles serialization, null-service guards produce clear signals, and unexpected exceptions become proper JSON-RPC errors. The other two add no value that typed records don't already provide.

**Fix:** Converge to typed records everywhere. Replace `AdminTool` string returns with a record type. Replace `AspNetSqlQueryTool` dictionary returns with a typed result record.

## 2. `MockServices.cs` Will Not Scale

Hand-written stubs for ~10 interfaces in a single 239-line file. Every interface change requires manual updates. No call verification. No per-invocation behavior customization. The stated reason ("no mocking library dependency") trades a NuGet package for ongoing maintenance cost.

**Fix:** Adopt NSubstitute (already a transitive dependency through ASP.NET Core test infrastructure). Replace `MockServices.cs` with lightweight NSubstitute-based test setup methods.

## 3. No Error-Path Test Coverage

Tests cover happy paths (storage round-trips, parsing, query results) but none verify error handling — null service guards, storage failures, invalid inputs. The Error Handling conventions documented in `AGENTS.md` are untested and will silently drift.

**Fix:** Add `ErrorHandlingTests` that verifies each MCP tool's degraded/null-service behavior. Coverage target: every guard clause in every tool method.

## 4. `IndexingEngine` Lifetime Inconsistency

- **AspNet:** `AddScoped` (wrapped in per-repo scope inside `IndexingHostedService`)
- **Mcp:** `AddSingleton` (direct resolution)

Same class, different contracts. If mutable state is ever added to `IndexingEngine`, it will work in AspNet but corrupt itself in Mcp.

**Fix:** Make `IndexingEngine` `AddSingleton` everywhere — it's currently stateless. Or document why it must be scoped in AspNet and add a safety assertion in Mcp.

## 5. Modifiers Stored as Opaque Comma-Separated String

Modifiers like `"public,static,async"` are stored as a single string field. This forces `LIKE '%public%'` instead of the natural `Modifiers CONTAINS 'public'` or `'public' = ANY(Modifiers)`. No SQL-idiomatic way to query tagged fields.

**Fix:** Normalize modifiers to a separate `SymbolModifier` table or store as a JSON array. Both require schema migration in `IStorageService` models and updates to the extraction pipeline in `RoslynSymbolExtractor` / `TreeSitterSymbolExtractor`.

## 6. `RelationshipRecord` May Be Empty Without Feedback

When the extraction pipeline produces no relationships, `RelationshipRecord` returns zero rows with no error or hint. A user sees an empty table and doesn't know if the extraction failed, is incomplete, or there genuinely are no relationships.

**Fix:** Add a "relationship extraction stats" method to `IStorageService` or a warning in `SqlQueryService` when `RelationshipRecord` is queried and the relationship count is zero. Could also expose via the `ping` response or a new `storage_stats` tool.

## 7. `ORDER BY Similarity DESC` Requires `Content LIKE` in WHERE

Vector search re-ranking cannot operate on unfiltered data — a `Content LIKE '%pattern%'` clause is required to seed the embedding comparison. `SELECT * FROM ChunkRecord ORDER BY Similarity DESC` (no WHERE) fails or returns unranked results.

**Fix:** Lift the `LIKE` requirement by allowing an optional explicit search text parameter, or auto-seeding with a default comparison vector when no filter is present. Requires changes to `SqlQueryService.ExecuteAsync` and the vector search path in the SQL engine.

---

## Suggested Order

| # | Item | Effort | Why |
|---|---|---|---|---|
| 3 | Error-path test coverage | Low | Fills actual test gap, prevents drift |
| 1 | Single error pattern | Medium | Requires touching 3 files, but removes a decision point |
| 7 | Vector search LIKE requirement | Medium | Usability gap in sql_query |
| 6 | Empty RelationshipRecord feedback | Low | Simple developer experience fix |
| 2 | Adopt NSubstitute | Medium | Replaces `MockServices.cs`, adds call verification |
| 4 | IndexingEngine lifetime | Low | Risk of latent bug, easy to fix |
| 5 | Modifiers as structured field | Large | Schema change, extraction + storage updates |
