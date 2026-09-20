# TODO — #132: mature GitIgnoreParser + needed wiring (branch `khurram/132`)

## Problem

[Issue #132](https://github.com/khurram-uworx/CodeMemory/issues/132): default
ignores only cover root-level `node_modules`; nested dependency dirs and build
output pollute `find_related_code` results.

Confirmed root causes in `main`:

1. `FileCrawler.isDirIgnored` checks **only the first path segment**
   (`relDir.Split('/').FirstOrDefault()`) against `AlwaysIgnored`, so nested
   `node_modules` under any sub-folder falls through.
2. `GitIgnoreParser` is an approximation with real semantic gaps:
   - negations are collected into a separate set and applied **globally before
     anything else** — git is per-pattern, last-match-wins;
   - only the **root** `.gitignore` is loaded (nested `.gitignore` files in
     subdirectories are never read);
   - anchoring is wrong (`foo/bar` gets a `(.*/)?` prefix and matches
     `a/foo/bar`; git anchors it to the `.gitignore`'s dir);
   - dir-only patterns are weakened (trailing `/` trimmed but never enforced,
     so `bin/` would also ignore a file literally named `bin`);
   - `**` handling is approximate.
3. `FileWatcherService` has a divergent, weaker check (root `.gitignore` only,
   no `AlwaysIgnored`, no config `exclude`) — edits under ignored dirs can
   re-enter the index.
4. `rescan_repository(excludePatterns)` is **dead code** — the parameter is
   accepted but never applied; and `isFileIgnoredByConfig` can't express the
   globs its own description promises (`**/bin/**`, `**/*.generated.cs`).

## Approved direction (per human)

- **Do not hardcode build-output dirs** (`bin`, `obj`, `dist`, `target`) in
  code — durable exclusions live in a repo's `.gitignore`; we honor it *fully*.
- Sticking with (maturing) the **home-grown `GitIgnoreParser`** — no new
  dependency, no reinvention.
- `rescan_repository(excludePatterns)` becomes a **transient one-shot** layer;
  no persistence into `.codememory.json` (gitignore is the durable source).
- **No** new `find_related_code` filter param — with the index clean by
  construction, results are clean by construction.
- Init-tool scaffolding of default ignore entries is **deferred** → issue #140.

## Proposed changes

### 1. `GitIgnoreParser` — ordered, git-compliant rule engine

Rewrite `src/CodeMemory/Indexing/GitIgnoreParser.cs`:

- Keep API: `Parse(string[])`, `Load(path)`, `Empty`, and
  `IsIgnored(string relativePath)`; **add** `IsIgnored(string relativePath,
  bool isDir = false)`. Keep `Parse`/`Empty` semantics so `FileCrawlerTests`
  and `CodeMemoryInitServiceTests` compile unchanged.
- Replace the literal/regex split with an **ordered `IgnoreRule[]`** where each
  rule is `{ Negated, DirOnly, Regex }`. Evaluate in file order and keep the
  **last match**; result is ignored iff the last match is a non-negated rule.
- Correct pattern semantics (per git-scm):
  - bare name (no `/`) → matches the basename at **any depth**
    (`(?:^|.*/)patt$`);
  - leading `/` or embedded `/` → **anchored** to the `.gitignore`'s directory;
  - trailing `/` → **dir-only**: matches the directory itself and everything
    under it, but never a file with the exact name (`bin/` must not ignore a
    file named `bin`);
  - `*` → `[^/]*`; `?` → `[^/]`; `[a-z]` char classes pass through; `**`
    handled (`**/` = any prefix dirs, trailing `/**` = everything inside,
    `a/**/b` = zero or more dirs between);
  - `\#`, `\!`, `\ ` escapes (trailing-space nuance documented as a known
    limitation, not implemented);
  - blank lines and `#` comments skipped.
- Add `FromPatterns(IEnumerable<string>)` so config `exclude` and rescan
  `excludePatterns` reuse the same matcher.

### 2. New `GitIgnoreEvaluator` — nested `.gitignore` + extra root patterns

Small new class (same file or `src/CodeMemory/Indexing/GitIgnoreEvaluator.cs`):

- `GitIgnoreEvaluator(string rootPath, IReadOnlyList<string>? extraRootPatterns = null)`.
- `IsIgnored(string relPath, bool isDir)`: walks ancestor directories
  root→deepest, loads `<dir>/.gitignore` if present (cached), evaluates `relPath`
  relative to each `.gitignore`'s directory, and lets the **deepest matching
  rule win** (nested file overrides outer — git precedence). `extraRootPatterns`
  form an extra additive ignore layer (applied last, no negation).
- Memoize per-directory rule lists so a BFS walk stays O(dirs).

### 3. `FileCrawler` — any-depth `AlwaysIgnored` + full gitignore honoring

`src/CodeMemory/Indexing/FileCrawler.cs`:

- `isDirIgnored`: match `AlwaysIgnored` against **every segment** of `relDir`;
  then `evaluator.IsIgnored(relDir, isDir: true)`.
- `isFileIgnored`: skip the file if **any segment** is in `AlwaysIgnored`
  (covers `.codememory.json` at root even without `.gitignore` coverage); then
  `evaluator.IsIgnored(relPath, isDir: false)`.
- `WalkAsync` keeps its existing parameter shape (`ignoreParser`,
  `additionalExclusions`): when `ignoreParser` is supplied use it as a
  single-layer root parser (unit-test path unchanged); otherwise use a
  `GitIgnoreEvaluator`. `additionalExclusions` flow into `extraRootPatterns`
  (now with real glob support — `**/bin/**` finally works).
- `AlwaysIgnored` **unchanged contents** (`.git`, `.codememory`, `.memori`,
  `.codememory.json`, `node_modules`) — only the matching becomes any-depth.

### 4. `FileWatcherService` — consistent ignore rules

`src/CodeMemory/Services/FileWatcherService.cs`:

- Replace the root-only `GitIgnoreParser` with a `GitIgnoreEvaluator`
  (nested files) + `CodeMemoryConfig.Load(repoRoot).Exclude` as extra root
  patterns, built in `StartAsync`.
- `isGitIgnored(fullPath)` → `evaluator.IsIgnored(relPath, isDir: false)`.

### 5. `IndexingEngine` + `AdminTool` — wire `excludePatterns` end-to-end

- `IndexingEngine.RunIndexingAsync(repoRoot, ct, progress = null,
  IReadOnlyList<string>? extraExclusions = null)` — optional param; merge
  `config.Exclude` + `extraExclusions` before calling `WalkAsync` (all 6
  existing callers compile unchanged).
- `AdminTool.RescanRepositoryAsync` splits `excludePatterns` on `,`, trims,
  passes non-empty lists through as `extraExclusions`. Tool description updated:
  durable exclusions belong in `.gitignore` / `.codememory.json` `exclude`;
  `excludePatterns` is a one-shot filter.

### 6. Docs

- `AGENTS.md` §Repository Configuration: `AlwaysIgnored` is matched at any
  depth; `exclude` patterns now follow gitignore glob semantics.
- `packages/code-memory/README.md` + `src/CodeMemory.Mcp/README.md`:
  `rescan_repository` row mentions `excludePatterns` is additive/one-shot and
  durable excludes come from `.gitignore`.

## Verification steps

- `dotnet build` after each logical unit.
- `dotnet test` (full NUnit suite) — **human confirmation required before
  running**.
- New/extended tests:
  - `GitIgnoreParserTests` (new): any-depth bare names; last-match-wins
    ordering incl. re-ignore-after-unignore; dir-only (`bin/` vs file `bin`);
    anchoring (`/build`, `sub/build`); `**` forms; `?` + `[Bb]in`; escapes;
    empty parser.
  - `GitIgnoreEvaluatorTests` (new): nested `.gitignore` un-ignores a root
    ignore (`*.cs` at root, `!keep.cs` in `app/` → keep.cs indexed);
    deeper file wins; extra root patterns layer.
  - `FileCrawlerTests`: nested `node_modules` at depth excluded; nested
    `.gitignore` honored; config `exclude` with `**/bin/**` works; existing 4
    tests stay green.
  - `FileWatcherServiceTests`: file created under a nested ignored dir is not
    indexed.
  - `IndexingEngine`/tool-level test: rescan `excludePatterns` excludes files.
- Live MCP re-verification after the fix: human restarts the harness against
  the `main` tree (never force-kill the MCP server — AGENTS.md); confirm the
  index no longer contains `find_related_code` noise from ignored dirs, and
  this repo's own `.gitignore` gains `.delta/` (repo config, not product code).

## Planned commits

1. `docs: plan #132 gitignore maturation in TODO.md` (this file)
2. `feat: mature GitIgnoreParser to git-compliant ordered semantics` — parser
   rewrite + `GitIgnoreParserTests`
3. `feat: add GitIgnoreEvaluator for nested .gitignore files` — evaluator +
   tests
4. `fix: honor nested .gitignore and any-depth AlwaysIgnored in FileCrawler` —
   crawler + tests
5. `fix: apply consistent ignore rules in FileWatcherService` — watcher + test
6. `fix: wire rescan_repository excludePatterns into indexing` — engine +
   AdminTool + tool test
7. `docs: document ignore semantics and rescan excludePatterns`
8. `docs: remove TODO.md — plan executed` (after G2 reviews clear)

## Blast radius

- `GitIgnoreParser`: consumers = `FileCrawler`, `FileWatcherService`,
  `CodeMemoryInitService` (coverage detection), `FileCrawlerTests`. Negation
  ordering and dir-only semantics change **behavior** (toward git); init
  coverage cases (`.codememory.json`, `*.json`) are simple and expected green.
- `FileCrawler`: consumer = `IndexingEngine` (Mcp host + AspNet hosts via
  `IndexingHostedService`, `CloneIndexService`, `RebuildIndexHostedService`,
  Mcp `Program.cs`). Net effect is **more** exclusions for repos whose
  subdirectories carry `.gitignore` files and for nested `node_modules` —
  exactly the #132 ask.
- `FileWatcherService`: consumer = Mcp host `Program.cs`.
- `IndexingEngine`: new **optional** param — all 6 callers compile unchanged.
- `AdminTool.RescanRepositoryAsync` (`rescan_repository` tool): dead param
  becomes live.
- Tests: `FileCrawlerTests`, `CodeMemoryInitServiceTests`, watcher tests;
  new `GitIgnoreParserTests`, `GitIgnoreEvaluatorTests`.

## Known limitations (locked in tests/docs, not silent)

- Trailing-space escaping in ignore patterns (`foo\ `) not handled.
- `.git/info/exclude` and global core.excludesFile are out of scope.
- Re-including files inside an already-ignored directory is not supported —
  matches git (a parent dir exclusion cannot be overridden for descendants).

## GitHub issues log

- [#132](https://github.com/khurram-uworx/CodeMemory/issues/132) — this work
  (OPEN, milestone 0.7).
- [#140](https://github.com/khurram-uworx/CodeMemory/issues/140) — init tool
  should scaffold default `.gitignore` entries for common dep/build dirs
  (created while planning #132; deferred out of scope for this branch).
- As each task executes: any newly discovered deferred work → create a GitHub
  issue immediately (`gh issue create --repo khurram-uworx/CodeMemory`) and
  record it here. Do not rely on memory.