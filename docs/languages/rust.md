# Rust Walkthrough

This guide walks you through installing cop, setting up a Rust project for analysis, writing a simple rule, and running it.

---

## 1. Install Cop

Download the latest release for your platform from [GitHub Releases](https://github.com/KrzysztofCwalina/cop/releases) and add it to your PATH.

Verify the installation:

```bash
cop --version
```

---

## 2. Initialize a Rust Project

Create (or navigate to) a Rust project:

```bash
cargo init my-project
cd my-project
```

Add some code to `src/main.rs`:

```rust
use std::collections::HashMap;

/// A public user struct.
#[derive(Debug, Clone)]
pub struct User {
    pub name: String,
    pub age: u32,
    email: String,
}

impl User {
    pub fn new(name: String, age: u32, email: String) -> Self {
        User { name, age, email }
    }

    pub fn display_name(&self) -> String {
        format!("{} (age {})", self.name, self.age)
    }

    fn validate(&self) -> bool {
        !self.name.is_empty() && self.age > 0
    }
}

pub enum Status {
    Active,
    Inactive,
    Suspended,
}

fn helper(x: i32) -> i32 {
    if x < 0 {
        panic!("x must be non-negative");
    }
    x * 2
}

fn main() {
    let user = User::new("Alice".to_string(), 30, "alice@example.com".to_string());
    println!("{}", user.display_name());
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

## 4. Run the Built-in Rust Checks

The fastest way to get value is the **`rust-checks`** package — a curated set of Rust
correctness, style, and documentation checks. It hardcodes the Rust provider, so no `-p`
flag is needed:

```bash
cop run rust-checks -t .
```

It flags common issues such as:

- `.unwrap()` / `.expect()` — panicking APIs that should propagate errors with `?`
- `panic!` / `todo!` / `unimplemented!` — aborts and unfinished code
- `println!` / `eprintln!` / `dbg!` — console output that belongs in a logging framework
- Types that are not `UpperCamelCase` and functions that are not `snake_case`
- Public types and methods missing doc comments

Example output:

```
src/main.rs(33): warning: Avoid panic! in library code — return a Result instead
src/main.rs(40): warning: Avoid println! — use a logging framework (log/tracing)
src/main.rs(25): warning: Public type Status is missing a doc comment
src/main.rs(11): warning: User (impl) has public methods without doc comments
```

---

## 5. Write a Custom Rule

Create a file called `checks.cop` in your project root:

```cop
import rust
import code
import code

let cb = rust.parse()

# Flag public structs/enums/traits that have no doc comment (skip impl blocks)
predicate isDeclaredType(Type) => Type.Kind == Struct || Type.Kind == Enum || Type.Kind == Interface
predicate isUndocumented(Type) => isDeclaredType && isPublic && !Type.Documented

# Flag uses of panic! (prefer Result returns in library code)
predicate isPanicCall(Statement) => Statement.Kind == throw && Statement.MemberName == 'panic'

let undocumented = cb.Types:isUndocumented
    :toWarning('Public type {item.Name} is missing documentation')

let panics = cb.Statements:isPanicCall
    :toWarning('Avoid panic! at line {item.Line} — prefer returning Result')

command MAIN = CHECK(undocumented + panics)
```

This rule does two things:
1. **Finds public types without doc comments** (`///` above the declaration)
2. **Finds uses of `panic!`** — a common code smell in library code

---

## 6. Run the Rule

From your project root:

```bash
cop checks.cop -t .
```

Example output:

```
src/main.rs(25): warning: Public type Status is missing documentation
src/main.rs(33): warning: Avoid panic! at line 33 — prefer returning Result
```

---

## 7. Enforce Crate Layering

Cop discovers your Cargo crates and their dependencies (from each `Cargo.toml`, including
workspace-shorthand `dep.workspace = true` and `[dependencies.<name>]` forms). The
language-agnostic **`code-layering`** package lets you enforce architectural rules across
crates — for example, that foundation crates must not depend on higher-level service crates.

Create `layering.cop`:

```cop
import rust
import code
import code

let cb = codebase(rust.parse())

# Foundation crates must not depend on service crates.
let foundation-crates = ['my_core']
let service-crates = ['my_storage' 'my_identity']

predicate isFoundationCrate(Project) => Project.Name:in(foundation-crates)
predicate isServiceCrateName(string) => string:in(service-crates)
predicate dependsOnService(Project) => Project.References:any(isServiceCrateName)

let violations = cb.Projects:isFoundationCrate:dependsOnService
    :toError('Foundation crate {item.Name} must not depend on a service crate')

command MAIN = CHECK(violations)
```

Run it against your workspace root:

```bash
cop layering.cop -t .
```

The check exits non-zero (and prints each offending crate) when a foundation crate
references a service crate, so you can wire it into CI.

> Tip: `cb.Projects` exposes each crate's `Name` and `References` (its Cargo dependencies).
> Use `Project.References:any(predicate)` to test whether a crate depends on a set of crates.

---

## 8. Explore Further

### List all types in your project

```cop
import rust

let cb = rust.parse()
command MAIN = foreach cb.Types => '{item.Name} ({item.Kind})'
```

### List methods of each type

```cop
import rust

let cb = rust.parse()

# Each type lists its method names (impl blocks hold a type's methods)
command MAIN = foreach cb.Types => '{item.Name}: {item.MethodNames}'
```

### Check for missing documentation on public methods

```cop
import rust
import code
import code

let cb = rust.parse()

predicate isUndocumentedMethod(Method) => isPublic && !Method.Documented
predicate hasUndocumentedMethods(Type) => Type.Methods:any(isUndocumentedMethod)

let violations = cb.Types:hasUndocumentedMethods
    :toWarning('{item.Name} has public methods without doc comments')

command MAIN = CHECK(violations)
```

---

## Available Collections

The `rust.parse()` function returns a `Codebase` with these collections:

| Collection | Description |
|------------|-------------|
| `cb.Types` | All structs, enums, traits, and impl blocks |
| `cb.Statements` | Function calls, macro invocations, panic!/todo! |
| `cb.Calls` | Just the call statements (method/function/macro calls) |
| `cb.Files` | Source files with metadata |
| `cb.Lines` | Every line of code (with kind: code/comment/blank) |
| `cb.Projects` | Cargo crates with their dependencies (from `Cargo.toml`) |

### Type Kinds

| Rust Construct | Cop TypeKind |
|----------------|-------------|
| `struct` | Struct |
| `enum` | Enum |
| `trait` | Interface |
| `impl` block | Class |

---

## Tips

- Use `cop verify checks.cop` to check your rule for syntax/type errors before running
- Start with the built-in `cop run rust-checks -t .` before writing custom rules
- Enforce crate dependency rules with the `code-layering` package (see section 7)
- Use `-t path/` to target a specific subdirectory
- Combine with other providers: `import rust` + `import python` to analyze polyglot projects
- Run `cop help code` to see all available predicates and types
