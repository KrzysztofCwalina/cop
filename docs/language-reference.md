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
| `number` | Floating-point values (64-bit double) |
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
| `let` | Declare a named list (base or subset) |
| `command name =` | Define a named command (implicit output, SAVE, or composition) |
| `predicate name(Param) =>` | Define a named predicate for subsetting |
| `function name(Param) =>` | Define a named function (expression-body or record-body) |
| `SAVE` | Command that writes to a file |
| `DEBUG` | Command that writes to console only when `-d` flag is active |

Declarations are **private to the current project** (folder of `.cop` files) unless prefixed with `export`.

### Imports

Use `import` to bring types and lists from a package into scope:

```ruby
import code
```

Import statements must appear before predicates and commands.

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

2. Restore packages locally (downloads them into your project's `packages/` directory):

```bash
cop package restore my-checks.cop
```

3. Run your program (imports resolve from the local `packages/` directory):

```bash
cop run my-checks.cop
```

The `cop restore` command reads `feed` and `import` declarations, downloads the referenced packages from GitHub, resolves transitive dependencies, and places all files under `packages/` in your project root. After restore, `cop run` resolves imports entirely from local directories — no network access is required at runtime.

> **Tip:** Commit the restored `packages/` directory to version control so CI/CD pipelines and teammates don't need to run `cop restore` separately.

### Export

`export` makes declarations visible to packages that import the current package. Without `export`, declarations are private to the current project (folder of `.cop` files):

```ruby
export predicate isClient(Type) => Type.Name:endsWith('Client')
export let Clients = Code.Types:isClient
export command list-clients = foreach Clients => '{item.Name}'
export type ClientInfo = { Name : string, Path : string }
export function clientInfo(Type) => ClientInfo { Name = Type.Name, Path = Type.File.Path }
```

Any declaration — `predicate`, `let`, `command`, `type`, `flags`, or `function` — can be exported.

### Types

Types describe the property structure of objects:

```ruby
# Object with named properties
type Foo = { Name : string, Age : int }

# Optional properties (may be null)
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
predicate notAbstract(Type) => Type.Modifiers:isClear(Abstract)
```

| Predicate | Meaning |
|-----------|---------|
| `isSet(flag)` | True if the flag bit is set — `(value & flag) != 0` |
| `isClear(flag)` | True if the flag bit is clear — `(value & flag) == 0` |

The `code` package defines a `Modifier` flags enum and provides `isX` predicates for all common modifiers (see [Code Package Reference](packages/code.md)).

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
let sdkTypes = csharp.Types('../azure-sdk/')
let sdkPublic = csharp.Types('../azure-sdk/'):isPublic
let localFiles = filesystem.DiskFiles('src/lib/')
```

Path-scoped collections query the provider against the given path instead of the default root (CWD or `-t`). The path is resolved relative to the process working directory. Results are cached by `(provider, collection, absolutePath)` so repeated references with the same path are efficient. Each collection is parameterized individually — `csharp.Types('../sdk/')` does not affect `csharp.Statements`.

### Predicates

A predicate is a named boolean expression that operates on a typed item. Predicates are the primary mechanism for creating subsets:

```ruby
predicate client(Type) => Type.Name:endsWith('Client')
predicate publicAsync(Method) => Method:isPublic && Method:isAsync
predicate usesVar(Statement) => Statement.Keywords:contains('var')
```

Predicates compose by reference:

```ruby
predicate optionsType(Parameter) => Parameter.Type.Name:endsWith('Options')
predicate hasOptions(Constructor) => Constructor.Parameters:any(optionsType)
predicate missingOptions(Type) => Type.Constructors:none(hasOptions)
```

**Subset predicates** — a predicate over a list name creates a named subset. The predicate’s body filters are AND-combined with the base list:

```ruby
predicate Clients(Types) => client && !isAbstract
```

This declares `Clients` as a subset of `Types` where `client` is true and `isAbstract` is false.

**Narrowing predicates** — a predicate can narrow items to a more specific type using `: NarrowedType`:

```ruby
predicate call(Statement) : Call => Statement.Kind == 'call'
```

When applied as a filter, items are narrowed to `Call` (a superset of Statement’s properties).

#### Constrained Predicates

Append `:constraint` to the parameter type to create predicate overloads constrained by another predicate. The constraint is any predicate — language names like `csharp` and `python` are just predicates that match by file language:

```ruby
predicate client(Type) => Type.Name:endsWith('Client')
predicate client(Type:csharp) => Type.Name:endsWith('Client')
predicate client(Type:python) => Type.Name:endsWith('_client')
```

Resolution order:
1. Exact constraint match (e.g., `:python` for Python files)
2. Unconstrained fallback
3. No match → `false`

Constraints are not limited to languages — any predicate can be used:

```ruby
predicate sealed(Type:csharp) => Type:isSealed
predicate sealed(Type:python) => Type.Decorators:any(Decorator:contains('final'))
```

### Functions

Functions come in two forms: **expression-body** (returns a computed value) and **record-body** (produces a structured object).

#### Expression-Body Functions

An expression-body function takes a named, typed parameter and returns the result of an expression:

```ruby
function inc(x:number) => x + 1
function double(n:number) => n * 2
function greet(name:string) => 'Hello, ' + name
function isLarge(t:Type) => t.Methods.Count > 20
```

Expression-body functions can be called anywhere an expression is expected:

```ruby
inc(5)                    # → 6
inc(inc(5))               # → 7
double(3) + 1             # → 7
Types.Select(isLarge)     # → list of booleans
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
function handle(Request:Path:eq('/')) => Response {
    StatusCode = 200
    Body = '{"message":"hello world!"}'
}

function handle(Request:Path:eq('/health')) => Response {
    StatusCode = 200
    Body = '{"status":"healthy"}'
}

function handle(Request) => Response {
    StatusCode = 404
    Body = '{"error":"not found"}'
}
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
import csharp
import python

# Query a single provider
let cs = Code([csharp])
foreach cs.Types:isPublic => '{item.Name}'

# Query multiple providers — results are unioned
let codebase = Code([csharp, python])
foreach codebase.Types => '{item.Name}'
```

Provider identifiers must be imported packages. The proxy exposes the same collections as the providers (Types, Methods, Statements, etc.). Collections are queried lazily — only when accessed.

> **Note:** `Code.Types` (the legacy syntax) still works and resolves to the ambient code collections. `Code([csharp])` is the explicit, composable alternative.

## Operations

Agent Cop uses two operators for accessing members:

- **`:` (colon)** — applies a **predicate** (returns bool). Predicates use `camelCase` names: `:equals()`, `:startsWith()`, `:any()`, `:contains()`.
- **`.` (dot)** — accesses a **property** or **transform** (returns a value). Properties and transforms use `PascalCase` names: `.Name`, `.Count`, `.Where()`, `.Select()`.

### Subset (`:`)

The `:` operator filters a list with a predicate, producing a subset:

```ruby
Types:isClient                       # Types where isClient is true
Statements:csharp:usesVar            # Statements in C# files using var
Types:isClient:notSealed             # AND-chained: client types that aren’t sealed
Types:isClient:!isAbstract           # negated filter
```

Multiple `:` filters are AND-combined — each filter produces a smaller subset.

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
X == "value"    # equality
X != "value"    # inequality
X > 1           # greater than
X < 10          # less than
X >= 5          # greater than or equal
X <= 100        # less than or equal
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

If no arm matches and no `_` default exists, the expression returns null (falsy).

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

#### Short Aliases

For brevity, predicates also accept short aliases:

| Long form | Short | Long form | Short |
|-----------|-------|-----------|-------|
| `equals` | `eq` | `greaterThan` | `gt` |
| `notEquals` | `ne` | `lessThan` | `lt` |
| `startsWith` | `sw` | `greaterOrEqual` | `ge` |
| `endsWith` | `ew` | `lessOrEqual` | `le` |
| `contains` | `ct` | `containsAny` | `ca` |
| `matches` | `rx` | `sameAs` | `sm` |

Example: `Type.Name:startsWith('I')` is equivalent to the short alias `Type.Name:sw('I')`.

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
Items.First                  # first item (null if empty)
Items.Last                   # last item (null if empty)
Items.Single                 # single item (null if 0 or 2+)
```

### List Predicates

Predicate applications test a list and return a boolean:

```ruby
Items:any(predicate)         # true if any item matches
Items:none(predicate)        # true if no items match
Items:all(predicate)         # true if all items match
Items:contains('value')      # true if list contains value
Items:empty                  # true if list has no items
```

### List Transforms

Transforms return a new list or value from an existing list:

```ruby
Items.Where(predicate)       # subset of matching items
Items.First(predicate)       # first matching item
Items.Last(predicate)        # last matching item
Items.Single(predicate)      # single matching item
Items.ElementAt(n)           # item at index n
Items.Select(expr)           # project each item to a new value
Items.Text(template)         # format each item and join into a single string
Items.OrderBy(expr)          # sort ascending by expression
Items.OrderByDescending(expr) # sort descending by expression
Items.Distinct(expr)         # deduplicate by expression (or by value if no arg)
Items.GroupBy(expr)          # group into Group objects with Key, Items, Count
Items.Sum(expr)              # sum numeric values
Items.Min(expr)              # minimum numeric value
Items.Max(expr)              # maximum numeric value
Items.Average(expr)          # average numeric value
Items.Reduce(op, expr, sep?) # aggregate with operator
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
predicate test(Statement) => Types.MethodNames:ct(Statement.MemberName + 'Async')
```

#### Collection Flattening (Property Access on Lists)

Accessing a property on a collection flattens (SelectMany) that property across all items:

```ruby
# Types.MethodNames → flat list of all method names across all types
predicate hasAsyncVariant(Statement) =>
    Types.MethodNames:ct(Statement.MemberName + 'Async')
```

#### Select and Text Examples

`.Select()` projects each item into a new value using `item` as the element variable. `.Text()` formats each item and joins with newlines:

```ruby
let names = Code.Types.Select(item.Name)
let nameLengths = Code.Types.Select(item.Name.Length)
let summary = Code.Types:client.Text('{item.Name} — {item.File.Path}')
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
predicate noMethods(Type) => Type.Methods:none(isPublic)
predicate allAbstract(Type) => Type.Methods:all(isAbstract)
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
| `Text(expr)` | any → string | Converts a value to its textual representation |
| `File(path)` | string → [byte] | Reads a file and returns its bytes (sandboxed, 10MB max) |
| `Path(pattern)` | string → bool | Tests if the current file path matches a glob pattern |
| `Matches(pattern)` | string → bool | Tests if the current item text matches a regex |

`Path` uses glob patterns: `*` matches within a segment, `**` matches across segments, `?` matches one character.

## Commands

Commands produce side effects — output to the console, files, or test results. Use `foreach` to iterate over a collection:

```
foreach List:filter1:filter2 => 'template expression'
```

Commands are **named** using `command`, which makes them invocable by name with `cop run <name>`:

```ruby
command list-types = foreach Types => '{item.Name}'
command export-names = foreach Types:csharp:client => SAVE('names.txt', '{item.Name}')
command test-has-types = ASSERT(csharp.Types)
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
foreach Types:csharp:client => '{error:@red} {item.Name} is a client'
```

| Part | Required | Description |
|---|---|---|
| `foreach List` | no | What to iterate — a named list or subset |
| `:filter` | no | One or more predicate filters (AND-combined) |
| `'...'` | yes | Template string with `{Expr}` interpolation |

#### Language Filtering

Use a language name as a filter (`:csharp`, `:python`, etc.) to scope iteration to files of that language:

```ruby
foreach Clients:csharp:!isSealed => '{error:@red} {item.Name} should be sealed'
foreach Lines:python:matches(@'\bprint\s*\(') => '{warning:@yellow} Use logging instead of print'
```

### PRINT

Explicitly prints output with full template interpolation and styling support. Use when a program needs to emit additional output beyond what expressions produce implicitly:

```ruby
PRINT('{Analysis complete@green-bold}')
PRINT('Found {Types.Count} types')

let status = 'OK'
PRINT('{status@green}: all checks passed')
```

PRINT honors styled interpolated strings — use `{text@style}` for colored/styled output.

### SAVE

Writes output to a file. The first argument is the file path (relative to the codebase root), followed by a content template. Use `foreach` to iterate.

```ruby
SAVE('output.txt', 'Hello World')                                                      # bare — writes once
foreach Types:csharp:client => SAVE('clients.txt', '{item.Name}')                      # list — one line per item
foreach Clients:csharp:!isSealed => SAVE('report.txt', '{item.Name}: not sealed')        # filtered subset
```

| Part | Required | Description |
|---|---|---|
| `foreach List` | no | What to iterate — a named list or subset |
| `'path'` | yes | Relative file path for output |
| `'...'` | yes | Template string with `{Expr}` interpolation |

SAVE commands only run when explicitly invoked (e.g., `cop run export-names`), never during normal check runs. File paths must be relative and within the codebase directory. The file is overwritten on each run.

### ASSERT

Tests that a collection is non-empty. Run with `cop test`.

```ruby
command test-has-types = ASSERT(csharp.Types)
command test-public = ASSERT(csharp.Types:isPublic, 'expected public types')
```

| Part | Required | Description |
|---|---|---|
| `collection` | yes | A collection name or filtered chain |
| `'message'` | no | Custom failure message (defaults to command name) |

Passes when at least one item matches. Fails when the collection is empty.

### ASSERT_EMPTY

Tests that a collection is empty. The inverse of `ASSERT`.

```ruby
command test-no-var = ASSERT_EMPTY(csharp.Statements:isVar)
command test-clean = ASSERT_EMPTY(violations, 'should have no violations')
```

| Part | Required | Description |
|---|---|---|
| `collection` | yes | A collection name or filtered chain |
| `'message'` | no | Custom failure message (defaults to command name) |

Passes when zero items match. Fails when items are found.

ASSERT and ASSERT_EMPTY commands only run via `cop test`, never during `cop run`. See [Testing with Agent Cop](testing-with-cop.md) for details.

### DEBUG

Diagnostic output that only appears when the `-d` (diagnostic) flag is active. Works exactly like implicit output but produces no output during normal runs.

```ruby
foreach Types:client => DEBUG('Client found: {item.Name}')
DEBUG('Total count: {Types.Count}')
```

Use `DEBUG` for printf-style troubleshooting of your `.cop` rules. Output is prefixed with `[debug]` and written to stderr alongside other diagnostic trace information.

Run with diagnostics enabled:
```bash
cop run -d          # shows [trace] and [debug] output
cop test -d         # shows [trace] and [debug] output during tests
```

## Strings

```ruby
'hello'              # regular string
@'\bvar\b'           # verbatim string — backslashes are literal (for regex)
```

Interpolated strings in commands use `{Expr}` placeholders:

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

## Comments

### Single-line comment

```ruby
# This is a single-line comment
predicate client(Type) => Type.Name:endsWith('Client')  # also valid at end of line
// Legacy comment syntax (also supported)
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
| `filesystem` | `import filesystem` | File and folder analysis — see [Filesystem Package Reference](packages/filesystem.md) |

More packages are listed in the [Getting Started](../README.md#available-packages) guide.

## Further Reading

- [Getting Started](../README.md) — walkthrough with practical examples
- [CLI Reference](cli-reference.md) — all commands and options for `cop.exe`
- [Static Analysis](static-analysis-with-cop.md) — writing and organizing checks
- [Testing](testing-with-cop.md) — writing and running tests with ASSERT
- [Code Package Reference](packages/code.md) — Type, Statement, File, etc.
