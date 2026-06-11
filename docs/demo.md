# Agent Cop Demo

This demo shows three capabilities of cop: enforcing rules on agent-generated code, running multi-language analysis, and computing code slop scores.

## Prerequisites

```bash
# cop must be on PATH (install from GitHub releases or build locally)
cop --version
```

## Part 1: Agents Adding Rules

You can ask an agent to add analysis rules to any project. This enforces coding standards on agent-generated (or human-written) code.

**Prompt an agent:**

> Add a rule to disallow unsealed types

**Target:** `C:\demos\copdemo`

The agent creates a `.cop` file in the project's `cop-checks/` folder:

```cop
import csharp
import code-analysis

predicate isUnsealedClass(Type) => Type.Kind == 'class' && Type.IsSealed == false

export let unsealed-violations = csharp.Types:isUnsealedClass
    :toError('{item.Name} must be sealed')
```

**Run:**

```bash
cop cop-checks/main.cop -t C:\demos\copdemo
```

The rule catches any unsealed class in the project. Agents can add, modify, or compose rules — no manual .cop authoring required.

## Part 2: Multi-Language Analysis

Run built-in checks on the cop repo — no extra flags needed:

```bash
cop csharp-checks -t C:\git\cop
```

```
cop/cli/Program.cs(42): 1: Do not use 'var' for parser
cop/runtime/Engine.cs(115): 1: Do not use 'var' for result
```

Now show that cop can analyze multiple languages in one pass — C# and Python together:

```cop
import csharp
import python
import code-analysis

# Unified codebase from both providers
let codebase = codebase(csharp, python)

# Rule: flag types with too many methods
predicate hasTooManyMethods(Type) => Type.Methods.count() > 20
let violations = codebase.Types:hasTooManyMethods
    :toWarning('Type {item.Name} has {item.Methods.count()} methods (max 20)')

command main = CHECK(violations)
```

```bash
cop my-checks.cop -t C:\git\cop
```

This analyzes both C# and Python code in one pass, reporting violations from either language.

## Part 3: Computing Code Slop

The `code-metrics` package computes an aggregate slop (code quality) score:

```bash
cop code-metrics -t C:\git\cop -p csharp
```

**Output:**

```json
{
  "totalViolations": 102,
  "errors": 98,
  "warnings": 4,
  "info": 0,
  "weightedScore": 80.4,
  "linesOfCode": 39503,
  "slopPerKloc": 2.58,
  "weightedSlopPerKloc": 2.04
}
```

Key metrics:
- **slopPerKloc** — raw violation density (violations per 1000 lines of code)
- **weightedSlopPerKloc** — severity-weighted density (higher severity violations count more)

Lower scores indicate cleaner code. Track this over time to prevent quality regression.

