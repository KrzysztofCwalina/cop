# Provider query representation

Cop keeps a structured provider-query representation and a source-shaped **Cop**
representation of supported queries. Providers still receive typed objects or JSON,
**not Cop text**; no new query language or transport has been introduced.
This document describes implemented query support, including the changes in
[PR #74](https://github.com/KrzysztofCwalina/cop/pull/74), not a proposed language.
If those changes have not landed in your build, its execution and optimization
behavior may differ.

## From Cop expressions to provider queries

Cop expressions build deferred collections. When possible, the interpreter translates
their predicates into `FilterExpression` trees and sends them in a `ProviderQuery`.
CLR providers receive the objects directly; process providers receive their JSON form.
The provider name identifies the destination separately from the query object.

For example, `cb.Types:hasAlphaPrefix:isClient` can accumulate two conditions into a
single query. If those predicates test the name's prefix and suffix, its readable
filter representation is:

```text
Name:startsWith('Alpha'), Name:endsWith('Client')
```

The predicates can be defined in separate files loaded into the same program.
A composite predicate referencing both names can produce the same filter tree.
The provider receives the conditions, not the original predicate names or filenames.

## Source-shaped Cop queries

Run a program with `-d` to see `[query-cop]` immediately before a provider query is
materialized. This is generated from retained Cop syntax and resolved dependencies,
not reconstructed from the flattened JSON filter tree.

For example, this program can load its two predicates from separate files:

```cop skip
let prefix = 'Alpha'
predicate hasAlphaPrefix(Type) => Type.Name:startsWith(prefix)
predicate isClient(Type) => Type.Name:endsWith('Client')
command MAIN = cb.Types:hasAlphaPrefix:isClient
```

Its provider-side Cop representation is:

```cop skip
predicate hasAlphaPrefix(Type) => Type.Name:startsWith('Alpha')
predicate isClient(Type) => Type.Name:endsWith('Client')
command MAIN = Types:hasAlphaPrefix:isClient
```

`Types` means the requested schema collection in the provider/root context printed
on the diagnostic header. Replaying this program requires that collection binding
and its schema types; it is not an independently configured CLI program. Root paths,
provider identity, exclusions, and options remain request metadata, not invented Cop
syntax.

The representation retains colon chains, `.Where` calls, lambda parameter names,
logical grouping, negation, and required named predicate definitions. It resolves
collection aliases, literal bindings and their aliases, and already evaluated
scalar values. Only needed predicate definitions are included, in dependency order.
Captured values are snapshotted; conflicting definition names are renamed only when
necessary to preserve meaning.

Formatting is normalized, not byte-for-byte source preservation: comments, whitespace,
redundant parentheses, literal quoting, and equivalent parameter annotations may
change. String literals are escaped without accidental interpolation. Numbers use
invariant, parseable Cop notation, including values .NET normally prints in exponent
notation. Output is not truncated.

Planning and rendering do not enumerate collections or execute arbitrary thunks.
Computed or effectful bindings that have not already been evaluated are not executed
to make a query look more resolved; an unsupported predicate remains local.

`[query-local]` identifies host-only predicates, projections, sorting, indexing,
terminals such as `.Count`, and concatenation. A local predicate still refers to
bindings in the original program; it is not advertised as a closed provider query.
Local expressions outside the source writer's supported subset are explicitly
identified as nonportable, rather than printed as misleading runnable Cop.
Filters after a local stage stay local. For concatenation, filters are distributed
to the provider-backed inputs and each actual provider query gets its own Cop
representation; concatenation itself remains local.

In .NET, `CopQueryable.CopQuery` exposes a single provider collection's generated
program without executing it. A concatenation is not one provider query: use `-d`
to inspect its individual provider programs instead.
Source provenance is not stored on `ProviderQuery`, so it neither changes JSON packets
nor affects query-cache identity. Queries constructed directly from a filter tree
without Cop provenance use a normalized `.Where` representation when supported;
an unnamed collection or an unsupported programmatic filter reports source as
unavailable instead of fabricating an equivalent.

See [ten captured examples](query-examples.md) for actual generated Cop, independently
replayed queries, and their received JSON packets.

## Query fields

| CLR field | JSON field | Meaning |
|-----------|------------|---------|
| `RootPath` | `rootPath` | Target path; can be absent for non-file-backed providers. |
| `Collection` | `collection` | Requested collection, such as `Types`; absent means all collections. |
| `Filter` | `filter` | Combined filter tree. |
| `CollectionFilters` | `collectionFilters` | Map from collection names to their filter trees. |
| `ExcludedDirectories` | `excludedDirectories` | Directory names to exclude during traversal. |
| `Options` | `options` | Provider-specific string-valued options. |

The host normalizes filtered single-collection queries so the combined filter is
also available under that collection's entry in `CollectionFilters`.

## Readable diagnostic notation

`FilterExpression.Format` renders trees for diagnostic output:

| Filter | Example |
|--------|---------|
| Boolean property is true | `IsPublic` |
| Boolean property is false | `!IsPublic` |
| String predicate | `Name:startsWith('Alpha')` |
| Ordinal string equality | `Name:equalsExact('AlphaClient')` |
| Numeric comparison | `Line>=10` |
| AND | `IsPublic, Name:endsWith('Client')` |
| OR | `(IsPublic \|\| IsAbstract)` |
| NOT | `!(Name:startsWith('Test'))` |
| String contains any candidate | `Name:containsAny(['Get', 'Set'])` |
| String equals a candidate | `Extension:in(['.cs', '.vb'])` |
| Collection membership | `Keywords:contains('var')` |
| Collection item predicate | `Methods:any(IsPublic)` |
| Collection count comparison | `Parameters:count()>3` |
| Flags test | `Modifiers:isSet(1)` |

For example, a query and its provider-returned record count can appear as:

```text
[query] Query csharp.Types root=C:\example filter=Name:startsWith('Alpha'), Name:endsWith('Client')
[query-result] csharp.Types returned=1
```

These lines are diagnostics, not executable query statements. The formatter is not a
round-trip serializer: it does not define comprehensive escaping or preserve every
grouping boundary. There is no parser accepting these strings as provider requests.
Use the structured tree or JSON representation for machine-to-machine communication.
The returned count is measured before the host applies residual filtering.

## JSON representation

Process-provider query requests use a `method`/`params` envelope. This example omits
the optional `collectionFilters`, `excludedDirectories`, and `options` fields:

```json
{
  "method": "query",
  "params": {
    "rootPath": "C:\\example",
    "collection": "Types",
    "filter": {
      "kind": "and",
      "conditions": [
        {
          "kind": "string",
          "property": "Name",
          "op": 0,
          "value": "Alpha"
        },
        {
          "kind": "string",
          "property": "Name",
          "op": 1,
          "value": "Client"
        }
      ]
    }
  }
}
```

Messages are UTF-8 JSON with `Content-Length` framing. Object fields use camel case,
and each filter node has a `kind` discriminator:

| `kind` | Node fields |
|--------|-------------|
| `property` | `property`, boolean `value` |
| `string` | `property`, numeric `op`, string `value` |
| `comparison` | `property`, numeric `op`, numeric `value` |
| `and` | `conditions`: array of filters |
| `or` | `conditions`: array of filters |
| `not` | `inner`: filter |
| `containsAny` | `property`, `values`: array of strings |
| `in` | `property`, `values`: array of strings |
| `collectionContains` | `property`, string `value` |
| `collectionAny` | `property`, `itemFilter`: filter |
| `collectionCount` | `property`, numeric `op`, integer `value` |
| `flags` | `property`, numeric `op`, integer `value` |

Operation codes depend on the node:

| Node | Operation codes |
|------|-----------------|
| `string` | `0` startsWith, `1` endsWith, `2` contains, `3` equals, `4` matches, `5` sameAs, `6` equalsExact |
| `comparison`, `collectionCount` | `0` >, `1` <, `2` ==, `3` >=, `4` <= |
| `flags` | `0` isSet, `1` isClear |

String starts-with, ends-with, contains, and `equals` are case-insensitive.
`matches` uses case-sensitive regular expressions, and `equalsExact` uses ordinal
equality. The legacy `sameAs` operation compares invariant-lowercased identifiers
after removing underscores and hyphens. Cop's string `==` translates to
`equalsExact`, not `equals`.

## Representation versus optimization support

The filter model is broader than the interpreter's current pushdown subset.
A node existing in the model does not imply that every corresponding Cop expression
will be translated or that every provider can optimize it.

Translation supports direct scalar fields, literal comparisons, supported string
predicates, and unambiguous compatible named predicates, including AND, OR, and NOT.
Nested relationships, arbitrary functions, unevaluated computed captures, and unsupported predicate
overloads or guards can require local evaluation. A local-only stage can prevent
subsequent filters from reaching the provider.

Providers may ignore unsupported filter hints and return extra records. The host
reapplies the requested filter for correctness. A provider must not discard possible
matches by applying an incomplete NOT or OR condition.

The provider query has no projection, ordering, join, limit, or aggregate nodes.
Those operations remain local. A union distributes filters into its provider-backed
inputs rather than becoming a provider-side union query.

See [Provider extensibility](extensibility.md) for provider architecture and transport
details.

## Implementation references

- [ProviderQuery](../cop/shared/providers/ObjectProvider.cs)
- [FilterExpression nodes and diagnostic formatter](../cop/shared/providers/FilterExpression.cs)
- [Predicate translation](../cop/shared/interpreter/PredicateCompiler.cs)
- [Retained query syntax and dependency resolution](../cop/shared/interpreter/QuerySource.cs)
- [Portable Cop query source writer](../cop/shared/ast/QuerySourceWriter.cs)
- [Query execution and host residual filtering](../cop/runtime/ProviderQueryService.cs)
- [Process-provider serialization](../cop/runtime/ProcessObjectProvider.cs)
