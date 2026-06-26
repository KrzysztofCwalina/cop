# Java Walkthrough

This guide walks you through analyzing a Java project with cop. The main workflow is
**agent-driven**: as you build, you ask your coding agent to turn problems you notice into
permanent, enforceable cop rules. Later sections cover writing rules by hand, running the
built-in Java checks, and enforcing module layering.

---

## 1. Install Cop

Download the latest release for your platform from [GitHub Releases](https://github.com/KrzysztofCwalina/cop/releases) and add it to your PATH.

Verify the installation:

```bash
cop --version
```

---

## 2. Initialize a Java Project

Create (or navigate to) a Java project:

```bash
mkdir my-project && cd my-project
mkdir -p src/main/java/com/example
```

Add some code to `src/main/java/com/example/User.java`:

```java
package com.example;

import java.util.Objects;

/**
 * Represents a user in the system.
 */
public class User {
    private String name;
    private int age;
    public String email;

    public User(String name, int age, String email) {
        this.name = Objects.requireNonNull(name);
        this.age = age;
        this.email = email;
    }

    public String getName() {
        return name;
    }

    public boolean isAdult() {
        return age >= 18;
    }

    private void validate() {
        if (name.isEmpty()) {
            throw new IllegalArgumentException("Name cannot be empty");
        }
    }
}
```

Add another file `src/main/java/com/example/Status.java`:

```java
package com.example;

public enum Status {
    ACTIVE,
    INACTIVE,
    SUSPENDED
}
```

And an interface `src/main/java/com/example/Repository.java`:

```java
package com.example;

import java.util.List;

public interface Repository<T> {
    T findById(String id);
    List<T> findAll();
    void save(T entity);
    void delete(String id);
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
patterns you want to ban going forward — a raw `throw new RuntimeException`, a `System.out.println`,
a public type without Javadoc. Instead of leaving a code-review comment that gets forgotten,
ask your agent to capture the problem as a cop rule. Because `cop init` taught the agent how cop
works, it writes the rule into your `cop-checks/` folder, runs it, and fixes the violations —
just like a compiler error.

Just ask:

> "Flag raw `throw new RuntimeException(...)` — use a specific exception type"

> "Create a cop rule that every public type has a Javadoc comment"

> "Ban `System.out.println` — use a logger"

### The self-check loop

When your agent produces code in a shape you don't like, turn that feedback into a permanent rule:

1. The agent writes code with a pattern you dislike (e.g. it leaves a `System.out.println` behind).
2. You say: **"Add a self-check that flags `System.out.println` — we use a logger here."**
3. The agent adds a focused check to your `cop-checks/` folder.
4. From now on, `cop` catches that pattern before it reaches code review.

The next sections show what such a rule looks like and how to run it yourself.

---

## 5. Write and Run a Rule by Hand

You don't need an agent — you can author `.cop` files directly. Create a file called
`checks.cop` in your project root:

```cop
import java
import code
import code

let cb = java.parse()

# Flag classes that have no Javadoc comment
predicate isUndocumented(Type) => Type.Documented == false && Type:isPublic

# Flag throw statements (prefer checked exceptions or Result patterns)
predicate isThrowStmt(Statement) => Statement.Kind == throw

let undocumented = cb.Types:isUndocumented
    :toWarning('Public type {item.Name} is missing Javadoc')

let throws = cb.Statements:isThrowStmt
    :toWarning('throw at line {item.Line} — consider a checked exception or validation')

command MAIN = CHECK(undocumented + throws)
```

This rule does two things:
1. **Finds public types without Javadoc** (`/** ... */` above the declaration)
2. **Finds throw statements** — which might indicate missing validation

Verify it, then run it from your project root. cop analyzes the current directory by default;
`-t <path>` points it at another folder:

```bash
cop verify checks.cop      # catch syntax/type errors first
cop checks.cop -t .
```

Example output:

```
src/main/java/com/example/Status.java: warning: Public type Status is missing Javadoc
src/main/java/com/example/User.java: warning: throw at line 32 — consider a checked exception or validation

2 violation(s) found.
```

Exit code is `0` when clean and `1` when violations are found — suitable for CI. To organize
many rules, put one check per file in a `cop-checks/` folder with a `main.cop` entry point and
run `cop cop-checks/main.cop -t .` (this is exactly what your agent does for you).

---

## 6. Use Built-In Checks

Cop ships a built-in Java check package — no `.cop` files needed:

```bash
cop run java-checks                        # all Java conventions
cop run java-checks -t src/                # analyze a specific directory
```

Run `cop help java-checks` to see every check the package provides.

### Excluding checks and violations

You won't want every rule on every project. There are two ways to opt out.

Each violation in the output ends with the **name of the check** that produced it, in brackets — e.g.
`Main.java(8): error: Avoid System.exit() — throw an exception or return an error instead [system-exit]`.
That bracketed name is exactly the identifier you subtract.

**Exclude a whole rule** — take that bracketed name and subtract one or more checks from the package in a small `.cop`
file. The `-` operator removes those violations; everything else still runs:

```cop
import java-checks
import code

# run every java-checks rule except console-output and print-stack-trace
let my-checks = java-checks - console-output - print-stack-trace

command MAIN = CHECK(my-checks)
```

```bash
cop my-checks.cop -t .
```

You can also compose just the checks you want with `+` (`console-output`,
`print-stack-trace`, `system-exit`):

```cop
import java-checks
import code

# only flag System.exit()
let my-checks = system-exit

command MAIN = CHECK(my-checks)
```

**Exclude a single violation** — add a `// cop-ignore: <check>` comment on the line directly
above the one to exempt. Only that line is silenced; the rule keeps firing everywhere else:

```java
void save() {
    // cop-ignore: console-output
    System.out.println("debug");  // exempted — NOT flagged
    System.out.println("more");   // still flagged
}
```

---

## 7. Enforce Module Layering

Cop discovers your Maven/Gradle modules and their dependencies (from each `pom.xml` or
`build.gradle`). The language-agnostic **`code-layering`** package lets you enforce
architectural rules across modules — for example, that foundation modules must not
depend on higher-level service modules.

Create `layering.cop`:

```cop
import java
import code
import code

let cb = codebase(java.parse())

# A module's Name is its Maven artifactId; its References are 'groupId:artifactId' strings.
let foundation-modules = ['core']
let service-modules = ['com.example:service' 'com.example:identity']

predicate isFoundationModule(Project) => Project.Name:in(foundation-modules)
predicate isServiceModuleReference(string) => string:in(service-modules)
predicate dependsOnService(Project) => Project.References:any(isServiceModuleReference)

let violations = cb.Projects:isFoundationModule:dependsOnService
    :toError('Foundation module {item.Name} must not depend on a service module')

command MAIN = CHECK(violations)
```

Run it against your project root:

```bash
cop layering.cop -t .
```

The check exits non-zero (and prints each offending module) when a foundation module
references a service module, so you can wire it into CI.

> Tip: `cb.Projects` exposes each module's `Name` and `References` (its `groupId:artifactId` dependencies).
> Use `Project.References:any(predicate)` to test whether a module depends on a set of modules.

---

## 8. Explore Further

### List all types in your project

```cop
import java

let types = java.parse().Types

foreach types => '{item.Name} ({item.Kind})'
```

### Count methods per class

```cop
import java
import code

let types = java.parse().Types:isClass

foreach types => '{item.Name}: {item.Methods.Count} methods'
```

### Find classes with too many methods

```cop
import java
import code

predicate hasTooManyMethods(Type) => Type.Methods.Count > 10

let violations = java.parse().Types:hasTooManyMethods
    :toWarning('{item.Name} has {item.Methods.Count} methods — consider splitting')

command MAIN = CHECK(violations)
```

---

## Available Collections

The `java.parse()` function returns a `Codebase` with these collections:

| Collection | Description |
|------------|-------------|
| `cb.Types` | All classes, interfaces, enums, and records |
| `cb.Statements` | Method calls, object creation, throw, catch |
| `cb.Files` | Source files with package and import info |
| `cb.Lines` | Every line of code (with kind: code/comment/blank) |
| `cb.Projects` | Maven/Gradle modules (`pom.xml` / `build.gradle`) with their dependencies |

### Syntax-error reporting

`java.parse()` uses a real lexer + recursive-descent parser (not a line scanner). When a `.java`
file contains a syntax error — an unterminated string/text-block, an unterminated comment, or
unbalanced `{}`/`()` — cop surfaces it as a **warning** of the form
`path(line,col): error: message` and still analyzes the rest of that file and every other file.
Malformed sources are reported, never silently skipped.

---

## Next Steps

- Use `cop verify checks.cop` to check your rule for errors before running
- Run `cop help java-checks` to see all built-in Java checks
- Run `cop help java` to see all available types and functions
- Run `cop help code` for the full code analysis API
- Combine with other providers: `import csharp` for polyglot analysis
- See the [Language Reference](../language-reference.md) for full Cop syntax
