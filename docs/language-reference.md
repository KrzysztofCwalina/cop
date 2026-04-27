# Cop Language Reference

Cop is a DSL for processing data organized as **lists**. Files use the `.cop` extension.

All data in cop is either a **primitive** (string, int, bool, byte) or a **list** of items. An **object** is a list of named properties — types describe the shape of those property lists. The language provides three kinds of operations:

1. **Subset** (`:`) — filter a list with a predicate to produce a smaller list
2. **Superset** (`&`) — merge property schemas or combine lists into a larger set
3. **Primitive operations** — comparisons, string predicates, arithmetic

And one kind of side-effect:

4. **Commands** — routines that produce output (`PRINT`) or write files (`SAVE`)

> **Note:** Most examples use the [`code` package](packages/code-package-reference.md) (`import code`), which provides types for source code analysis. See [Code Package Reference](packages/code-package-reference.md) for the full type catalog.

## Data Model

### Primitives

| Type | Description |
|------|-------------|
| `string` | Text values |
| `int` | Integer values (64-bit signed) |
| `number` | Floating-point values (64-bit double) |
| `bool` | `true` or `false` |
| `byte` | Integer 0-255 |

### Lists

A list is an ordered sequence of items, written as `[T]` where `T` is the item type:

```ruby
[string]             # list of strings
[int]                # list of integers
[Type]               # list of Type objects
[1, 2, 3]            # list literal
["a", "b", "c"]      # string list literal
[]                   # empty list
```

Lists are the fundamental data structure. Objects contain lists of properties. Packages provide lists of items to process. Filtering produces subsets. Most operations take a list in and produce a list or boolean out.

### Objects

An object is a list of named properties. Each property has a name and a value, where the value is either a primitive or a list. **Types** describe the shape of an object’s property list:

```ruby
type Person = { Name : string, Age : int }
```

A `Person` object is a list of properties: `[Property("Name", "Alice"), Property("Age", 42)]`.

The dot operator (`.`) is syntactic sugar for navigating properties:

```ruby
Person.Name          # the value of the "Name" property
Person.Age           # the value of the "Age" property
```

## Declarations

A `.cop` file contains these kinds of declarations:

| Declaration | Purpose |
|---|---|
| `import` | Bring types and lists from a package into scope |
| `type` | Describe the shape of an object’s property list |
| `flags` | Define a flags enum for bitwise operations |
| `let` | Declare a named list (base or subset) |
| `command name =` | Define a named command (PRINT, SAVE, or composition) |
| `predicate name(Param) =>` | Define a named predicate for subsetting |
| `PRINT` | Command that writes to console |
| `SAVE` | Command that writes to a file |

Declarations are **private to the current project** (folder of `.cop` files) unless prefixed with `export`.

### Imports

Use `import` to bring types and lists from a package into scope:

```ruby
import code
```

Import statements must appear before predicates and commands.

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

`flags` defines a set of named bit constants for use with the `&` (bitwise AND) and `|` (bitwise OR) operators:

```ruby
flags Modifier = Public | Private | Protected | Internal | Static | Sealed | Abstract | Virtual
```

Each member is assigned a power of 2 automatically (Public=1, Private=2, Protected=4, ...). Use with an integer property and `&` to test individual bits:

```ruby
predicate isPublic(Type) => Type.Modifiers & Public != 0
predicate isSealed(Type) => Type.Modifiers & Sealed != 0
```

The `code` package defines a `Modifier` flags enum and provides `isX` predicates for all common modifiers (see [Code Package Reference](packages/code-package-reference.md)).

### Let Declarations

