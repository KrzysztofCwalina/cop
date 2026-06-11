# Packaging in Cop

This document describes how packaging works in cop: what a package is, how to create one, how packages are discovered and downloaded, and what happens when a `.cop` file imports a package.

---

## Definitions

**`.cop` file** — A text file written in the Cop language. It can define types, predicates, functions, checks, and UPPERCASE output functions.

**Package** — A directory on disk that contains `.cop` source files and a `cop.json` manifest. A directory is recognized as a package if it contains a `cop.json` file.

**Manifest** — A JSON file named `cop.json` at the root of a package directory. It contains metadata (name, version, description, dependencies, etc.).

**Group folder** — A directory that is *not* itself a package but contains other packages as subdirectories. For example, the `dotnet/` directory under `packages/` is a group folder containing packages like `csharp-checks/`, `csharp-library-checks/`, etc.

**Feed path** — A local directory that cop searches when looking for packages. Feed paths contain package directories (or group folders containing packages) as their children.

**GitHub feed** — A GitHub repository identified as `github.com/{owner}/{repo}`, from which cop can download packages remotely. The default GitHub feed is `github.com/KrzysztofCwalina/cop`.

**Package cache** — The directory `~/.cop/packages/` where cop stores packages downloaded from GitHub feeds. On Windows, `~` means `C:\Users\{username}`.

---

## Package Structure

A minimal package is a directory with a `src/` subdirectory containing at least one `.cop` file:

```
my-checks/
  src/
    my-checks.cop
```

A fully scaffolded package (created by `cop package new`) includes:

```
my-checks/
  cop.json            # package manifest (metadata)
  README.md           # documentation
  instructions/       # natural-language instructions
  skills/             # reusable skills
  checks/             # check definitions
  tests/              # test files
```

When a package is **imported**, cop reads all `.cop` files from the `types/` subdirectory (if it exists) or else the `src/` subdirectory. Only one of these is used — `types/` takes priority.

### Manifest Format

The manifest is a JSON file named `cop.json`. Example:

```json
{
  "name": "code-analysis",
  "version": "1.0.0",
  "title": "Code Analysis Package",
  "description": "Types and functions for structured code analysis results",
  "authors": "Krzysztof Cwalina",
  "tags": ["code", "analysis"],
  "dependencies": []
}
```

Fields:

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Package name. Lowercase letters, numbers, hyphens only. Must match the directory name. |
| `version` | Yes | Semantic version (`X.Y.Z`). |
| `title` | Yes | Human-readable title. |
| `description` | Yes | Short description (max 1000 chars). |
| `authors` | Yes | Author name(s). |
| `tags` | No | Array of string tags for search/discoverability. |
| `language` | No | Primary programming language (e.g., `csharp`, `python`, `rust`, `go`, `java`). |
| `provider` | No | Set to `clr` if the package contains a .NET data provider assembly. |
| `providerEntry` | No | Fully-qualified class name of the data provider (required when `provider` is `clr`). |
| `providerAssembly` | No | Filename of the provider DLL (e.g., `csharp-provider.dll`). Required when `lib/` contains multiple DLLs. |
| `dependencies` | No | List of fully-qualified package references (e.g., `github.com/org/repo/other-pkg: 1.0.0`). |

---

## Creating a Package

Run `cop package new <name>` to scaffold a new package:

```
cop package new my-checks
```

This creates `packages/my-checks/` under the current directory with the standard directory structure and a manifest template. Package names must start with a lowercase letter and contain only lowercase letters, numbers, and hyphens.

---

## Feeds

A feed is a source from which cop discovers and downloads packages. There are two kinds:

1. **Local feed paths** — directories on disk containing packages. These are discovered automatically (see "How Cop Finds Packages" below) or declared explicitly in `.cop` files.

2. **GitHub feeds** — GitHub repositories. Cop uses the GitHub API to list and download packages from these repos. The default GitHub feed (`github.com/KrzysztofCwalina/cop`) is always present.

### Feed Configuration

