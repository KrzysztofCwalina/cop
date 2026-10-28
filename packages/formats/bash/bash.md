# bash

Analyze Bash/Shell scripts (`.sh`, `.bash`) by extracting simple commands, their logical
command text, line numbers, file positions, and per-script strict-mode metadata. The parser
is hand-rolled C# with no third-party shell parser dependency.

Conditional prefixes (`if`, `while`, `then`, `do`, and similar simple forms) do not hide
the following command. `HasStrictMode` requires `errexit`, `nounset`, and `pipefail`;
options can be enabled separately anywhere in the script and later disabled with `set +...`.
This is lexical option tracking, not proof that every control-flow path enables strict mode.
Shell expansions, nested substitutions, here-documents, and dynamically constructed command
names are not a complete Bash AST and can require a dedicated shell analyzer.

Collections are read lazily per file and share one execution snapshot. Directory symlinks
and reparse points are not followed; file or directory access failures are reported.

## Types

- **ShellCommand** — a command occurrence. Fields: `Name`, `Text`, `Line`, `File`, `Source`.
  `Name` skips simple environment assignments plus `sudo`/`env` where possible.
- **ShellScript** — one item per script. Fields: `HasStrictMode`, `Line`, `File`, `Source`.

## Usage

```cop
import bash

let shell = bash.parse()
let scriptCommands = bash.parse('scripts').Commands
```

Both types conform to `TextFilePosition`, so they work directly with `toError`,
`toWarning`, and `toInfo` from `code-analysis`.

## Example — remote pipe to shell

```cop
import bash
import code

predicate isRemotePipeToShell(ShellCommand) =>
    (ShellCommand.Name:equals('curl') || ShellCommand.Name:equals('wget'))
    && ShellCommand.Text:matches('.*\\|\\s*(sh|bash)(\\s|$).*')

let violations = bash.parse().Commands:isRemotePipeToShell
    :toError('Avoid piping remote downloads directly to a shell: {item.Text}')

command MAIN = CHECK(violations)
```

See `samples/remote-pipe-to-shell.cop` for a runnable check with fixtures.
