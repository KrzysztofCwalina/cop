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

## Toolchain discovery and diagnostics

Cop probes `pwsh` and, on Windows, `powershell` on the process `PATH`; an unusable
launcher is not treated as an available parser. The helper starts without profiles
and uses the installed engine's standard AST API, so no PowerShell SDK or extra module
is needed. The installed engine determines which PowerShell syntax is supported.
Cop never installs PowerShell or executes the analyzed scripts.

An unavailable engine permits the warned fallback described above. Source syntax
errors and failures after a native request has started remain analysis errors, rather
than silently turning into successful fallback results. Warnings are emitted once
per relevant scope in an execution, and remain plain text when stderr is redirected
or `--no-color` is used. To enable native analysis, make a working PowerShell executable
available to Cop and rerun the check.

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
