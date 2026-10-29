# JavaScript / TypeScript Walkthrough

This guide walks you through analyzing a JavaScript or TypeScript project with cop. The main
workflow is **agent-driven**: as you build, you ask your coding agent to turn problems you
notice into permanent, enforceable cop rules. Later sections cover writing rules by hand,
running the built-in JS/TS checks, and enforcing package layering.

---

## 1. Install Cop

Download the latest release for your platform from [GitHub Releases](https://github.com/KrzysztofCwalina/cop/releases) and add it to your PATH.

Verify the installation:

```bash
cop --version
```

---

### JavaScript / TypeScript toolchain and fallback

The richer parser requires **Node.js and an installed TypeScript compiler**, including
when analyzing JavaScript. Cop discovers Node automatically and looks for TypeScript
in the project's ancestor `node_modules` directories or through an installed `tsc`
launcher. Node alone is not sufficient. An existing project-local TypeScript installation
works without a global installation or Cop-specific configuration.

Cop uses the TypeScript compiler API for `.js`, `.jsx`, `.ts`, and `.tsx` syntax and
available symbol/type facts. It reads TypeScript configuration and installed declarations;
it does not run the application, npm scripts, compiler plugins, or dependency installation.
Dynamic or unresolved symbols remain unknown rather than being presented as resolved types.

A bounded Node helper and compiler state are reused within one Cop execution as files are
requested. Native semantic models are deliberately not cached across executions: changes
to imported declarations, libraries, or configuration must not leave stale type facts.
This means a later native run still has compiler startup work.

When Node or a usable TypeScript compiler cannot be found, Cop emits a **yellow warning**
and uses its simpler built-in syntax parser. This does not provide equivalent compiler
semantics or complete coverage of advanced TypeScript/JSX syntax. Warnings remain visible
on cached fallback runs, and are plain text when redirected or with `--no-color`.
Actual source errors, timeouts, and helper failures are errors, not a reason to silently
switch to a successful fallback. Cop never runs npm or downloads Node or TypeScript.

For richer analysis, make Node available to Cop and use an already installed local or
global TypeScript compiler, then rerun the check. If fallback is reported unexpectedly,
check both installations rather than only `node --version`.

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
patterns you want to ban going forward — a stray `console.log`, a `var`, a missing JSDoc
comment. Instead of leaving a code-review comment that gets forgotten, ask your agent to
capture the problem as a cop rule. Because `cop init` taught the agent how cop works, it
writes the rule into your `cop-checks/` folder, runs it, and fixes the violations — just like
a compiler error.

Phrase the request as a cop check, so the agent reaches for a permanent rule instead of a
one-off edit:

> "Add a cop rule that flags `console.log` — we use a logger"

> "Add a cop check that bans `var` — use `const` or `let`"

> "Create a cop rule that every exported class has a JSDoc comment"

> "Add a cop rule that forbids `debugger` statements"

It works as a tight feedback loop: the moment your agent writes code in a shape you don't
like, ask it to add a cop check — `cop` then catches that pattern on every future run, before
it reaches code review. The next sections show what such a rule looks like and how to run it
yourself.

---

## 4. Write and Run a Rule by Hand

You don't need an agent — you can author `.cop` files directly. cop analyzes the `.js` and
`.ts` files you already have; a typical project looks like this:

```
src/
  models/
    user.ts
    order.ts
  services/
    api.ts
package.json
```

Create a file called `checks.cop` in your project root:

```cop
import javascript
import code
import code

let cb = javascript.parse()

# Flag classes without JSDoc comments
predicate isUndocumented(Type) => Type.Documented == false && Type:isPublic

# Flag console.log calls (use a proper logger)
predicate isConsoleLog(Statement) => Statement.MemberName == 'log'
    && Statement.CallTarget != nic && Statement.CallTarget.Kind == memberAccess
    && Statement.CallTarget.Target != nic && Statement.CallTarget.Target.Kind == identifier
    && Statement.CallTarget.Target.Name == 'console'

let undocumented = cb.Types:isUndocumented
    :toWarning('Class {item.Name} is missing JSDoc documentation')

let consoleLogs = cb.Statements:isConsoleLog
    :toWarning('Remove console.log at line {item.Line} — use a logger')

command MAIN = CHECK(undocumented + consoleLogs)
```

Verify it, then run it from your project root. By default cop analyzes the current directory;
`-t <path>` narrows analysis to a subfolder (here `src/`):

```bash
cop verify checks.cop      # catch syntax/type errors first
cop checks.cop -t src/
```

Example output:

```
src/models/order.ts: warning: Class Order is missing JSDoc documentation
src/services/api.ts: warning: Remove console.log at line 23 — use a logger
src/services/api.ts: warning: Remove console.log at line 47 — use a logger

3 violation(s) found.
```

Exit code is `0` when clean and `1` when violations are found — suitable for CI. To organize
many rules, put one check per file in a `cop-checks/` folder with a `main.cop` entry point and
run `cop cop-checks/main.cop -t .` (this is exactly what your agent does for you).

---

## 5. Use Built-In Checks

Cop ships with comprehensive JavaScript/TypeScript check packages — no `.cop` files needed:

```bash
cop run javascript-checks                  # all JS/TS conventions
cop run javascript-library-checks          # library API design rules
cop run javascript-library-azure-checks    # Azure SDK conventions
```

### Excluding checks and violations

You won't want every rule on every project. There are two ways to opt out.

Each violation in the output ends with the **name of the check** that produced it, in brackets — e.g.
`app.ts(5): error: Do not use eval() — it is a security risk [eval-calls]`. That bracketed name is
exactly the identifier you subtract.

**Exclude a whole rule** — take that bracketed name and subtract one or more checks (or whole groups) from the package in
a small `.cop` file. The `-` operator removes those violations; everything else still runs:

```cop
import javascript-checks
import code

# run every javascript-checks rule except console-calls and var-declarations
let my-checks = javascript-checks - console-calls - var-declarations

command MAIN = CHECK(my-checks)
```

```bash
cop my-checks.cop -t .
```

Checks are also grouped, so you can compose just the groups you want with `+`
(`javascript-correctness-checks`, `typescript-checks`):

```cop
import javascript-checks
import code

# only the core correctness checks
let my-checks = javascript-correctness-checks

command MAIN = CHECK(my-checks)
```

**Exclude a single violation** — add a `// cop-ignore: <check>` comment on the line directly
above the one to exempt. Only that line is silenced; the rule keeps firing everywhere else:

```javascript
function save() {
    // cop-ignore: console-calls
    console.log("debug");  // exempted — NOT flagged
    console.log("more");   // still flagged
}
```

`cop-ignore` works for all of these statement-level checks (console, alert, eval, debugger,
var, swallowed exceptions, await-thenable).

---

## 6. Enforce Package Layering

Cop discovers your JavaScript/TypeScript packages and their dependencies (from each
`package.json`). The language-agnostic **`code-layering`** package lets you enforce
architectural rules across packages — for example, that foundation packages must not
depend on higher-level feature or app packages.

Create `layering.cop`:

```cop
import javascript
import code
import code

let cb = codebase(javascript.parse())

# Foundation packages must not depend on feature or app packages.
let foundation-packages = ['@example/core']
let feature-packages = ['@example/app' '@example/identity']

predicate isFoundationPackage(Project) => Project.Name:in(foundation-packages)
predicate isFeaturePackageName(string) => string:in(feature-packages)
predicate dependsOnFeature(Project) => Project.References:any(isFeaturePackageName)

let violations = cb.Projects:isFoundationPackage:dependsOnFeature
    :toError('Foundation package {item.Name} must not depend on a feature package')

command MAIN = CHECK(violations)
```

Run it against your workspace root:

```bash
cop layering.cop -t .
```

The check exits non-zero (and prints each offending package) when a foundation package
references a feature package, so you can wire it into CI.

> Tip: `cb.Projects` exposes each package's `Name` and `References` (its dependency package names).
> Use `Project.References:any(predicate)` to test whether a package depends on a set of packages.

---

## 7. Explore Further

### List all classes and their methods

```cop
import javascript
import code

let cb = javascript.parse()
command MAIN = foreach cb.Types => '{item.Name} ({item.Kind}) - {item.Methods.count()} methods'
```

### Find async functions without error handling

<!-- cop norun: `cb.Types.Methods:<methodPredicate>` fatals at runtime (expects Method, got collection) while `cop verify` passes — tracked in #50 -->
```cop norun
import javascript
import code
import code

let cb = javascript.parse()

predicate hasNoCatch(Method) => Method.Statements:isErrorHandler.count() == 0

let violations = cb.Types.Methods:isAsync:hasNoCatch
    :toInfo('Async method {item.Name} has no try/catch')

command MAIN = CHECK(violations)
```

### Check for use of `var` (prefer const/let)

```cop
import javascript
import code

let cb = javascript.parse()

predicate isVar(Line) => Line.Text:matches('\\bvar\\b')

let violations = cb.Lines:isVar
    :toWarning('Use const or let instead of var (line {item.Number})')

command MAIN = CHECK(violations)
```

---

## Available Collections

The `javascript.parse()` function returns a `Codebase` with these collections:

| Collection | Description |
|------------|-------------|
| `cb.Types` | Classes, interfaces, enums, and type aliases |
| `cb.Methods` | Class methods and module/nested functions, including arrows |
| `cb.Statements` | Nested calls, declarations, control flow, return, throw, catch, await |
| `cb.Files` | Source files with parsed import names and aliases |
| `cb.Lines` | Every line of code (with kind: code/comment/blank) |
| `cb.Projects` | package.json packages and compiler-defined tsconfig/jsconfig projects |

### JavaScript-Specific Features

- **Source formats**: `.js`, `.jsx`, `.ts`, `.tsx`, `.mjs`, `.cjs`, `.mts`, and `.cts`
- **Declarations**: Visibility, fields, accessors, interface members, enum names, aliases,
  generic parameter names, structured type annotations, optional/rest/destructured parameters
- **Import tracking**: Default, named, and namespace aliases in `File.Imports`;
  `File.Usings` also includes re-exports, dynamic literal imports, and `require()` modules
- **Async/await**: `async` functions are flagged with the `Async` modifier
- **JSDoc detection**: `/** ... */` comments preceding declarations are detected
- **Parsed expressions**: `CallTarget`, `ArgumentExpressions`, and `ValueExpression` contain
  syntax nodes, with decoded string literal values. Unsupported forms remain `other`.
- **Ownership**: Calls retain `Method`, `Parent`, `Children`, source lines, and 1-based
  columns/exclusive end positions. Module functions are not hidden in a synthetic class.
- **Project discovery**: All four dependency groups (runtime, development, peer, optional);
  workspace patterns and exclusions; compiler-defined `files`/`include`/`exclude`/`extends`,
  references, and path aliases for `tsconfig*.json`/`jsconfig*.json`

### Installed compiler and fallback

When Node.js and TypeScript are already installed, cop uses the TypeScript compiler API.
It discovers ancestor `node_modules/typescript` installations and global `tsc` launchers,
including Unix symlinks; it does not assume Node can resolve globally installed modules.
No npm install, downloads, project-code execution, plugin loading, or configuration changes
are performed. The helper is reused within one execution, with bounded time/output and
cleanup when execution ends. Deferred `First`/`Take` does not parse unrelated later files;
the compiler can read imports and library declarations required by the demanded file.
The bundled helper is stored by content hash under `~/.cop/helpers/javascript/`, outside
the analyzed project. Only its path and the installed compiler path are passed to Node,
avoiding platform command-line length limits; concurrent installs use atomic replacement.

If either tool is missing, or the installed compiler cannot supply the required API, cop
prints a **yellow warning** and uses its simpler in-process syntax parser. That path retains
basic classes, module functions/arrows, nested calls, decoded literal arguments, and method
ownership, but is not a complete TypeScript/JSX parser and does not infer types. An analysis
request that fails or times out is reported as an error, not silently retried as fallback.

`Statement.TypeName` is a compiler-resolved receiver type when known, **not receiver source
text**. For example, `client.send()` can resolve to `Client`; `dynamic.send()` remains
unknown. Match the parsed `CallTarget` to identify a receiver such as `console`.
Dynamic dispatch, unresolved modules, `any`, and `unknown` are not fabricated as resolved
types. Cop does not claim whole-program runtime behavior or TypeScript type-checker diagnostics.

`Statement.ReceiverType` describes the compiler-known receiver, and `ValueType` describes
the expression result (for example, the object returned by a factory). `ReferencedCallable`
identifies the compiler-selected declaration and overload, including its signature, parameter
and return types, declaring type, and source span. It is not a prediction of runtime virtual
dispatch. Unresolved or dynamic calls leave unavailable facts null; match `CallTarget` for
syntax regardless of whether binding succeeds. These facts reuse the existing checker and
worker, not a second compiler process.

Compiler-backed models are reused within an execution but deliberately not persisted across
runs: imported declarations, compiler libraries, and configuration can change independently
of a source file. Simpler syntax models retain persistent caching. This avoids stale semantic
results rather than presenting a misleading warm-cache speedup.

Package `References`/`Packages` summarize declared dependency names, not lockfile-resolved
versions or a complete runtime dependency graph. Project `Properties` names that scope,
retains each dependency group's version declarations, and exposes `WorkspaceRoot`,
`PathAliases`, and `BaseUrl` where available. Compiler projects have exact `SourceFiles`
membership; package manifests alone use directory-based membership.

### Syntax-error reporting

Compiler-backed analysis reports TypeScript syntax diagnostics. The fallback uses a lexer and
recursive-descent parser, detecting unterminated strings/comments and unbalanced delimiters.
When a source contains a syntax error, cop surfaces it as a **warning** of the form
`path(line,col): error: message` and still analyzes the rest of that file and every other file.
Malformed sources are reported, never silently skipped.

---

## Tips

- Use `cop verify checks.cop` to check your rule for errors before running
- Use `-t path/` to limit analysis to a specific directory
- Run `cop help javascript-checks` to see all built-in JS/TS checks
- Run `cop help code` to see available types and predicates
- Combine with other providers: `import javascript` + `import python` for full-stack analysis
