# yaml

Analyze YAML configuration files — CI workflows (GitHub Actions, Azure Pipelines), Kubernetes
manifests, and docker-compose — by flattening every mapping key into a dotted **path** with its
scalar **value** and **line** number. The parser is hand-rolled (no third-party YAML library).

## Types

- **YamlEntry** — a single mapping key. Fields: `Path` (dotted key path; sequence elements appear
  as `[]`, e.g. `jobs.build.steps[].uses`), `Key` (leaf key), `Value` (scalar value, empty for
  nested mappings/sequences), `Line`, `Document` (0-based document index), `File`, `Source`.
- **YamlDocument** — a document within a file (files may hold several, separated by `---`).
  Fields: `Index`, `Line`, `File`, `Source`.

## Usage

```cop
import yaml

let entries = yaml.parse().Entries            # parse YAML in the target directory
let workflows = yaml.parse('.github/workflows').Entries
```

`YamlEntry` and `YamlDocument` conform to `TextFilePosition`, so they work directly with
`toError` / `toWarning` / `toInfo` from `code-analysis`.

## Example — require actions pinned to a commit SHA

```cop
import yaml
import code-analysis

predicate isUnpinnedActionUse(YamlEntry) =>
    YamlEntry.Key:equals('uses')
    && !YamlEntry.Value:matches('.*@[0-9a-f]{40}$')

let unpinned = yaml.parse().Entries:isUnpinnedActionUse
    :toWarning('Action {item.Value} is not pinned to a commit SHA')

command MAIN = CHECK(unpinned)
```

See `samples/pin-actions-to-sha.cop` for a runnable version.
