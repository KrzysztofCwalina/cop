# Go Walkthrough

This guide walks you through analyzing a Go project with cop — from setup to writing and running custom rules.

---

## 1. Install Cop

Download the latest release for your platform from [GitHub Releases](https://github.com/KrzysztofCwalina/cop/releases) and add it to your PATH.

Verify the installation:

```bash
cop --version
```

---

## 2. Initialize a Go Project

Create (or navigate to) a Go project:

```bash
mkdir myapp && cd myapp
go mod init github.com/example/myapp
```

Add some code to `main.go`:

```go
package main

import (
	"fmt"
	"net/http"
)

// User represents an application user.
type User struct {
	Name  string
	Age   int
	email string
}

// Greeter defines greeting behavior.
type Greeter interface {
	Greet(name string) string
	Farewell(name string) string
}

// NewUser creates a new User instance.
func NewUser(name string, age int, email string) *User {
	return &User{Name: name, Age: age, email: email}
}

// DisplayName returns the user's formatted display name.
func (u *User) DisplayName() string {
	return fmt.Sprintf("%s (age %d)", u.Name, u.Age)
}

func (u *User) validate() bool {
	return u.Name != "" && u.Age > 0
}

type RequestStatus int

func processData(data []byte) error {
	if len(data) == 0 {
		panic("empty data")
	}
	fmt.Println("processing...")
	http.Get("http://example.com")
	return nil
}

func main() {
	user := NewUser("Alice", 30, "alice@example.com")
	fmt.Println(user.DisplayName())
}
```

---

## 3. Set Up Agent Context

Run `cop init` to generate instruction files that teach **GitHub Copilot** how to write cop rules in your project:

```bash
cop init
```

Commit the generated files (`.github/copilot-instructions.md`, `AGENTS.md`) to your repo.

<sub>Using Claude Code? Run `cop init --claude` to generate Claude Code instruction files (`.claude/commands/cop.md`) instead.</sub>

---

## 4. Write a Simple Rule

Create a file called `checks.cop` in your project root:

```cop
import go
import code
import code

let cb = go.parse()

# Flag exported types without doc comments
predicate isUndocumented(Type) => Type.Documented == false && Type:isPublic

# Flag uses of panic() — prefer returning errors
predicate isPanic(Statement) => Statement.Kind == throw && Statement.MemberName == 'panic'

let undocumented = cb.Types:isUndocumented
    :toWarning('Exported type {item.Name} is missing a doc comment')

let panics = cb.Statements:isPanic
    :toWarning('Avoid panic() at line {item.Line} — return an error instead')

command MAIN = CHECK(undocumented + panics)
```

This rule checks two common Go conventions:
1. **Exported types should have doc comments** (per `go vet` / `golint`)
2. **Avoid `panic()` in library code** — idiomatic Go returns errors

---

## 5. Run the Rule

From your project root:

```bash
cop checks.cop -t .
```

Example output:

```
main.go: warning: Exported type RequestStatus is missing a doc comment
main.go: warning: Avoid panic() at line 43 — return an error instead

2 violation(s) found.
```

---

## 6. Enforce Module Layering

Cop discovers your Go modules and their dependencies (from each `go.mod`). The
language-agnostic **`code-layering`** package lets you enforce architectural rules across
modules — for example, that foundation modules must not depend on higher-level service
modules.

Create `layering.cop`:

```cop
import go
import code
import code

let cb = codebase(go.parse())

# Foundation modules must not depend on service modules.
let foundation-modules = ['core']
let service-modules = ['example.com/storage' 'example.com/identity']

predicate isFoundationModule(Project) => Project.Name:in(foundation-modules)
predicate isServiceModulePath(string) => string:in(service-modules)
predicate dependsOnService(Project) => Project.References:any(isServiceModulePath)

let violations = cb.Projects:isFoundationModule:dependsOnService
    :toError('Foundation module {item.Name} must not depend on a service module')

command MAIN = CHECK(violations)
```

Run it against your workspace root:

```bash
cop layering.cop -t .
```

The check exits non-zero (and prints each offending module) when a foundation module
references a service module, so you can wire it into CI.

> Tip: `cb.Projects` exposes each module's `Name` (the last path segment) and `References` (its full module-path dependencies).
> Use `Project.References:any(predicate)` to test whether a module depends on a set of modules.

---

## 7. Explore Further

### List all exported types

```cop
import go
import code

let cb = go.parse()
command MAIN = foreach cb.Types:isPublic => '{item.Name} ({item.Kind})'
```

### List all function calls

```cop
import go

let cb = go.parse()
command MAIN = foreach cb.Statements => '{item.Kind}: {item.MemberName} (line {item.Line})'
```

### Check for unexported types with exported methods

```cop
import go
import code
import code

let cb = go.parse()

predicate hasExportedMethods(Type) => Type.Methods:isPublic.count() > 0
predicate isUnexported(Type) => Type:isPublic == false

let violations = cb.Types:isUnexported:hasExportedMethods
    :toInfo('Unexported type {item.Name} has exported methods')

command MAIN = CHECK(violations)
```

---

## Available Collections

The `go.parse()` function returns a `Codebase` with these collections:

| Collection | Description |
|------------|-------------|
| `cb.Types` | All structs, interfaces, and type declarations |
| `cb.Statements` | Function calls, panic(), defer, go statements |
| `cb.Files` | Source files with metadata |
| `cb.Lines` | Every line of code (with kind: code/comment/blank) |
| `cb.Projects` | go.mod projects with dependencies |

### Type Kinds

| Go Construct | Cop TypeKind |
|--------------|-------------|
| `type X struct` | Struct |
| `type X interface` | Interface |
| `type X <other>` | Class |
| Methods with receiver | Attached to struct's Methods |

### Go Conventions in Cop

- **Exported = Public**: In Go, names starting with uppercase are exported. Cop maps this to the `IsPublic` modifier, so `:isPublic` filters for exported identifiers.
- **Doc comments**: Go doc comments are `//` comments immediately preceding a declaration. Cop detects these via the `Documented` property.

### Syntax-error reporting

`go.parse()` uses a real lexer + recursive-descent parser (not a line scanner). When a `.go`
file contains a syntax error — an unterminated string, an unterminated comment, a missing closing
`}`/`)`, or a malformed declaration — cop surfaces it as a **warning** of the form
`path(line,col): error: message` and still analyzes the rest of that file and every other file.
Malformed sources are reported, never silently skipped.

---

## Tips

- Use `cop verify checks.cop` to check your rule for syntax/type errors before running
- Use `-t path/` to target a specific subdirectory
- Combine with other providers: `import go` + `import python` to analyze polyglot projects
- Run `cop help code` to see all available predicates and types
