# dockerfile

Analyze Dockerfiles by exposing parsed instructions and build stages from files named
`Dockerfile`, `Dockerfile.*`, or `*.dockerfile`. The parser is hand-rolled and handles blank
lines, full-line comments, line continuations (including the `escape` directive),
`FROM image AS name` aliases, and simple named heredocs. Inline `#` text is preserved
as instruction argument text, including heredoc contents.

JSON-form instructions expose decoded `Arguments` and `IsJsonForm`. `From` records a
`COPY`/`ADD --from=` source; `ReferencedStage` is the prior stage index for a stage alias
or numeric copy reference, or -1 for an external/unresolved source. `FROM` references
to prior named stages are resolved too. Variable expansion, custom frontends, and shell
semantics within `RUN` are not evaluated.

Collections are read lazily per file and share one execution snapshot. Directory symlinks
and reparse points are not followed; file or directory access failures are reported.

## Types

- **DockerInstruction** — an instruction with `Instruction` (uppercased keyword), `Argument`,
  `Line`, `Stage`, `IsJsonForm`, `Arguments`, `From`, `ReferencedStage`, `File`, and `Source`.
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
