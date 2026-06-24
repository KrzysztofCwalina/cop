# Java Walkthrough

This guide walks you through installing cop, setting up a Java project for analysis, writing a simple rule, and running it.

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
import java
import code
import code-analysis

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

---

## 5. Run the Rule

From your project root:

```bash
cop checks.cop -t .
```

Example output:

```
src/main/java/com/example/Status.java: warning: Public type Status is missing Javadoc
src/main/java/com/example/User.java: warning: throw at line 32 — consider a checked exception or validation

2 violation(s) found.
```

---

## 6. Explore Further

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
import code-analysis

predicate hasTooManyMethods(Type) => Type.Methods.Count > 10

let violations = java.parse().Types:hasTooManyMethods
    :toWarning('{item.Name} has {item.Methods.Count} methods — consider splitting')

command MAIN = CHECK(violations)
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
import code-layering

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

## Available Collections

The `java.parse()` function returns a `Codebase` with these collections:

| Collection | Description |
|------------|-------------|
| `cb.Types` | All classes, interfaces, enums, and records |
| `cb.Statements` | Method calls, object creation, throw, catch |
| `cb.Files` | Source files with package and import info |
| `cb.Lines` | Every line of code (with kind: code/comment/blank) |
| `cb.Projects` | Maven/Gradle modules (`pom.xml` / `build.gradle`) with their dependencies |

---

## 8. Next Steps

- Run `cop help java` to see all available types and functions
- Run `cop help code` for the full code analysis API
- Combine with other providers: `import csharp` for polyglot analysis
- See the [Language Reference](../language-reference.md) for full Cop syntax
