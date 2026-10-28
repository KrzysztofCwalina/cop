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

### Rust syntax and semantic analysis

Cop supports a **pinned rust-analyzer frontend** (`ra_ap_*` 0.0.358), bundled with
standard-library sources in the self-contained Cop executable. It does not depend on
the optional `rust-analyzer` rustup component. On first Rust source demand, Cop extracts
the helper/sources/notices to a content-addressed user cache, validates their integrity,
and reuses them. It does not write into the analyzed project. Adjacent helper/bundle
discovery also supports development builds; a source checkout does not build it automatically.

The maintained AST supplies declarations, signatures, imports, generic parameters,
source spans, decoded literal arguments, and nested call/control relationships.
Requesting `Statement.ReceiverType`, `Statement.ValueType`, or
`Statement.ReferencedCallable` additionally runs rust-analyzer's name resolution and
type inference. This resolves module/import aliases, inferred factory/variable receivers,
and static trait/impl method selections across local files. `ReferencedCallable` identifies
the selected declaration, **not a promise about runtime dynamic dispatch**. Its signature
preserves declaration generics; inferred result and receiver types are separate facts.
Unknown required parameter types leave the whole callable reference null, rather than
silently dropping parameters.

The frontend reads Cargo manifests and lockfiles directly. It supports path dependencies,
renamed dependencies, workspace inheritance, default features, target dependencies using
`rustc --print cfg`, and pinned registry/Git dependency sources already in Cargo's local
cache. Ambiguous source identities are errors, not guessed resolutions. Each semantic demand
snapshots source files and crate/features context; edits rebuild the execution-scoped
database. Structural FIRST/TAKE queries do not load a semantic project. Rich-mode syntax
disk caching is currently disabled so dependency-bound callbacks cannot be lost or stale.

**Source compatibility is explicit.** Matching rust-src under the installed compiler's
sysroot is preferred; otherwise the shipped sources require an exact compiler release
and commit match. The current source pin is Rust 1.99.1, commit
`58cf8f9c35b355199727627d07ae34a4e2846d67`. A mismatch warns and leaves unavailable
standard-library bindings unknown rather than substituting another version.
The source-only sysroot graph supplies core/alloc/std/prelude inference, including
generic standard-library receivers. External sysroot implementation dependencies and
platform/generated internals can still be unresolved without their build context.

**Partial context is explicit.**
Custom Cargo target/rustflags configurations are not assumed to be host defaults; semantic
binding is withheld with a warning. Build-script cfg/environment, generated source, and
procedural macros are unavailable. This is not full `cargo check` equivalence.

If the helper is not deployed, Cop prints a **yellow fallback warning** and uses its
built-in structural analyzer; this is not a claim that `rustc` is absent. A configured
helper that fails, incompatible protocols, and source parse failures are errors rather
than silent fallback. Warnings are plain text when redirected or with `--no-color`.
Neither backend runs Cargo, analyzed applications, build scripts, procedural macros,
compiler plugins, or generators; runtime analysis never downloads dependencies/components.

### Building the frontend for distribution

Release builders need a Rust compiler/Cargo and the target platform's linker/toolchain.
The lockfile pins every dependency. From the repository root, for example:

```powershell
providers\rust-provider\native\build.ps1 -Runtime win-x64 -OutputDirectory artifacts\rust-win-x64
```

Prepare the pinned official source archive separately in the same payload directory:

```powershell
providers\rust-provider\native\prepare-stdlib.ps1 -ArchivePath rust-src-1.99.1.tar.xz -OutputDirectory artifacts\rust-win-x64
```

`native/stdlib.json` records its immutable URL, SHA256, release, and compiler commit;
preparation verifies both archive hash and component identity. Downloading this build
input happens in release CI, never during analysis. `-Offline` requires build dependencies
already restored. Supported release IDs are `win-x64`,
`win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`; cross targets must be
provisioned in release CI, not installed on users' machines. Windows builds statically
link the C runtime. Linux GNU builds must use the release's minimum-supported glibc
baseline. Package the helper, **`rust-frontend-licenses/`, and `rust-stdlib/`** together;
the build script collects every locked dependency's license metadata and license/notice texts,
including pinned upstream notices omitted from published crates. Missing attribution fails
the build. The payload ZIP is embedded as `Cop.RustFrontend.Bundle.zip` by release publishing.
rust-analyzer is MIT OR Apache-2.0; transitive dependencies require their own notices.
The official source component's licenses, copyright inventory, and nested notices are
preserved alongside its source.

---

## 2. Set Up Agent Context

Run `cop init` once, in your **repository root** (not in `src/` or any other subfolder):

```bash
cop init
```

This generates instruction files (`.github/copilot-instructions.md`, `AGENTS.md`) that teach
**GitHub Copilot** how to write and run cop rules. Commit them to your repo.

<sub>Using Claude Code? Run `cop init --claude` to generate Claude Code instruction files (`.claude/commands/cop.md`) instead.</sub>

---

## 3. Create Rules with Your Agent

This is the primary way to use cop. As you build, you (or your coding agent) will notice
patterns you want to ban going forward — a `panic!`, an `unwrap()`, a missing `///` doc
comment. Instead of leaving a code-review comment that gets forgotten, ask your agent to
capture the problem as a cop rule. Because `cop init` taught the agent how cop works, it
writes the rule into your `cop-checks/` folder, runs it, and fixes the violations — just like
a compiler error.

