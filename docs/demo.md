# Agent Cop Demo

This demo shows three capabilities of cop: running existing analysis packages, computing slop scores, and writing custom analysis rules.

## Prerequisites

```bash
# cop must be on PATH (install from GitHub releases or build locally)
cop --version
```

## Part 1: Run Built-in Checks (No Code Required)

Run the `csharp-checks` package directly — no .cop file needed:

```bash
cop csharp-checks -t <folder> -p csharp
```

The `-p csharp` flag explicitly loads the C# provider. The package analyzes C# code and reports violations.

**Example output:**

```
src/Parser.cs(42): 1: Do not use 'var' for parser
src/Engine.cs(115): 1: Do not use 'var' for result
```

## Part 2: Computing Slop Score

The `code-metrics` package computes an aggregate slop (code quality) score as JSON:

```bash
cop code-metrics -t <folder> -p csharp
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

## Part 3: Writing a Custom Rule

Create `my-checks.cop` — this demonstrates analyzing a mixed-language codebase:

```cop
import csharp
import python
import javascript
import code-metrics

# Create a unified codebase from all language providers
let codebase = codebase(csharp, python, javascript)

# Custom rule: flag types with too many methods (god classes)
predicate hasTooManyMethods(Type) => Type.Methods.count() > 20
let large-types = codebase.Types:hasTooManyMethods
    :toViolation('Type {item.Name} has {item.Methods.count()} methods', 0.6, 0.95)

# Combine with built-in slop
let my-slop = slop + large-types

command main = METRICS(my-slop, codebase.Lines)
```

**Run:**

```bash
cop my-checks.cop -t <folder>
```

**Output** (scores are higher due to additional violations):

```json
{
  "totalViolations": 126,
  "errors": 98,
  "warnings": 28,
  "info": 0,
  "weightedScore": 99.8,
  "linesOfCode": 39503,
  "slopPerKloc": 3.19,
  "weightedSlopPerKloc": 2.53
}
```

