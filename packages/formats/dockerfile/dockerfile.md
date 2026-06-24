# dockerfile

Analyze Dockerfiles by exposing parsed instructions and build stages from files named
`Dockerfile`, `Dockerfile.*`, or `*.dockerfile`. The parser is hand-rolled and handles blank
lines, comments, line continuations, and `FROM image AS name` aliases.

## Types

- **DockerInstruction** — an instruction with `Instruction` (uppercased keyword), `Argument`,
  `Line`, `Stage`, `File`, and `Source`.
- **DockerStage** — a `FROM` stage with `Name`, `Image`, `Index`, `Line`, `File`, and `Source`.

## Usage

```cop
import dockerfile

let instructions = dockerfile.parse().Instructions
let stages = dockerfile.parse('containers').Stages
```

Both types conform to `TextFilePosition`, so they work directly with `toError`, `toWarning`,
and `toInfo` from `code-analysis`.

## Example — flag mutable base images

```cop
import dockerfile
import code

predicate isUnpinnedFrom(DockerInstruction) =>
    DockerInstruction.Instruction:equals('FROM')
    && (DockerInstruction.Argument:contains(':latest') || !DockerInstruction.Argument:contains(':'))

let unpinned = dockerfile.parse().Instructions:isUnpinnedFrom
    :toWarning('Base image {item.Argument} should be pinned')

command MAIN = CHECK(unpinned)
```

See `samples/pin-base-images.cop` for a runnable version.
