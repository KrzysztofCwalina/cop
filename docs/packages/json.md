## json

Parse JSON files into typed collections. &nbsp; `import json`

**Source:** [`packages/json/src/json.cop`](../../packages/json/src/json.cop)

---

### Parse Function

```cop
let Collection = Parse('path/to/file.json', [TypeName])
```

| Parameter | Description |
|---|---|
| `'path'` | Relative or absolute path to a JSON file. Relative paths resolve from the working directory. |
| `[TypeName]` | Target type name in brackets. Must match a `type` declaration in the script. |

The JSON file must contain a **top-level array** of objects. Each object is deserialized into a `ScriptObject` with fields matching the type definition.

---

### Defining Types

Define the schema for your JSON records using `type` declarations:

```cop
type Person = {
    name : string,
    age : int
}
```

Supported property types:

| Type | JSON Kind | Description |
|---|---|---|
| `string` | String | Text values |
| `int` | Number | Integer values |
| `number` | Number | Floating-point values |
| `bool` | Boolean | True/false |
| Nested type | Object | Define a sub-type and reference it by name |
| Collection (`[T]`) | Array | Use `collection` modifier in type property |

---

### Example

**data.json:**
```json
[
    { "name": "Alice", "age": 30, "active": true },
    { "name": "Bob", "age": 25, "active": false }
]
```

**checks.cop:**
```cop
import json

type Person = {
    name : string,
    age : int,
    active : bool
}

let People = Parse('data.json', [Person])

predicate canVote(Person) => Person.age >= 18
predicate activeVoter(Person) => Person:canVote && Person.active == true

let ActiveVoters = People:activeVoter
foreach ActiveVoters => '{item.name} is an active voter'
```

---

### Nested Types

```cop
type Address = {
    city : string,
    zip : string
}

type Employee = {
    name : string,
    address : Address
}

let Staff = Parse('employees.json', [Employee])
foreach Staff => '{item.name} lives in {item.address.city}'
```

---

### Notes

- Property names are **case-sensitive** and must match the JSON field names exactly.
- Unknown JSON fields (not in the type definition) are silently ignored.
- `Parse()` requires `import json` in the script.
