# Squad Description: Addressing Code Slop

**Squad Name:** Addressing Code Slop

**Description:** Deterministic enforcement of architectural rules and conventions for code produced by coding agents

**Cycle / Dates:** June 2026 (3-week sprint)

**DRI:** Krzysztof Cwalina

**Workstream / Area:** Copilot Quality

---

## Mission

Ship Agent Cop (`cop.exe`) — a static analysis tool that detects and prevents code slop in agent-generated code. The tool serves two audiences: (1) developers using coding agents who need deterministic enforcement of architectural rules and conventions, and (2) developers building coding agents who need to evaluate and benchmark slop rates across agents and models.

Success means: by end of cycle, cop.exe is published as a downloadable release with built-in check packages for C#, Python, and JavaScript, usable both as a CI enforcement step for development teams and as a quality evaluation framework for agent builders.

---

## Business Impact

Coding agents promise a revolution in developer productivity — but in practice, the #1 blocker to enterprise adoption is **code slop**: code that technically works but violates architectural constraints, ignores team conventions, and accumulates design debt at machine speed.

Today's guardrails (Copilot instructions files, system prompts, custom rules in chat context) are advisory — agents drift from them, and architects still can't trust output without manual review. The fundamental issue: natural language instructions lack the deterministic enforcement that code quality demands.

This squad closes that gap by:

- **Eliminating the architect bottleneck:** Architects define rules once in a formal specification; enforcement is automatic. No more reviewing 100x the code volume manually.

- **Closing the feedback loop:** Violations feed back to agents in compiler-error format. Agents auto-fix — no human in the loop.

- **Enabling objective agent comparison:** The same architect-defined checks run against code from different agents (Copilot, Claude, Codex), producing quantitative pass/fail results that measure which agent respects architectural rules best.

- **Shifting the competitive narrative:** From "which agent writes code fastest" to "which agent writes code that passes the checks architects care about" — positioning Copilot as the quality leader.

---

## Success Metrics

**Primary:**

- cop.exe published as a self-contained downloadable release (GitHub Releases) for Windows, Linux, and macOS
- Built-in check packages ship for C#, Python, and JavaScript covering common architectural and convention rules
- cop.exe successfully used in at least one eval run to measure agent code quality

**Secondary:**

- End-to-end CI workflow demonstrated: cop runs as a build step, detects violations, agent auto-remediates
- At least one external team (or internal pilot) can install cop and write a custom check in <10 minutes
- Documentation (README, language reference, package reference) is complete enough for self-service onboarding

**Validation:**

- cop.exe detects real violations in real agent-generated code (not just synthetic examples)
- Violation output is actionable: an agent receiving cop output can fix the issues without human guidance
- Agent comparison mode produces meaningful quality differentiation between models

---

## Scope

### In Scope

- Publish cop.exe as a self-contained release for win-x64, linux-x64, and osx-arm64 (no .NET runtime required)
- Ship built-in check packages: `csharp-checks`, `python-checks`, `javascript-checks` with common rules (naming, formatting, error handling, API patterns)
- Core DSL features: predicates, filters, `CHECK` violations, `ASSERT` testing, package imports, custom rules
- Multi-language source analysis: C#, Python, JavaScript/TypeScript via built-in providers
- CI integration with standard exit codes (0 = clean, 1 = violations, 2 = fatal error)
- Eval/benchmark mode: run checks against agent-generated code to quantify slop rates
- Documentation: getting started, language reference, package reference, CI integration guide
- VS Code extension for syntax highlighting and completions

### Out of Scope

- Full IDE integration (real-time diagnostics, code actions) — future enhancement
- Agent-specific integrations (Copilot Chat plugin, Claude tool-use) — future enhancement
- Hosted service or SaaS version — cop is a local CLI tool
- Building a violation dashboard or trend tracking UI
- Modifying coding agents themselves — cop surfaces issues, agent teams own the fixes
- Cross-run regression detection or historical trending (future cycle)

---

## Plan and Milestones

### Week 1: Release Readiness

- Harden cop.exe for public distribution (error messages, edge cases, graceful failures)
- Finalize built-in check packages for C#, Python, and JavaScript
- Set up GitHub Releases pipeline (automated build → publish self-contained binaries)
- Write/polish user-facing documentation (README, quick start, CI integration)

**Deliverable:** cop.exe downloadable from GitHub Releases; built-in checks pass on real-world repos

### Week 2: Eval Integration

- Define eval protocol: how cop measures slop in agent-generated code (input: repo + checks, output: violation counts and details)
- Run cop against actual agent-generated code from compete/eval runs
- Produce first "agent quality report" comparing violation rates across models
- Tune checks to minimize false positives on real agent output

**Deliverable:** cop.exe used in at least one eval cycle; agent quality comparison data produced

### Week 3: Closed-Loop Demonstration

- Demonstrate full loop: cop detects violation → output fed to agent → agent auto-fixes → cop re-runs clean
- Document the "architect workflow": write a custom check, run it in CI, agent self-remediates
- Polish eval workflow and document how agent teams can use cop to benchmark their models
- Identify and document next-cycle investments (IDE integration, more packages, hosted eval)

**Deliverable:** End-to-end demo of both use cases (enforcement + eval); documented workflow for both audiences

---

## Dependencies and Risks

### Dependencies

- Access to agent-generated code from compete/eval runs (ATIF trajectories or repo snapshots) for validation
- GitHub Releases infrastructure for publishing binaries
- Cooperation from at least one team to pilot cop in a real CI pipeline (optional but desirable)
- Clarity on eval framework integration points (how cop output feeds into quality scoring)

### Risks

- **Signal quality:** Built-in checks may have high false-positive rates on real codebases initially; tuning requires real-world feedback
- **Language coverage gaps:** Provider-based source analysis (AST parsing via regex/heuristic) may miss edge cases in complex codebases; this is acceptable for v1 but needs documentation
- **Adoption friction:** If cop's DSL feels too unfamiliar, architects won't write custom rules; mitigate with excellent documentation and examples
- **Solo execution risk:** Single-person squad means no parallelism; must ruthlessly prioritize and cut scope if needed
- **Eval integration:** May depend on specific output formats or APIs from the eval pipeline that aren't documented yet

---

## Squad Composition

| Name | Role | Discipline | Allocation | Notes |
|------|------|-----------|------------|-------|
| Krzysztof Cwalina | Core | Engineering (Principal) | Full | DRI, architecture, implementation, documentation |

---

## Definition of Done

The squad is successful when:

1. **cop.exe is published** as a self-contained release on GitHub Releases for Windows, Linux, and macOS — anyone can download and run it without installing .NET

2. **Built-in checks ship** for C#, Python, and JavaScript covering naming conventions, error handling patterns, and common architectural rules — a team can run `cop csharp-checks` on their repo and get meaningful results

3. **Custom rules work end-to-end** — an architect can write a `.cop` file expressing a custom rule, run it locally and in CI, and get deterministic pass/fail results

4. **Eval use case is demonstrated** — cop is used against agent-generated code to produce quantitative quality scores, with at least one comparison across models

5. **Closed-loop remediation works** — violation output from cop is fed to a coding agent, the agent fixes the violations, and re-running cop shows a clean result

6. **Documentation is self-service** — a new user can install cop, run built-in checks, and write a custom check without asking for help (validated by at least one person going through the flow)
