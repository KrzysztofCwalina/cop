# Working with the REPL

The Agent Cop REPL (Read-Eval-Print Loop) lets you develop `.cop` files while testing progress interactively, try new expressions, and prototype checks before committing them to a file. Start it by running `cop` with no arguments in any directory.

## Getting Started

Create a project directory with a `.cop` file that imports the packages you need:

```bash
mkdir my-checks
cd my-checks
```

Create `main.cop`:

```cop
import csharp

let publicTypes = Code.Types:isPublic
```

Now start the REPL:

```bash
cop
```

```
cop working on my-checks/main.cop

cop>
```

The REPL loads all `.cop` files in the current directory and resolves imported packages from `~/.cop/packages/` and any `packages/` directories up the tree. Packages are auto-restored on first use.

## Evaluating Expressions

Type any collection expression at the prompt. The REPL evaluates it against your target directory:

```
cop> publicTypes
MyClient
MyOptions
ServiceConnection

cop> Code.Types:isPublic.Name:endsWith('Client')
MyClient
```

You can prototype new expressions without editing your file:

```
cop> Code.Types:isAbstract
BaseService
AbstractHandler
```

### Line References

Type a line number with `!` to evaluate that line from your `.cop` file:

```
cop> 3!
MyClient
MyOptions
ServiceConnection
```

This reads line 3 from `main.cop`, evaluates it, and prints the result. Use this to quickly re-run expressions while you edit your file — change a line, type `r!` to reload, then type the line number with `!`.

### Value Expressions

Literals, arithmetic, function calls, and objects evaluate directly:

```
cop> 42
42

cop> 1 + 2
3

cop> 'hello world'
hello world

cop> ['red' 'green' 'blue']
red
green
blue
```

#### Function Calls

Functions defined in your `.cop` file can be called in the REPL:

```
cop> inc(5)
6

cop> inc(inc(5))
7
```

#### Object Expressions

Object literals output as JSON. Multi-line objects in your source file can be evaluated via line references:

```
cop> 5!
{
    "Name": "Chip",
    "Age": 32
}
```

### Running Commands

Type a command name to run it. Commands are `foreach` blocks defined in your `.cop` file:

```
cop> CHECK
ERROR: MyClient.cs:15 method 'getData' should use PascalCase
```

## Tab Completion

The REPL provides context-aware completions:

- **Tab** on partial text → completes or shows a popup of candidates
- **After `:`** → shows predicate names (equals, startsWith, contains, isPublic, etc.)
- **After `.`** → shows properties and transforms (Name, Count, Trim, etc.)
- **Up/Down** → navigate the completion popup
- **Tab or Enter** → accept the selected completion
- **Escape** → dismiss the popup

Completions trigger automatically when you type `:` or `.` — no need to press Tab first.

## Built-in Commands

| Command | Short | Description |
|---------|-------|-------------|
| `quit!` | `q!` | Exit the REPL |
| `clear!` | `c!` | Clear the screen |
| `reload!` | `r!` | Force reload `.cop` files from disk |
| `src!` | `s!` | Print the full source of loaded `.cop` files with line numbers |
| `list!` | `l!` | List available commands, let bindings, and collections |
| `help!` | `h!` | Show help |

All built-in commands require the `!` suffix to distinguish them from cop identifiers. Files are auto-reloaded when changed — `reload!` is rarely needed.

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| Tab | Trigger/accept completion |
| Up/Down | History navigation (or popup navigation) |
| Ctrl+D | Exit |
| Ctrl+L | Clear screen |
| Ctrl+K | Kill to end of line |
| Ctrl+U | Kill to start of line |
| Ctrl+V | Paste from clipboard |
| Ctrl+A / Ctrl+E | Move to start/end of line |

## Walkthrough: Developing a Check Interactively

Here is a complete workflow showing how to build a C# naming check from scratch using the REPL.

**1. Set up the project**

```bash
mkdir naming-check
cd naming-check
```

Create `main.cop`:

```cop
import csharp
```

**2. Start the REPL and explore**

```bash
cop
```

```
cop working on naming-check/main.cop

cop> Code.Types.Count
12

cop> Code.Types:isPublic.Name
UserService
UserServiceOptions
IUserRepository
```

**3. Prototype a filter expression**

```
cop> Code.Types:isPublic.Name:startsWith('I')
IUserRepository
```

Good — we can find interfaces. Now let's look at method names:

```
cop> Code.Types:isPublic.Methods:isPublic.Name
GetUser
UpdateUser
getData
deleteAll
```

We can see `getData` and `deleteAll` violate PascalCase. Let's find them with a pattern:

```
cop> Code.Types:isPublic.Methods:isPublic.Name:matches('[a-z].*')
getData
deleteAll
```

**4. Add the check to your file**

Edit `main.cop` to add your new check:

```cop
import csharp
import code-analysis

let publicTypes = Code.Types:isPublic

foreach publicTypes.Methods:isPublic:matches('[a-z].*')
    => :toWarning('{item.Name} should use PascalCase')
```

**5. Run it — files auto-reload**

```
cop> main
WARNING: UserService.cs:8 'getData' should use PascalCase
WARNING: UserService.cs:14 'deleteAll' should use PascalCase
```

**6. Iterate**

Keep editing `main.cop` and re-running expressions — the REPL automatically picks up file changes.

## Tips

- The REPL auto-reloads `.cop` files when they change on disk — just save and re-run your expression.
- The REPL lazily loads providers — the first time you reference a collection like `Code.Types`, it scans your project files. Subsequent queries are fast.
- You can import any installed package (`csharp`, `filesystem`, `code`, etc.) in your `.cop` file and their predicates become available in the REPL.
- Packages are auto-restored on first use — no manual restore step needed.
