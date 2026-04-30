# code-analysis

Provides types and functions for producing structured code analysis results.

Defines the `Violation` type and `toError`/`toWarning`/`toInfo` functions that transform
source code items (Statements, Types, etc.) into typed violations with severity,
message, file path, and line number.

## Usage

```cop
import code-analysis

let VarErrors = Statements:csharp:varDeclaration:toError("Do not use var for {item.MemberName}")
foreach VarErrors => '{item.Severity}: {item.Message} ({item.File}:{item.Line})'
```