`let` declares a named list. It has two forms:

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
predicate Clients(Types) => client && !clientOptions
```

This declares `Clients` as a subset of `Types` where `client` is true and `clientOptions` is false.

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

## Operations

Cop uses two operators for accessing members:

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

#### String Predicates

```ruby
Name:endsWith('Client')              # case-insensitive suffix match
Name:startsWith('Azure')               # case-insensitive prefix match
Name:contains('Options')             # case-insensitive substring match
Name:matches(@'\bList<.*>')         # regex match (case-sensitive)
Name:equals('Program')             # case-insensitive equality
Name:notEquals('Object')              # case-insensitive inequality
Name:sameAs('configure_await')     # convention-insensitive (matches ConfigureAwait, configureAwait, etc.)
Name:containsAny(['Get', 'Set'])        # any item in list is a substring
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
Items.Select(template)       # project each item into a new value
Items.Text(template)         # format each item and join into a single string
```

#### Inline Expressions

Instead of defining a named predicate, write the condition inline. The element variable uses the singular form of the property name:

```ruby
Type.Methods:any(Method:isPublic && Method:isAsync)
Type.Constructors:none(Constructor:isProtected)
Type.BaseTypes:any(BaseType:contains('Service'))
File.Usings:any(Using:contains('System.IO'))
```

Recognized singular names: `Type`, `Method`, `Constructor`, `Parameter`, `Statement`, `Line`, `BaseType`, `Decorator`, `Using`, `Package`, `File`, `NestedType`.

### Built-in Functions

| Function | Signature | Description |
|----------|-----------|-------------|
| `Text(expr)` | any → string | Converts a value to its textual representation |
| `File(path)` | string → [byte] | Reads a file and returns its bytes (sandboxed, 10MB max) |
| `Path(pattern)` | string → bool | Tests if the current file path matches a glob pattern |
| `Matches(pattern)` | string → bool | Tests if the current item text matches a regex |

`Path` uses glob patterns: `*` matches within a segment, `**` matches across segments, `?` matches one character.

## Commands

Commands produce side effects — output to the console or files. Use `foreach` to iterate over a collection:

```
foreach List:filter1:filter2 => COMMAND("args...")
```

Commands are **named** using `command`, which makes them invocable by name with `cop run <name>`:

```ruby
command list-types = foreach Types => PRINT('{item.Name}')
command export-names = foreach Types:csharp:client => SAVE('names.txt', '{item.Name}')
```

### PRINT

Writes output to the console. One line per matching item.

```ruby
PRINT('Hello World')                                                      # bare — outputs once
foreach Types:csharp:client => PRINT('{error:@red} {item.Name} is a client')  # list — one line per item
```

| Part | Required | Description |
|---|---|---|
| `foreach List` | no | What to iterate — a named list or subset |
| `:filter` | no | One or more predicate filters (AND-combined) |
| `'...'` | yes | Template string with `{Expr}` interpolation |

#### Action Aliases

`ERROR`, `WARNING`, and `INFO` are syntactic aliases for `PRINT` (backward compatibility). They all produce the same plain text output:

```ruby
foreach Types:client => PRINT('{error:@red} {item.Name} is invalid')
foreach Types:client => ERROR('{error:@red} {item.Name} is invalid')     # same as PRINT
foreach Types:client => WARNING('{warning:@yellow} {item.Name} needs review')  # same as PRINT
foreach Types:client => INFO('{info:@cyan} {item.Name} found')             # same as PRINT
```

#### Language Filtering

Use a language name as a filter (`:csharp`, `:python`, etc.) to scope iteration to files of that language:

```ruby
foreach Clients:csharp:!Sealed => PRINT('{error:@red} {item.Name} should be sealed')
foreach Lines:python:matches(@'\bprint\s*\(') => PRINT('{warning:@yellow} Use logging instead of print')
```

### SAVE

Writes output to a file. The first argument is the file path (relative to the codebase root), followed by a content template. Use `foreach` to iterate.

```ruby
SAVE('output.txt', 'Hello World')                                                      # bare — writes once
foreach Types:csharp:client => SAVE('clients.txt', '{item.Name}')                      # list — one line per item
foreach Clients:csharp:!Sealed => SAVE('report.txt', '{item.Name}: not sealed')        # filtered subset
```

| Part | Required | Description |
|---|---|---|
| `foreach List` | no | What to iterate — a named list or subset |
| `'path'` | yes | Relative file path for output |
| `'...'` | yes | Template string with `{Expr}` interpolation |

SAVE commands only run when explicitly invoked (e.g., `cop run export-names`), never during normal check runs. File paths must be relative and within the codebase directory. The file is overwritten on each run.

## Strings

```ruby
'hello'              # regular string
@'\bvar\b'           # verbatim string — backslashes are literal (for regex)
```

Interpolated strings in commands use `{Expr}` placeholders:

```ruby
foreach Clients:!Sealed => PRINT('{error:@red} {item.Name} should be sealed')
foreach Clients:hasAsyncWithoutCancellation => PRINT('{warning:@yellow} {item.File.Path}:{item.Line} {item.Name} missing cancellation token')
```

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
foreach Clients:missingOptions => PRINT('{warning:@yellow} {item.Name} should accept options')
```

`##` doc comments are captured and displayed as rule descriptions in the UI.
Multiple consecutive `##` lines merge into a single doc comment.

## Packages

Packages provide domain-specific types, lists, and runtime data. Import a package to bring its types and lists into scope.

| Package | Import | Description |
|---------|--------|-------------|
| `code` | `import code` | Source code structural analysis — see [Code Package Reference](packages/code-package-reference.md) |

More packages are listed in the [Getting Started](getting-started.md#available-packages) guide.
