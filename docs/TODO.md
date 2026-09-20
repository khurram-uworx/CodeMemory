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

### Change 4 — context-aware reference resolution in `TreeSitterRelationshipExtractor`

Live verification of Changes 1–3 exposed a residual defect: `find_related_code` on a Java symbol
still surfaced a cross-module "mixed set" even though the symbol now resolves correctly. Root cause:
`TreeSitterRelationshipExtractor.findSymbolByName` resolves reference identifiers by **bare simple
name + `First()`** (`byName[name].First()`), ignoring package, import, and qualified-name context.
With qualified FullNames, `byFullName` misses for bare identifiers, so every `ServiceError` /
`getResponseType` reference resolves to whichever duplicate crawled first repo-wide — the
error-package `ServiceException(ServiceError)` constructor gets edges to the *event-processor*
`ServiceError.getResponseType()`, and even an Angular TS `name` property. Pre-existing (pre-#131
collisions took the same arbitrary first match via `byFullName`); #131 Change 3 fixed the GUID
collision half; this is the extraction-time target-selection half.

Change (intent; shape to code):

- `ExtractRelationships` builds a per-file resolution context:
  - Java — `package_declaration` prefix; explicit `import a.b.C;` map (simple → qualified);
    wildcard `import a.b.*;` package prefixes. (Grammar shapes to be confirmed by probe — same
    node API as `getProgramScopeName`.)
  - TypeScript — same-file + uniqueness rules only: `import { X } from './m'` cannot be mapped to
    index FullNames without module resolution, so imports add no qualifier (documented limitation;
    scoped/namespace-qualified refs resolve via full-name).
