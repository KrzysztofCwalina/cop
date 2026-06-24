# Working with CodeQL

Cop provides two packages for integrating with [CodeQL](https://codeql.github.com/):

| Package | Purpose |
|---------|---------|
| `analysis-codeql` | Read CodeQL analysis results (SARIF) as cop collections |
| `csharp-codeql-export` | Programmatically build and save `.ql` query files |

---

## Package: `analysis-codeql` — Query CodeQL Results

The `analysis-codeql` package reads SARIF output from `codeql database analyze` and exposes findings as typed cop collections.

### Prerequisites

1. Install the [CodeQL CLI](https://github.com/github/codeql-cli-binaries/releases)
2. Create a database for your project:
   ```bash
   codeql database create my-db --language=csharp
   ```
3. Run analysis and output SARIF:
   ```bash
   codeql database analyze my-db --format=sarif-latest --output=results.sarif
   ```

### Usage

```cop
import analysis-codeql
import code

# Load results from a SARIF file
let results = analysis-codeql.Load('results.sarif')

# All CodeQL violations are available via codeql-checks
command MAIN = CHECK(codeql-checks)
```

### Collections

When SARIF files exist in the target directory, collections are auto-populated:

| Collection | Type | Description |
|-----------|------|-------------|
| `analysis-codeql.Violations` | `Violation` | All analysis findings |
| `analysis-codeql.Rules` | `Rule` | Rule definitions from the SARIF |

### Types

**Violation** (from `code-analysis` package)
| Property | Type | Description |
|----------|------|-------------|
| `File` | string | Relative file path of the finding |
| `Line` | int | Line number |
| `Severity` | string | `error`, `warning`, or `info` |
| `Message` | string | Finding message (includes rule ID prefix, e.g., `cs/sql-injection: ...`) |
| `Source` | string | Tool name (`codeql`) |

**Rule**
| Property | Type | Description |
|----------|------|-------------|
| `Id` | string | Rule identifier |
| `Name` | string | Rule display name |
| `Description` | string | Rule description |
| `Severity` | string | Default severity level |
| `Tags` | string | Comma-separated tags (e.g., `security, correctness`) |
| `Precision` | string | Precision level (e.g., `high`, `medium`) |

---

## Package: `csharp-codeql-export` — Build CodeQL Queries

The `csharp-codeql-export` package provides functions for constructing CodeQL `.ql` query files programmatically from cop.

### Usage

<!-- cop norun: csharp-codeql-export package is not runtime-loadable in the offline test feed (fatals 'Undefined variable csharp-codeql-export') -->
```cop norun
import csharp-codeql

# Build a query that finds public abstract classes
let query = csharp-codeql-export.qlQuery(
  'Find public abstract classes',
  'Classes that are both public and abstract',
  csharp-codeql-export.qlFrom('c', 'Class'),
  csharp-codeql-export.qlAnd(
    csharp-codeql-export.qlModifier('c', 'public'),
    csharp-codeql-export.qlModifier('c', 'abstract')
  ),
  'c, "Class " + c.getName() + " is public and abstract"'
)

# Save to a .ql file
command MAIN = csharp-codeql-export.qlSave('codeql/public-abstract.ql', query)
```

This generates a complete `.ql` file:

```ql
/**
 * @name Find public abstract classes
 * @description Classes that are both public and abstract
 * @kind problem
 * @problem.severity warning
 */

import csharp

from Class c
where c.isPublic() and
  c.isAbstract()
select c, "Class " + c.getName() + " is public and abstract"
```

### Functions

#### Query Builders

| Function | Description |
|----------|-------------|
| `qlQuery(name, desc, from, where, select)` | Build a complete query with a where clause |
| `qlQueryAll(name, desc, from, select)` | Build a query with no where clause (select all) |

#### From Clause

| Function | Description |
|----------|-------------|
| `qlFrom(variable, type)` | Single variable: `'Class c'` |
| `qlFrom2(var1, type1, var2, type2)` | Two variables: `'Class c, Method m'` |

#### Where Clause Combinators

| Function | Description |
|----------|-------------|
| `qlAnd(left, right)` | Combine with `and` |
| `qlOr(left, right)` | Combine with `or` (parenthesized) |
| `qlNot(condition)` | Negate: `not <condition>` |

#### Condition Builders

| Function | Description |
|----------|-------------|
| `qlModifier(var, modifier)` | Modifier check: `c.isPublic()` |
| `qlNameMatches(var, pattern)` | Regex match: `c.getName().regexpMatch(...)` |
| `qlNameStartsWith(var, prefix)` | Starts with: `c.getName().indexOf(...) = 0` |
| `qlNameContains(var, substring)` | Contains: `c.getName().matches("%...%")` |
| `qlExists(declaration, condition)` | Existential: `exists(Type v \| ...)` |

#### Output

| Function | Description |
|----------|-------------|
| `qlSave(path, content)` | Write query string to a `.ql` file |

### Example: Multiple Queries

<!-- cop norun: csharp-codeql-export package is not runtime-loadable in the offline test feed (fatals 'Undefined variable csharp-codeql-export') -->
```cop norun
import csharp-codeql

# Query 1: Find classes with no documentation
let undocumented = csharp-codeql-export.qlQuery(
  'Undocumented public classes',
  'Public classes missing Javadoc/XML documentation',
  csharp-codeql-export.qlFrom('c', 'Class'),
  csharp-codeql-export.qlAnd(
    csharp-codeql-export.qlModifier('c', 'public'),
    csharp-codeql-export.qlNot('c.getDoc().hasBody()')
  ),
  'c, "Class " + c.getName() + " has no documentation"'
)

# Query 2: Find methods with too many parameters
let tooManyParams = csharp-codeql-export.qlQuery(
  'Methods with too many parameters',
  'Methods with more than 5 parameters',
  csharp-codeql-export.qlFrom('m', 'Method'),
  'm.getNumberOfParameters() > 5',
  'm, "Method " + m.getName() + " has " + m.getNumberOfParameters() + " parameters"'
)

command MAIN = {
  csharp-codeql-export.qlSave('codeql/undocumented.ql', undocumented)
  csharp-codeql-export.qlSave('codeql/too-many-params.ql', tooManyParams)
}
```

---

## Workflow: End-to-End

A typical workflow combining both packages:

1. **Write checks in cop** (using the `code` provider for fast local analysis)
2. **Export key checks to CodeQL** (using `csharp-codeql-export` for CI/CD integration)
3. **Query CodeQL results in cop** (using `analysis-codeql` to analyze/report on findings)

<!-- cop norun: analysis-codeql/csharp-codeql-export not runtime-loadable offline (fatals on '.count' / Undefined variable) -->
```cop norun
import analysis-codeql
import csharp-codeql

# Read existing CodeQL results
let results = analysis-codeql.Load('results.sarif')
let critical = results:isError

# Generate a new query for something CodeQL doesn't check
let customQuery = csharp-codeql-export.qlQuery(
  'Find singleton patterns',
  'Classes using the singleton anti-pattern',
  csharp-codeql-export.qlFrom('c', 'Class'),
  csharp-codeql-export.qlExists(
    'Field f',
    'f.getDeclaringType() = c and f.isStatic() and f.getType() = c'
  ),
  'c, "Class " + c.getName() + " appears to be a singleton"'
)

command MAIN = {
  print('Critical CodeQL findings: {critical.count}')
  csharp-codeql-export.qlSave('codeql/singletons.ql', customQuery)
}
```
