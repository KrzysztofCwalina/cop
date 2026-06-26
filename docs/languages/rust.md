# Rust Walkthrough

This guide walks you through analyzing a Rust project with cop. The main workflow is
**agent-driven**: as you build, you ask your coding agent to turn problems you notice into
permanent, enforceable cop rules. Later sections cover writing rules by hand, running the
built-in Rust checks, and enforcing crate layering.

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

That's the code we'll analyze — cop reads your source files in place, with nothing to add or
configure. There's nothing to run yet, though: cop needs a rule first (section 4 or 5).

---

## 3. Set Up Agent Context

Run `cop init` once, in your **repository root** (the project root above — not a subfolder like `src/`):

```bash
cop init
```

This generates instruction files (`.github/copilot-instructions.md`, `AGENTS.md`) that teach
**GitHub Copilot** how to write and run cop rules. Commit them to your repo.

<sub>Using Claude Code? Run `cop init --claude` to generate Claude Code instruction files (`.claude/commands/cop.md`) instead.</sub>

---

## 4. Create Rules with Your Agent

This is the primary way to use cop. As you build, you (or your coding agent) will notice
patterns you want to ban going forward — a `panic!`, an `unwrap()`, a missing `///` doc
comment. Instead of leaving a code-review comment that gets forgotten, ask your agent to
capture the problem as a cop rule. Because `cop init` taught the agent how cop works, it
writes the rule into your `cop-checks/` folder, runs it, and fixes the violations — just like
a compiler error.

Just ask:

> "Flag `panic!`, `unwrap()`, and `expect()` in library code — propagate errors with `?`"

> "Ban `println!` and `dbg!` — use the `log` or `tracing` crate"

> "Create a cop rule that every public type has a `///` doc comment"

### The self-check loop

When your agent produces code in a shape you don't like, turn that feedback into a permanent rule:

1. The agent writes code with a pattern you dislike (e.g. it calls `.unwrap()` on a `Result`).
2. You say: **"Add a self-check that flags `.unwrap()` — we propagate errors with `?` here."**
3. The agent adds a focused check to your `cop-checks/` folder.
4. From now on, `cop` catches that pattern before it reaches code review.

The next sections show what such a rule looks like and how to run it yourself.

---

## 5. Write and Run a Rule by Hand

You don't need an agent — you can author `.cop` files directly. Create a file called
`checks.cop` in your project root:

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

Verify it, then run it from your project root. cop analyzes the current directory by default;
`-t <path>` points it at another folder:

```bash
cop verify checks.cop      # catch syntax/type errors first
cop checks.cop -t .
```

Example output:

```
src/main.rs(25): warning: Public type Status is missing documentation
src/main.rs(33): warning: Avoid panic! at line 33 — prefer returning Result
```

Exit code is `0` when clean and `1` when violations are found — suitable for CI. To organize
many rules, put one check per file in a `cop-checks/` folder with a `main.cop` entry point and
run `cop cop-checks/main.cop -t .` (this is exactly what your agent does for you).

---

## 6. Use the Built-In Rust Checks

Beyond your own rules, the **`rust-checks`** package is a curated set of
[Clippy](https://doc.rust-lang.org/clippy/)-inspired correctness, style, complexity, safety,
performance, and documentation checks. It hardcodes the Rust provider, so no `-p` flag is
needed:

```bash
cop run rust-checks -t .
```

It flags common issues such as:

- `.unwrap()` / `.expect()` — panicking APIs that should propagate errors with `?`
- `panic!` / `todo!` / `unimplemented!` — aborts and unfinished code
- `mem::forget` and `transmute` — leaks and undefined-behavior hazards
- `== None` / `!= None` — prefer `.is_none()` / `.is_some()`
- `println!` / `eprintln!` / `dbg!` — console output that belongs in a logging framework
- `use path::*` wildcard imports; types not `UpperCamelCase`; functions not `snake_case`
- functions with more than 7 parameters, and very large function bodies
- public `unsafe fn`s missing a `# Safety` doc section
- `.clone()` calls worth reviewing for unnecessary allocation
- public types and methods missing doc comments

Example output:

```
src/main.rs(33): warning: Avoid panic! in library code — return a Result instead [panic-macros]
src/main.rs(40): warning: Avoid println! — use a logging framework (log/tracing) [console-output]
src/main.rs(25): warning: Public type Status is missing a doc comment [undocumented-types]
src/main.rs(11): warning: User (impl) has public methods without doc comments [undocumented-methods]
```

Each line ends with the **name of the check** in brackets (e.g. `[panic-macros]`). That bracketed
name is exactly the identifier you subtract to turn the rule off — so you never have to read the
package source to find it.

### Excluding checks and violations

You won't want every rule on every project. There are two ways to opt out.

**Exclude a whole rule** — take the bracketed name from the output and subtract one or more checks
(or whole groups) from the package in a small `.cop` file. The `-` operator removes those
violations; everything else still runs:

```cop
import rust-checks
import code

# run every rust-checks rule except panic-macros and needless-clone
let my-checks = rust-checks - panic-macros - needless-clone

command MAIN = CHECK(my-checks)
```

```bash
cop my-checks.cop -t .
```

Checks are also grouped, so you can compose just the groups you want with `+`
(`rust-correctness-checks`, `rust-style-checks`, `rust-complexity-checks`,
`rust-safety-checks`, `rust-perf-checks`, `rust-doc-checks`):

```cop
import rust-checks
import code

# only correctness and safety — skip style, complexity, perf, and docs
let my-checks = rust-correctness-checks + rust-safety-checks

command MAIN = CHECK(my-checks)
```

**Exclude a single violation** — add a `// cop-ignore: <check>` comment on the line directly
above the one you want to exempt. Only that line is silenced; the rule keeps firing
everywhere else:

```rust
pub fn load_config(path: &str) -> Config {
    // cop-ignore: unwrap-calls
    let raw = std::fs::read_to_string(path).unwrap();  // exempted — NOT flagged
    parse(&raw).unwrap()                               // still flagged
}
```

`cop-ignore` works for the statement- and line-level checks (`unwrap-calls`, `expect-calls`,
`panic-macros`, `unfinished-code`, `mem-forget`, `transmute-calls`, `console-output`,
`needless-clone`, `eq-to-none`, `wildcard-imports`). Type- and method-level checks (naming,
docs, `too-many-arguments`, `large-function`, `missing-safety-doc`) have no per-line anchor —
exclude those with the whole-rule approach above.

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
| `cb.Methods` | All functions and methods (free functions and `impl` methods) |
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

### Syntax-error reporting

`rust.parse()` uses a real lexer + recursive-descent parser (not a line scanner). When a `.rs`
file contains a syntax error — an unterminated string/raw-string, an unterminated (possibly
nested) block comment, a malformed `fn`/`struct` header, or unbalanced `()`/`[]`/`{}` — cop
surfaces it as a **warning** of the form `path(line,col): error: message` and still analyzes the
rest of that file and every other file. Malformed sources are reported, never silently skipped.

---

## Tips

- Use `cop verify checks.cop` to check your rule for syntax/type errors before running
- Run the built-in `cop run rust-checks -t .` alongside your own rules
- Enforce crate dependency rules with the `code-layering` package (see section 7)
- Use `-t path/` to target a specific subdirectory
- Combine with other providers: `import rust` + `import python` to analyze polyglot projects
- Run `cop help code` to see all available predicates and types