- `findSymbolByName` → context-aware resolution with a deterministic precedence and **no arbitrary
  first match** (parity with Change 2's storage behavior):
  1. fully-qualified text / exact `byFullName` hit;
  2. explicit import map (unique qualified target);
  3. same-file candidates — unique, or an overload family of one declaring type (documented
     first-match tradeoff, matching Change 2's prefix step);
  4. same-package candidates (Java) — unique, or one overload family;
  5. wildcard-import package candidates — unique;
  6. unique `byName` overall;
  7. otherwise `null` → edge skipped (no arbitrary cross-module targets).

Consequence: fewer but correct edges (intra-module references preserved; cross-module garbage
eliminated). Re-index required for persisted indexes.

Grounding (probe `tree-sitter`): Java `import_declaration` exposes the dotted path as a named
`scoped_identifier` child with an optional named `asterisk` child for wildcards (token fields have
empty keys — read named children); TypeScript `import_statement` carries `import_clause` +
`string` (source) children, which cannot be expanded to index FullNames without module resolution,
so TS resolution relies on same-file/uniqueness rules only (documented limitation).

**Addendum (human-approved, implemented in the Change 4 commit):** three extensions on top of the
commit `fa36fd9` resolver, driven by the live residual edge analysis on `khoji-x`:

- **`byFullName` dot-gate** — the fully-qualified fast path now fires only for reference text
  containing `.`. A bare `name` no longer collides with a top-level TypeScript symbol whose
  FullName is exactly `name` (the `error.name()` → TS `name` garbage edges).
- **Language-same guard** — `Symbol` gains a `Language` tag (tree-sitter extractor → source
  language; Roslyn extractor → `CSharp`; default `Unknown`). The candidate by-name pool is
  filtered to the source file's language, so a Java reference can never resolve to a TypeScript
  member (cross-language relationships are meaningless by construction).
- **Receiver-type resolution** — for `obj.method()`, resolve `obj`'s declared type from the
  current file (formal parameter / local / class field, searched innermost scope outward) and
  prefer that type's members as a resolution step *after the import map, before same-file*.
  Zero matches means the member is not in the index (e.g. implicit enum methods like
  `error.name()`, external types) → edge skipped rather than guessed. `this`/`super` receivers
  fall through to the general pipeline.
- **`typesOnly` candidate pool for type references** — constructor/method/field symbols that share
  a type's name no longer compete when resolving `extends`/`new X()`/field-type annotations;
  the pool is narrowed to type kinds. Fixes `new ServiceError(...)`-style references too.

## Verification steps

1. `dotnet build` the solution (may require an opencode session restart first — the running MCP
   server holds the built DLLs, MSB3021 file-lock before that).
2. Ask the human, then `dotnet test` — new + full relevant suites.
3. Live verify on `khoji-x` (after restart): re-index; SQL shows qualified Java FullNames
   (0/505 classes simple); bare `Request` and bare `ServiceException` return suggestions, not an
   arbitrary/mixed match.
4. Change 4 live check: `sql_query` edges of `uk.co.uworx.khoji.agile.internal.error.ServiceException`
   class and its constructors point only at error-module symbols (no event-processor/TS targets);
   `find_related_code` of that class returns the error module's own related set (no cross-module
   mixed set).

## Grounding (G1) — verified before implementation

Probe via the new `tests/CodeMemory.Probes` (`tree-sitter` mode, `TreeSitter.DotNet` 1.3.0):

- **Java** — `package_declaration` is a *sibling* of classes under `program` (never an ancestor);
  it exposes **no `name` field** in this grammar version; the dotted package is its last named
  child (`scoped_identifier` → `uk.co.uworx.khoji.agile.internal.error`). Default-package files
  emit no `package_declaration`. ⇒ extractor must scan `program` children and read the named-child
  text (with a keyword/';'-strip fallback).
- **TypeScript** — `namespace Foo {}` → concrete `internal_module` node (with a `name` field)
  wrapped by `expression_statement`, so `internal_module` must join the walk set; `module Foo {}`
  → `module` node, already in `tsKindMap` (qualifies today).
- **C++** — `namespace_definition` remains a direct class ancestor — unchanged.
- Blast radius confirmed: all tree-sitter languages' FullNames; persisted (AspNet/SQLite) indexes
  must be re-indexed to pick up qualified FullNames (in-memory re-indexes each start).

## Probe lifecycle record

- The tree-sitter grammar probe was prototyped at
  `C:\Users\khurram\AppData\Local\Temp\opencode\ts-probe`, judged reusable (grammar-version
  verification will be needed again) and relocated to `tests/CodeMemory.Probes` (`tree-sitter`
  mode); the temp copy was deleted. `tests/CodeMemory.Probes` is the single accumulating console
  project for probes (see AGENTS.md §Probes).

## Planned commits

1. `docs: plan fix for issue 131 in TODO.md`
2. `fix(indexing): qualify Java/TS full names with package/namespace in tree-sitter extractor`
3. `fix(storage): resolve last-segment and reject ambiguous symbol paths`
4. `fix(relationships): resolve tree-sitter reference targets with package/import context` (Change 4)
5. `docs: remove TODO.md — plan executed` (after G2)

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
- **`TreeSitterRelationshipExtractor`** (Change 4) — relationship edges for all tree-sitter
  languages (Java/TS/Python/Rust/C++/Go). Fewer, correct edges: intra-module references preserved,
  arbitrary cross-module/`First()` targets eliminated. `find_related_code`, `trace_dependency`,
  `impact_analysis`, `get_edit_context` outputs change accordingly. In-memory indexes rebuild per
  start; persisted (AspNet/SQLite) indexes require re-index. Existing single-file relationship
  tests must stay green (same-file contexts unaffected).
- **Tests** — `TreeSitterSymbolExtractorTests`, `StorageServiceTests`,
  `HybridStorageServiceTests`, `FindRelatedCodeToolTests`, `TreeSitterRelationshipExtractorTests`.
  Existing resolution tests (StorageServiceTests 644-704) must stay green.

## GitHub issues log

- Change 4 discovery: relationship extraction misattributes reference targets by bare simple-name
  `First()` when duplicate names span packages/languages — captured live on khoji-x
  (error-package ctor → event-processor `ServiceError`, TS `name`). Fixed in-plan as Change 4;
  no separate issue filed.
- (none else — #131 itself is tracked; any deferred concern found during execution gets an issue here)