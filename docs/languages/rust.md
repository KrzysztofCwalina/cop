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

Run `cop init` to generate instruction files that teach coding agents (GitHub Copilot, Claude Code) how to write cop rules in your project:

```bash
cop init
```

Commit the generated files (`.github/copilot-instructions.md`, `AGENTS.md`) to your repo.

---

## 4. Write a Simple Rule

Create a file called `checks.cop` in your project root:

```cop
import rust
import code
import code-analysis

let cb = rust.parse()

# Flag structs that have no doc comment
predicate isUndocumented(Type) => Type.Documented == false && Type:isPublic

# Flag uses of panic! (prefer Result returns in library code)
predicate isPanic(Statement) => Statement.Kind == throw && Statement.MemberName == 'panic'

let undocumented = cb.Types:isUndocumented
    :toWarning('Public type {item.Name} is missing documentation')

let panics = cb.Statements:isPanic
    :toWarning('Avoid panic! at line {item.Line} — prefer returning Result')

command MAIN = CHECK(undocumented + panics)
```

This rule does two things:
1. **Finds public types without doc comments** (`///` above the declaration)
2. **Finds uses of `panic!`** — a common code smell in library code

---

## 5. Run the Rule

From your project root:

```bash
cop checks.cop -t .
```

Example output:

```
src/main.rs: warning: Public type Status is missing documentation
src/main.rs: warning: Avoid panic! at line 34 — prefer returning Result

2 violation(s) found.
```

---

## 6. Explore Further

### List all types in your project

```cop
import rust

let cb = rust.parse()
command MAIN = foreach cb.Types => '{item.Name} ({item.Kind})'
```

### List public methods

```cop
import rust
import code

let cb = rust.parse()
let public-methods = cb.Types.Methods:isPublic
command MAIN = foreach public-methods => '{item.Name} (line {item.Line})'
```

### Check for missing documentation on public functions

```cop
import rust
import code
import code-analysis

let cb = rust.parse()

predicate isUndocumented(Method) => Method.Documented == false && Method:isPublic

let violations = cb.Types.Methods:isUndocumented
    :toWarning('Public method {item.Name} has no doc comment')

command MAIN = CHECK(violations)
```

---

## Available Collections

The `rust.parse()` function returns a `Codebase` with these collections:

| Collection | Description |
|------------|-------------|
| `cb.Types` | All structs, enums, traits, and impl blocks |
| `cb.Statements` | Function calls, macro invocations, panic!/todo! |
| `cb.Files` | Source files with metadata |
| `cb.Lines` | Every line of code (with kind: code/comment/blank) |
| `cb.Projects` | Cargo.toml projects with dependencies |

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
- Use `-t path/` to target a specific subdirectory
- Combine with other providers: `import rust` + `import python` to analyze polyglot projects
- Run `cop help code` to see all available predicates and types
