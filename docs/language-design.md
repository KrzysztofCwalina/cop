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

### Types (Shape Descriptors)

Types describe the **shape** of objects — what fields they have and what kind of values those fields hold. DataObject is the invisible runtime representation; users think in terms of types.

```cop
type Request = {
    Method: string,
    Path: string,
    Headers: [Header],
    Body: string?
}

type Response = {
    StatusCode: int,
    Body: string,
    ContentType: string
}

type Type = {
    Name: string,
    Namespace: string,
    Methods: [Method],
    Properties: [Property],
    Visibility: string
}
```

Types serve four purposes:
1. **Shape description** — what fields an object has
2. **Parameter matching** — `function f(Type) =>` means "accepts objects shaped like Type"
3. **Pattern matching** — constrained dispatch based on value (see below)
4. **Tooling** — library documentation, statement completion, type-ahead suggestions

### The Object Tree (Uniform Thunks)

At runtime, everything from providers down to leaf fields is a DataObject. The tree is uniform:

```
csharp                → DataObject (typed as Provider; fields: Types, Methods, ...)
  .Types              → DataObject (iterable of Type-shaped DataObjects)
    [0]               → DataObject (typed as Type; fields: Name, Methods, ...)
      .Methods        → DataObject (iterable of Method-shaped DataObjects)
        [0]           → DataObject (typed as Method; fields: Name, Parameters, ...)
          .Name       → scalar (string — terminal)
```

**Every `.` is the same operation** at every level. DataObject is the uniform runtime mechanism, but the USER sees typed objects (Type, Method, Request, etc.).

### What is `csharp`?

`import csharp` binds the name `csharp` to a provider — a DataObject with typed, lazy fields:
- `.Types` — iterable of Type objects (loaded on demand)
- `.Methods` — iterable of Method objects (loaded on demand)
- `.Code('path')` — function that returns a scoped view

