## csharp-style

StyleCop-equivalent formatting, naming, and style checks. &nbsp; `import csharp-style`

**Source:** [`packages/dotnet/csharp-style/src/`](../../../packages/dotnet/csharp-style/src/)

---

### Predicates

| Predicate (Rule) | Applies To | Matches |
|---|---|---|
| `interfaceMissingIPrefix` (SA1302) | [Type](../code.md#type) | Interface not starting with `I` + uppercase |
| `lowercaseTypeName` (SA1300) | [Type](../code.md#type) | Type name starts with lowercase |
| `lowercasePublicMethod` (SA1300) | [Method](../code.md#method) | Public method starts with lowercase |
| `multipleTypesInFile` (SA1402) | [Type](../code.md#type) | File contains multiple types |
| `commentMissingSpace` (SA1005) | [Line](../code.md#line) | `//` comment without leading space |
| `containsTab` (SA1027) | [Line](../code.md#line) | Line contains tab character |
| `trailingWhitespace` (SA1028) | [Line](../code.md#line) | Line ends with spaces or tabs |

---

### Checks

| Check (Rule) | Severity | Message |
|---|---|---|
| `interface-prefix` (SA1302) | warning | Interface should begin with 'I' + uppercase |
| `type-name-casing` (SA1300) | warning | Type should begin with uppercase letter |
| `method-name-casing` (SA1300) | warning | Public method should begin with uppercase letter |
| `single-type-per-file` (SA1402) | warning | File should contain only a single type |
| `comment-spacing` (SA1005) | warning | Single-line comment should begin with a space |
| `no-tabs` (SA1027) | warning | Use spaces instead of tabs |
| `no-trailing-whitespace` (SA1028) | warning | Remove trailing whitespace |
