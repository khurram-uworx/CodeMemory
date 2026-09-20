# TODO — Fix #131: package/namespace-qualified symbol path resolution

## Problem

`find_related_code` (and every symbol-resolution path) cannot resolve package/namespace-qualified
symbol paths for tree-sitter languages such as Java: `find_related_code({symbolPath:
"uk.co.uworx.khoji.agile.internal.error.ServiceException"})` returns 0 results + suggestions while
the bare name `ServiceException` resolves (190 results), because:

1. **Extractor** — `TreeSitterSymbolExtractor.buildFullName` walks class-like ancestors and stops at
   `program`. Java's `package_declaration` is a *sibling* of the class at program level (not an
   ancestor), so the package never lands in `SymbolRecord.FullName` (521/585 Java types on the live
   `khoji-x` index have `FullName == Name`). TypeScript namespaces parse to concrete node types
   (`internal_module`, `external_module`) that aren't in `classLikeTypes` nor the KindMap, so they're
   likewise skipped (the `module` query supertype is matched, but the ancestor walk tests the
   concrete `current.Type`). C++ already works via `namespace_definition`.
2. **Resolution** — `StorageService.GetSymbolByFullNameAsync` (and AspNet's
   `HybridStorageService.GetSymbolByFullNameAsync`) match only: exact FullName → signature-insensitive
   prefix (first match) → bare `Name` (first match). A dotted query can never match a simple FullName,
   and no last-segment fallback exists. Bare-name matches pick an arbitrary `FirstOrDefault`.
3. **Data integrity** — `IndexingEngine.fullNameToGuid` is keyed by (colliding) FullName; duplicate
   simple names (`Request` ×4 across packages on khoji-x) share a Guid, so relationships from
   different classes are conflated (live: a *.cs* file surfaced in a Java bare-`Request` lookup).

## Proposed changes

### Change 1 — `TreeSitterSymbolExtractor.buildFullName` (extractor)

- When the ancestor walk reaches `program`, scan its children for `package_declaration` (Java) and
  prepend the dotted package name (via the node's `name` field, with a strip-`package`/`;` fallback).
  Default (no `package_declaration`) builds keep simple names — old Java tests stay green.
- Add a `namespaceLikeTypes` set (`internal_module`, `external_module`, `module`,
  `namespace_declaration`, `module_declaration`) consulted alongside `classLikeTypes` /
  `config.KindMap` in the ancestor walk, so TS classes inside namespaces become `Ns.Class`.
- **Do NOT** include Go `package_clause` (per-file label, would falsely conflate same-named types
  across files) or Python/Rust (no package decls surfaced).

Sketch:

```csharp
while (current != default && current.Type != "program")
{
    if (classLikeTypes.Contains(current.Type)
        || namespaceLikeTypes.Contains(current.Type)
        || config.KindMap.ContainsKey(current.Type))
    {
        var parentName = getNodeName(current);
        if (parentName != null) parts.Insert(0, parentName);
    }
    current = current.Parent;
}

if (current.Type == "program")
{
    var packageName = getProgramScopeName(current); // Java package_declaration child
    if (packageName != null) parts.Insert(0, packageName);
}
```

### Change 2 — Resolution: unique-or-null last-segment fallback

Both `StorageService` and `HybridStorageService.GetSymbolByFullNameAsync` extend the fallback chain:

- exact FullName (first match) — unchanged;
- signature-insensitive prefix (first match) — **unchanged** (existing test
  `GetSymbolByFullNameAsync_Overloads_ResolvesToFirstStoredMatch` locks this; overloads are the same
  method, first-match is a documented tradeoff);
- **new last-segment step**: `lastSegment = SymbolName.LastSegment(query)`; collect symbols where
  `Name == lastSegment`; resolve when **exactly one** matches, return `null` (→ existing
  "not found + suggestions" diagnostics) when zero or **>1**. Dotted queries on old/simple indexes
  now resolve via the unique short name; ambiguous bare names (`Request`) stop picking an arbitrary
  `FirstOrDefault`.

EF Core (Hybrid) query stays translatable: `Where(s => s.Name == lastSegment)`.

### Change 3 — `IndexingEngine.fullNameToGuid` verification only

With qualified FullNames keys are unique; confirm no other Name-keyed maps. Re-index required for
persistent (AspNet/SQLite) indexes — note in PR body/CHANGELOG.

## Verification steps

1. `dotnet build` the solution (may require an opencode session restart first — the running MCP
   server holds the built DLLs, MSB3021 file-lock before that).
2. Ask the human, then `dotnet test` — new + full relevant suites.
3. Live verify on `khoji-x` (after restart): re-index; SQL shows qualified Java FullNames;
   `find_related_code("uk.co.uworx.khoji.agile.internal.error.ServiceException")` equals the bare
   results; bare `Request` returns suggestions, not the mixed language set.

## Planned commits

1. `docs: plan fix for issue 131 in TODO.md`
2. `fix(indexing): qualify Java/TS full names with package/namespace in tree-sitter extractor`
3. `fix(storage): resolve last-segment and reject ambiguous symbol paths`
4. `docs: remove TODO.md — plan executed` (after G2)

## Blast radius

- **`TreeSitterSymbolExtractor`** — all tree-sitter languages' indexed FullNames. Java gains package
  prefix; TS-in-namespace gains namespace prefix; C++ unchanged; Python/Go/Rust/JS unchanged. Index
  contents change ⇒ any persisted index must be re-indexed (in-memory re-indexes per start anyway).
  Callers: `IndexingEngine` (FullName-keyed `fullNameToGuid`, `seenFullNames` dedup by FullName).
- **`StorageService.GetSymbolByFullNameAsync`** — every MCP tool that resolves symbol paths:
  `find_related_code`, `trace_dependency`, `impact_analysis`, `get_edit_context`, `get_symbol_history`,
  `SymbolLookup.ResolveAsync`. Ambiguous bare names now return null → tools already render
  message+suggestions (post-#122/#120 diagnostics); no new plumbing.
- **`HybridStorageService.GetSymbolByFullNameAsync`** — same change for the AspNet host (SQLite).
- **Tests** — `TreeSitterSymbolExtractorTests`, `StorageServiceTests`,
  `HybridStorageServiceTests`, `FindRelatedCodeToolTests`. Existing resolution tests
  (StorageServiceTests 644-704) must stay green.

## GitHub issues log

- (none yet — #131 itself is tracked; any deferred concern found during execution gets an issue here)