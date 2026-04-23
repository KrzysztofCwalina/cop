# C# Style Package Reference

The `csharp-style` package provides StyleCop-equivalent formatting, naming, and style checks for C# code. It covers interface naming (SA1302), type/method casing (SA1300), single type per file (SA1402), and whitespace hygiene (SA1005, SA1027, SA1028).

**Source:** [`packages/dotnet/csharp-style/src/`](../../packages/dotnet/csharp-style/src/)

## Import

```ruby
import csharp-style
```

## Predicates

Defined in `definitions.cop`:

| Predicate | StyleCop Rule | Matches |
|---|---|---|
| `interfaceMissingIPrefix(Type)` | SA1302 | Interface not starting with `I` + uppercase |
| `lowercaseTypeName(Type)` | SA1300 | Type name starts with lowercase |
| `lowercasePublicMethod(Method)` | SA1300 | Public method starts with lowercase |
| `multipleTypesInFile(Type)` | SA1402 | File contains multiple types |
| `commentMissingSpace(Line)` | SA1005 | `//` comment without leading space |
| `containsTab(Line)` | SA1027 | Line contains tab character |
| `trailingWhitespace(Line)` | SA1028 | Line ends with spaces or tabs |

## Checks

Defined in `checks.cop`:

| Check | Rule | Severity | Message |
|---|---|---|---|
| `interface-prefix` | SA1302 | warning | Interface {item.Name} should begin with 'I' followed by an uppercase letter |
| `type-name-casing` | SA1300 | warning | Type {item.Name} should begin with an uppercase letter |
| `method-name-casing` | SA1300 | warning | {item.Name} has a public method that should begin with an uppercase letter |
| `single-type-per-file` | SA1402 | warning | {item.Name} — file should contain only a single type |
| `comment-spacing` | SA1005 | warning | Single-line comment should begin with a space |
| `no-tabs` | SA1027 | warning | Use spaces instead of tabs |
| `no-trailing-whitespace` | SA1028 | warning | Remove trailing whitespace |

All checks are combined into the `csharp-style` array.

## Usage

```ruby
import csharp-style

# Run all style checks
CHECK(csharp-style)
```
