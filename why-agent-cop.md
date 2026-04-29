# Why Agent Cop

## The Problem: Code Slop Is Blocking Agent Adoption

Coding agents promise a revolution in developer productivity. In practice, the #1 blocker to their adoption in enterprise development shops is **code slop** — code that technically works but violates architectural constraints, ignores team conventions, introduces subtle inconsistencies, and accumulates design debt at machine speed.

## The Bottleneck: Architects Can't Scale

Junior developers using coding agents now produce **10x–100x the volume of code** they could write manually. But quantity without quality is a liability. Architects and senior engineers overseeing these projects feel compelled to review all agent-generated code for slop — creating a crushing bottleneck that negates the productivity gains agents were supposed to deliver.

The math doesn't work: one architect cannot meaningfully review 100x the output. Teams either slow down to pre-agent velocity (defeating the purpose) or let slop accumulate (creating a maintenance crisis).

## Why Current Guardrails Fail

Teams are attempting to solve this with agent-level instructions — Copilot instructions files, system prompts, custom rules embedded in chat context. These help initially, but **agents inevitably drift**. Instructions are advisory, not enforced. There is no compiler error when an agent ignores a convention. The architect still can't trust the output without manual review.

The fundamental issue: natural language instructions lack the deterministic enforcement that code quality demands.

## The Solution: Static Analysis for the Agent Era

We propose **Agent Cop** — a static analysis tool purpose-built to detect and measure code slop. Just as compilers catch syntax errors and type checkers catch type errors, Agent Cop catches *architectural and convention errors* that agents routinely introduce.

### How It Works

1. **Deterministic enforcement**: Agent Cop runs as a build step. Violations are hard failures — not suggestions. Agents cannot ignore them any more than they can ignore a compiler error.

2. **Closed-loop remediation**: Violation output feeds directly back to the coding agent in the same format as compiler errors. The agent sees "file(line): error: Client types must be sealed" and fixes it automatically — no human in the loop.

3. **Built-in + customizable**: Agent Cop ships with checks for common architectural and code quality rules (naming, error handling, API design patterns). For organization-specific requirements, a purpose-built DSL lets architects express custom rules that are as enforceable as the built-in ones.

### Architects Write Formal Specifications — In Minutes, Not Sprints

The key insight: architects already *know* the rules — they just have no way to encode them into something deterministic. Today they write wiki pages, leave PR comments, and repeat themselves in every review. Agent Cop gives them a direct path from architectural intent to a **formal specification** that is enforced automatically.

For example, an architect building a web application who wants "all controller actions must validate input" or "never call the database directly from a controller" doesn't need to file a tooling request or wait for a custom analyzer. They write a short formal specification:

```
predicate controller-direct-db-call(Statement) =>
    Statement.File.Path contains 'Controllers'
    and Statement.TypeName in ['DbContext', 'SqlConnection', 'IDbCommand']

CHECK controller-db-calls => error('Controllers must not call the database directly — use a service layer')
```

This is the entire specification — not a plugin, not a code review checklist item, not a Copilot instruction that may be ignored. It runs in CI, blocks the PR, and tells the agent exactly what to fix. The architect writes it once; it's enforced forever.

Another common example — enforcing dependency direction in a layered architecture:

```
import arch-layering

let ui-projects   = ['MyApp.Web', 'MyApp.Api']
let data-projects = ['MyApp.Data', 'MyApp.EF']

predicate uiReferencesData(Project) =>
    Project.Name:in(ui-projects) && Project.References:containsAny(data-projects)

CHECK arch-layering => Code.Projects:uiReferencesData:toError('UI must not reference Data directly')
```

Agents routinely violate layering constraints because they optimize for "make it work" — they'll add whatever project reference gets the code to compile. A formal specification catches this instantly and forces the agent to route through the correct abstraction. It also flags new projects that haven't been assigned to a layer — preventing architectural drift as the codebase grows.

The DSL is intentionally a **formal specification language** — readable by any developer, writable by any architect — because the goal is to make expressing architectural intent as easy as expressing it in a code review comment, but with the determinism and permanence of a compiler check.

## The Strategic Opportunity: Benchmarking Agents

Agent Cop creates a **quantitative framework for measuring agent quality**. By running the same architect-defined checks against code produced by different agents (GitHub Copilot, Claude, Codex, etc.), we can:

- Produce objective pass/fail results that compare how well each agent respects architectural rules
- Demonstrate that **Microsoft Copilot produces fewer violations** than competitors
- Give customers a data-driven reason to choose Copilot — not just speed, but *quality*

This shifts the competitive narrative from "which agent writes code fastest" to "which agent writes code that passes the checks architects care about."

## Summary

| | Without Agent Cop | With Agent Cop |
|---|---|---|
| **Agent output** | Unchecked, requires manual review | Deterministically validated |
| **Architect role** | Bottleneck reviewer of all code | Defines rules once, trusts enforcement |
| **Feedback loop** | Human reviews → files issue → agent may fix | Tool detects → agent auto-fixes |
| **Agent comparison** | Subjective ("feels better") | Objective: passes or fails architect-defined checks |
| **Copilot positioning** | Feature parity race | Quality differentiation |

## Prototype

A working prototype demonstrating the core static analysis engine and DSL is available:

🔗 [Agent Cop Prototype](https://github.com/KrzysztofCwalina/cop/blob/master/docs/static-analysis-with-cop.md)

The prototype already supports multi-language analysis (C#, Python, JavaScript), built-in check packages, custom rule authoring via DSL, and CI integration with standard exit codes.
