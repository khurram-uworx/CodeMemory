---
name: plan-issue
description: Use when planning a GitHub issue before implementation. Takes a GitHub issue for the CodeMemory repo (number, URL, or pasted title/body), fetches it via gh, maps the affected code through the code-memory MCP (symbols, relationships, call graphs, blast radius, you will love its sql tool), grounds API/behavior claims in official docs via the microsoft-learn MCP, and produces an execution-ready plan document. Front-load triggers: "plan issue", "plan #N", "plan github issue", "plan <title>". Read-only planning — execution hands off to the iterative-work skill. Do NOT use for general codebase questions, feature ideas without an issue, or execution work.
---

# Plan-Issue Workflow

Turn a GitHub issue into an execution-ready, evidence-grounded implementation plan
without touching the source tree. Planning is read-only: fetch the issue, map the
affected code through the code-memory MCP, ground API/behavior claims in official
documentation via the microsoft-learn MCP, cross-check the live ADR and roadmap
documents, and write a plan document. Execution is handed off to the `iterative-work`
skill.

## Ground rules

1. **No assumptions.** Every claim about code must trace to evidence:
   - Code claims → a symbol (FullName) via the code-memory MCP, or `file:line` via Read.
   - API/behavior claims → an official microsoft-learn URL + title fetched during planning.
   - Anything unverifiable → mark it **UNKNOWN** and add it to the decision gates. Never guess.
2. **Tasteful engineering, not quick fixes.** Explore design options with tradeoffs; reuse
   existing kernels/utilities/patterns; align with repo conventions (AGENTS.md, `.editorconfig`,
   `docs/adr/`, `docs/plan/`, CHANGELOG.md). If the issue deserves a bigger, correct design
   (a shared kernel, an ADR, a full phase), say so — that is the point of planning.
3. **Honor accepted ADRs; consult `docs/adr/` as a live folder.** Read `docs/adr/` fresh each
   session (new ADRs may have been added). Never re-open an accepted decision without a
   decision gate and a tracking issue referencing the ADR's amendment process.
4. **Cross-check `docs/plan/` as a live folder.** List and read the REVIEW/ROADMAP docs relevant
   to the issue each session. Respect non-goals, note phase status and dependencies, and record
   alignment as references — do not copy roadmap content into the plan.
5. **Read-only on code.** Do not edit source, create branches, or commit during planning. The
   only artifact written is the plan document, and only with human confirmation.
6. **Ask before long-running commands** (`dotnet build`, `dotnet test`) or any write.
7. **Deferred work → follow-up issue proposals** in the plan; file them via `gh` only when the
   human confirms. During execution, `iterative-work` files them at discovery time.

## Inputs

- The issue: number (`#N`), URL, or pasted title/body. Default repo: `khurram-uworx/CodeMemory`
  (override with an explicit `--repo owner/repo` if the human provides one).
- If the human pastes a title/body without a number, first try
  `gh issue list --repo khurram-uworx/CodeMemory --search <title>`; if it is not found, plan against
  the pasted text and state explicitly in the plan that it has no issue number.

## Workflow

### Phase 0 — Scope & fetch the issue

- Fetch the issue: `gh issue view <n> --repo khurram-uworx/CodeMemory --json number,title,body,labels,state,milestone,comments,linkedPullRequests` (fall back to `--json number,title,body,labels,state` if a field is unsupported).
- Check for related work: `gh issue list --repo khurram-uworx/CodeMemory --state open --search <topic>` and `gh pr list --repo khurram-uworx/CodeMemory --state open` for duplicates or linked PRs.
- Restate the issue in your own words: explicit requirements, implicit requirements, constraints, acceptance criteria. If the issue is ambiguous, ask the human before planning.
- Read the comments — decisions and follow-ups often live there.

### Phase 1 — Map the code with the code-memory MCP

Work from the symbol level, not ad-hoc grepping:

- `get_architecture_overview` / `get_component_clusters` — locate the affected component.
- `semantic_search` — find symbols related to the issue's domain.
- For each candidate symbol:
  - `get_edit_context` — source, dependencies, and test coverage.
  - `impact_analysis` — downstream callers, affected files, affected components, and test files (this is the blast radius).
  - `trace_dependency` — upstream (what it depends on) and downstream (who calls it) chains.
  - `find_related_code` / `get_symbol_history` / `get_hotspots` — related symbols, recent churn, frequently-changed files.
  - `sql_query` — structured questions (which symbols touch a kernel, which tests cover a class, etc.).
- Read the top candidate files directly (Read) to verify details and find reusable patterns:
  existing kernels (`KernelSelector`, `TensorsHelper`, `NumericTensorKernels`, `GradKernels`),
  pooling (`BufferPool`, `ArrayPool`), storage (`ColumnStorageFactory`), diagnostics, and test
  fixtures.
- Record evidence as `Symbol` → `file:line` in a working-notes section; this becomes the plan's
  Blast radius + Grounding sections.

### Phase 2 — Ground API/behavior claims in the microsoft-learn MCP

- For each technology the design touches (e.g., `TensorPrimitives`, generic math `INumber<T>` /
  `IFloatingPointIeee754<T>`, `Span<T>`/`Memory<T>`, `ArrayPool`, readonly structs, LINQ), run:
  - `microsoft_docs_search` — official overview of the API/behavior.
  - `microsoft_code_sample_search` — idiomatic usage samples.
  - `microsoft_docs_fetch` — full pages for the high-value references.
