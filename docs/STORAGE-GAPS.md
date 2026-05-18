# Storage Plan Gap Audit

Audit date: 2026-05-19

This file records gaps found while comparing `docs/STORAGE.md` against the current repository implementation.

## Gap List

1. **Relationship key mismatch**
   - Type: Docs / implementation mismatch
   - Plan says Task 2 should configure a composite key for relationships.
   - Current implementation uses `RelationshipEntity.Id` as the primary key in `src/CodeMemory.AspNet/Storage/CodeMemoryDbContext.cs`.
   - Resolution needed: either update the plan to say `Id` is the key, or change the EF model to use a composite key.

2. **Migration SQL acceptance criterion is not covered**
   - Type: Verification / scope mismatch
   - Task 2 acceptance criteria mention EF Core migration SQL.
   - Current implementation uses `Database.EnsureCreatedAsync`; there are no migrations or migration scripts.
   - Resolution needed: remove that criterion from the current phase or add migration-generation verification.

3. **HybridStorageService constructor docs do not match code**
   - Type: Docs mismatch
   - Plan text mentions `DbContext` / `IDbContextFactory<CodeMemoryDbContext>`.
   - Current implementation takes `Func<CodeMemoryDbContext>` in `src/CodeMemory.AspNet/Storage/HybridStorageService.cs`.
   - Resolution needed: update docs to describe the factory delegate, or refactor to `IDbContextFactory`.

4. **ClearAllAsync does not recreate storage**
   - Type: Docs / behavior mismatch
   - Routing table says `ClearAllAsync` should drop and recreate tables and collections.
   - Current implementation drops the chunks collection and EF tables, then marks the service uninitialized.
   - Resolution needed: update docs to say drop/reset, or recreate storage before returning.

5. **SQLite schema behavior differs from per-repo schema wording**
   - Type: Docs mismatch
   - Plan describes one schema per repository for all provider paths.
   - SQLite uses schema `main` in `ServiceCollectionExtensions`.
   - Resolution needed: document SQLite as a special case with file-level isolation rather than schema isolation.

6. **In-memory fallback path wording is imprecise**
   - Type: Docs mismatch
   - Plan says `CreateStorage` keeps returning `StorageService` for `inmemory`.
   - Current `CreateStorage` returns `null` for `inmemory`, and `Program.cs` falls back to `CreateInMemoryStorage`.
   - Resolution needed: update docs to reflect the actual fallback flow.

7. **AspNet sql_query is not parameterized**
   - Type: Implementation gap
   - Task 5 constraints say parameterized queries should prevent SQL injection.
   - Current `AspNetSqlQueryTool` validates SELECT-only and table scope, then executes translated raw SQL via `DbCommand.CommandText`.
   - Resolution needed: either remove the parameterization claim or implement a safer query model.

8. **AspNet sql_query uses SqlParserCS despite docs saying no custom parser needed**
   - Type: Docs mismatch
   - Plan says AspNet SQL can avoid a custom SQL parser.
   - Current implementation references `SqlParserCS` and parses SQL for SELECT/table validation.
   - Resolution needed: update docs to say the parser is used for validation and table detection, not query execution.

9. **AspNet sql_query supports only single-table SELECTs**
   - Type: Docs / capability mismatch
   - The storage plan motivation mentions SQL analytics and joins.
   - Current tool rejects joins and multiple `FROM` tables.
   - Resolution needed: document single-table-only scope or implement join support.

10. **MCP tool discovery for AspNet sql_query has not been verified**
    - Type: Verification gap
    - Code registers `AspNetSqlQueryTool` in `Program.cs`.
    - The acceptance criterion says the tool appears in MCP tool list, but discovery was not run.
    - Resolution needed: verify via AspNet MCP tool list once the user can run the relevant tests/host checks.

11. **Ping remains global rather than current-repo-specific**
    - Type: Behavior / docs mismatch
    - Task 6 wording emphasizes per-repo completion.
    - Current `McpTools.Ping()` calls `IndexingState.IsCompleted()` without repo context, which means “all repos completed” in multi-repo AspNet.
    - Resolution needed: decide whether ping should stay global or become repo-context-aware for AspNet.

12. **Shared transaction scope claim is not implemented**
    - Type: Docs mismatch
    - Plan says the VectorStore and EF Core stores share the same database transaction scope.
    - Current implementation does not coordinate a transaction across the vector store and EF Core.
    - Resolution needed: remove the claim or implement coordinated transaction handling where supported.

13. **Final checklist remains unchecked**
    - Type: Docs status mismatch
    - `docs/STORAGE.md` tasks are marked complete, but the final checklist is still unchecked.
    - Resolution needed: update the checklist after deciding which gaps are acceptable and which require follow-up work.

## Build / Test Notes

- `dotnet build src\CodeMemory.AspNet\CodeMemory.AspNet.csproj` passed during implementation.
- `dotnet build CodeMemory.slnx` passed during implementation.
- Tests were not run by the agent because the project preference is for the user to run tests and report results.