Feeds are configured in `~/.cop/feeds.json`. On a fresh machine, this file does not exist and cop uses only the default feed.

CLI commands for managing feeds:

- **`cop package feed list`** — show all configured feeds
- **`cop package feed add <url>`** — add a feed (e.g., `github.com/myorg/my-packages` or a local path)
- **`cop package feed remove <url>`** — remove a feed (cannot remove the default feed)

---

## Importing Packages

A `.cop` file imports a package by name:

```cop
import csharp
import code-analysis
import files

command main = foreach csharp.types()
    => '{item.Name}'
```

Import statements must appear at the top of the file, after any `feed` declarations and before any type definitions, predicates, or UPPERCASE functions.

When cop processes an import, it finds the package directory, parses all `.cop` files in it, and makes the package's definitions available. Imports are **transitive**: if `code-analysis` imports `code` and `files`, those packages are automatically resolved too.

---

## How Cop Finds Packages

When cop encounters `import xxx`, it needs to find a package directory named `xxx` on disk. It searches a list of **feed paths** in order — the first one that contains the package wins.

### Search Algorithm Within a Feed Path

Given a feed path and a package name, cop searches as follows:

1. Check for a direct child directory matching the name. If it exists and is a package, use it.
   Example: looking for `code` in `~/.cop/packages/` → checks `~/.cop/packages/code/`

2. If not found, recursively enter each child directory that is a **group folder** (i.e., not itself a package) and repeat the search.
   Example: looking for `csharp-checks` in `packages/` → enters group folder `packages/dotnet/` → finds `packages/dotnet/csharp-checks/`

---

## What Happens When You Run `cop foo.cop`

Here is the complete sequence, step by step, starting from a fresh machine where only `cop.exe` is installed.

### Starting State

- `cop.exe` is installed and on the PATH
- `~/.cop/` does not exist (no configuration, no cached packages)
- There are no `packages/` directories on disk
- The user has created `C:\myproject\foo.cop`:

```cop
import csharp
import code-analysis

command main = foreach csharp.types()
    => '{item.Name} has {item.Methods.Count} methods'
```

The user runs: `cop foo.cop`

---

### Step 1: Determine the Script Directory

Cop resolves `foo.cop` to its full path (`C:\myproject\foo.cop`) and sets the **script directory** to its parent: `C:\myproject\`.

All `.cop` files in the script directory and its subdirectories will be loaded later as user scripts.

---

### Step 2: Auto-Restore Missing Packages

Before executing anything, the CLI ensures all imported packages are available locally. This is called **auto-restore**.

#### Step 2a: Collect Import Names

Cop parses every `.cop` file in the script directory to extract `import` statements. In this example, it finds one import: `code-analysis`.

#### Step 2b: Build the List of Known Feed Paths

Cop assembles a list of local directories where packages might already exist:

1. If `~/.cop/packages/` exists, include it. (On a fresh machine, it does not exist.)
2. Starting from the current working directory, walk up the directory tree. At each level, if a `packages/` subdirectory exists, include it. (On a fresh machine, none exist.)
3. Starting from the script directory, walk up the directory tree the same way. (On a fresh machine, none exist.)

Result on a fresh machine: the list is **empty**.

#### Step 2c: Check Which Imports Are Missing

For each import name, cop searches every known feed path using the algorithm described in "How Cop Finds Packages." If the package is not found in any feed path, it is classified as **missing**.

On a fresh machine with no feed paths, every import is missing. In this example: `code-analysis` is missing.

#### Step 2d: Download Missing Packages from GitHub

Cop loads the list of GitHub feeds from `~/.cop/feeds.json`. If that file does not exist (as on a fresh machine), cop uses only the default feed: `github.com/KrzysztofCwalina/cop`.

For each missing package, cop tries each GitHub feed in order:

1. Construct a reference: `github.com/KrzysztofCwalina/cop/code-analysis`
2. Use the GitHub API to locate the package directory in the repository (searching `packages/code-analysis`, then group folders like `packages/dotnet/code-analysis`, etc.)
3. Download all files in the package directory
4. Save them to `~/.cop/packages/code-analysis/` (creating `~/.cop/` and `~/.cop/packages/` if they don't exist)

If the `GITHUB_TOKEN` environment variable is set, cop uses it for GitHub API authentication. This is needed for private repositories or to avoid rate limits.

If a package is not found in any GitHub feed, cop prints a warning and continues.

#### Step 2e: Resolve Transitive Imports

After downloading a package, cop parses its `.cop` files to discover its own imports. For example, `code-analysis` contains:

```cop
import code
import files
```

These newly discovered import names are added to a download queue. Cop repeats the download process for each, then checks *their* imports, and so on. This is a breadth-first traversal of the import graph. A package that has already been downloaded is never downloaded again.

After auto-restore on a fresh machine, `~/.cop/packages/` contains:

```
~/.cop/packages/
  code-analysis/        ← imported by foo.cop
    cop.json
    src/code-analysis.cop
  code/                 ← imported by code-analysis
    cop.json
    src/code.cop
  files/                ← imported by code-analysis
    cop.json
    src/files.cop
