---
name: json
version: 1.0.0
title: JSON File Parsing
description: Parse JSON files into typed collections using user-defined type schemas
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

let People = Parse('data.json', [Person])
```

## Parse Function

`Parse(path, [Type])` reads a JSON file and deserializes it into a typed collection.

- **path** — Relative or absolute path to a JSON file. Relative paths resolve from the working directory.
- **[Type]** — A single-element list containing the target type name. The type must be defined with a `type` declaration.

The JSON file must contain a top-level array of objects matching the type schema.
