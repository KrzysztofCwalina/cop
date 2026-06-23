# Agent Cop Language Reference

Agent Cop is a data processing DSL optimized for writing static analysis and report generation programs. Files use the `.cop` extension.

The language combines **declarative filtering** with **functional expressions** for data transformation and analysis:

- **Data types** — primitives, lists, and objects (maps with named properties)
- **Declarations** — `let` bindings, `type` definitions, `predicate` and `function` definitions, `import`/`export`
- **Filtering** — subset (`:`) narrows collections with predicates; superset (`&`) combines schemas
- **Expressions** — member access, lambdas (`.Select(expr)`), ternary conditionals, match expressions, arithmetic, string predicates
- **Commands** — `foreach` for iteration with templates, `SAVE` for file output

> **Note:** Most examples use the [`code` package](packages/code.md) (`import code`), which provides types for source code analysis. See [Code Package Reference](packages/code.md) for the full type catalog.

## Data Model

### Primitives

| Type | Description |
|------|-------------|
| `string` | Text values |
| `int` | Integer values (64-bit signed) |
| `float` | Floating-point values (64-bit double) |
| `bool` | `true` or `false` |
| `byte` | Integer 0-255 |
| `nic` | Null — absence of a value |

### Lists

A list is an ordered sequence of items. List **types** are written as `[T]`:

```ruby
[string]             # list of strings
[int]                # list of integers
[Type]               # list of Type objects
```

#### List Literals

A list literal is written as `[elements]` — elements are space-separated:

```ruby
[1 2 3]              # int list
['Get' 'Set' 'Create']  # string list
[]                   # empty list
```

List literals can be used anywhere an expression is expected — as predicate arguments, in `let` declarations, or combined with `+`:

```ruby
Name:containsAny(['Get' 'Set'])       # inline list as predicate argument
Name:in(['Create' 'Update' 'Delete']) # inline list for membership check
let Allowed = ['Get' 'Set' 'Create']  # list literal bound to a name
let Combined = [1 2] + [3 4]         # list concatenation → [1 2 3 4]
let Appended = [1 2] + 3             # append element → [1 2 3]
```

Lists and objects are the fundamental data structures. Packages provide collections of objects to process. Filtering produces subsets. Expressions transform individual values.

### Objects

An object is a map of named properties. Each property has a name (key) and a value. Keys can be identifiers or quoted strings:

```ruby
let person = {
    Name = 'Alice'
    Age = 42
}
let colors = {
    'error' = 'red'
    'warning' = 'yellow'
    'info' = 'blue'
}
```

Both forms produce the same runtime type. Use quoted keys when names contain special characters (e.g., `'content-type'`).

**Types** describe the expected shape of an object:

```ruby
type Person = { Name : string, Age : int }
```

#### Property Access

The dot operator (`.`) accesses properties by name:

```ruby
person.Name              # 'Alice'
person.Age               # 42
```

Dynamic access uses `.Get(key)`:

```ruby
person.Get('Name')       # same as person.Name
```

#### Object Operations

These work on all objects — literals, function results, and provider objects (Types, Methods, etc.):

```ruby
obj.Get('Name')          # dynamic property lookup (case-insensitive)
obj:containsKey('Name')  # true if property exists
obj.Keys                 # list of all property names
obj.Values               # list of all property values
obj.Count                # number of properties
```

## Declarations

A `.cop` file contains these kinds of declarations:

| Declaration | Purpose |
|---|---|
| `feed` | Declare where to find packages (GitHub repo or local path) |
| `import` | Bring types and lists from a package into scope |
| `export` | Make a declaration visible to importing packages |
| `type` | Describe the shape of an object’s property list |
| `flags` | Define a flags enum for bitwise operations |
| `enum` | Define an extensible enum with named values |
| `let` | Declare a named list (base or subset) |
| `function NAME() = { ... }` | Define a named output function (implicit output, SAVE, or composition) |
| `foreach` | Iterate over a collection sequentially |
| `async foreach` | Iterate over a collection with parallel processing |
| `predicate name(Param) =>` | Define a named predicate for subsetting |
| `function name(Param) =>` | Define a named function (expression-body or record-body) |
| `SAVE` | Output action that writes to a file |
| `DEBUG` | Output action that writes to console only when `-d` flag is active |

Declarations are **private to the current project** (folder of `.cop` files) unless prefixed with `export`.

### Imports

Use `import` to bring types and lists from a package into scope:

```ruby
import code
```

Package import statements must appear before predicates and UPPERCASE functions.

#### Type Member Imports

`import` can also promote the members of a `flags` or `enum` type into scope, so they can be used as bare names:

```ruby
import Modifier
```

This makes all members of `Modifier` (e.g., `Public`, `Static`, `Abstract`) available as bare identifiers in the current file. Without this, members must be qualified: `Modifier.Public`.

Type member imports appear in the body of the file (after declarations begin), distinguishing them from package imports at the top.

### Feed

`feed` directives tell cop where to find packages for `import` statements:

```ruby
feed 'github.com/owner/repo'     # remote GitHub feed
feed '../my-packages'             # local relative path
```

Remote feeds point to GitHub repos containing a `packages/` directory. Local feeds point to directories on disk. Feed directives must appear before `import` statements.

#### Importing packages from GitHub

To use packages hosted in a GitHub repository:

1. Declare the feed and imports in your `.cop` file:

```ruby
feed 'github.com/KrzysztofCwalina/cop'
import code
import csharp-library
```

