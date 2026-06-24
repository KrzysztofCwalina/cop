# powershell

Analyze PowerShell scripts (`.ps1`, `.psm1`, `.psd1`) by extracting simple commands,
their logical command text, line numbers, file positions, and per-script strict-mode
metadata. The parser is hand-rolled C# with no third-party PowerShell parser dependency.

## Types

- **PowerShellCommand** — a command occurrence. Fields: `Name`, `Text`, `Line`, `File`, `Source`.
  `Text` is the full logical command line, so pipeline checks can inspect the complete command.
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
