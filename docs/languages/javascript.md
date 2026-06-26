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

Just ask:

> "Add a cop rule that flags `console.log` — we use a logger"

> "Ban `var` — use `const` or `let`"

> "Create a cop rule that every exported class has a JSDoc comment"

> "Add a cop rule that forbids `debugger` statements"

### The self-check loop

When your agent produces code in a shape you don't like, turn that feedback into a permanent rule:

1. The agent writes code with a pattern you dislike (e.g. it leaves a `console.log` behind).
2. You say: **"Add a self-check that flags `console.log` — we use our logger here."**
3. The agent adds a focused check to your `cop-checks/` folder.
4. From now on, `cop` catches that pattern before it reaches code review.

The next sections show what such a rule looks like and how to run it yourself.

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
predicate isConsoleLog(Statement) => Statement.TypeName == 'console' && Statement.MemberName == 'log'

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
cop run javascript-checks -c no-console    # just the "no console" check
cop run javascript-library-checks          # library API design rules
cop run javascript-library-azure-checks    # Azure SDK conventions
```

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
| `cb.Types` | All classes (ES6 class declarations) |
| `cb.Statements` | Function calls, new expressions, throw, catch |
| `cb.Files` | Source files with import/require info |
| `cb.Lines` | Every line of code (with kind: code/comment/blank) |
| `cb.Projects` | package.json projects with dependencies |

### JavaScript-Specific Features

- **TypeScript support**: Both `.js` and `.ts` files are parsed
- **ES6 classes**: `class`, `extends`, decorators, static methods
- **Import tracking**: Both `import ... from` and `require()` are captured
- **Async/await**: `async` functions are flagged with the `Async` modifier
- **JSDoc detection**: `/** ... */` comments preceding declarations are detected
- **Project discovery**: Parses `package.json` for dependencies and devDependencies

### Syntax-error reporting

`javascript.parse()` uses a real lexer + recursive-descent parser (not a line scanner). When a
`.js`/`.ts` file contains a syntax error — an unterminated string/template/regex, an unterminated
comment, or unbalanced `()`/`[]`/`{}` — cop surfaces it as a **warning** of the form
`path(line,col): error: message` and still analyzes the rest of that file and every other file.
Malformed sources are reported, never silently skipped.

---

## Tips

- Use `cop verify checks.cop` to check your rule for errors before running
- Use `-t path/` to limit analysis to a specific directory
- Run `cop help javascript-checks` to see all built-in JS/TS checks
- Run `cop help code` to see available types and predicates
- Combine with other providers: `import javascript` + `import python` for full-stack analysis