`csharp.Types` is field access. The same `.` operation at every level. But `csharp` is typed (it's a Provider), and `csharp.Types` returns objects typed as Type.


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

## Pattern Matching (Value-Based Dispatch)

Functions can have **multiple overloads** with the same name and parameter type. The runtime selects the most specific match based on **value constraints** — like Haskell's pattern matching with guards.

### Basic pattern matching

```cop
function handle(Request:Path.equals('/')) => Response {
    StatusCode = 200
    Body = '{"message":"hello world!"}'
    ContentType = 'application/json'
}

function handle(Request:Path.equals('/health')) => Response {
    StatusCode = 200
    Body = '{"status":"healthy"}'
    ContentType = 'application/json'
}

function handle(Request) => Response {
    StatusCode = 404
    Body = '{"error":"not found"}'
    ContentType = 'application/json'
}
```

The runtime tries each overload top-to-bottom. The first match wins. The unconstrained overload is the fallback (like Haskell's `_`).

### Constraint syntax

The parameter type can be followed by `:` constraints — the same filter syntax used on collections:

```cop
function f(Type:isPublic) => ...             # matches when item is public
function f(Type:Name.startsWith('I')) => ... # matches when name starts with 'I'
function f(Type) => ...                      # matches any Type (fallback)
```

This is the `:` (filter/Where) operator applied to the parameter: "this function handles the subset of Type values where the constraint holds."

### Analogy to Haskell

```haskell
-- Haskell:
handle req | path req == "/"       = Response 200 "hello"
handle req | path req == "/health" = Response 200 "healthy"
handle req                         = Response 404 "not found"
```

```cop
-- Cop:
function handle(Request:Path.equals('/'))       => Response { ... }
function handle(Request:Path.equals('/health')) => Response { ... }
function handle(Request)                        => Response { ... }
```

The semantics are identical. The constraint after the type IS the guard.

### Why this works with extension methods

Since predicates are extension methods, they can be used as constraints:

```cop
predicate isGetRequest(Request) => item.Method.equals('GET')
predicate isPostRequest(Request) => item.Method.equals('POST')

function handle(Request:isGetRequest:Path.equals('/users')) => Response { ... }
function handle(Request:isPostRequest:Path.equals('/users')) => Response { ... }
function handle(Request) => Response { StatusCode = 405, ... }
```

The `:` chain in the parameter is an AND of conditions — same semantics as collection filtering.


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

Providers are the bridge between external data and the Cop type system.

### What is `csharp`?

`csharp` is an **object**. Its type is defined by the csharp provider package:

```cop
# Defined by the csharp package (simplified):
type CSharpProvider = {
    Types: function(path: string) => [Type],
    Methods: function(path: string) => [Method],
    Fields: function(path: string) => [Field],
    Properties: function(path: string) => [Property],
    Events: function(path: string) => [Event],
    Statements: function(path: string) => [Statement]
}
```

The fields are **functions** — they take a path and return typed collections:

```cop
import csharp

# Types is a function that takes a path to scan:
let types = csharp.Types('c:\git\myproject')     # → [Type, Type, ...]
let methods = csharp.Methods('c:\git\myproject') # → [Method, Method, ...]

# Filter as usual:
let publicTypes = types:isPublic
let bigTypes = types:Methods.Count > 20
```

### `import` — formal definition

`import <package>` does three things:

1. **Instantiates** — the runtime creates an instance of the package's module type (e.g., `CSharpProvider`)
2. **Binds a name** — the instance is bound to an immutable variable named after the package (e.g., `csharp`)
3. **Brings exports into scope** — the package's exported types, predicates, and functions become resolvable names

```cop
import csharp
#        │
#        ├─ (1) runtime creates: CSharpProvider { Types = <func>, Methods = <func>, ... }
#        ├─ (2) binds variable:  let csharp = <that instance>
#        └─ (3) scope injection: Type, Method, isPublic, ... are now resolvable names
```

So `import csharp` is equivalent to:
```cop
let csharp = <runtime-created CSharpProvider instance>
use csharp.[Type, Method, Statement, ...]     # bring exported types into scope
use csharp.[isPublic, isAbstract, ...]         # bring exported predicates into scope
```

(The `use` lines are not real syntax — they illustrate what `import` does behind the scenes.)


### Who creates what?

| What | Who creates it | When |
|------|---------------|------|
| The provider object (`csharp`) | The runtime, triggered by `import` | At import time |
| Objects in provider collections (`csharp.Types[0]`) | The provider, by reading external data | On first field access (lazy) |
| User-defined objects (`Violation { ... }`) | User code, via record construction | When the expression is evaluated |
| Function results (`checkMethodCount(type)`) | The function body | When the function is called |

The runtime is the only "magic" — it instantiates the provider when you `import`. Everything after that is regular field access, function calls, and record construction.



---

## Packages and User-Defined Types

Packages are reusable libraries of types, predicates, functions, and commands. They define **namespaces** — when you import a package, its exports become available under the package name.

### Defining a Package

A package is a directory with a `package.cop` manifest and source files:

```
packages/
  validation/
    package.cop
    src/
      types.cop
      predicates.cop
      rules.cop
```

**package.cop** — metadata:
```cop
name = 'validation'
version = '1.0.0'
description = 'Common validation types and predicates'
```

**src/types.cop** — define and export types:
```cop
type Severity = enum { Error, Warning, Info }

type Violation = {
    Rule: string,
    Message: string,
    Severity: Severity,
    File: string?,
    Line: int?
}

type ValidationResult = {
    Violations: [Violation],
    Passed: bool
}

export type Severity
export type Violation
export type ValidationResult
```

### Instantiating Types

Objects are created by naming the type and providing field values (like a record constructor):

```cop
# Direct construction — type name followed by field assignments:
let v = Violation {
    Rule = 'naming'
    Message = 'Type name too short'
    Severity = 'Warning'
    File = 'Foo.cs'
    Line = 12
}

# Optional fields can be omitted (default to null):
let v2 = Violation {
    Rule = 'naming'
    Message = 'Bad name'
    Severity = 'Error'
}

# Functions that return typed objects use the same syntax in their body:
function checkMethodCount(item: Type) => Violation {
    Rule = 'method-count'
    Message = '{item.Name} has {item.Methods.Count} methods'
    Severity = 'Warning'
    File = item.File
    Line = item.Line
}
```

Three ways instances come into existence:
1. **Record construction** — `TypeName { Field = value, ... }` (explicit)
2. **Function return** — a function body is a record constructor for its return type
3. **From providers** — providers produce typed objects (e.g., `csharp.Types` yields Type instances)


**src/predicates.cop** — extension methods on the types:
```cop
export predicate isError(Violation) => item.Severity.equals('Error')
export predicate isWarning(Violation) => item.Severity.equals('Warning')
export predicate isInFile(file: string, item: Violation) => item.File.equals(file)

export function violation(rule: string, message: string, severity: Severity) => Violation {
    Rule = rule
    Message = message
    Severity = severity
    File = null
    Line = null
}

export function validate(violations: [Violation]) => ValidationResult {
    Violations = violations
    Passed = violations:isError.Count.equals(0)
}
```

**src/rules.cop** — analysis rules using the types:
```cop
import csharp

export predicate tooManyMethods(Type) => item.Methods.Count > 20

export function checkMethodCount(item: Type) => Violation {
    Rule = 'method-count'
    Message = '{item.Name} has {item.Methods.Count} methods (max 20)'
    Severity = 'Warning'
    File = item.File
    Line = item.Line
}
```

### Using a Package

A consumer imports the package. All exported types, predicates, and functions become available under the package namespace:

```cop
import csharp
import validation

# Types from the package are now available for pattern matching:
let codebase = csharp.Code('c:\projects\myapp')

# Use exported predicates as extension methods:
let violations = codebase.Types:tooManyMethods.map(checkMethodCount)

# Use exported types for filtering/querying:
let errors = violations:isError
let warnings = violations:isWarning
let fileIssues = violations:isInFile('UserService.cs')

# Use exported functions to create typed objects:
let result = violations:validate

# Higher-order ops work on the typed collection:
let hasErrors = violations.any(isError)
let errorCount = violations.count(isError)

# Output:
command CHECK = foreach violations => '{item.Severity}: [{item.Rule}] {item.Message}'
```

### Namespaces

When two packages export the same name, qualify with the package namespace:

```cop
import csharp
import python
import validation

# Both csharp and python might export a 'Type' type.
# Collections are qualified by provider:
let csTypes = csharp.Types
let pyTypes = python.Types

# Predicates resolve by parameter type — no ambiguity if types differ.
# But if names collide, qualify:
let errors = violations:validation.isError
```

### Visibility Rules

```cop
# Exported — visible to importers:
export type Violation = { ... }
export predicate isError(Violation) => ...
export function validate(...) => ...
export command CHECK = ...

# Internal — only visible within the package:
type InternalHelper = { ... }
predicate helper(Violation) => ...
```

Only `export`-marked declarations are accessible to importing code. Everything else is package-internal.


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
