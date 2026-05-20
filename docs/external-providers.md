# External Providers (Node.js, Python)

Cop supports data providers implemented in any language that can communicate via stdin/stdout.
This guide covers the protocol and SDKs for Node.js and Python providers.

## Overview

External providers run as child processes. The Cop engine communicates with them using
**length-prefixed JSON messages** over stdin/stdout (the same framing used by LSP):

```
Content-Length: <byte_count>\r\n
\r\n
<json_payload>
```

The provider process stays alive across requests within a single Cop session.

## Package Manifest

Declare an external provider in `cop.json`:

```json
{
  "name": "my-provider",
  "version": "1.0.0",
  "title": "My Provider",
  "description": "Description of what this provider does",
  "authors": "your-name",
  "provider": "node",
  "providerEntry": "src/index.js"
}
```

| Field | Description |
|-------|-------------|
| `provider` | Runtime: `"node"` for Node.js, `"python"` for Python |
| `providerEntry` | Path to entry script (relative to package directory) |

## Protocol

The engine sends exactly two types of requests:

### GetSchema

Sent once at load time. Provider must return its type and collection definitions.

**Request:**
```json
{"method": "getSchema"}
```

**Response:**
```json
{
  "types": [
    {
      "name": "Diagnostic",
      "properties": [
        {"name": "FilePath"},
        {"name": "Line", "type": "int"},
        {"name": "Column", "type": "int"},
        {"name": "RuleId"},
        {"name": "Message"},
        {"name": "Severity"}
      ]
    }
  ],
  "collections": [
    {"name": "Diagnostics", "itemType": "Diagnostic"}
  ]
}
```

### Query

Sent when the engine needs collection data.

**Request:**
```json
{
  "method": "query",
  "params": {
    "rootPath": "/path/to/project",
    "requestedCollections": ["Diagnostics"],
    "excludedDirectories": [".git", "node_modules", "bin"]
  }
}
```

**Response:**
```json
{
  "Diagnostics": [
    {
      "FilePath": "src/app.js",
      "Line": 10,
      "Column": 5,
      "RuleId": "no-unused-vars",
      "Message": "'x' is defined but never used",
      "Severity": "warning"
    }
  ]
}
```

## Property Types

Properties default to `"string"`. Supported types:

| Type | JSON representation |
|------|-------------------|
| `string` | JSON string |
| `int` | JSON number (integer) |
| `number` | JSON number (float) |
| `bool` | JSON boolean |

Properties can also be `optional: true` or `collection: true` (array of items).

## Node.js SDK

The `@aspect/cop-provider-sdk` package handles all protocol details.

### Installation

```bash
# From your provider package directory:
npm install ../../sdk/node/cop-provider-sdk
```

### Usage

```javascript
const { defineProvider } = require('@aspect/cop-provider-sdk');

defineProvider({
  schema: () => ({
    types: [
      {
        name: 'Diagnostic',
        properties: [
          { name: 'FilePath' },
          { name: 'Line', type: 'int' },
          { name: 'Message' },
        ],
      },
    ],
    collections: [{ name: 'Diagnostics', itemType: 'Diagnostic' }],
  }),

  query: async (params) => {
    const { rootPath } = params;
    // ... run your analysis tool ...
    return {
      Diagnostics: [
        { FilePath: 'src/app.js', Line: 10, Message: 'Issue found' },
      ],
    };
  },
});
```

The `query` function can be sync or async (return a Promise).

## Python SDK

The `cop_provider_sdk` package handles all protocol details.

### Installation

```bash
# Add to your sys.path or install:
pip install ../../sdk/python
```

### Usage

```python
from cop_provider_sdk import define_provider

def get_schema():
    return {
        "types": [
            {
                "name": "Diagnostic",
                "properties": [
                    {"name": "FilePath"},
                    {"name": "Line", "type": "int"},
                    {"name": "Message"},
                ],
            }
        ],
        "collections": [{"name": "Diagnostics", "itemType": "Diagnostic"}],
    }

def query(params):
    root_path = params.get("rootPath", ".")
    # ... run your analysis tool ...
    return {
        "Diagnostics": [
            {"FilePath": "src/app.py", "Line": 10, "Message": "Issue found"},
        ]
    }

define_provider(schema=get_schema, query=query)
```

## Canonical Examples

### ESLint Provider (Node.js)

See `providers/eslint-provider/` — wraps ESLint's Node.js API to lint JS/TS files.

### Ruff Provider (Python)

See `providers/ruff-provider/` — wraps `ruff check --output-format=json` for Python linting.

## Error Handling

- If the provider process crashes, the engine reports the error and stderr output.
- If a response contains an `"error"` field, it's treated as a provider error.
- If the provider doesn't respond within 60 seconds, the engine times out.
- If the runtime (node/python) is not installed, the engine reports a helpful error.

## Debugging

Provider stderr is captured and included in error messages. Use stderr for debug logging:

```javascript
// Node.js
console.error('Debug: processing', filePath);
```

```python
# Python
import sys
print("Debug: processing", file_path, file=sys.stderr)
```
