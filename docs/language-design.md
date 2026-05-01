# Cop Language Design

## Philosophy

Cop is a **lazy, declarative language** for processing typed object graphs. It combines Haskell's evaluation model with syntax natural to C#/TypeScript developers, optimized for a single workflow: **read structured data → filter → transform → output**.

### Design Principles

1. **Lazy evaluation** — Nothing is computed until demanded. Collections are thunks; fields resolve on first access and memoize.
2. **Purity** — Expressions have no side effects. Output is the only effect, isolated in `command` blocks.
3. **Pipeline-first** — The primary operation is filtering and projecting typed collections. Syntax is optimized for `collection:filter:filter => output`.
4. **Structural typing** — Objects are open records. If it has the right fields, it matches the type.
5. **Case-insensitive** — All identifiers, string comparisons, and field lookups are case-insensitive by default.
6. **Minimal type kinds** — The type system has exactly: scalars, objects, collections, and functions. No special proxy types, no wrapper classes, no magic.

---

## Type System

### Scalars

| Type | Examples | Notes |
|------|----------|-------|
| `string` | `'hello'`, `'it''s'` | Single-quoted. `''` escapes quote. |
| `int` | `42`, `-1` | 32-bit signed integer |
| `number` | `3.14` | 64-bit float |
| `bool` | `true`, `false` | Also: any non-null value is truthy |
| `null` | `null` | Absence of value (alias: `nic`) |

### Objects

An **object** is an immutable record — a set of named fields, each holding a scalar, object, or collection.

```cop
# Object literal
let config = { Name = 'MyApp', Version = 2, Debug = false }

# Typed object (from provider or function)
# Has TypeName for runtime dispatch (e.g., "Type", "Method", "File")
```

Objects support:
- **Field access**: `obj.Name`, `obj.File.Path` (dot-chained)
- **Lazy fields**: Fields may be thunks, evaluated on first access and cached
- **Structural**: Two objects with the same fields are interchangeable

### Collections

A **collection** is a lazy, ordered sequence of objects (or scalars).

```cop
# List literal
let keywords = ['async', 'await', 'var']

# Provider collection (lazily loaded)
let types = csharp.Types

# Derived collection (lazily filtered)
let clients = types:isPublic:isClient
```

Collections support:
- **Properties**: `.Count`, `.First`, `.Last`, `.Single`
- **Projection**: `.Select(expr)`, `.Where(pred)`, `.OrderBy(expr)`
- **Aggregation**: `.Sum(expr)`, `.Min(expr)`, `.Max(expr)`, `.Average(expr)`
- **Flattening**: `collection.Field` → flattens field across all items
- **Concatenation**: `a + b` → lazy union

### Functions (Values)

Functions are first-class values. Partial application produces closures.

```cop
function greet(Person, greeting: string) => '{greeting}, {item.Name}!'
let hello = greet('Hello')   # closure: Person → string
```

---

## Declarations

All declarations are order-independent within a file. Evaluation is demand-driven.

### `let` — Bind a name to a value or collection

```cop
# Value binding (scalar, object, or function result)
let threshold = 10
let config = { MaxRetries = 3 }

# Collection binding (base + filter chain)
let publicTypes = Types:isPublic
let clients = publicTypes:Name:ew('Client')

# Provider binding (lazy — evaluated on field access)
let codebase = csharp.Code('path/to/project')
let types = codebase.Types

# External data
let docs = Load('path/to/assemblies')
let people = Parse('data.json', [Person])
```

**Key property**: `let` is lazy. The RHS is not evaluated until the name is used.

### `predicate` — Boolean function over a typed item

```cop
predicate isClient(Type) => item.Name:ew('Client')
predicate needsReview(Type) => item:isPublic && item.Methods.Count > 10
predicate usesVar(Statement) => item.Keywords:contains('var')
```

Predicates are the primary filtering mechanism. They compose naturally:

```cop
# Chained (AND): all conditions must hold
let targets = Types:isPublic:isClient:!isSealed

# Logical operators inside predicate body
predicate complex(Type) => isClient || isHandler
```