```

---

### Step 3: Parse User Scripts

The engine reads every `.cop` file in the script directory (and subdirectories) and parses each one into an in-memory representation. Each parsed file contains the file's import names, type definitions, predicates, functions, UPPERCASE output functions, and other declarations.

---

### Step 4: Build the Feed Path List for the Engine

The engine builds its own ordered list of feed paths. This list determines where it looks for packages when resolving import names:

1. **Walk up from the script directory.** Starting from `C:\myproject\`, at each level, if a `packages/` subdirectory exists, add it. (In this example, none exist.)

2. **`feed` declarations in `.cop` files.** A `.cop` file can declare additional feed paths:
   ```cop
   feed '../shared-packages'
   import xxx
   ```
   These paths are resolved relative to the `.cop` file's location. (In this example, there are none.)

3. **Package cache and CWD walk-up.** The CLI passes `~/.cop/packages/` (now it exists from Step 2) plus any `packages/` directories found walking up from the current working directory.

In this example, the only feed path is `~/.cop/packages/`.

The list is ordered. When multiple feed paths contain a package with the same name, the **first** feed path wins. This means local project packages (found by walking up from the script directory) take priority over cached packages.

---

### Step 5: Resolve Imports and Build the Type Registry

The engine processes all import names from the user's `.cop` files. This is import resolution.

#### Step 5a: Queue All Imports

All import names from all parsed user script files are collected into a queue. In this example: `["code-analysis"]`.

#### Step 5b: Process the Queue

The engine processes the queue breadth-first. For each import name:

1. **Skip if already resolved.** A set tracks which package names have been processed. This prevents infinite loops from circular imports and avoids loading the same package twice.

2. **Find the package directory.** Search each feed path in order using the algorithm from "How Cop Finds Packages." Use the first match.

3. **Locate and parse the `.cop` files.** Within the package directory, look for a `types/` subdirectory first. If it doesn't exist, look for `src/`. Parse every `.cop` file in that subdirectory.

4. **Register definitions.** The parsed definitions are added to a shared type registry:
   - All **type** definitions (e.g., `type Violation = { ... }`)
   - All **flags** definitions (e.g., `flags Modifier = Public | Private | ...`)
   - All **enum** definitions (e.g., `enum Severity = error | warning | info`)
   - All top-level collection bindings declared with `let`
   - All **predicates**, **functions**, and **let bindings** are kept in the parsed file for the interpreter

5. **Filter exported output functions.** For UPPERCASE `function` blocks (checks, `foreach` loops, named entry points), only those marked with the `export` keyword are kept. Non-exported output functions are private to the package and not visible to importers.

6. **Enqueue transitive imports.** The package's own import names are added to the queue. For example, when processing `code-analysis`, its imports `code` and `files` are enqueued.

The queue continues until empty. In this example, the resolution order is:

```
code-analysis  →  Severity enum, Violation type, toError/toWarning/toInfo functions
code           →  TypeKind/StatementKind enums, Type/Method/Statement types, Modifier flags
files          →  Folder/File types, Disk collection
```

#### Step 5c: Register User Definitions

After all imports are resolved, the engine registers the user's own type definitions, flags, enums, and collections. This happens last, so user definitions can reference types from imported packages.

---

### Step 6: Execute

With all types registered and all package definitions loaded, the engine executes the user's selected UPPERCASE functions. The interpreter has access to all definitions from both the user's files and all imported packages.

---

## What Is Visible After an Import

When `foo.cop` says `import xxx`, the following definitions from package `xxx` become available:

| Definition | Visible to importer? | Notes |
|------------|---------------------|-------|
| Types (`type Foo = { ... }`) | Yes | |
| Enums (`enum E = A \| B`) | Yes | |
| Flags (`flags F = X \| Y`) | Yes | |
| Predicates (`predicate p(T) => ...`) | Yes | |
| Functions (`function f(T, ...) => ...`) | Yes | |
| Let bindings (`let X = ...`) | Yes | |
| Collections (`let X = runtime::Y`) | Yes | |
| **UPPERCASE functions / checks** | **Only if marked `export`** | Non-exported output functions are private to the package |

Additionally, everything from `xxx`'s own imports is also available transitively. If `xxx` imports `yyy`, then `foo.cop` can use definitions from `yyy` without importing it explicitly.

---

## Searching for Packages

Run `cop package search <query>` to find packages in configured feeds:

```
cop package search csharp
```

This queries all configured GitHub feeds (via the GitHub API), lists package directories in each feed's `packages/` tree, and filters by name. Results include the package name, version, and title (from the manifest, if available).

---

## Restoring Packages Explicitly

While `cop` auto-restores missing imports, you can also restore packages explicitly:

```
cop package restore foo.cop
```

This parses the `.cop` file, reads its `feed` and `import` declarations, and downloads all imported packages (and their transitive dependencies) from the declared GitHub feed. Unlike auto-restore, `cop package restore` requires the `.cop` file to contain a `feed` declaration pointing to a GitHub repository:

```cop
feed 'github.com/myorg/my-packages'
import my-checks
```

The restored packages are placed under the `.cop/` directory in the project root (e.g., `.cop/packages/`, `.cop/checks/`, `.cop/analyzers/`), not in `~/.cop/packages/`.

---

## Publishing a Package

Publishing makes a package version available to others through its GitHub feed repository.

Run `cop package publish <name>`:

```
cop package publish my-checks
```

This command:

1. Locates the package under `packages/` in the current repository
2. Validates that the manifest exists and all required fields are populated (no `TODO` values)
3. Validates the version is in `X.Y.Z` format
4. Verifies required directories exist (`instructions/`, `skills/`, `checks/`, `tests/`)
5. Checks that the package does not depend on itself
6. Creates a **git tag** named `{packageName}/{version}` (e.g., `my-checks/1.0.0`)

After the tag is created locally, you must push it:

```
git push origin my-checks/1.0.0
```

Packages are not uploaded anywhere — they live in the git repository. The tag marks a specific version. When others restore or auto-restore the package, cop downloads the files directly from the repository via the GitHub API.

---

## Versioning

Package versions follow semantic versioning (`X.Y.Z`). Versions are tracked through **git tags**:

- Tag format: `{packageName}/{version}` (e.g., `code-analysis/1.0.0`)
- When restoring with a specific version, cop looks for the corresponding git tag
- When no version is specified, cop fetches the latest version by listing all tags matching `{packageName}/*` and selecting the highest semver

Auto-restore (during `cop`) does not use versions — it downloads the latest files from the default branch.

---

## Feed Path Priority (Summary)

When cop needs to find a package named `xxx`, it searches these locations in order. The first match wins.

1. `packages/` directories found by walking up from the **script directory**
2. Paths declared via `feed '...'` in the `.cop` files
3. `~/.cop/packages/` (the package cache, populated by auto-restore)
4. `packages/` directories found by walking up from the **current working directory**

This means a package in your project's `packages/` directory always takes priority over a cached version in `~/.cop/packages/`.