2. Restore packages locally (downloads them into your project's `.cop/` directory):

```bash
cop package restore my-checks.cop
```

3. Run your program (imports resolve from the local `.cop/packages/` directory):

```bash
cop my-checks.cop
```

The `cop package restore` command reads `feed` and `import` declarations, downloads the referenced packages from GitHub, resolves transitive dependencies, and places files under `.cop/` in your project root (e.g., `.cop/packages/`, `.cop/checks/`). After restore, `cop` resolves imports entirely from local directories — no network access is required at runtime.

> **Tip:** Commit the restored `.cop/` directory to version control so CI/CD pipelines and teammates don't need to run `cop package restore` separately.

### Export

`export` makes declarations visible to packages that import the current package. Without `export`, declarations are private to the current project (folder of `.cop` files):

```ruby
export predicate isClient(Type) => Type.Name:endsWith('Client')
export let Clients = csharp.Types:isClient
export function LIST-CLIENTS() = { foreach Clients => '{item.Name}' }
export type ClientInfo = { Name : string, Path : string }
export function clientInfo(Type) => ClientInfo { Name = Type.Name, Path = Type.File.Path }
```

Any declaration — `predicate`, `let`, `type`, `flags`, or `function` — can be exported.

#### Type Member Exports

`export` followed by a type name promotes the members of a `flags` or `enum` type into scope **and** makes them available to importers of the package:

```ruby
flags Modifier = Public | Private | Protected | Static | Sealed | Abstract
export Modifier
```

When a package exports a type, any file that imports that package gets the type's members as bare names (e.g., `Public` instead of `Modifier.Public`). Without `export`, the type is defined but its members require qualified access.

### Types

Types describe the property structure of objects:

```ruby
# Object with named properties
type Foo = { Name : string, Age : int }

# Optional properties (may be nic)
type Bar = { Value : string? }

# Properties whose values are lists
type Baz = { Items : [string], Count : int }

# Superset — has all of Method’s properties plus any additional ones
type Constructor = Method & {}
type SpecialMethod = Method & { Extra : string }
```

The `&` operator creates a property **superset** — `Constructor = Method & {}` means a Constructor has all the properties of a Method (and is a distinct nominal type).

### Flags

`flags` defines a set of named bit constants:

```ruby
flags Modifier = Public | Private | Protected | Internal | Static | Sealed | Abstract | Virtual
```

Each member is assigned a power of 2 automatically (Public=1, Private=2, Protected=4, ...). Test flags with the `:isSet()` and `:isClear()` predicates on integer properties:

```ruby
predicate isPublic(Type) => Type.Modifiers:isSet(Public)
predicate isNotAbstract(Type) => Type.Modifiers:isClear(Abstract)
```

| Predicate | Meaning |
|-----------|---------|
| `isSet(flag)` | True if the flag bit is set — `(value & flag) != 0` |
| `isClear(flag)` | True if the flag bit is clear — `(value & flag) == 0` |

Flag members can always be qualified with the type name (`Modifier.Public`). To use bare names (`Public`), the type must be imported with `import Modifier` (local scope) or the defining package must use `export Modifier` (available to all importers).

The `code` package defines a `Modifier` flags enum, exports its members, and provides `isX` predicates for all common modifiers (see [Code Package Reference](packages/code.md)).

### Enums

`enum` defines an extensible set of named string constants:

```ruby
enum TypeKind = Class | Struct | Interface | Enum
enum Language = csharp | python | javascript | rust | go | java | cop | text
```

Enum members are available as bare identifiers when the defining package exports the enum. Compare enum-typed properties directly to members:

```ruby
predicate isClass(Type) => Type.Kind == Class
predicate isCSharp(Type) => Type.File.Language == csharp
```

**Type-safe comparisons:** Comparing an enum-typed property to a raw string literal is an error caught by `cop verify`:

```ruby
# ERROR — use the enum member or explicit cast
predicate isClass(Type) => Type.Kind == 'class'
```

**Explicit cast for extensibility:** Since enums are extensible (providers may return values not in the defined set), use the enum name as a constructor to cast a string:

```ruby
# OK — explicit cast wraps the string as an enum value
predicate isCustom(Type) => Type.Kind == TypeKind('CustomKind')
```

This makes the intent clear: the developer knows the value isn't a predefined member but wants to compare it anyway.

### Let Declarations

`let` declares a named list. It has several forms:

**Base list** — declares a typed list whose data is provided by a package:

```ruby
let Types : [Type]          # a list of Type objects
let Statements : [Statement]
```

**Subset** — filters an existing list using predicates:

```ruby
let Clients = Types:isClient              # subset of Types where isClient is true
let Calls = Statements:call               # subset of Statements where call is true
let PublicClients = Clients:isPublic       # subset of a subset
```

**List literal** — binds a name to an inline list value:

```ruby
let Keywords = ['Test' 'Bench' 'Perf']
let Thresholds = [1 5 10 50]
let Empty = []
```

List literal bindings can be used wherever a list name is expected — for example as an argument to `containsAny` or `in`:

```ruby
let Prefixes = ['Get' 'Set' 'Create']
predicate hasBadPrefix(Method) => Method.Name:containsAny(Prefixes)
```

**Expression** — binds a name to an arbitrary computed value:

```ruby
let count = Types.Count                       # scalar from collection property
let names = Types.Select(item.Name)           # derived list
let total = Types.Sum(item.Methods.Count)     # aggregate
let label = 'Total: ' + count                 # string concatenation
```

Expression bindings can be referenced in templates using `{name}`:

```ruby
let count = Types.Count
foreach Types => '{count} types in total, current: {item.Name}'
```

**External data** — loads a JSON file into a typed collection (requires `import json`):

```ruby
type Person = { name : string, age : int }
let People = Parse('data.json', [Person])
```

`Parse(path, [Type])` reads a JSON file containing a top-level array and deserializes each element into a typed object. See [JSON Package Reference](packages/json.md).

**Path-scoped** — queries a provider against a specific directory:

```ruby
let sdkTypes = csharp-checks.Types('../azure-sdk/')
let sdkPublic = csharp-checks.Types('../azure-sdk/'):isPublic
let localFiles = files.Files('src/lib/')
```

Path-scoped collections query the provider against the given path instead of the default root (CWD or `-t`). The path is resolved relative to the process working directory. Results are cached by `(provider, collection, absolutePath)` so repeated references with the same path are efficient. Each collection is parameterized individually — `csharp-checks.Types('../sdk/')` does not affect `csharp-checks.Statements`.

**Typed binding** — associates a type annotation with a let value for schema enforcement:

```ruby
let db : SampleData = provider('sample')
let cb : Codebase = provider('csharp')
```

The type annotation (`: TypeName`) tells the runtime to enforce the type's declared properties on the bound value. This is used with `provider()` to turn a dynamic accessor into a schema-checked one. See [Provider Accessors](#provider-accessors-provider) for details.

### Pipe Operators

Providers expose globals that return typed lists (e.g., `Types`, `Requests`). The pipe operator (`=>`) dequeues items from a source, transforms them, and enqueues results into a sink:

```ruby
# foreach = repeat { dequeue from source → transform → enqueue to sink }
function SERVE() = { foreach Requests => handle => RESPONSES }

# async foreach = process items concurrently (parallel)
function SERVE() = { async foreach Requests => handle => RESPONSES }
```

Any global returning a list can serve as a source. Sinks are provider-registered targets (e.g., `Send`, `console`, `file`). The runtime handles thread-safe enqueue/dequeue. Use `async foreach` when items can be processed independently (e.g., HTTP requests).

### Predicates

A predicate is a named boolean expression that operates on a typed item. Predicates are the primary mechanism for creating subsets:

```ruby
predicate isClient(Type) => Type.Name:endsWith('Client')
predicate isPublicAsync(Method) => Method:isPublic && Method:isAsync
predicate usesVar(Statement) => Statement.Keywords:contains('var')
```

Predicates compose by reference:

```ruby
predicate isOptionsType(Parameter) => Parameter.Type.Name:endsWith('Options')
predicate hasOptions(Method) => Method.Parameters:any(isOptionsType)
predicate isMissingOptions(Type) => Type.Constructors:none(hasOptions)
```

**Subset predicates** — a predicate over a list name creates a named subset. The predicate’s body filters are AND-combined with the base list:

```ruby
predicate Clients(Types) => isClient && !isAbstract
```

This declares `Clients` as a subset of `Types` where `isClient` is true and `isAbstract` is false.

**Narrowing predicates** — a predicate can narrow items to a more specific type using `: NarrowedType`:

```ruby
predicate isCall(Statement) : Call => Statement.Kind == call
```

When applied as a filter, items are narrowed to `Call` (a superset of Statement’s properties).

Language providers use the same mechanism to expose **language-specific AST** on top of the
common `Codebase` model. The narrowing applies to **Types, Methods, and Statements** alike —
the same `:as<Language>` predicate is overloaded for each. For example, the `csharp` package
narrows a `Type` to a `CSharpType`, a `Method` to a `CSharpMethod`, and a `Statement` to a
`CSharpStatement` (each adding C#-only fields):

```ruby
predicate asCSharp(Type) : CSharpType => Type.File.Language == csharp
predicate asCSharp(Method) : CSharpMethod => Method.File.Language == csharp
predicate asCSharp(Statement) : CSharpStatement => Statement.File.Language == csharp

# Type facts (records), method facts (extension methods),
# and statement facts (lock / control blocks / error handling):
let records = codebase.Types:asCSharp:isRecord
    :toError('{item.Name} is a record')
let ext-methods = codebase.Methods:asCSharp:isExtensionMethod
    :toWarning('{item.Name} is an extension method')
let locks = codebase.Statements:asCSharp:isLock
    :toInfo('lock at line {item.Line}')
```

This keeps the common model language-agnostic — multi-language checks still run over plain
`Type`/`Method`/`Statement` — while a check that needs C# specifics narrows with `:asCSharp`
and reads the extra fields. The narrowed values still satisfy their base type, so base
predicates (`isPublic`, `isCSharp`, …) and `toError`/`toWarning` continue to work after narrowing.

> **Write language-specific checks only when the language-agnostic model can't express the
> rule.** Most rules should use `codebase.Types`, `Type.Name`, `Type.Kind`, `Type.Modifiers`,
> `Type.BaseTypes`, `Type.Decorators`, etc. — they work across every language. Reach for a
> language narrowing (`:asCSharp`, `:asRust`, `:asJava`, `:asPython`, `:asGo`, `:asJavaScript`)
> **only** for a fact the common model genuinely lacks (e.g. C# `record`/`partial`, Rust
> `unsafe`/traits, Java `record`). Run `cop help <language>` to see each language's extra fields.

#### Constrained Predicates

Append `:constraint` to the parameter type to create predicate overloads constrained by another predicate. The constraint is any predicate — language names like `csharp` and `python` are just predicates that match by file language:

```ruby
predicate isClient(Type) => Type.Name:endsWith('Client')
predicate isClient(Type:isCSharp) => Type.Name:endsWith('Client')
predicate isClient(Type:isPython) => Type.Name:endsWith('_client')
```

Resolution order:
1. Exact constraint match (e.g., `:isPython` for Python files)
2. Unconstrained fallback
3. No match → `false`

Constraints are not limited to languages — any predicate can be used:

```ruby
predicate isSealed(Type:isCSharp) => Type:isSealed
predicate isSealed(Type:isPython) => Type.Decorators:any(Decorator:contains('final'))
```

### Functions

Functions come in two forms: **expression-body** (returns a computed value) and **record-body** (produces a structured object).

#### Expression-Body Functions

An expression-body function takes a named, typed parameter and returns the result of an expression:

```ruby
function inc(x:float) => x + 1
function double(n:float) => n * 2
function greet(name:string) => 'Hello, ' + name
function isLarge(t:Type) => t.Methods.Count > 20
```

Expression-body functions can be called anywhere an expression is expected:

```ruby
inc(5)                    # → 6
inc(inc(5))               # → 7
5:inc                     # → 6 (colon pipe: same as inc(5))
5:inc:double              # → 12 (chain: inc(5) then double(6))
double(3) + 1             # → 7
Types.Select(isLarge)     # → list of booleans
```

#### Function Type Annotations

Parameters that accept callable values (predicates, projections, accumulators) use function type syntax to express their full signature:

```ruby
# (ParamTypes) => ReturnType
function where(items: [object], condition: (object) => bool) : [object]
function select(items: [object], transform: (object) => object) : [object]
function reduce(items: [object], accumulator: (object, object) => object, initial: object) : object
function sum(items: [object], project: (object) => float) : int
```

This enables documentation tooling to show full signatures and allows future compile-time verification that passed functions match the expected signature.

When calling these functions, you can pass either a named predicate/function or an inline lambda:

```ruby
Types:where(isPublic)                    # named predicate
Types:where((t) => t.Name:startsWith('I'))  # inline lambda
Types:select((t) => t.Name)              # projection lambda
items:reduce((acc, x) => acc + x, 0)     # accumulator lambda
```

#### Record-Body Functions

A record-body function produces a structured object with field mappings. The return type comes after `=>`:

```ruby
function clientInfo(Type) => ClientInfo {
    Name = Type.Name
    Path = Type.File.Path
}
```

This creates a `ClientInfo` record for each item, mapping properties from the input. Functions can be used with `.Select()` to project a collection:

```ruby
let details = Clients.Select(clientInfo)
```

#### Constrained Overloads

Functions can include inline filter constraints to create pattern-matched overloads. The constraint acts as a guard — the first matching overload is selected:

```ruby
function handle(Request:Path:equals('/')) => ok({ message: 'hello world!' })

function handle(Request:Path:equals('/health')) => ok

function handle(Request) => notFound()
```

The constraint syntax is the same filter chain used elsewhere: `Type:Field:predicate(args)`. Constrained overloads are evaluated in order; the unconstrained overload serves as the default fallback.

#### Partial Application (Currying)

When a function is called with fewer arguments than it requires, it returns a **closure** — a partially-applied function that waits for the remaining arguments:

```ruby
function format(Type, prefix: String, suffix: String) => '{prefix}{item.Name}{suffix}'

# Partial application: binds prefix, returns closure waiting for suffix
let bracketed = format('[')

# Complete the call by supplying the remaining argument
foreach csharp.Types:bracketed(']') => '{item}'
# Output: [MyClass], [MyInterface], etc.
```

Closures can be used in filter chains just like regular functions. They remember their bound arguments and apply them when invoked with the remaining ones.

#### Code() Aggregator Function

The built-in `Code()` function creates a lazy proxy that queries one or more code providers and unions their results:

```ruby
import csharp-checks
import python-checks

# Query a single provider
let cs = Code([csharp-checks])
foreach cs.Types:isPublic => '{item.Name}'

# Query multiple providers — results are unioned
let codebase = Code([csharp-checks, python-checks])
foreach codebase.Types => '{item.Name}'
```

Provider identifiers must be imported packages. The proxy exposes the same collections as the providers (Types, Methods, Statements, etc.). Collections are queried lazily — only when accessed.

> **Note:** `Code.Types` (the legacy syntax) still works and resolves to the ambient code collections. `Code([csharp])` is the explicit, composable alternative.

## Operations

Agent Cop uses two operators for accessing members:

- **`:` (colon)** — applies a predicate or function **to each item**. On collections it filters (`:isPublic`) or quantifies (`:any(pred)`, `:all(pred)`, `:none(pred)`, `:count(pred)`). On single values it pipes through a function (`:Text`, `:ok`).
- **`.` (dot)** — operates **on the object or collection itself**. Accesses properties (`.Name`, `.Count`), transforms collections (`.Where()`, `.Select()`, `.OrderBy()`), and projects values.

### Naming Convention

Built-in names follow a consistent casing convention based on their role:

| Convention | Role | Operator | Examples |
|-----------|------|----------|---------|
| `camelCase` | Predicates (return bool, applied per-item) | `:` | `startsWith`, `endsWith`, `any`, `none`, `isSet` |
| `PascalCase` | Transforms & properties (return values) | `.` | `Where`, `Select`, `Count`, `Text`, `Trim` |
| `UPPERCASE` | Output functions and actions (produce side effects) | — | `FAIL`, `PRINT`, `SAVE`, `ASSERT`, `DEBUG` |

User-defined predicates and functions should follow the same convention: `camelCase` for predicates, `PascalCase` for transforms and record-body functions.

### Subset (`:`)

The `:` operator filters a list with a predicate, producing a subset:

```ruby
Types:isClient                       # Types where isClient is true
Statements:isCSharp:usesVar            # Statements in C# files using var
Types:isClient:notSealed             # AND-chained: client types that aren't sealed
Types:isClient:!isAbstract           # negated filter
```

Multiple `:` filters are AND-combined — each filter produces a smaller subset.

### Value Pipe (`:`)

On a single value (not a collection), `:` calls a function with that value as input:

```ruby
someBytes:Text                       # Text(someBytes) — convert bytes to string
someString:ok                        # ok(someString) — create HTTP 200 response
expr:Text:ok                         # chain: Text(expr), then ok(result)
Request.Body:Text:complete.Body:Text:ok  # full pipe chain
```

This enables left-to-right data flow instead of nested function calls:

```ruby
# Nested (harder to read):
ok(Text(http.Post(url, headers, body).Body))

# Piped (reads left-to-right):
http.Post(url, headers, body).Body:Text:ok
```

Overload resolution uses the target’s type — `stringValue:ok` resolves to `ok(string)`, not `ok(Request)`.

Built-in functions (`Text`, `File`) also work via colon pipe when called without arguments.

### Superset (`&`)

On types, `&` merges property schemas (the result has all properties of both sides):

```ruby
type Constructor = Method & {}
type Call = Statement & { Signature : string }
```

### Member Access (`.`)

The dot operator navigates object properties — it is syntactic sugar for looking up a named property and returning its value:

```ruby
Type.Name                  # string value of the Name property
Type.Methods               # list of Method objects
Type.Methods.Count         # number of items in the list
Method.Parameters.First    # first item in the list
```

### Primitive Operations

#### Boolean Operators

```ruby
A && B          # logical AND
A || B          # logical OR
!A              # logical NOT
```

#### Bitwise Operators

```ruby
X & Y           # bitwise AND (used with flags enums)
X | Y           # bitwise OR
```

#### Comparison

```ruby
X == Y          # equality
X != Y          # inequality
X > 1           # greater than
X < 10          # less than
X >= 5          # greater than or equal
X <= 100        # less than or equal
```

**Enum-typed comparisons:** When a property is declared with an enum type (e.g., `Kind : TypeKind`), compare it to an enum member, not a raw string:

```ruby
Type.Kind == Class              # correct — enum member
Type.Kind == TypeKind('Class')  # correct — explicit enum cast
Type.Kind == 'Class'            # ERROR — string literal vs enum field
```

#### Ternary Conditional

```ruby
condition ? trueExpr | falseExpr
```

Binary choice: if `condition` is truthy, evaluates `trueExpr`; otherwise `falseExpr`.

```ruby
Type.IsPublic ? 'public' | 'internal'
isAbstract ? Type.IsPublic ? 'abs-pub' | 'abs-priv' | 'concrete'   # nested
```

#### Match Expression

Multi-branch conditional that tests a discriminant against patterns:

```ruby
discriminant ? pattern1 => result1 | pattern2 => result2 | _ => default
```

Each arm is `pattern => result`. Arms are evaluated left to right; the first matching pattern wins. `_` is the wildcard (matches anything). String matching is case-insensitive.

```ruby
# Map severity to color
item.Severity ? 'error' => 'red' | 'warning' => 'yellow' | _ => 'white'

# Classify types
Type.Methods.Count ? 0 => 'empty' | _ => 'has-methods'

# Use in templates
foreach Types => '{item.Name}: {item.Accessibility ? 'public' => '🟢' | _ => '⚪'}'
```

If no arm matches and no `_` default exists, the expression returns nic (falsy).

#### String Predicates

```ruby
Name:endsWith('Client')              # case-insensitive suffix match
Name:startsWith('Azure')               # case-insensitive prefix match
Name:contains('Options')             # case-insensitive substring match
Name:matches(@'\bList<.*>')         # regex match (case-sensitive)
Name:equals('Program')             # case-insensitive equality
Name:notEquals('Object')              # case-insensitive inequality
Name:sameAs('configure_await')     # convention-insensitive (matches ConfigureAwait, configureAwait, etc.)
Name:containsAny(['Get' 'Set'])        # any item in list is a substring
Name:in(allowedNames)          # value is a member of the list
```

| Predicate | Meaning |
|-----------|---------|
| `equals(v)` | Equal to (case-insensitive) |
| `notEquals(v)` | Not equal to |
| `startsWith(v)` | Starts with |
| `endsWith(v)` | Ends with |
| `contains(v)` | Contains substring |
| `containsAny(list)` | Any item in list is a substring |
| `matches(v)` | Matches regex (case-sensitive) |
| `sameAs(v)` | Convention-insensitive equality (ignores PascalCase/snake_case/camelCase) |
| `in(list)` | Value is a member of the list |
| `empty` | String is empty (zero length) |

#### Numeric Predicates

```ruby
Depth:greaterThan(3)                    # greater than
Depth:lessThan(10)                   # less than
Size:greaterOrEqual(100)                   # greater than or equal
Size:lessOrEqual(1000)                  # less than or equal
Depth:equals(0)                    # equal to
Size:notEquals(0)                     # not equal to
```

| Predicate | Meaning |
|-----------|---------|
| `equals(n)` | Equal to |
| `notEquals(n)` | Not equal to |
| `greaterThan(n)` | Greater than |
| `lessThan(n)` | Less than |
| `greaterOrEqual(n)` | Greater than or equal |
| `lessOrEqual(n)` | Less than or equal |
| `isSet(flag)` | Flags bit is set — `(value & flag) != 0` |
| `isClear(flag)` | Flags bit is clear — `(value & flag) == 0` |

### String Properties

```ruby
Name.Length                  # string length
Name.Lower                  # lowercase version
Name.Upper                  # uppercase version
Name.Normalized             # convention-insensitive canonical form (Foo_Bar → foobar)
Name.Words                  # split identifier into lowercase word list
```

### String Transforms

```ruby
Name.Trim('Async')           # remove suffix (→ 'GetItem' from 'GetItemAsync')
Name.Replace('old', 'new')   # replace substring
```

### List Properties

```ruby
Items.Count                  # number of items
Items.First                  # first item (nic if empty)
Items.Last                   # last item (nic if empty)
Items.Single                 # single item (nic if 0 or 2+)
```

### List Predicates (`:`)

Predicate applications test a list per-item and return a boolean. These use `:` because the predicate is applied **to each item**:

```ruby
Items:any(isPublic)          # true if any item matches (shorthand)
Items:none(isObsolete)       # true if no items match
Items:all(isPublic)          # true if all items match
Items:contains('value')      # true if list contains value
Items:empty                  # true if list has no items
```

Named predicates can be passed directly — no lambda wrapper needed:

```ruby
predicate isPublic(Type) => Type.Modifiers:isSet(Public)

Types:any(isPublic)          # equivalent to Types:any((item) => item:isPublic)
Types:all(isPublic)          # equivalent to Types:all((item) => item:isPublic)
Types:count(isPublic)        # count matching items
```

### List Transforms (`.`)

Transforms operate on the collection as a whole and return a new list or value. These use `.` because they are **collection-level operations**:

```ruby
Items.Where(predicate)       # subset of matching items — e.g. Items.Where(isPublic)
Items.First(predicate)       # first matching item
Items.Last(predicate)        # last matching item
Items.Single(predicate)      # single matching item
Items.ElementAt(n)           # item at index n
Items.Select(transform)     # project each item — e.g. Items.Select((t) => t.Name)
Items.Text(template)         # format each item and join into a single string
Items.OrderBy(keySelector)  # sort ascending — e.g. Items.OrderBy((t) => t.Name)
Items.OrderByDescending(keySelector) # sort descending
Items.Distinct(expr)         # deduplicate by expression (or by value if no arg)
Items.GroupBy(keySelector)  # group by key — e.g. Items.GroupBy((t) => t.Namespace)
Items.Sum(projection)        # sum numeric values — e.g. Items.Sum((f) => f.Size)
Items.Min(projection)        # minimum numeric value
Items.Max(projection)        # maximum numeric value
Items.Average(projection)    # average numeric value
Items.Reduce(accumulator, initial) # fold with (acc, item) => result
```

#### Collection Concatenation (`+`)

The `+` operator concatenates two collections of the same type:

```ruby
let allChecks = csharp-checks + python-checks
let combined = internalTypes + externalTypes
```

It also works with list literals:

```ruby
let numbers = [1 2] + [3 4]           # → [1 2 3 4]
let extended = numbers + 5            # → [1 2 3 4 5]
let words = ['Get' 'Set'] + ['Create'] # → ['Get' 'Set' 'Create']
```

#### String Concatenation (`+`)

The `+` operator also concatenates strings, including property values and literals:

```ruby
predicate hasAsyncName(Statement) => Types.MethodNames:contains(Statement.MemberName + 'Async')
```

#### Collection Flattening (Property Access on Lists)

Accessing a property on a collection flattens (SelectMany) that property across all items:

```ruby
# Types.MethodNames → flat list of all method names across all types
predicate hasAsyncVariant(Statement) =>
    Types.MethodNames:contains(Statement.MemberName + 'Async')
```

#### Select and Text Examples

`.Select()` projects each item into a new value using `item` as the element variable. `.Text()` formats each item and joins with newlines:

```ruby
let names = csharp.Types.Select(item.Name)
let nameLengths = csharp.Types.Select(item.Name.Length)
let summary = csharp.Types:client.text('{item.Name} — {item.File.Path}')
```

#### Sorting

`.OrderBy()` and `.OrderByDescending()` sort a collection by an expression:

```ruby
let sorted = Types.OrderBy(item.Name)
let byMethodCount = Types.OrderByDescending(item.Methods.Count)
```

#### Aggregation

`.Sum()`, `.Min()`, `.Max()`, `.Average()` compute aggregate values from a collection:

```ruby
let totalMethods = Types.Sum(item.Methods.Count)
let maxParams = Methods.Max(item.Parameters.Count)
let avgSize = Types.Average(item.Methods.Count)
```

#### Distinct

`.Distinct()` deduplicates items by expression (or by value when called without arguments):

```ruby
let uniqueNamespaces = Types.Distinct(item.Namespace)
let uniqueNames = names.Distinct()
```

#### GroupBy

`.GroupBy()` groups items by an expression. Returns a list of `Group` objects with `Key`, `Items`, and `Count` properties:

```ruby
let byNamespace = Types.GroupBy(item.Namespace)
foreach byNamespace => '{item.Key}: {item.Count} types'
```

#### Reduce

`.Reduce()` aggregates a collection into a single value. The first argument is the operator (as a string), the second is the item expression, and an optional third argument is the separator for string concatenation:

```ruby
let allNames = Types.Reduce('+', item.Name, ', ')
let total = Types.Reduce('+', item.Methods.Count)
```

#### Predicate-Based Collection Tests

Use `:any()`, `:none()`, and `:all()` to test sub-collections within predicates:

```ruby
predicate hasPublicCtor(Type) => Type.Constructors:any(isPublic)
predicate hasNoPublicMethods(Type) => Type.Methods:none(isPublic)
predicate isAllAbstract(Type) => Type.Methods:all(isAbstract)
```

#### Inline Expressions

Instead of defining a named predicate, write the condition inline using `item` as the element variable:

```ruby
Type.Methods:any(item:isPublic && item:isAsync)
Type.Constructors:none(item:isProtected)
Type.BaseTypes:any(item:contains('Service'))
File.Usings:any(item:contains('System.IO'))
```

The `item` keyword refers to the current element in the collection. It works with any expression — property access, predicates, arithmetic, ternary conditions:

```ruby
# Property access on item
Type.Methods:any(item.Name:startsWith('Get'))

# Arithmetic expression
Type.Methods:any(item.Parameters.Count > 5)

# Ternary expression
Types.Select(item.Methods.Count > 0 ? item.Name | 'empty')
```

### Built-in Functions

| Function | Signature | Description |
|----------|-----------|-------------|
| `provider(name)` | string → Object | Returns a dynamic accessor for the named provider |
| `source(name)` | string → Source | Returns an async streaming source handle |
| `sink(name)` | string → Sink | Returns an async sink handle |
| `Text(expr)` | any → string | Converts a value to its textual representation. Also: `expr:Text` |
| `read(path)` | string → string | Reads a file and returns its content (sandboxed, 10MB max). Also: `path:read` |
| `Path(pattern)` | string → bool | Tests if the current file path matches a glob pattern |
| `Matches(pattern)` | string → bool | Tests if the current item text matches a regex |

`Text` and `read` can be called via colon pipe: `response.Body:Text` is equivalent to `Text(response.Body)`.

`Path` uses glob patterns: `*` matches within a segment, `**` matches across segments, `?` matches one character.

#### Provider Accessors: `provider()`

`provider(name)` is the **intrinsic function** that returns a dynamic accessor object for any provider. Properties on the returned object generate queries to the named provider.

```ruby
import core
import code

# provider() — access any provider's collections
let db = provider('sample')
export let Widgets = db.Widgets       # queries 'sample' provider for Widgets

# Use with code providers too — type annotation enforces schema
let cb : Codebase = provider('csharp')
export let Types = cb.Types           # queries 'csharp' provider for Types
export let Statements = cb.Statements
```

**Dynamic vs. Typed access:**

Without a type annotation, `provider()` returns a fully dynamic object (similar to C# `dynamic`). Any property access is allowed — it becomes a query to the provider at runtime. If the provider doesn't have that collection, it fails.

With a type annotation on the let binding, the accessor is **schema-enforced** — only declared properties are accessible:

```ruby
# Define the schema for this provider
type SampleData = {
    Widgets : [Widget]
    Orders : [Order]
}

# Typed binding — only Widgets and Orders are accessible
let db : SampleData = provider('sample')
export let Widgets = db.Widgets     # ✓ allowed
export let Orders = db.Orders       # ✓ allowed
# db.Unknown                        # ✗ error: 'Unknown' is not defined on type 'SampleData'
```

This gives provider package authors the ability to document and enforce their schema while still using the same dynamic query mechanism under the hood. The `Codebase` type in the `code` package is an example of a typed schema — it declares all standard code collections (Types, Statements, Lines, etc.).

#### Streaming Accessors: `source()` and `sink()`

`source(name)` and `sink(name)` are intrinsic functions for **async streaming providers**. They return `Source` and `Sink` handles used in `async foreach` pipelines.

```ruby
import core

# Type annotation declares what items the source produces
export let Requests : [Request] = source('http')

# Type annotation declares what items the sink accepts
export let RESPONSES : [Response] = sink('http')

# Usage in a streaming pipeline:
async foreach Requests => handle => RESPONSES
```

Like `provider()`, the typed annotation provides documentation and schema enforcement — it declares the item type flowing through the stream or into the sink. Without an annotation, items are untyped.

## Output Functions

Output functions produce side effects — output to the console, files, or test results. Use `foreach` to iterate over a collection:

```
foreach List:filter1:filter2 => 'template expression'
```

Output functions are **named** with UPPERCASE `function` declarations, which makes them invocable by name with `cop <name>`:

```cop
function LIST-TYPES() = { foreach Types => '{item.Name}' }
function EXPORT-NAMES() = { foreach Types:isCSharp:client => save('names.txt', '{item.Name}') }
```

Tests are declared with the `test` keyword:

```cop
test has-types = assert(csharp.Types.Count > 0)
```

#### Output Function Composition

Chain multiple command expressions with `&` to compose a single named output function:

```ruby
function TYPE-COUNT() = { PRINT('{Code.Types.Count} types') }
function FILE-COUNT() = { PRINT('{Code.Files.Count} files') }
function STATISTICS() = { TYPE-COUNT & FILE-COUNT }
```

Running `cop STATISTICS` executes both functions in order.

#### Conditional Commands

Use `predicate? command` to conditionally execute a command — a degenerate ternary where the command is skipped when the condition is false:

```ruby
predicate shouldShowStats(Program) => Program.Args:contains('/s')
function LIST-TYPES() = { foreach Types => '{item.Name}' & shouldShowStats? TYPE-COUNT }
```

The `?` operator reads as: "if shouldShowStats is true, run TYPE-COUNT." For complex conditions, use parentheses:

```ruby
function LIST-TYPES() = { foreach Types => '{item.Name}' & (hasCode && showStats)? TYPE-COUNT }
```

### Implicit Output

Output is implicit — whatever a program evaluates to is its output. Any expression at top level produces output without needing an explicit `PRINT` call.

#### Bare Expressions

A bare expression at top level evaluates and its result is printed:

```ruby
'Hello World'            # string → outputs: Hello World
42                       # number → outputs: 42
1 + 2                    # arithmetic → outputs: 3
Types.Count              # property access → outputs the count
inc(5)                   # function call → outputs: 6
```

Lists output each item on a separate line:

```ruby
[1 2 3]                  # outputs: 1, 2, 3 (one per line)
Types:isPublic.Name      # outputs each public type name
```

Objects output as JSON:

```ruby
{
    Name = 'Chip'
    Age = 32
}
# outputs:
# {
#     "Name": "Chip",
#     "Age": 32
# }
```

#### Foreach with Templates

Use `foreach` to iterate over a collection with formatted output — one line per item:

```ruby
foreach Types:isCSharp:client => '{error:@red} {item.Name} is a client'
```

| Part | Required | Description |
|---|---|---|
| `foreach List` | no | What to iterate — a named list or subset |
| `:filter` | no | One or more predicate filters (AND-combined) |
| `'...'` | yes | Template string with `{Expr}` interpolation |

#### Pipe Sinks

Use `=> target` after the template to pipe output to a collection instead of the console:

```ruby
# Pipe to a provider-backed collection (e.g., http response)
function SERVE() = { foreach Requests => handle => RESPONSES }

# Process items in parallel
function SERVE() = { async foreach Requests => handle => RESPONSES }

# Pipe to a file
foreach Types => SAVE('types.txt', '{item.Name}')
```

The pipe operator means **dequeue → transform → enqueue**: items are dequeued from the source, transformed, and enqueued to the sink. `async foreach` processes items concurrently with bounded parallelism. See [Pipe Operators](#pipe-operators).

#### Language Filtering

Use the language predicates (`:isCSharp`, `:isPython`, `:isJavaScript`, `:isRust`, `:isGo`, `:isJava`) from the `code` package to scope iteration to items from files of a specific language:

```ruby
foreach Clients:isCSharp:!isSealed => '{error:@red} {item.Name} should be sealed'
foreach Lines:isPython:matches(@'\bprint\s*\(') => '{warning:@yellow} Use logging instead of print'
```

### PRINT

Explicitly prints output with full template interpolation and styling support. Use when a program needs to emit additional output beyond what expressions produce implicitly:

```ruby
PRINT('{Analysis complete@green-bold}')
PRINT('Found {Types.Count} types')

let status = 'OK'
PRINT('{status@green}: all checks passed')
```

PRINT honors styled interpolated strings — use `{text@style}` for colored/styled output. Expressions with member access can also be styled: `{item.Name@dim}` evaluates the expression and renders the result with the style applied.

### SAVE

Writes output to a file. The first argument is the file path (relative to the codebase root), followed by a content template. Use `foreach` to iterate.

```ruby
SAVE('output.txt', 'Hello World')                                                      # bare — writes once
foreach Types:isCSharp:client => SAVE('clients.txt', '{item.Name}')                      # list — one line per item
foreach Clients:isCSharp:!isSealed => SAVE('report.txt', '{item.Name}: not sealed')        # filtered subset
```

| Part | Required | Description |
|---|---|---|
| `foreach List` | no | What to iterate — a named list or subset |
| `'path'` | yes | Relative file path for output |
| `'...'` | yes | Template string with `{Expr}` interpolation |

Functions that use `SAVE` only run when explicitly invoked (e.g., `cop EXPORT-NAMES`), never during normal check runs. File paths must be relative and within the codebase directory. The file is overwritten on each run.

### test

Declares a test assertion. Run with `cop test`.

```cop
test has-types = assert(csharp.Types.Count > 0)
test public = assert(csharp.Types:isPublic.Count > 0, 'expected public types')
```

| Part | Required | Description |
|---|---|---|
| `<name>` | yes | Test identifier (shown in output) |
| `condition` | yes | A boolean expression to evaluate |
| `'message'` | no | Custom failure message (defaults to test name) |

Passes when the condition is true. Fails when false. Commonly used with `.Count > 0` (non-empty) or `.Count == 0` (empty):

```cop
test no-var = assert(csharp.Statements:isVar.Count == 0)
test clean = assert(violations.Count == 0, 'should have no violations')
```

Test assertions only run via `cop test`, never during normal execution. See [Testing with Agent Cop](testing-with-cop.md) for details.

### DEBUG

Diagnostic output that only appears when the `-d` (diagnostic) flag is active. Works exactly like implicit output but produces no output during normal runs.

```ruby
foreach Types:client => DEBUG('Client found: {item.Name}')
DEBUG('Total count: {Types.Count}')
```

Use `DEBUG` for printf-style troubleshooting of your `.cop` rules. Output is prefixed with `[debug]` and written to stderr alongside other diagnostic trace information.

Run with diagnostics enabled:
```bash
cop -d          # shows [trace] and [debug] output
cop test -d         # shows [trace] and [debug] output during tests
```

## Strings

```ruby
'hello'              # regular string
@'\bvar\b'           # verbatim string — backslashes are literal (for regex)
```

Interpolated strings in output functions use `{Expr}` placeholders:

```ruby
foreach Clients:!isSealed => '{error:@red} {item.Name} should be sealed'
foreach Clients:hasAsyncWithoutCancellation => '{warning:@yellow} {item.File.Path}:{item.Line} {item.Name} missing cancellation token'
```

## Null (`nic`)

The keyword `nic` represents the absence of a value:

```ruby
let x = nic                          # null binding
let obj = { name = 'hello', value = nic }  # null field in an object
Type.Base == nic ? 'none' | Type.Base      # null comparison in ternary
```

`nic` is falsy — `ToBool(nic)` evaluates to `false`. In JSON output, `nic` serializes as `null`.

## Errors

### Operational Errors (`error`)

The `error` constructor creates an error value — a data object representing an operational failure:

```ruby
error                     # bare error (no message)
error('timeout')          # error with message
error('not found: {id}')  # error with interpolated message
```

Error values have these fields:

| Field | Type | Description |
|-------|------|-------------|
| `Message` | string | Error message (may be nic) |
| `Source` | string | Formatted as `file(line)` |

### Detecting Errors (`isError`)

The built-in predicate `isError` tests whether a value is an error:

```ruby
# As a filter — keep only errors
items:isError

# As a filter — exclude errors
items:!isError

# In predicate body
predicate hasFailed(Item) => isError
```

### Error Handling in Pipelines

In streaming pipelines (`foreach source => transform => sink`), errors can be handled by defining a transform function overload for the `Error` type:

```ruby
import http

# Normal request handler
function handle(Request:Uri:eq('/hello')) => ok({ message: 'Hello' })

# Error handler — receives network errors, logs and swallows them
function handle(Error) => print(Error.Message)

async foreach Requests => handle => RESPONSES
```

Error handling behavior:
- If the transform has an `Error` overload → it's called with the error value
- If the error handler returns null → the error is swallowed (dropped from pipeline)
- If the error handler returns a value → that value is sent to the sink
- If no `Error` overload exists → the error passes directly to the sink (default: HTTP 500)

In batch foreach, errors output as `ERROR: message` instead of crashing.

Sink behavior for errors:

| Sink | Behavior |
|------|----------|
| Console (default) | Writes `ERROR: message` to stderr |
| `file.Write` | Skips the error (does not write) |
| `http.RESPONSES` | Returns HTTP 500 with JSON error body |

### Code Bugs (`FAIL`)

`FAIL` terminates execution immediately — use for situations that should never occur:

```ruby
# Output position (with collection — triggers if any items match)
FAIL('types must be sealed') foreach Types:!isSealed

# Expression position (terminates during evaluation)
predicate isRoute(Request) =>
    Request.Method == 'GET'  ? getHandler(Request)
  | Request.Method == 'POST' ? postHandler(Request)
  | FAIL('unsupported method')
```

Output: `FATAL: file(line): message`

`FAIL` is NOT an error value — it does not produce data. It immediately halts the program.

## Comments

### Single-line comment

```ruby
# This is a single-line comment
predicate isClient(Type) => Type.Name:endsWith('Client')  # also valid at end of line
```

### Multi-line comment

```ruby
#
This is a multi-line comment.
Everything between # markers is ignored.
#
```

A `#` alone on a line opens a block comment. Another `#` alone on a line closes it.

### Doc comment

```ruby
## Client types must have a constructor that accepts an Options parameter
foreach Clients:missingOptions => '{warning:@yellow} {item.Name} should accept options'
```

`##` doc comments are captured and displayed as rule descriptions in the UI.
Multiple consecutive `##` lines merge into a single doc comment.

## Packages

Packages provide domain-specific types, lists, and runtime data. Import a package to bring its types and lists into scope.

| Package | Import | Description |
|---------|--------|-------------|
| `code` | `import code` | Source code structural analysis — see [Code Package Reference](packages/code.md) |
| `json` | `import json` | JSON file parsing into typed collections — see [JSON Package Reference](packages/json.md) |
| `files` | `import files` | File and folder analysis — see [Files Package Reference](packages/files.md) |

More packages are listed in the [Getting Started](../README.md#available-packages) guide.

## Further Reading

- [Getting Started](../README.md) — walkthrough with practical examples
- [CLI Reference](cli-reference.md) — all commands and options for `cop.exe`
- [Static Analysis](static-analysis.md) — writing and organizing checks
- [Testing](testing-with-cop.md) — writing and running tests with ASSERT
- [Code Package Reference](packages/code.md) — Type, Statement, File, etc.
