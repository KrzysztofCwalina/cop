# Magic Audit: Hardcoded Identifiers & Domain-Specific Behavior

This document tracks places where the C# engine/interpreter/CLI has hardcoded knowledge of domain-specific concepts. The goal is for cop to be a general-purpose runner where the interpreter has **no hardcoded identifiers** — all domain concepts belong in packages.

---

## Problems To Fix

No active problems. The engine/interpreter is domain-agnostic — all domain concepts live in packages.

---

## Design Principle

The interpreter and CLI should be **completely domain-agnostic**. No hardcoded identifiers. All domain concepts (violations, checks, severity levels, error/warning/info) must live in `.cop` packages. The engine should provide general mechanisms (e.g., "this function is terminal", "this let produces output") that packages can use, without the engine knowing what the output *means*.