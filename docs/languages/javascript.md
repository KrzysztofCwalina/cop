# JavaScript / TypeScript Walkthrough

This guide walks you through analyzing a JavaScript or TypeScript project with cop — from setup to writing and running custom rules.

---

## 1. Install Cop

Download the latest release for your platform from [GitHub Releases](https://github.com/KrzysztofCwalina/cop/releases) and add it to your PATH.

Verify the installation:

```bash
cop --version
```

---

## 2. Target a JavaScript/TypeScript Project

Navigate to any directory containing `.js` or `.ts` files. Cop scans all JavaScript and TypeScript files in the target directory tree.

Example project structure:

```
src/
  models/
    user.ts
    order.ts
  services/
    api.ts
package.json
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
import javascript
import code
import code-analysis

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

---

## 5. Run the Rule

From your project root:

```bash
cop checks.cop -t src/
```

Example output:

```
src/models/order.ts: warning: Class Order is missing JSDoc documentation
src/services/api.ts: warning: Remove console.log at line 23 — use a logger
src/services/api.ts: warning: Remove console.log at line 47 — use a logger

3 violation(s) found.
```

---

## 6. Use Built-In Checks

Cop ships with comprehensive JavaScript/TypeScript check packages:

```bash
cop run javascript-checks                  # all JS/TS conventions
cop run javascript-checks -c no-console    # just the "no console" check
cop run javascript-library-checks          # library API design rules
cop run javascript-library-azure-checks    # Azure SDK conventions
```

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
import code-analysis

let cb = javascript.parse()

predicate hasNoCatch(Method) => Method.Statements:isErrorHandler.count() == 0

let violations = cb.Types.Methods:isAsync:hasNoCatch
    :toInfo('Async method {item.Name} has no try/catch')

command MAIN = CHECK(violations)
```

### Check for use of `var` (prefer const/let)

```cop
import javascript
import code-analysis

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

---

## Tips

- Use `cop verify checks.cop` to check your rule for errors before running
- Use `-t path/` to limit analysis to a specific directory
- Run `cop help javascript-checks` to see all built-in JS/TS checks
- Run `cop help code` to see available types and predicates
- Combine with other providers: `import javascript` + `import python` for full-stack analysis
