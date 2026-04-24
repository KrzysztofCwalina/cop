# Code Package Reference

The `code` package provides types and lists for source code structural analysis. It is the first domain package for cop, enabling static analysis checks across multiple programming languages.

**Source:** [`packages/code/src/code.cop`](../../packages/code/src/code.cop)

## Import

```ruby
import code
```

This brings the following types and lists into scope.

## Lists

The `code` package provides a `Codebase` object with four lists. The runtime engine populates them by parsing source files in the target codebase.

| List | Item Type | Description |
|---|---|---|
| `Code.Types` | `Type` | Classes, structs, interfaces, enums, records |
| `Code.Statements` | `Statement` | Individual code statements (declarations, invocations, catch, etc.) |
| `Code.Lines` | `Line` | Raw text lines of the source file |
| `Code.Files` | `File` | The source file itself (one item per file) |

The package also provides convenience subsets:

| Subset | Definition | Description |
|---|---|---|
| `Calls` | `Statements:call` | Statements narrowed to Call type |
| `Declarations` | `Statements:declaration` | Statements narrowed to Declaration type |
| `ErrorHandlers` | `Statements:errorHandler` | Statements narrowed to ErrorHandler type |
| `Attributes` | `Statements:attribute` | Statements narrowed to Attribute type |

## Language Keywords

The `code` package recognizes these language keywords for scoping checks to specific file types:

| Keyword | File Types |
|---|---|
| `csharp` | `.cs` files |
| `python` | `.py` files |
| `java` | `.java` files |
| `go` | `.go` files |
| `typescript` | `.ts` files |

Use a language filter on the list name to scope to a specific language:

```ruby
foreach Types:csharp:client => PRINT('{warning:@yellow} {item.Name} needs review')
foreach Lines:python:rx(@'\bprint\s*\('):!Path('**/tests/**') => PRINT('{warning:@yellow} Use logging instead of print')
```

## Type Reference

All types below are defined in `packages/code/src/code.cop`.

### Type

Represents a class, struct, interface, enum, or record declaration.

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Type name |
| `Kind` | `string` | `Class`, `Struct`, `Interface`, `Enum`, `Record` |
| `Public` | `bool` | Has public modifier |
| `Sealed` | `bool` | Has sealed modifier |
| `Abstract` | `bool` | Has abstract modifier |
| `Static` | `bool` | Has static modifier |
| `BaseTypes` | `[string]` | Base types and interfaces |
| `Constructors` | `[Method]` | Constructor declarations |
| `Methods` | `[Method]` | Method declarations |
| `MethodNames` | `[string]` | Method names (for quick string matching) |
| `NestedTypes` | `[Type]` | Nested type declarations |
| `EnumValues` | `[string]` | Enum member names (for enums) |
| `Decorators` | `[string]` | Attributes/decorators |
| `Line` | `int` | Source line number |

### Method

Represents a method declaration. `Constructor` is a superset of `Method` (defined as `type Constructor = Method & {}`).

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Method name |
| `Public` | `bool` | Has public modifier |
| `Protected` | `bool` | Has protected modifier |
| `Private` | `bool` | Has private modifier |
| `Internal` | `bool` | Has internal modifier |
| `Async` | `bool` | Has async modifier |
| `Static` | `bool` | Has static modifier |
| `Abstract` | `bool` | Has abstract modifier |
| `Virtual` | `bool` | Has virtual modifier |
| `Override` | `bool` | Has override modifier |
| `ReturnType` | `TypeReference?` | Return type (null for constructors, void methods) |
| `Parameters` | `[Parameter]` | Parameter declarations |
| `Decorators` | `[string]` | Attributes/decorators |
| `Line` | `int` | Source line number |

### Constructor

```ruby
type Constructor = Method & {}
```

Constructor is a nominal superset of Method — it has all Method properties and is a distinct type for predicate dispatch.

### Parameter

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Parameter name |
| `Type` | `TypeReference?` | Parameter type (null for untyped parameters) |
| `Variadic` | `bool` | Is `params`/`*args` |
| `Kwargs` | `bool` | Is `**kwargs` (Python) |
| `Defaulted` | `bool` | Has a default value |
| `Line` | `int` | Source line number |

### TypeReference

Represents a type reference (e.g., `List<string>`, `Azure.Core.TokenCredential`).

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | Type name without namespace |
| `Namespace` | `string?` | Namespace prefix (null if unqualified) |
| `Generic` | `bool` | Has generic arguments |
| `GenericArguments` | `[TypeReference]` | Generic type arguments |
| `Length` | `int` | Length of the original type text |

