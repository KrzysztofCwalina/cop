# Code Metrics

Computes aggregate code quality metrics (the "slop score") from a collection of violations and outputs them as a JSON object.

## Usage

```cop
import code
import csharp
import code-analysis
import code-metrics

let cb = code.codebase('csharp')
let violations = my-checks  # any [Violation] collection

command main = METRICS(violations, cb)
```

## Output

The `METRICS` command outputs a JSON object:

```json
{
  "totalViolations": 42,
  "errors": 5,
  "warnings": 30,
  "info": 7,
  "linesOfCode": 10000,
  "slopPerKloc": 4.2,
  "errorsPerKloc": 0.5,
  "warningsPerKloc": 3.0,
  "infoPerKloc": 0.7
}
```

## Fields

| Field | Description |
|-------|-------------|
| `totalViolations` | Total number of violations |
| `errors` | Count of error-severity violations |
| `warnings` | Count of warning-severity violations |
| `info` | Count of info-severity violations |
| `linesOfCode` | Lines of code (excludes blanks and comments) |
| `slopPerKloc` | Total violations per 1000 lines of code |
| `errorsPerKloc` | Errors per 1000 lines of code |
| `warningsPerKloc` | Warnings per 1000 lines of code |
| `infoPerKloc` | Info per 1000 lines of code |

## Notes

- The `slopPerKloc` is the primary "slop score" — lower is better.
- Lines of code excludes blank lines and comment-only lines.
- If lines of code is zero, all per-KLOC rates are reported as 0.
