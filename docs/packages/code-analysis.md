## code-analysis

Structured violation reporting for source code checks. &nbsp; `import code-analysis`

**Source:** [`packages/code-analysis/src/code-analysis.cop`](../../packages/code-analysis/src/code-analysis.cop) &nbsp; **Depends on:** code, filesystem

---

### Types

#### Violation

| Property | Type | Description |
|---|---|---|
| `Severity` | string | `'error'`, `'warning'`, or `'info'` |
| `Message` | string | Human-readable violation message |
| `File` | string | Path to the file containing the violation |
| `Line` | int | Line number (0 for folder-level violations) |
| `Source` | string | Source code text |

---

### Functions

Each function has overloads for [`Statement`](code.md#statement), [`Type`](code.md#type), [`Line`](code.md#line), and [`Folder`](filesystem.md#folder). The `message` parameter supports template interpolation (e.g., `'Missing docs for {item.Name}'`).

| Function | Description |
|---|---|
| `toError(item, message)` | Creates a Violation with severity `'error'` |
| `toWarning(item, message)` | Creates a Violation with severity `'warning'` |
| `toInfo(item, message)` | Creates a Violation with severity `'info'` |

---

### Commands

| Command | Description |
|---|---|
| `CHECK(violations)` | Prints formatted violations as `file(line): severity: message` |

---

### Predicates

| Predicate | Description |
|---|---|
| `callsSyncWhenAsyncExists(Statement)` | Flags calls to sync methods when an async variant (method name + `Async`) exists on any type in the codebase |

---

### Examples

```ruby
import code-analysis

# Flag sync calls when async variant exists
CHECK prefer-async => Code.Statements:callsSyncWhenAsyncExists
    :toWarning('Use {item.MemberName}Async instead of {item.MemberName}')
```

```ruby
import code-analysis

let errors = Code.Statements:csharp:varDeclaration
    :toError('Do not use var for {item.MemberName}')

CHECK(errors)
```

```ruby
import code-analysis

let violations = Code.Types:csharp:publicType
    :!Name:endsWith('Base')
    :toWarning('{item.Name} should not have a Base suffix')

CHECK(violations)
```