### Statement

Represents an individual code statement within a method or class body.

| Property | Type | Description |
|---|---|---|
| `Kind` | `string` | `declaration`, `call`, `throw`, `catch`, `return`, `using`, `foreach`, `await` |
| `Keywords` | `[string]` | Language keywords used (e.g., `["var"]`, `["dynamic"]`, `["await"]`) |
| `TypeName` | `string?` | Receiver type (e.g., `Thread` in `Thread.Sleep`) |
| `MemberName` | `string?` | Member name (e.g., `Sleep`, variable name) |
| `Arguments` | `[string]` | Call arguments |
| `Line` | `int` | Source line number |
| `InMethod` | `bool` | Whether statement is inside a method body |
| `File` | `File?` | The file containing this statement |

Statement has several subsets defined by narrowing predicates:

| Subset Type | Predicate | Meaning |
|---|---|---|
| `Call` | `call` | `Kind == 'call'` — adds `Signature : string` |
| `Declaration` | `declaration` | `Kind == 'declaration'` |
| `ErrorHandler` | `errorHandler` | `ErrorHandler == true` — error-handling constructs (catch, except, etc.) |
| `Attribute` | `attribute` | `Kind == 'attribute'` |

### Line

Represents a single line of source text.

| Property | Type | Description |
|---|---|---|
| `Text` | `string` | Raw text content |
| `Number` | `int` | Line number |

### File

Represents a source file.

| Property | Type | Description |
|---|---|---|
| `Path` | `string` | Relative file path |
| `Language` | `string?` | Language name (`csharp`, `python`, etc.) |
| `Namespace` | `string?` | Declared namespace (null if none) |
| `Usings` | `[string]` | Import/using directives |
| `Types` | `[Type]` | Type declarations in the file |

### Codebase

The top-level object provided by the runtime. Contains all parsed data:

| Property | Type | Description |
|---|---|---|
| `Files` | `[File]` | All source files |
| `Types` | `[Type]` | All type declarations |
| `Statements` | `[Statement]` | All code statements |
| `Lines` | `[Line]` | All text lines |

## String Operations

Cop supports two comparison modes for cross-language checks:

| Mode | Syntax | Behavior |
|---|---|---|
| **CaseInsensitive** | `==`, `ct`, etc. | Ignores letter case (default) |
| **ConventionInsensitive** | `:sm()`, `.Normalized` | Ignores case AND naming convention |

### Comparison Operators

| Operator | Example | Description |
|---|---|---|
| `==` | `Name == 'foo'` | Case-insensitive equality |
| `!=` | `Name != 'bar'` | Case-insensitive inequality |

### String Predicates

| Predicate | Example | Description |
|---|---|---|
| `eq` | `Name:eq('Client')` | Case-insensitive equality |
| `ne` | `Name:ne('Object')` | Case-insensitive inequality |
| `ct` | `Name:ct('task')` | Case-insensitive substring |
| `sw` | `Name:sw('I')` | Case-insensitive prefix |
| `ew` | `Name:ew('Client')` | Case-insensitive suffix |
| `ca` | `Name:ca(List)` | Any item in list is a substring |
| `rx` | `Name:rx('^Foo$')` | **Case-sensitive** regex |
| `sm` | `Name:sm('foo_bar')` | Convention-insensitive equality |
| `in` | `Name:in(List)` | Value is a member of the list |
| `empty` | `Name:empty` | String is empty (zero length) |

### String Transforms

| Transform | Example | Description |
|---|---|---|
| `Trim` | `Name.Trim('Async')` | Remove suffix |
| `Replace` | `Name.Replace('old', 'new')` | Replace substring |

### Numeric Predicates

| Predicate | Example | Description |
|---|---|---|
| `eq` | `Depth:eq(0)` | Equal to |
| `ne` | `Size:ne(0)` | Not equal to |
| `gt` | `Size:gt(1000)` | Greater than |
| `lt` | `Depth:lt(3)` | Less than |
| `ge` | `Size:ge(100)` | Greater than or equal |
| `le` | `Depth:le(5)` | Less than or equal |

### String Properties

