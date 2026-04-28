---
name: json
version: 1.0.0
title: JSON File Parsing
description: Parse JSON files into typed collections using user-defined type schemas
authors: cop-team
tags: json, parsing, data
---

# JSON File Parsing

`Parse()` is a built-in function that reads JSON files into typed collections. No import is required.

## Usage

```cop
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

## Supported Property Types

- `string` — JSON strings
- `int` — JSON integers
- `number` — JSON floating-point numbers
- `bool` — JSON booleans
- Collection properties — JSON arrays
- Nested types — JSON objects (define sub-types and reference them)

## Example

```json
[
    { "name": "Alice", "age": 30 },
    { "name": "Bob", "age": 25 }
]
```

```cop
type Person = { name : string, age : int }
let People = Parse('data.json', [Person])

predicate young(Person) => Person.age < 30
foreach People:young => PRINT('{item.name} is young')
```
