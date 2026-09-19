---
name: iterative-work
description: Use when completing discrete steps of multi-step work. Suggests/asks for a feature branch and switches to it, writes the plan to docs/TODO.md first, grounds the plan via microsoft-learn MCP and raises any decisions/red flags to the human (Gate G1), commits locally after each logical change unit (does NOT push), asks before running tests, runs a two-gate review (branch as a whole + against docs/TODO.md) before deleting docs/TODO.md (Gate G2) and deletes only when both clear, then offers to push and create/update a PR. Push and PRs are always human-confirmed. Escalate decisions to the human rather than assume; prefer proper engineering over quick wins.
---

# Iterative Work Workflow

When working on multi-step tasks, commit after each logical change unit so the human can review incremental progress.

Work as proper engineering, not quick wins: rigor (grounding, reviews, escalation) beats speed, and a
decision or red flag is never resolved by assumption when the human can be asked.

## Rules

1. **Commit frequently** — after completing each discrete step (bug fix, feature addition, refactor, test update, etc.)
2. **Never push** — commits are local only. The human pushes manually after reviewing changes outside the session.
3. **Write clear commit messages** — describe what changed and why, in imperative mood.
4. **Stage selectively** — only stage files relevant to the completed step, not unrelated changes.
5. **Verify before committing** — run lint/typecheck/build/tests if available before each commit.
6. **Ask before running tests** — `dotnet test` and other long-running verification commands require explicit human confirmation before starting (see AGENTS.md).
7. **One logical change per commit** — don't bundle unrelated changes together.
8. **Create GitHub issues during execution, not after** — as each task executes, if you find deferred work or a concern (known limitations, follow-ups, refactors) that is outside the current plan, create a tracked issue immediately via `gh issue create --repo khurram-uworx/CodeMemory` and record its number in the GitHub issues log in `docs/TODO.md`. Don't rely on memory or wait until the plan finishes — compaction can lose it.
9. **Involve the human at decision points** — if a plan, grounding pass, review, or failure surfaces a decision or red flag, stop and ask rather than assume — especially when the blast radius is more than trivial. Quick wins are a warning sign, not the goal.

## Probe and harness lifecycle

When the work calls for a performance measurement, an A/B comparison, or an isolated code probe:

1. **Start in a temp location** — create the harness or probe under a temp directory
   (e.g. `C:\Users\khurram\AppData\Local\Temp\opencode\`) as a standalone project, just as
   agents naturally do. Run it there and evaluate the results.

2. **Decide if it's reusable** — after the probe runs, determine whether the harness or probe
   measures something that will be needed again (a regression gate, an A/B revisited across
   branches, a future perf decision). If yes, incorporate it before committing the branch.
   If it's a one-off throwaway, leave the temp artifact for the human to clean up.

3. **Incorporate into the matching `tests/` project** — place the reusable harness/probe into
   the appropriate existing project rather than leaving it in a temp dir or creating a new
   standalone project:
   Only create a brand-new probe project when no existing category fits.

4. **Record the lifecycle in `docs/TODO.md`** — note the probe's temp origin, the reusability
   decision, and its final location, so the branch review (G2) sees the full story and future
   agents know where to extend it.

5. **Clean up the temp copy** — once the incorporated harness builds and is verified in
   `tests/`, delete the temp directory so there is no drift between the two copies.

## Plan-first workflow

Persist the plan before executing so it is saved at highest fidelity, even if context is later lost.

1. **Suggest/ask for the branch** — propose a short feature branch name (e.g., `khurram/<feature>`), ask the human to confirm, then create it off the current base (typically `main`): `git checkout -b <branch>`. Do not proceed until the human confirms the branch.
2. **Write the plan to `docs/TODO.md` first** — document the problem, proposed changes (with code sketches where useful), verification steps, planned commit list, and a **GitHub issues log** (see below). Include the reminder: as each task executes, if you find deferred work or a concern, create a GitHub issue immediately (`gh issue create --repo khurram-uworx/CodeMemory`) and record its number in the log — don't rely on memory or wait until the end of the plan, as compaction during execution can lose important items. Commit it as its own logical unit (`docs: plan <work> in TODO.md`).
3. **Ground the plan (Resistance G1)** — immediately after the plan commit, and before any implementation, ground the plan via the microsoft-learn MCP server and codebase navigation (code-memory MCP), and state the blast radius (see "Grounding the plan"). If grounding surfaces any decision to make, any red flag, or any assumption that needs resolving, **stop and involve the human** — never resolve it by assumption. Implementation begins only after the human clears the plan (as written or amended).
4. **Execute iteratively** — complete one logical change at a time, committing after each (see Workflow below). Ask before running `dotnet test`.
5. **Review and conclude (Resistance G2)** — before deleting `docs/TODO.md`, run the two pre-deletion reviews — (a) the branch work as a whole, and (b) the branch work against `docs/TODO.md` (see "Branch review (from a distance)"). Only when both clear, remove the plan and commit the removal (`git rm docs/TODO.md` → `docs: remove TODO.md — plan executed`). If a review surfaces an issue, fix it as an additional commit and re-run both reviews; if a decision is needed with more than trivial blast radius, involve the human. Leave the plan in place only if an item is still pending.
6. **Offer push + PR** — report the completed work, then offer to push the branch and create (or update) a pull request. Ask explicitly; do not push or open a PR without the human's confirmation. Push remains human-controlled by default.

## Resistance gates (proper engineering, not quick wins)

Two hard gates check the default fast path. Their whole point is to stop the work at the two moments
where an agent is most tempted to skip rigor: before implementation (planning) and before declaring
done (plan deletion).

- **G1 — Grounding gate (right after the plan is committed).** Ground the plan in official
  documentation and examples via the microsoft-learn MCP server, navigate the codebase with
  code-memory MCP, and state the blast radius. If grounding surfaces any decision to make, any red
  flag, or any assumption baked into the plan, **stop and involve the human** — never resolve it by
  assumption. Implementation begins only after the human clears the plan (as written or amended).
- **G2 — Pre-deletion review gate (right before `docs/TODO.md` is deleted).** Run two reviews of the
  work that just landed: (a) the branch work as a whole, and (b) the branch work against
  `docs/TODO.md` item by item. Only when both clear may the plan be deleted and the work concluded.
  If a review surfaces an issue, fix it as an additional commit and re-run both reviews. If a
  decision is needed with more than trivial blast radius, involve the human — do not take the
  assumption yourself.

## Branch review (from a distance)

This is the work that clears **G2** — it runs on the branch, **before** `docs/TODO.md` is deleted and
therefore before anything is pushed. The branch is the cheapest place to catch problems: the diff is
compact, one logical change per commit, and nothing has hit `main`. It is a fresh, critical pass —
not a re-run of step-by-step work.

### (a) The branch work as a whole

1. **Re-read each issue spec** — `gh issue view <n> --repo khurram-uworx/CodeMemory` and compare the problem/root cause/acceptance criteria against the corresponding commit. Ask per issue:
   - Is the root cause actually fixed (not just the symptom)?
   - Are **all** acceptance criteria met, literally?
   - Is the fix minimal and scoped, with no unrelated changes bundled in?
2. **Inspect each commit's diff** — `git show <hash>` / `git diff main...<branch> --stat`. Look for:
   - Edits that are dead code (defined but never called) or leave similar dead code behind.
   - Silent failure modes the fix claims to eliminate but doesn't fully cover (e.g. a widened accumulator that can still wrap, a cache key that can still collide, a per-chunk path left ungated).
   - Correctness under chunked/reused/cached execution (offsets, masks, shared cached delegates) — not just the whole-column case.
3. **Verify the tests pin the acceptance criteria** — do the added tests actually assert the failure mode from the issue (e.g. the exact overflow/collision case), or just a happy path? Are edge cases (chunked paths, cross-backend parity, masked backing values) covered?

### (b) The branch work against `docs/TODO.md`

Read the plan top to bottom and map every item to landed, verified work:

1. **Problem** — does each stated problem correspond to what the branch actually addresses?
2. **Proposed changes** — does the landed diff match the plan's shape (type dispatch, kernels, fallbacks) with no silent drift?
3. **Verification steps** — was each planned verification run, and did it pass (build, targeted tests, full suite)?
4. **Planned commits** — does the branch log contain every planned commit, plus only additive fix/test commits?
5. **Blast radius** — does the documented blast radius still match reality after implementation?
6. **GitHub issues log** — every captured issue is still tracked (not lost), and still accurate.

### Completing the gate

- **Fix before deleting** — if either review surfaces a real gap, fix it properly (regression test + CHANGELOG if the public contract changed) as an **additional commit** on the same branch, then re-run both reviews. Breaking changes are acceptable while the library is early — prefer correctness over preserving a wrong contract, but if the blast radius is more than trivial, confirm the direction with the human first.
- **Escalate decisions** — never resolve a review finding by assumption. If the right call is not unambiguous, stop and ask the human.
- **Dispose of residual concerns** — anything real but out of scope → create a GitHub issue immediately (`gh issue create --repo khurram-uworx/CodeMemory`) and record it in the issues log; never hold it in memory.
- **Conclude** — only once both reviews clear does the workflow delete `docs/TODO.md` and move on to "Offer push + PR".

## GitHub issues log

Include this section in every `docs/TODO.md` plan and keep it updated as work progresses:

```
## GitHub issues log

- [ ] #NNN — one-line description (created while working on <task>)
```

- Create the issue at discovery time via `gh issue create --repo khurram-uworx/CodeMemory`, then record the issue number here.
- If the issue already exists, reference its number instead of creating a duplicate.
- The log also feeds the final review — confirm each captured issue is still relevant before removing `docs/TODO.md`.

## Grounding the plan

- **Tensors/Vectors are new in .NET** — always ground the implementation plan and strategy in official documentation and examples fetched via the microsoft-learn MCP server (`microsoft_docs_search`, `microsoft_code_sample_search`, `microsoft_docs_fetch`). This is **Resistance G1**: it runs immediately after the plan is committed and before any implementation, and anything it surfaces that needs a decision is escalated to the human — never resolved by assumption.
- **Navigate source with code-memory MCP** — when exploring the codebase for planning or design, prefer the code-memory MCP tools (symbols, relationships, call graphs, impact analysis, you will love its sql tool) over ad-hoc grepping.
- **Assess blast radius** — for every plan, identify and state the blast radius: what files/components/symbols each change affects, what depends on them (downstream callers), and which tests cover them. Include this in `docs/TODO.md` so the human can judge risk before execution.

## Commit Message Format

```
<short summary in imperative mood>

<optional body explaining why and what changed>
```

Examples:
```
Fix NaN in Adam optimizer from uninitialized ArrayPool buffers

ArrayPool.Rent() does not zero buffers. First step used garbage
values for expAvg/expAvgSq, producing NaN in weight updates.
Added AsSpan(0,size).Clear() after every Rent in all optimizers.
```

```
Add oProj output projection to TransformerBlock forward pass

MultiHeadAttention result was bypassing the output projection,
going directly to the residual add. oProj was allocated but
never called, resulting in null gradients for the parameter.
```

## Workflow

1. Complete the step (code change + build verification)
2. Ask the human before running `dotnet test` or any long-running verification command
3. `git status` to see changed files
4. `git diff` to review changes
5. Capture deferred work — scan the step for anything worth doing later (limitations, follow-ups, concerns). If found, create a GitHub issue now (`gh issue create --repo khurram-uworx/CodeMemory`) and record it in the GitHub issues log in `docs/TODO.md`; never hold it in memory.
6. `git add <specific files>` — stage only the files for this step
7. `git commit -m "<message>"`
8. Report to the human what was committed (without pushing)
9. Continue to next step

## When a test fails

A failing test is an opportunity to assess the root cause properly — not a signal to patch around it. Before changing anything:

1. **Diagnose first** — determine whose expectations are wrong:
   - Did **our change** introduce a regression (a bug in the code)?
   - Did the **design/contract** change (feature, API, or behavior intentionally changed, so the old expectation no longer holds)?
   - Is the **test stale** — asserting behavior we superseded?
2. **If our code change is the problem**, fix the code — don't edit the test to make it pass.
3. **If the expectation legitimately changed** (design change, new contract), update the test to assert the new correct behavior and record the intent in the commit message / `docs/TODO.md`.
4. **Don't rush quick fixes** — approach it the right engineering way, even if it takes longer.

## Test coverage balance

- **Existing tests are the consistency guardrail** — as we progress and sometimes break things, the existing suite is how we catch regressions and stay consistent. When adding something new, keep existing tests green unless a contract deliberately changed (see "When a test fails").
- **Cover new work with good-enough tests** — enough to verify the new behavior and its edge cases, but not so much that tests become brittle or a maintenance chore. Prefer extending the existing suite over adding redundant new tests.
- **Keep tests focused and readable** — assert behavior, not implementation details; over-testing internals makes refactors painful.

## What NOT to do

- Do NOT use `git push` at any point
- Do NOT amend previous commits unless explicitly asked
- Do NOT use interactive rebase or squash
- Do NOT commit secrets, keys, or credentials
- Do NOT commit generated files or build artifacts