- Check the target framework: `net10.0` with `System.Numerics.Tensors 10.0.10` (see the relevant
  `.csproj` / `Directory.Build.props`). Watch for net11-only APIs — the repo defers those
  (e.g., BFloat16 kernels, `Tensor.Transpose`/`MatrixMultiply`). If the only grounded solution is
  net11-only, flag it as a decision gate rather than assuming it ships.
- Record the URL + title for every API/behavior claim so the plan is traceable.

### Phase 3 — Design, decision gates, ADR/roadmap cross-check, scope

**Consult the live folders before designing:**

- `List docs/adr/` and `List docs/plan/` each session — new ADRs or roadmap docs may exist.
- Read the ADRs that apply to the issue's component (all AutoDiff work touches ADR-001/002/003;
  storage/validity work touches ADR-001; interop work touches ADR-001's boundary). Honor them:
  cite which apply and what they constrain. If the design would revisit an accepted decision,
  make it a **decision gate** and propose a tracking issue referencing the ADR.
- Read the relevant REVIEW/ROADMAP docs from `docs/plan/`. Identify which phase(s) the issue
  belongs to, whether it is delivered/remaining/backlog, its dependencies, and any non-goals it
  must respect. Record alignment as *references* (path + one-line constraint), not copies.

**Scope-widening offer (only in such cases):**

- If the ask is a narrow slice of a larger roadmap phase or issue cluster (e.g., one window
  function while the phase remainder is open), **offer** to widen the plan to the whole coherent
  unit — state what the wider scope covers and why it is the better engineering outcome — and
  **ask for approval before widening**. Never widen silently and never scope-creep on unrelated
  asks. If the human declines, keep the plan scoped to the small ask, planned properly.

**Then design:**

- Enumerate 2+ design options with tradeoffs; recommend one and justify it against repo
  conventions, the applicable ADRs, roadmap alignment, existing patterns, and the grounded docs
  from Phase 2.
- Identify decision gates — breaking changes, public API shape, deprecation paths,
  performance-vs-correctness, scope — and mark each "needs human decision".
- Assess blast radius explicitly: affected files, downstream callers (by symbol), affected
  components, and test files. Distinguish public-API breaks from internal-only changes.
- Check overlap with other open issues; record deferrals as follow-up proposals.

### Phase 4 — Write the plan document

Path: `docs/plan/issue-<n>-<slug>.md` (persistent, matches the `docs/plan/` convention). Confirm
the path with the human before writing. Structure:

```markdown
# <Title> — plan for issue #<n>

**Status:** proposed · **Tracker:** <issue url> · **Branch (later):** khurram/issue-<n> (created by iterative-work at execution)

## Problem
What the issue asks for + verified context (evidence: symbols / file:lines).

## Requirements
Explicit and implicit requirements; acceptance criteria; constraints.

## Design
Options with tradeoffs; recommended approach with rationale; code sketches where useful.

## Decision gates
- [ ] G1 — <question> (needs human decision; e.g., breaking change? API shape? revisit an ADR?)

## ADR & roadmap alignment
- ADRs that constrain this plan: `docs/adr/00X-*.md` — one-line constraint each (reference, not copy).
- Roadmap phases this belongs to / depends on: `docs/plan/*-ROADMAP.md` — status, non-goals respected.

## Scope decision
Narrow slice of a larger unit? Widened (approved) or kept scoped (declined) — state which and why.

## Blast radius
- Files: ... · Symbols: ... · Downstream callers: ... · Test files: ...

## Implementation tasks
Follow `docs/TASKS-TEMPLATE.md` shape (goal, scope, constraints, acceptance criteria, files likely involved, dependencies, parallelizable?).

## Verification
- `dotnet build CodeMemory.slnx` after each step; ask before `dotnet test`.
- Targeted test classes to run as regression guards.

## Planned commits
1. `docs: plan issue #<n> ...` (written by this skill on the execution branch)
2. ...

## Follow-ups / GitHub issues log
- #NNN — deferred item (proposed; file via `gh` when the human confirms)

## Grounding
- Code: Symbol → file:line for every claim.
- Docs: microsoft-learn URL + title for every API claim.
- UNKNOWN items listed explicitly (these become decision gates).

## Assumptions register
Anything not verified, marked as assumed-with-risk.
```

### Phase 5 — Post & handoff

- Report the plan in chat: problem, recommended design, decision gates, blast radius (concise).
- Offer to post the plan (or a condensed version) as a comment on the issue:
  `gh issue comment <n> --repo khurram-uworx/CodeMemory --body-file docs/plan/issue-<n>-<slug>.md`.
  Ask explicitly first.
- Hand off execution to the `iterative-work` skill (branch, `docs/TODO.md`, commits). The human
  invokes it; do not start executing in this session.

## What NOT to do

- Do NOT edit source, create branches, or commit during planning.
- Do NOT run `dotnet build`/`dotnet test` without asking.
- Do NOT invent APIs, behaviors, or blast radius from memory — every claim is evidence or UNKNOWN.
- Do NOT copy ADR or roadmap content into the skill or the plan; reference the live docs.
- Do NOT re-open an accepted ADR without a decision gate and a tracking issue.
- Do NOT widen scope without explicit human approval.
- Do NOT rush a minimal fix when the issue deserves a properly designed solution.
