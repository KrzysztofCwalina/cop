## code
Source code structural analysis across multiple languages. &nbsp; `import code`

**Source:** [`packages/code/src/code.cop`](../../packages/code/src/code.cop)

---

### Collections

`Code` is the top-level `Codebase` object containing:

| Collection | Type | Description |
|---|---|---|
| `Code.Types` | [`[Type]`](#type) | Classes, structs, interfaces, enums, records |
| `Code.Statements` | [`[Statement]`](#statement) | Individual code statements |
| `Code.Lines` | [`[Line]`](#line) | Raw text lines |
| `Code.Files` | [`[File]`](#file) | Source files |

Convenience subsets:

| Subset | Definition | Description |
|---|---|---|
| `Calls` | `Statements:call` | Call statements |
| `Declarations` | `Statements:declaration` | Declaration statements |
| `ErrorHandlers` | `Statements:errorHandler` | Error-handling statements |
| `Attributes` | `Statements:attribute` | Attribute statements |

Language keywords (`csharp`, `python`, `java`, `go`, `typescript`) scope collections to matching file types: `Types:csharp`, `Lines:python:matches(...)`, etc.

---

### Types

#### Type

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Type name |
| `Kind` | `string` | `Class`, `Struct`, `Interface`, `Enum`, `Record` |
| `Modifiers` | `int` | Modifier bitfield (use predicates) |
| `BaseTypes` | `[string]` | Base types and interfaces |
| `Constructors` | [`[Method]`](#method) | Constructor declarations |
| `Methods` | [`[Method]`](#method) | Method declarations |
| `MethodNames` | `[string]` | Method names (quick string matching) |
| `NestedTypes` | [`[Type]`](#type) | Nested type declarations |
| `EnumValues` | `[string]` | Enum member names |
| `Decorators` | `[string]` | Attributes/decorators |
| `Line` | `int` | Source line number |

#### Method

`Constructor` is a nominal superset of `Method` (`type Constructor = Method & {}`) — same properties, distinct type for predicate dispatch.

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Method name |
| `Modifiers` | `int` | Modifier bitfield (use predicates) |
| `ReturnType` | [`TypeReference`](#typereference)`?` | Return type (null for constructors/void) |
| `Parameters` | [`[Parameter]`](#parameter) | Parameter declarations |
| `Decorators` | `[string]` | Attributes/decorators |
| `Line` | `int` | Source line number |

#### Parameter

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Parameter name |
| `Type` | [`TypeReference`](#typereference)`?` | Parameter type (null if untyped) |
| `Variadic` | `bool` | Is `params`/`*args` |
| `Kwargs` | `bool` | Is `**kwargs` (Python) |
| `Defaulted` | `bool` | Has default value |
| `Line` | `int` | Source line number |

#### TypeReference

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Type name without namespace |
| `Namespace` | `string?` | Namespace prefix (null if unqualified) |
| `Generic` | `bool` | Has generic arguments |
| `GenericArguments` | [`[TypeReference]`](#typereference) | Generic type arguments |
| `Length` | `int` | Length of original type text |

#### Statement

| Property | Type | Description |
|---|---|---|
| `Kind` | `string` | `declaration`, `call`, `throw`, `catch`, `return`, `using`, `foreach`, `await` |
| `Keywords` | `[string]` | Language keywords (e.g. `["var"]`, `["await"]`) |
| `TypeName` | `string?` | Receiver type (e.g. `Thread` in `Thread.Sleep`) |
| `MemberName` | `string?` | Member name (e.g. `Sleep`, variable name) |
| `Arguments` | `[string]` | Call arguments |
| `Line` | `int` | Source line number |
| `InMethod` | `bool` | Inside a method body |
| `File` | [`File`](#file)`?` | Containing file |

Subset types narrow by predicate:

| Subset | Predicate | Notes |
|---|---|---|
| `Call` | `call` | Adds `Signature : string` |
| `Declaration` | `declaration` | `Kind == 'declaration'` |
| `ErrorHandler` | `errorHandler` | Error-handling constructs (catch, except) |
| `Attribute` | `attribute` | `Kind == 'attribute'` |

#### Line

| Property | Type | Description |
|---|---|---|
| `Text` | `string` | Raw text content |
| `Number` | `int` | Line number |

#### File

| Property | Type | Description |
|---|---|---|
| `Path` | `string` | Relative file path |
| `Language` | `string?` | Language (`csharp`, `python`, etc.) |
| `Namespace` | `string?` | Declared namespace |
| `Usings` | `[string]` | Import/using directives |
| `Types` | [`[Type]`](#type) | Type declarations in file |

#### Codebase

| Property | Type | Description |
|---|---|---|
| `Files` | [`[File]`](#file) | All source files |
| `Types` | [`[Type]`](#type) | All type declarations |
| `Statements` | [`[Statement]`](#statement) | All code statements |
| `Lines` | [`[Line]`](#line) | All text lines |

---

### Predicates

Modifier predicates (apply with `:` syntax, e.g. `Type:isPublic`, `Method:isAsync`):

| Predicate | Applies To | Condition |
|---|---|---|
| `isPublic` | Type, Method | Has public modifier |
| `isSealed` | Type | Has sealed modifier |
| `isAbstract` | Type, Method | Has abstract modifier |
| `isStatic` | Type, Method | Has static modifier |
| `isDocumented` | Type, Method | Has documentation |
| `isProtected` | Method | Has protected modifier |
| `isPrivate` | Method | Has private modifier |
| `isInternal` | Method | Has internal modifier |
| `isAsync` | Method | Has async modifier |
| `isVirtual` | Method | Has virtual modifier |
| `isOverride` | Method | Has override modifier |

---

### String Operations

Two comparison modes for cross-language checks:

| Mode | Syntax | Behavior |
|---|---|---|
| **CaseInsensitive** | `==`, `contains`, etc. | Ignores letter case (default) |
| **ConventionInsensitive** | `:sameAs()`, `.Normalized` | Ignores case AND naming convention |

#### String Predicates

| Predicate | Description |
|---|---|
| `equals(v)` | Case-insensitive equality |
| `notEquals(v)` | Case-insensitive inequality |
| `contains(v)` | Case-insensitive substring |
| `startsWith(v)` | Case-insensitive prefix |
| `endsWith(v)` | Case-insensitive suffix |
| `containsAny(list)` | Any list item is substring |
| `matches(regex)` | **Case-sensitive** regex |
| `sameAs(v)` | Convention-insensitive equality |
| `in(list)` | Value is member of list |
| `empty` | String is empty |

#### Numeric Predicates

| Predicate | Description |
|---|---|
| `equals(n)` | Equal to |
| `notEquals(n)` | Not equal to |
| `greaterThan(n)` | Greater than |
| `lessThan(n)` | Less than |
| `greaterOrEqual(n)` | ≥ |
| `lessOrEqual(n)` | ≤ |

#### Properties & Transforms

| Member | Description |
|---|---|
| `.Length` | String length |
| `.Lower` | Lowercase |
| `.Upper` | Uppercase |
| `.Normalized` | Convention-insensitive form (`Foo_Bar` → `foobar`) |
| `.Words` | Split identifier into lowercase word list |
| `.Trim(s)` | Remove suffix |
| `.Replace(a, b)` | Replace substring |

`.Words` normalizes across conventions — `TaskCompletionSource`, `task_completion_source`, and `taskCompletionSource` all yield `['task', 'completion', 'source']`. Chain collection predicates: `Name.Words:contains('task')`.

---

### Examples

```ruby
import code

# Client library design checks
predicate client(Type) => Type.Name:endsWith('Client')
predicate clientOptions(Type) => Type.Name:endsWith('ClientOptions')
predicate Clients(Types) => client && !clientOptions
predicate optionsType(Parameter) => Parameter.Type.Name:endsWith('Options')
predicate hasOptions(Constructor) => Constructor.Parameters:any(optionsType)
predicate missingOptions(Type) => Type.Constructors:none(hasOptions)
predicate notSealedOrAbstract(Type) => !Type:isSealed && !Type:isAbstract

predicate cancellationToken(Parameter) => Parameter.Type.Name == 'CancellationToken'
predicate publicAsync(Method) => Method:isPublic && Method:isAsync
predicate missingCancellationToken(Method) => Method.Parameters:none(cancellationToken)
predicate asyncWithoutCancellation(Type) => Type.Methods.Where(publicAsync):any(missingCancellationToken)

foreach Clients:csharp:missingOptions => PRINT('{warning:@yellow} {item.Name} should accept an options parameter')
foreach Clients:csharp:notSealedOrAbstract => PRINT('{error:@red} {item.Name} should be sealed or abstract')
foreach Clients:csharp:asyncWithoutCancellation => PRINT('{warning:@yellow} {item.Name} is async without CancellationToken')
```

```ruby
import code

# Code style enforcement
predicate usesVar(Statement) => Statement.Keywords:contains('var')
predicate usesDynamic(Statement) => Statement.Keywords:contains('dynamic')
predicate threadSleep(Statement) => Statement.TypeName == 'Thread' && Statement.MemberName == 'Sleep'

foreach Statements:csharp:usesVar:!Path('**/Tests/**') => PRINT('{error:@red} {item.File.Path}:{item.Line} uses var')
foreach Statements:csharp:usesDynamic => PRINT('{error:@red} Do not use dynamic')
foreach Statements:csharp:threadSleep => PRINT('{error:@red} Use Task.Delay instead of Thread.Sleep')
```

```ruby
import code

# Cross-language — convention-insensitive matching
predicate client(Type) => Type.Name.Words:contains('client')
predicate notPublic(Type) => !Type:isPublic

foreach Types:client:notPublic => PRINT('{warning:@yellow} {item.Name} should be public')
```

Language-specific overloads when rules differ per language:

```ruby
predicate client(Type:csharp) => Type.Name:endsWith('Client')
predicate client(Type:python) => Type.Name:endsWith('_client')

foreach Types:client:notPublic => PRINT('{warning:@yellow} {item.Name} should be public')
```

```ruby
import code

# Domain architecture rules
predicate bannedUsing(File) => Path('**/Domain/**') && File.Usings:any(Using:contains('System.IO'))

foreach Files:csharp:bannedUsing => PRINT('{error:@red} Domain layer must not reference System.IO in {File.Path}')
```
