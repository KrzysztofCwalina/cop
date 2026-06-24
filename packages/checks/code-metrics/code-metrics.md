# Code Metrics

Computes weighted code quality metrics (the "slop score") from a curated collection of high-confidence violations.

## Quick Start

```cop
import code
import csharp
import code
import code-metrics

let cb : Codebase = provider('csharp')

command main = METRICS(slop, cb.Lines)
```

The `slop` collection contains violations from high-confidence rules with severity values reflecting their importance (resource leaks = 1.0, naming = 0.3).

## Excluding Generated Code

```cop
import code
import csharp
import code
import code-metrics

let cb : Codebase = provider('csharp')

predicate isSourceLine(Line) => Line.File.Path:contains('Generated') == false
predicate isSourceViolation(Violation) => Violation.File:contains('Generated') == false

let source-lines = cb.Lines:isSourceLine
let source-violations = slop:isSourceViolation

command main = METRICS(source-violations, source-lines)
```

## Custom Rules and Weights

```cop
import code
import csharp
import csharp-checks
import code
import code-metrics

let cb : Codebase = provider('csharp')

# Add a custom rule with explicit severity (0.7) and certainty (0.9)
predicate isTooLong(Method) => Method.Statements.count() > 50
let long-methods = cb.Types:Methods:isTooLong
    :toViolation('Method {item.Name} too long', 0.7, 0.9)

# Override severity of an existing rule
let heavy-var = var-declarations:withSeverity(0.8)

# Combine
let my-slop = slop + long-methods + heavy-var

command main = METRICS(my-slop, cb.Lines)
```

## Output

```json
{
  "totalViolations": 42,
  "errors": 5,
  "warnings": 30,
  "info": 7,
  "weightedScore": 28.5,
  "linesOfCode": 10000,
  "slopPerKloc": 4.2,
  "weightedSlopPerKloc": 2.85
}
```

## Fields

| Field | Description |
|-------|-------------|
| `totalViolations` | Total number of violations |
| `errors` | Violations with Severity >= 0.7 |
| `warnings` | Violations with Severity 0.4–0.7 |
| `info` | Violations with Severity < 0.4 |
| `weightedScore` | Sum of all violation severities |
| `linesOfCode` | Lines of code (excludes blanks and comments) |
| `slopPerKloc` | Raw violations per 1000 lines of code |
| `weightedSlopPerKloc` | Weighted score per 1000 lines of code |

## Severity Scale

| Range | Label | Examples |
|-------|-------|----------|
| >= 0.9 | critical | Resource leaks, deadlocks |
| 0.7–0.9 | error | Swallowed exceptions, banned throws |
| 0.4–0.7 | warning | Thread.Sleep, naming conventions |
| < 0.4 | info | Field casing, style |

## Built-in Slop Rules

The `slop` collection includes these high-confidence rules:

| Rule | Severity | Category |
|------|----------|----------|
| undisposed-new | 1.0 | Resource leak |
| sync-over-async-calls | 0.9 | Deadlock risk |
| sync-wait-in-async | 0.9 | Deadlock risk |
| swallowed-exceptions | 0.8 | Silent failures |
| no-swallow-cancellation | 0.8 | Cancellation broken |
| no-banned-throws | 0.8 | Too-broad exception |
| thread-sleep-calls | 0.6 | Blocks thread pool |
| no-public-fields | 0.5 | Encapsulation |
| interface-prefix | 0.5 | Naming |
| type-name-casing | 0.5 | Naming |
| method-name-casing | 0.5 | Naming |
| exception-suffix | 0.5 | Naming |
| attribute-suffix | 0.5 | Naming |
| param-camel-case | 0.3 | Naming |
| private-field-naming | 0.3 | Naming |
| static-field-naming | 0.3 | Naming |
| const-naming | 0.3 | Naming |

## Notes

- `weightedSlopPerKloc` is the primary metric — it accounts for relative importance of violations.
- `slopPerKloc` treats all violations equally (backward-compatible).
- Lines of code excludes blank lines and comment-only lines.
- The `slop` collection can be filtered, extended, or replaced entirely.

