# Extensibility

Cop is designed to be extended in two ways:

1. **Data Providers** — supply new data collections (types, APIs, metrics, anything) to `.cop` programs
2. **External Analyzers** — wrap existing analysis tools (Ruff, ESLint, etc.) so their results flow through cop's unified output

Both approaches produce packages that can be published, shared, and composed with other packages.

---

## Adding New Data Providers

A data provider supplies typed collections to `.cop` programs. For example, the built-in `code` provider supplies `Types`, `Statements`, `Lines`, and `Files` by parsing source code. You can create providers that supply any data: API definitions, database schemas, config files, metrics, or anything else.

### Choosing a Runtime

| Runtime | Best for | Performance | Setup |
|---------|----------|-------------|-------|
| **CLR** (.NET) | High-performance, large datasets | Fastest (in-process binary) | Requires .NET SDK to build |
| **Python** | Wrapping Python tools, quick prototyping | Good (process + JSON) | Requires Python on target |
| **Node.js** | Wrapping JS tools, npm ecosystem | Good (process + JSON) | Requires Node.js on target |

### Python Provider (Recommended for Tool Integration)

Create a package directory with `cop.json` and a Python entry script:

```
my-provider/
├── cop.json
└── src/
    ├── main.py
    └── my-provider.cop
```

**cop.json:**
```json
{
  "name": "my-provider",
  "version": "1.0.0",
  "title": "My Provider",
  "description": "Supplies Widget data for analysis",
  "provider": "python",
  "providerEntry": "src/main.py"
}
```

**src/main.py:**
```python
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', '..', '..', 'sdk', 'python'))
from cop_provider_sdk import define_provider

def get_schema():
    return {
        "types": [
            {
                "name": "Widget",
                "properties": [
                    {"name": "Name"},
                    {"name": "Category"},
                    {"name": "Weight", "type": "float"},
                    {"name": "IsActive", "type": "bool"},
                ],
            }
        ],
        "collections": [{"name": "Widgets", "itemType": "Widget"}],
    }

def query(params):
    root_path = params.get("rootPath", ".")
    # Load or compute your data here
    return {
        "Widgets": [
            {"Name": "Sprocket", "Category": "hardware", "Weight": 2.5, "IsActive": True},
        ]
    }

define_provider(schema=get_schema, query=query)
```

**src/my-provider.cop:**
```ruby
let data = provider('my-provider', nic)

export let Widgets = data.Widgets

type Widget = {
    Name : string,
    Category : string,
    Weight : float,
    IsActive : bool
}
```

### Node.js Provider

Same structure, but use `"provider": "node"` in cop.json and a JavaScript entry:

```javascript
const { defineProvider } = require('@aspect/cop-provider-sdk');

defineProvider({
  schema: () => ({
    types: [{ name: 'Widget', properties: [{ name: 'Name' }, { name: 'Weight', type: 'float' }] }],
    collections: [{ name: 'Widgets', itemType: 'Widget' }],
  }),
  query: async (params) => {
    return { Widgets: [{ Name: 'Sprocket', Weight: 2.5 }] };
  },
});
```

### CLR Provider (Advanced)

For maximum performance, implement a .NET class that extends `ObjectProvider`. CLR providers run in-process and use a binary format — no serialization overhead. See the [Provider Guide](provider-guide.md) for the full walkthrough.

### Protocol

All external providers (Python, Node.js) communicate with the cop engine via length-prefixed JSON over stdin/stdout:

```
Content-Length: <byte_count>\r\n
\r\n
<json_payload>
```

The engine sends two request types:

1. **getSchema** — called once at load time; provider returns type and collection definitions
2. **query** — called when data is needed; provider returns collection contents

See [External Providers](external-providers.md) for the full protocol specification.

---

## Adding External Analyzers

An external analyzer package wraps an existing tool (Ruff, ESLint, Pylint, etc.) so its output flows through cop. The analyzer runs as-is — cop doesn't reimplement its rules. The package translates the tool's native output into cop's unified format.

### Why Wrap an External Tool?

- **Unified output** — all findings (from cop rules, Ruff, ESLint, etc.) appear in the same `file(line): severity: message` format
- **Composability** — combine external findings with native cop checks using `+`
- **Filtering** — use `.cop` predicates to exclude rules or paths
- **Aggregation** — run multiple tools in one command and get a single report

### Architecture

An external analyzer package is just a Python (or Node.js) provider that:

1. Runs the external tool as a subprocess
2. Parses its structured output (JSON, SARIF, etc.)
3. Returns the results as a typed collection (e.g., `Diagnostics`)