### `function` — Transform one type into another

```cop
# Record-producing function (maps input fields to output fields)
function summarize(Type) => Summary {
    Name = item.Name
    MethodCount = item.Methods.Count
    IsLarge = item.Methods.Count > 20
}

# Expression function (returns a value directly)
function fullName(Person) => '{item.FirstName} {item.LastName}'
```

Functions are used in `.Select()` transforms and as standalone projections.

### `type` — Define a structural record type

```cop
type Person = {
    Name: string,
    Age: int,
    Email: string?       # optional field
    Tags: [string]       # collection field
}

# Type extension (adds fields to base)
type Employee = Person & {
    Department: string,
    Manager: Person?
}
```

### `flags` — Bit-flag enumeration

```cop
flags Modifier = Public | Private | Protected | Internal | Static | Abstract | Sealed
```

Flags support `:isSet(Flag)` predicate for bitwise testing.

### `command` — Named output action

```cop
command LIST-TYPES = foreach Types:isPublic => '{item.Name}'
command CHECK = foreach Violations => PRINT('{item.Severity}: {item.Message}')
command REPORT = SAVE('output.md', ReportTemplate)
```

---

## Operators

### `:` — Subset (filter)

The colon applies a predicate to filter a collection. Chaining is AND.

```cop
Types:isPublic              # items where isPublic(item) is true
Types:!isSealed             # negated: items where NOT isSealed
Types:Name:sw('I')          # inline: items where Name starts with 'I'
Types:isPublic:isClient     # AND: both must hold
```

**Built-in predicates** (2-letter short forms):
- `eq`, `ne` — equals, not-equals
- `sw`, `ew` — starts-with, ends-with
- `ct` — contains
- `ca` — contains-any (from list)
- `rx` — matches regex
- `sm` — same-as (normalized identifier comparison)
- `gt`, `lt`, `ge`, `le` — numeric comparisons
- `in` — membership in a list
- `empty` — empty string or collection

### `.` — Member access and transforms

The dot navigates object fields and applies transforms.

```cop
# Field access
item.Name
item.File.Path

# Collection property
types.Count
types.First

# Collection flattening
types.Methods          # → all methods across all types (flat list)

# String transforms
item.Name.Lower
item.Name.Words        # → ['Get', 'User', 'Name']
item.Name.Normalized   # → 'getusername'

# Collection transforms
items.Where(pred)
items.Select(expr)
items.OrderBy(expr)
items.Distinct()
items.GroupBy(expr)    # → [{Key, Items, Count}]
items.Text(', ')      # → joined string
items.Reduce('+', expr, ', ')
```

### `?` / `|` — Conditional and match

```cop
# Ternary
item.IsPublic ? 'public' | 'private'

# Match expression (multi-arm)
item.Status ? 'active' => '{green@}' 
            | 'deprecated' => '{red@}'
            | _ => '{gray@}'
```

### `+` / `-` — Union and exclusion

```cop
let allTypes = csharpTypes + pythonTypes    # union
let filtered = allTypes - excludedTypes     # set difference
```

---

## Evaluation Model

### Lazy by Default

Every binding is a thunk until forced:

```cop
let codebase = csharp.Code('large/project')    # not evaluated yet
let types = codebase.Types                      # still not evaluated
let count = types.Count                         # NOW: forces types → forces codebase
```

Results are memoized — subsequent access returns cached value.

### Expression Evaluation

Expressions evaluate in a context: the current item and its type.

```cop
predicate check(Type) => item.Methods:any(m => m:isPublic && m.Parameters.Count > 5)
```

The `item` keyword refers to the current object in scope. Inside nested predicates, the inner `item` shadows the outer.

### Filter Pushdown

