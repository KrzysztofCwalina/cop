# Cop Language Design

## Philosophy

Cop is a **lazy, declarative language** for processing typed object graphs. It combines Haskell's evaluation model with syntax natural to C# developers, optimized for a single workflow: **read structured data → filter → transform → output**.

### Design Principles

1. **Lazy evaluation** — Nothing is computed until demanded. Fields resolve on first access and memoize. Like Haskell, everything is a thunk until forced.
2. **Purity** — Expressions have no side effects. Output is the only effect, isolated in `command` blocks.
3. **One type** — The language has exactly one composite type: DataObject. Collections, functions, providers — all DataObjects with different protocols.
4. **Two operators** — `.` navigates (field access, extension methods). `:` filters (Where).
5. **Extension methods** — Predicates and functions are extension methods resolved by their first-parameter type, like C#.
6. **Case-insensitive** — All identifiers, string comparisons, and field lookups are case-insensitive by default.

---

## The Type System: ONE Type

### DataObject

**DataObject** is the universal composite type. Every non-scalar value in the language is a DataObject. A DataObject may support any combination of **protocols**:

| Protocol | Capability | Example |
|----------|-----------|---------|
| **Field access** | Has named fields, accessed via `.` | `type.Name`, `csharp.Types` |
| **Iteration** | Is a sequence of items, supports `:` and higher-order ops | `types`, `methods` |
| **Application** | Is callable (function, predicate, closure) | `isPublic`, `summarize` |
| **Scalar** | Has a terminal value (string, int, bool, null) | `'hello'`, `42`, `true` |

These are NOT exclusive. A string has a scalar value AND supports field access (`.Length`). A collection supports iteration AND field access (`.Count`, `.First`).

### Scalars

Scalars are the terminal values — the base case where navigation stops:

| Type | Examples | Notes |
|------|----------|-------|
| `string` | `'hello'`, `'it''s'` | Single-quoted. `''` escapes quote. |
| `int` | `42`, `-1` | 32-bit signed integer |
| `number` | `3.14` | 64-bit float |
| `bool` | `true`, `false` | |
| `null` | `null` | Absence of value |

### The Object Tree (Uniform Thunks)

Everything from providers down to leaf fields is a DataObject. The tree is uniform — no level is special:

```
csharp                → DataObject (provider; fields: Types, Methods, ...)
  .Types              → DataObject (iterable; each item is a DataObject)
    [0]               → DataObject (a Type; fields: Name, Methods, Properties, ...)
      .Methods        → DataObject (iterable; each item is a DataObject)
        [0]           → DataObject (a Method; fields: Name, Parameters, ...)
          .Name       → scalar (string — terminal)
```

**Every `.` is the same operation** at every level. There is no distinction between "accessing a provider collection" and "accessing a field on an object." It's all field access on a DataObject.

### What is `csharp`?

`import csharp` binds the name `csharp` to a DataObject. That DataObject has lazy fields:
- `.Types` — forces to an iterable DataObject (collection of Type DataObjects)
- `.Methods` — forces to an iterable DataObject (collection of Method DataObjects)
- `.Code('path')` — a callable field (function) that returns a scoped DataObject

There is no "namespace" concept. There is no special resolution path. `csharp` is a DataObject like any other. `csharp.Types` is field access like any other.

---

## The Two Operators

### `.` — Navigate

The dot operator does ONE thing: access a named member on a DataObject. Its behavior depends on what it finds:

**Field access** (member is a field):
```cop
type.Name                # access field → string
type.Methods             # access field → iterable DataObject
csharp.Types             # access field → iterable DataObject
```

**Extension method** (member is a predicate/function defined on the type):
```cop
type.isPublic            # apply predicate → bool
type.hasPrefix('I')      # apply predicate with arg → bool
type.summarize('json')   # apply function with arg → DataObject
```

**Project on iterable** (left side is iterable, member is a field on items):
```cop
types.Name               # project → list of strings (one per item)
types.Methods            # flatten → list of all Methods across all Types
```

**Higher-order operator** (left side is iterable, member is a collection operator):
```cop
types.any(isPublic)      # → bool (true if any item satisfies)
types.all(isPublic)      # → bool (true if all satisfy)
types.count(isPublic)    # → int (how many satisfy)
types.first(isPublic)    # → first matching item or null
types.orderBy(Name)      # → sorted iterable
types.groupBy(Namespace) # → iterable of {Key, Items, Count}
types.sum(Methods.Count) # → int (aggregate)
```

**Built-in iterable properties**:
```cop
types.Count              # number of items
types.First              # first item
types.Last               # last item
types.Single             # the item (asserts exactly one)
```

### `:` — Filter (Where)

The colon operator is the **filter** operator. It evaluates a predicate against each item in a collection and keeps only items where the result is truthy.

```cop
types:isPublic                # keep Types where isPublic(item) is true
types:hasPrefix('I')          # keep Types where hasPrefix(item, 'I') is true
types:isPublic:isSealed       # chain (AND): both must hold
```

**`:` preserves the item type.** It only reduces cardinality — never changes what kind of object you get back.

**Inline field predicates** — the expression after `:` is evaluated against each item:
```cop
types:Name.startsWith('I')        # keep Types where item.Name starts with 'I'
types:Methods.Count > 5           # keep Types with more than 5 methods
```

**Key distinction between `.` and `:` on collections:**
```cop
types.Name          # PROJECT → ['UserService', 'IRepo', ...] (strings! Types lost)
types:isPublic      # FILTER  → [Type, Type, ...] (still Types, just fewer)
```