Phrase the request as a cop check, so the agent reaches for a permanent rule instead of a
one-off edit:

> "Add a cop check that flags `panic!`, `unwrap()`, and `expect()` in library code — propagate errors with `?`"

> "Add a cop check that bans `println!` and `dbg!` — use the `log` or `tracing` crate"

> "Create a cop rule that every public type has a `///` doc comment"

It works as a tight feedback loop: the moment your agent writes code in a shape you don't
like, ask it to add a cop check — `cop` then catches that pattern on every future run, before
it reaches code review. The next sections show what such a rule looks like and how to run it
yourself.

---

## 4. Write and Run a Rule by Hand

You don't need an agent — you can author `.cop` files directly. cop analyzes the `.rs` files
you already have; a typical Rust project looks like this:

```
src/
  main.rs
  user.rs
Cargo.toml
```

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
predicate isPanicCall(Statement) => Statement.Kind == throw && Statement.MemberName == 'panic!'

let undocumented = cb.Types:isUndocumented
    :toWarning('Public type {item.Name} is missing documentation')

let panics = cb.Statements:isPanicCall
    :toWarning('Avoid panic! at line {item.Line} — prefer returning Result')

command MAIN = CHECK(undocumented + panics)
```

Verify it, then run it from your project root. By default cop analyzes the current directory;
`-t <path>` narrows analysis to a subfolder (here `src/`):

```bash
cop verify checks.cop      # catch syntax/type errors first
cop checks.cop -t src/
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

## 5. Use the Built-In Rust Checks

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
src/main.rs(11): warning: User has public methods without doc comments [undocumented-methods]
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

## 6. Enforce Crate Layering

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

## 7. Explore Further

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

# Declared types own same-file impl methods; unresolved external impls retain their methods.
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
| `cb.Types` | Structs, enums, traits, aliases, impl views, and synthetic free-function containers |
| `cb.Statements` | Nested calls, macro invocations, declarations, returns, if/else, loops, match, await |
| `cb.Calls` | Call statements (method/function/ordinary macro calls); panic-like macros use `throw` in `cb.Statements` |
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
| `type` alias | Alias |

Impl targets and trait paths preserve their full spelling, including generic arguments.
When an impl's module-qualified target identifies exactly one declared struct/enum in the
same file, its methods are attached to that declaration. The impl view remains available
for trait/unsafe/negative-impl facts, without duplicating those methods in `cb.Methods`.
Unresolved or external impls retain their methods on the impl view; cross-file ownership is
not guessed. An associated function named `new` is an ordinary method, not a constructor.

Tuple struct fields use numeric names (`0`, `1`, ...). Enum payloads are exposed as nested
struct-shaped types named after their variants. Parameters preserve destructuring patterns
and their real source lines. Type references preserve source spelling and expose parsed
path namespaces and generic arguments; declarations expose type/lifetime/const parameters.
Nested calls have `CallTarget` and `ArgumentExpressions`, with decoded Rust string literals
(`StringValue`). Byte strings, C strings, and character literals are not misreported as
Rust strings. Calls, control statements, and their containing methods have linked
`Parent`/`Children`/`Method` relationships and source locations. Macro names always retain
`!`, including `panic!`, `todo!`, `unimplemented!`, and `unreachable!`.

### Analysis capabilities and safety

The provider uses a built-in token-based structural parser. **It is not a full Rust compiler
front end or semantic analyzer.** Stable `rustc` does not expose a supported portable
parse-only AST/semantic export; nightly flags and rustup component installation are not
used. A `rust-analyzer` rustup launcher on PATH does not prove that the component is usable.
Cop reports a yellow syntax-only/degraded-analysis warning rather than silently claiming
toolchain-backed parity, whether or not a Rust toolchain is installed.

No Cargo command, build script, procedural macro, or analyzed target code is executed.
There are no downloads or setup steps. Macro token trees are unexpanded: visible nested
call syntax can be inspected, but macro expansion, name/type resolution, trait solving,
and conditional-compilation evaluation are not known. Unsupported expressions have
`CodeExpression.Kind == 'other'`, not fabricated semantic facts. Deep inputs are bounded
and diagnosed instead of hanging or overflowing the stack.

Cargo summaries are read directly from manifests. Both quoting styles, quoted keys,
dependency subtables, target/dev/build dependencies, renamed crates, and nearest-workspace
dependency inheritance are supported. `References` contains production dependencies;
`Packages` includes dev/build dependencies as well. Manifest read/format errors and unresolved
workspace inheritance are diagnosed rather than silently discarded.

### Syntax-error reporting

`rust.parse()` uses a real lexer + recursive-descent parser (not a line scanner). When a `.rs`
file contains a recognized syntax error — an unterminated string/raw-string, an unterminated (possibly
nested) block comment, a malformed `fn`/`struct` header, or unbalanced `()`/`[]`/`{}` — cop
surfaces it as a **warning** of the form `path(line,col): error: message` and still analyzes the
rest of that file and every other file. Mismatched balanced delimiters are also diagnosed.
These are structural diagnostics, not complete Rust grammar or compiler validation.

---

## Tips

- Use `cop verify checks.cop` to check your rule for syntax/type errors before running
- Run the built-in `cop run rust-checks -t .` alongside your own rules
- Enforce crate dependency rules with the `code-layering` package (see section 6)
- Use `-t path/` to target a specific subdirectory
- Combine with other providers: `import rust` + `import python` to analyze polyglot projects
- Run `cop help code` to see all available predicates and types
