# CodeMemory.Probes

Standalone, run-manually console app that accumulates diagnostic **probes** for
CodeMemory — grammar shapes, parser/extractor behavior, indexing output. It is
**not** a test project (no NUnit SDK), so `dotnet test` and CI runs skip it
entirely. Mirrors the probe pattern used in `Nivara` (`tests/Nivara.SimdProbe`).

## Build & Run

```bash
dotnet run --project tests/CodeMemory.Probes            # all probes
dotnet run --project tests/CodeMemory.Probes -- tree-sitter   # one probe
```

Run from the repo root. The project references only the packages a probe needs
(currently `TreeSitter.DotNet` — deliberately **not** the `CodeMemory` library,
so building it never contends with a running MCP server's locked DLLs, and probe
output is fast to build).

## Adding a probe

1. Create `internal static class <Name>Probe { public static int Run() }` at the
   project root.
2. Register it in `Program.cs` (add a `"<name>" => <Name>Probe.Run()` case to the
   switch, and a line in `Usage()`).
3. Document what it measures and the last observed results in this README.
4. If the probe was first prototyped in a temp directory, delete the temp copy —
   this project is the single home for probes (see the probe lifecycle rule in
   `AGENTS.md`).

## Probes

### `tree-sitter` — grammar shape dump

Dumps the named-node parse tree (with field values) for the shapes
`TreeSitterSymbolExtractor` indexes, plus the qualification chain `buildFullName`
walks and the program-level package it now reads. Grounding for language work
(issue #131): verifies the concrete node types and field names a grammar version
actually emits, without guessing.

Observed on `TreeSitter.DotNet` 1.3.0 (see probe output for the full trees):

- **Java** — `package_declaration` is a *sibling* of classes under `program`
  (never an ancestor); it has **no** `name` field; the dotted package is its
  `scoped_identifier` named child (`uk.co.uworx.…`). Default-package files emit
  no `package_declaration`.
- **TypeScript** — `namespace Foo {}` produces a concrete `internal_module` node
  (with `name` field) wrapped by `expression_statement`; `module Foo {}` produces
  a `module` node. The `module` query supertype matches both in extraction.
- **C++** — `namespace MyNs {}` is `namespace_definition` (a direct ancestor of
  classes), qualified names already work for C++.
- **Java `import_declaration`** (grounding for Change 4, relationship resolution) —
  the dotted path is the named `scoped_identifier` child (never a `name` field);
  wildcard imports add a second named `asterisk` child (`import a.b.field.*;` →
  `scoped_identifier` `a.b.field` + `asterisk` `*`); token fields all have empty
  keys, so the named children (not fields) are the reliable read.
- **TypeScript `import_statement`** (Change 4) — named children are `import_clause`
  (`{ A, B }` / `DefaultThing` / `* as Alias`) and `string` (the module specifier).
  `'./relative/module'` cannot be expanded to index FullNames without module
  resolution ⇒ TS imports add no qualifier; same-file/uniqueness rules only.

## Files

- `Program.cs` — CLI dispatch (`tree-sitter` / `all`).
- `TreeSitterGrammarProbe.cs` — the grammar-shape dumper.