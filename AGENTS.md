# CodeMemory — AGENTS.md

## Purpose

Engineering constraints and implementation guidance for AI coding agents contributing to CodeMemory.

**First read [ARCHITECTURE.md](ARCHITECTURE.md)** for the system architecture, data flow, dependency layering, storage schema, and current limitations.

---

## Core Principle

Do not reinvent infrastructure. Prefer existing .NET and ecosystem primitives over custom solutions.

Forbidden: custom LLM clients, custom embedding pipelines (use `IEmbeddingGenerator`), custom DI, custom vector DBs, custom chat orchestration.

---

## MCP-First Design

MCP is the **only** external interface. Every feature MUST be exposed as an MCP tool.

Tool rules:
- deterministic where possible, return structured JSON
- no freeform prompting or unstructured text blobs inside tool logic
- composable (tools should work independently and together)

---

## AI Agent Behavior

- Prefer composition over new frameworks
- Reuse .NET primitives first
- Document reasoning for non-standard decisions
- Default to: `Microsoft.Extensions.AI`, `VectorData` abstractions, MCP exposure

---

## Extensibility

New features MUST:
- extend the MCP tool surface
- reuse existing abstractions
- not introduce parallel frameworks

---

## Non-Goals

CodeMemory is NOT: an IDE, a chat assistant, a code generator, or a standalone AI agent runtime.

It IS: a repository intelligence and memory substrate exposed via MCP. Includes dependency graphs, architecture overviews, component clustering, and git history — all accessible through MCP tools.

---

## Long-Term Vision

All contributions must reinforce:

> A persistent, queryable, semantic memory layer for software systems.

Anything that does not improve repository cognition is out of scope.

---

## Task Format

Use `docs/TASKS-TEMPLATE.md` for new task breakdowns. Each task must include: Priority, Goal, Scope, Acceptance Criteria, Files Likely Involved.

---

## Summary

When uncertain: use `Microsoft.Extensions.AI` abstractions, expose via MCP, avoid reinventing infrastructure, and prioritize repository understanding over feature expansion. Architecture intelligence services (`DependencyGraphService`, `ArchitectureService`, `ComponentClusteringService`, `GitHistoryService`) follow the same patterns — compose existing abstractions, register in `Program.cs`, expose via MCP tools.