```
┌──────────────┐       ┌──────────────┐       ┌──────────────┐
│  cop engine  │──────▶│  provider.py │──────▶│  ruff/eslint │
│              │◀──────│  (adapter)   │◀──────│  (tool)      │
└──────────────┘ JSON  └──────────────┘ JSON  └──────────────┘
```

### Example: Ruff (Python Linter)

The `python-ruff` package demonstrates the pattern. Here's how it works:

**cop.json:**
```json
{
  "name": "python-ruff",
  "version": "1.0.0",
  "title": "Ruff Python Linter",
  "description": "Runs ruff and exposes diagnostics",
  "provider": "python",
  "providerEntry": "src/main.py"
}
```

**src/main.py:**
```python
import json, os, subprocess, sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', '..', '..', 'sdk', 'python'))
from cop_provider_sdk import define_provider

def get_schema():
    return {
        "types": [
            {
                "name": "Diagnostic",
                "properties": [
                    {"name": "FilePath"},
                    {"name": "Line", "type": "int"},
                    {"name": "Column", "type": "int"},
                    {"name": "RuleId"},
                    {"name": "Message"},
                    {"name": "Severity"},
                ],
            }
        ],
        "collections": [{"name": "Diagnostics", "itemType": "Diagnostic"}],
    }

def query(params):
    root_path = params.get("rootPath") or os.getcwd()

    result = subprocess.run(
        ["ruff", "check", "--output-format=json", "."],
        capture_output=True, text=True, encoding="utf-8",
        cwd=root_path,
    )

    if result.returncode not in (0, 1):
        return {"Diagnostics": []}

    ruff_results = json.loads(result.stdout)
    diagnostics = []

    for item in ruff_results:
        file_path = item.get("filename", "")
        if os.path.isabs(file_path):
            file_path = os.path.relpath(file_path, root_path)

        rule_code = item.get("code", "")
        severity = "error" if rule_code.startswith("E") else "warning"
        location = item.get("location", {})

        diagnostics.append({
            "FilePath": file_path.replace("\\", "/"),
            "Line": location.get("row", 0),
            "Column": location.get("column", 0),
            "RuleId": rule_code,
            "Message": item.get("message", ""),
            "Severity": severity,
        })

    return {"Diagnostics": diagnostics}

define_provider(schema=get_schema, query=query)
```

**src/python-ruff.cop:**
```ruby
import code-analysis

let data = provider('python-ruff', nic)

export function diagnostics() => data.Diagnostics
export let checks = data.Diagnostics

command main = foreach checks
    => '{item.FilePath}({item.Line}): {item.Severity}: {item.RuleId}: {item.Message}'
```

### Running It

```bash
# Run ruff checks on a Python project
cop python-ruff -t path/to/project

# Output:
# src/app.py(1): warning: F401: `os` imported but unused
# src/app.py(9): error: E711: Comparison to `None` should be `cond is None`
```

### Creating Your Own Analyzer Package

To wrap a different tool, follow the same pattern:

1. **Create the package directory** with `cop.json` declaring `"provider": "python"` (or `"node"`)
2. **Write the provider script** that:
   - Defines a schema with a `Diagnostic` (or similar) type
   - Runs the tool via `subprocess.run` with JSON/structured output
   - Parses the output and maps it to your schema
3. **Write the `.cop` file** that exports the collection and defines a `command main`

Key considerations:

- **Use `encoding="utf-8"`** in subprocess calls (avoids encoding errors on Windows)
- **Use structured output** from the tool (JSON, SARIF) — don't parse human-readable text
- **Map severity** to `"error"`, `"warning"`, or `"info"` for consistency
- **Use relative paths** in `FilePath` for portable output
- **Handle tool not installed** gracefully (return empty collection, log to stderr)

### Composing Multiple Analyzers

```ruby
import python-ruff
import python-checks
import code-analysis

# All findings from both ruff and native cop checks
let all-checks = checks + python-checks

command main = CHECK(all-checks)
```

### Performance at Scale

External analyzer packages handle large repos well because the heavy lifting is done by purpose-built tools:

| Repo | Files | Findings | Time |
|------|-------|----------|------|
| azure-sdk-for-python | 55,000+ | 52,625 | ~21s |

The bottleneck is typically the external tool itself, not cop. Ruff is written in Rust and processes thousands of files per second.

---

## Further Reading

- [External Providers](external-providers.md) — full protocol specification and SDK reference
- [Provider Guide](provider-guide.md) — writing CLR (C#) providers
- [Static Analysis](static-analysis.md) — running and customizing checks
- [Packaging](packaging.md) — publishing and distributing packages
