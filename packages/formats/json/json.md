---
name: json
version: 1.0.0
title: JSON File Parsing
description: Parse JSON files into typed objects or collections using user-defined type schemas
authors: cop-team
tags: json, parsing, data
provider: clr
providerEntry: Cop.Providers.JsonProvider
---

# JSON File Parsing

Enables `Parse()` for JSON files. Import with `import json`.

## Usage

```cop
import json

type Person = {
    name : string,
    age : int
}

let People = Parse('data.json', 'Person')
```

## Parse Function

`Parse(path, typeName)` reads a JSON file and deserializes it into a typed object or collection.

- **path** — Relative or absolute path to a JSON file. Relative paths resolve from the working directory.
- **typeName** — The name of the target type. The type must be defined with a `type` declaration.

Returns:
- If the JSON root is an **array**, returns a collection of typed objects.
- If the JSON root is an **object**, returns a single typed instance.
- If the JSON root is a **primitive**, returns the value directly.