---

## Predicates and Functions (Extension Methods)

### Predicates

A predicate is a function that returns bool. It's defined with a first-parameter type, making it an **extension method** on that type:

```cop
predicate isPublic(Type) => item.Visibility.equals('public')
predicate isClient(Type) => item.Name.endsWith('Client')
predicate hasPrefix(prefix: string, item: Type) => item.Name.startsWith(prefix)
```

One definition, multiple uses:
```cop
type.isPublic           # extension method call on single item → bool
types:isPublic          # filter collection → fewer Types
types.any(isPublic)     # higher-order operator → bool
types.all(isPublic)     # higher-order operator → bool
types.count(isPublic)   # higher-order operator → int
```

### Functions

A function transforms one type into another. Same extension method pattern:

```cop
function summarize(Type) => Summary {
    Name = item.Name
    MethodCount = item.Methods.Count
    IsLarge = item.Methods.Count > 20
}

type.summarize          # apply to single item → Summary DataObject
types.map(summarize)    # apply to each item → list of Summaries
```

### Predicates ARE Functions

A predicate is just a function that returns bool. The `predicate` keyword is syntactic sugar — it signals that `:` can use this as a filter.

---

## Declarations

All declarations are order-independent within a file. Evaluation is demand-driven (lazy).

### `let` — Bind a name to a value

```cop
let threshold = 10
let codebase = csharp.Code('path/to/project')
let types = codebase.Types
let publicTypes = types:isPublic
let names = publicTypes.Name
```

`let` is lazy. The RHS is not evaluated until the name is used.

### `predicate` — Extension method returning bool

```cop
predicate isPublic(Type) => item.Visibility.equals('public')
predicate needsReview(Type) => item.isPublic && item.Methods.Count > 10
```

### `function` — Extension method returning a value

```cop
function summarize(Type) => Summary {
    Name = item.Name
    MethodCount = item.Methods.Count
}

function fullName(Person) => '{item.FirstName} {item.LastName}'
```

### `type` — Define a structural record type

```cop
type Summary = {
    Name: string,
    MethodCount: int,
    IsLarge: bool
}
```

### `command` — Named output action

```cop
command CHECK = foreach Types:isPublic => '{item.Name}'
command REPORT = foreach Violations => '{item.Severity}: {item.Message}'
```

---

## Higher-Order Collection Operators

These are the explicit operations on iterable DataObjects — like Haskell's `filter`, `map`, `any`, `foldl`:

| Operator | Haskell equivalent | Result |
|----------|-------------------|--------|
| `items:pred` | `filter pred items` | Fewer items of same type |
| `items.Name` | `map name items` | List of field values |
| `items.any(pred)` | `any pred items` | bool |
| `items.all(pred)` | `all pred items` | bool |
| `items.count(pred)` | `length . filter pred` | int |
| `items.first(pred)` | `find pred items` | item or null |
| `items.orderBy(field)` | `sortBy field items` | Sorted collection |
| `items.groupBy(field)` | `groupBy field items` | [{Key, Items, Count}] |
| `items.sum(expr)` | `sum . map expr` | number |
| `items.map(func)` | `map func items` | Transformed list |
| `items.Count` | `length items` | int |
| `items.First` | `head items` | First item |
| `items.Last` | `last items` | Last item |

---

## Providers

Providers supply typed object graphs to Cop programs. They are DataObjects.

### Import and Access

```cop
import csharp

# csharp is now a DataObject with lazy fields:
csharp.Types             # collection of all types
csharp.Methods           # collection of all methods

# Scoped to a path:
let codebase = csharp.Code('path/to/project')
codebase.Types           # types in that project only
codebase.Methods         # methods in that project only
```

### Provider Contract

A provider:
1. Exposes a **DataObject** with lazy fields for its collections
2. Responds to field access by querying data on demand
3. Supports **filter pushdown** — `:` predicates passed down for optimization
4. Operates **read-only** — providers never mutate data

---

## Packages

Packages are reusable collections of predicates, functions, types, and commands.

```cop
import my-package

# All exported predicates become extension methods in scope
# All exported functions, types, commands become available
```

### Visibility

```cop
export predicate isClient(Type) => item.Name.endsWith('Client')   # visible to importers
predicate helper(Type) => ...                                      # internal only
```

---

## Output and Commands

Commands are the only place with side effects.

```cop
# foreach iterates and outputs:
foreach Types:isPublic => '{item.Name}'

# Named commands:
command CHECK = foreach Violations => '{item.Message}'

# Output actions:
PRINT('message')
SAVE('path.md', template)
```

### Templates

```cop
'{item.Name} has {item.Methods.Count} methods'
'{Warning@yellow}: {item.Message}'       # styled text
```

---

## Design Invariants

1. **One type.** Every composite value is a DataObject. No proxy types, no wrappers, no special cases.
2. **`.` is always field access / extension method.** Same operation at every level of the tree.
3. **`:` is always filter (Where).** Preserves item type. Reduces cardinality.
4. **Lazy and memoized.** Nothing evaluates until demanded. Once forced, the result is cached.
5. **Predicates are extension methods.** Resolved by first-parameter type. One definition works everywhere (single item, filter, any, all).
6. **Providers are DataObjects.** Not namespaces. Not magic. Just DataObjects with lazy fields.
7. **Pipeline flows left to right.** `types:isPublic.Name.Count` reads naturally from left to right.