Built-in predicates (`:eq`, `:sw`, `:ct`, etc.) are pushed down to providers as filter hints. Providers can optimize queries based on these hints (e.g., skip parsing files that can't match).

---

## Providers

Providers supply typed object graphs to Cop programs. They are the boundary between the outside world and the pure language.

### Import and Access

```cop
import csharp                    # load provider package
import python

# Access collections directly
foreach csharp.Types => ...

# Or via Code() for aggregated/scoped access
let codebase = csharp.Code('path/to/project')
foreach codebase.Types => ...
foreach codebase.Methods => ...

# Multi-provider aggregation
let all = Code([csharp, python])
foreach all.Types => ...
```

### Provider Contract

A provider:
1. Declares a **schema** — types and collections it exposes
2. Responds to **queries** — returns typed objects lazily
3. Supports **filter pushdown** — predicates passed down for optimization
4. Operates **read-only** — providers never mutate data

### Built-in Providers

| Provider | Collections | Description |
|----------|-------------|-------------|
| `csharp` | Types, Methods, Statements, Fields, Properties, Events | C# source analysis |
| `python` | Types, Methods, Statements | Python source analysis |
| `javascript` | Types, Methods, Statements | JS/TS source analysis |
| `filesystem` | Files, Folders | File system access |
| `json` | (via Parse) | JSON data loading |
| `http` | Requests | HTTP server (streaming) |

---

## Packages

Packages are reusable collections of types, predicates, functions, and commands.

### Structure

```
packages/
  my-package/
    src/
      main.cop         # exported symbols
      helpers.cop      # internal helpers
    package.cop        # metadata (name, version, dependencies)
```

### Visibility

```cop
# In package source:
export predicate isClient(Type) => ...    # visible to importers
predicate helper(Type) => ...             # internal only

export let MaxMethods = 20                # exported constant
```

### Import

```cop
import my-package                          # import all exported symbols
```

Imported predicates, functions, types, and let bindings become available in the importing file.

---

## Commands and Output

Commands are the only place with side effects. They define what a Cop program produces.

### `foreach` — Iterate and output

```cop
# Simple template output
foreach Types:isPublic => '{item.Name}'

# Styled output
foreach Violations => '{item.Severity @red}: {item.Message}'

# With transform
foreach Types => '{item.Name} ({item.Methods.Count} methods)'
```

### Output Actions

```cop
PRINT('message')                    # write to stdout
SAVE('path.md', template)          # write to file
ASSERT(condition)                   # test assertion
ASSERT_EMPTY(collection)           # assert no items
DEBUG(expr)                        # diagnostic output
```

### Named Commands

```cop
command CHECK = foreach Violations => '{item.Message}'
command REPORT = SAVE('report.md', ReportBody)
command LIST(pattern: string) = foreach Types:Name:ct(pattern) => '{item.Name}'
```

Invoked via CLI: `cop run CHECK`, `cop run LIST --pattern Service`

---

## Strings and Templates

### String Literals

```cop
'simple string'
'it''s escaped'              # '' → '
'''
multi-line
string
'''
@'c:\path\to\file'          # verbatim (no escaping)
```

### Template Interpolation

Inside `'...'` strings:
- `{expr}` — interpolate expression value
- `{text@style}` — styled literal text
- `{expr@style}` — styled expression
- `{{`, `}}` — literal braces

**Styles** (kebab-case, combinable with `-`):
`red`, `green`, `blue`, `yellow`, `cyan`, `magenta`, `white`, `gray`, `bold`, `dim`

```cop
'{item.Name @red-bold} in {item.File.Path @dim}'
```

---

## Design Invariants

These hold throughout the language — no exceptions:

1. **All evaluation is lazy and memoized.** No eager computation except output.
2. **All data is immutable.** Objects and collections are never modified in place.
3. **All string operations are case-insensitive.** No exceptions.
4. **`:` always filters (reduces cardinality).** `.` always accesses or transforms.
5. **Types have exactly one kind of member: fields.** No methods on types — behavior lives in predicates and functions.
6. **Providers are the only source of data.** The language has no I/O beyond providers and output commands.
7. **The runtime has exactly 4 value kinds**: scalar, object (DataObject), collection (IList), function/closure. No proxy types, no wrappers, no special cases.