| Property | Example | Description |
|---|---|---|
| `Length` | `Name.Length > 50` | String length |
| `Lower` | `Name.Lower` | Lowercase version |
| `Upper` | `Name.Upper` | Uppercase version |
| `Normalized` | `Name.Normalized` | Convention-insensitive canonical form (`Foo_Bar` → `foobar`) |
| `Words` | `Name.Words` | Split identifier into lowercase word list |

### Convention-Insensitive Comparison with `:sm`

Use `:sm()` when identifiers may follow different naming conventions across languages:

```ruby
# All true — PascalCase, snake_case, camelCase, UPPER_SNAKE are all equivalent
Type.Name:sm('ConfigureAwait')
Type.Name:sm('configure_await')
Type.Name:sm('configureAwait')
```

### Identifier Normalization with `.Words`

The `.Words` property splits identifiers into lowercase words, normalizing across naming conventions:

| Input | `.Words` Result |
|---|---|
| `TaskCompletionSource` | `['task', 'completion', 'source']` |
| `taskCompletionSource` | `['task', 'completion', 'source']` |
| `task_completion_source` | `['task', 'completion', 'source']` |
| `HTTPClient` | `['http', 'client']` |

Since `.Words` returns a list, you can chain collection predicates:
```ruby
Type.Name.Words:contains('task')        # does identifier contain the word "task"?
Method.Name.Words:any(=> == 'get')      # does method name start with word "get"?
```

## Examples

### Client Library Design Checks

```ruby
import code

predicate client(Type) => Type.Name:ew('Client')
predicate clientOptions(Type) => Type.Name:ew('ClientOptions')
predicate Clients(Types) => client && !clientOptions
predicate optionsType(Parameter) => Parameter.Type.Name:ew('Options')
predicate hasOptions(Constructor) => Constructor.Parameters:any(optionsType)
predicate missingOptions(Type) => Type.Constructors:none(hasOptions)
predicate notSealedOrAbstract(Type) => !Type.Sealed && !Type.Abstract

predicate cancellationToken(Parameter) => Parameter.Type.Name == 'CancellationToken'
predicate publicAsync(Method) => Method.Public && Method.Async
predicate missingCancellationToken(Method) => Method.Parameters:none(cancellationToken)
predicate asyncWithoutCancellation(Type) => Type.Methods.Where(publicAsync):any(missingCancellationToken)

## Client types must have a constructor that accepts an Options parameter
foreach Clients:csharp:missingOptions => PRINT('{warning:@yellow} {item.Name} should accept an options parameter')

## Client types must be sealed or abstract
foreach Clients:csharp:notSealedOrAbstract => PRINT('{error:@red} {item.Name} should be sealed or abstract')

## Async methods must accept a CancellationToken parameter
foreach Clients:csharp:asyncWithoutCancellation => PRINT('{warning:@yellow} {item.Name} is async without CancellationToken')
```

### Code Style Enforcement

```ruby
import code

predicate usesVar(Statement) => Statement.Keywords:contains('var')
predicate usesDynamic(Statement) => Statement.Keywords:contains('dynamic')
predicate threadSleep(Statement) => Statement.TypeName == 'Thread' && Statement.MemberName == 'Sleep'

foreach Statements:csharp:usesVar:!Path('**/Tests/**') => PRINT('{error:@red} {item.File.Path}:{item.Line} uses var')
foreach Statements:csharp:usesDynamic => PRINT('{error:@red} Do not use dynamic')
foreach Statements:csharp:threadSleep => PRINT('{error:@red} Use Task.Delay instead of Thread.Sleep')
```

### Cross-Language Checks

Use `:sm()` for convention-insensitive matching across languages:

```ruby
import code

# One predicate handles all naming conventions:
# C# PascalCase (MyClient), Python snake_case (my_client), JS camelCase (myClient)
predicate client(Type) => Type.Name.Words:contains('client')
predicate notPublic(Type) => !Type.Public

foreach Types:client:notPublic => PRINT('{warning:@yellow} {item.Name} should be public')
```

Or use language-specific overloads when the rules differ per language:

```ruby
predicate client(Type:csharp) => Type.Name:ew('Client')
predicate client(Type:python) => Type.Name:ew('_client')

foreach Types:client:notPublic => PRINT('{warning:@yellow} {item.Name} should be public')
```

### Domain Architecture Rules

```ruby
import code

predicate bannedUsing(File) => Path('**/Domain/**') && File.Usings:any(Using:ct('System.IO'))

foreach Files:csharp:bannedUsing => PRINT('{error:@red} Domain layer must not reference System.IO in {File.Path}')
```
