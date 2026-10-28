# powershell

Analyze PowerShell scripts (`.ps1`, `.psm1`, `.psd1`) with the installed PowerShell
parser (`pwsh`, or Windows `powershell`). No SDK package, module installation, or additional
setup is needed. The parser reads ASTs only: analyzed scripts are never executed.
A no-profile JSON-lines helper is reused during one Cop execution, with bounded time/output,
and disposed afterward. Syntax errors are reported rather than silently skipped.

When PowerShell is absent or unusable, Cop prints a yellow warning and uses simpler analysis.
The fallback recognizes assignment RHS and parenthesized invocations and skips literal
here-string bodies, but does not fully analyze interpolation, nested syntax, or dynamic
command names. Native AST analysis includes calls inside expandable strings; dynamic
invocation names that cannot be determined statically are not invented.

Collections are read lazily per file and share one execution snapshot. Directory symlinks
and reparse points are not followed; file or directory access failures are reported.

## Types

- **PowerShellCommand** — a command occurrence. Fields: `Name`, `Text`, `Line`, `File`, `Source`.
  `Text` includes the containing pipeline, so pipeline checks can inspect its commands.
- **PowerShellScript** — one item per script. Fields: `UsesStrictMode`, `Line`, `File`, `Source`.

## Usage

```cop
import powershell

let ps = powershell.parse()
let scriptCommands = powershell.parse('scripts').Commands
```

Both types conform to `TextFilePosition`, so they work directly with `toError`,
`toWarning`, and `toInfo` from `code-analysis`.

## Example — dangerous dynamic execution

```cop
import powershell
import code

predicate isDynamicExec(PowerShellCommand) =>
    PowerShellCommand.Name:equals('Invoke-Expression')
    || (PowerShellCommand.Name:equals('iex') && !PowerShellCommand.Text:contains('| iex'))
    || ((PowerShellCommand.Name:equals('Invoke-WebRequest') || PowerShellCommand.Name:equals('iwr') || PowerShellCommand.Name:equals('curl'))
        && PowerShellCommand.Text:contains('| iex'))

let violations = powershell.parse().Commands:isDynamicExec
    :toError('Avoid dynamic PowerShell execution: {item.Text}')

command MAIN = CHECK(violations)
```

See `samples/dangerous-dynamic-exec.cop` for a runnable check with fixtures.
