using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;

namespace Cop.Cli.Commands;

public static class InitCommand
{
    public static Command Create()
    {
        var command = new Command("init", "Generate agent instruction files for writing cop rules")
        {
        };

        command.SetAction(_ => Execute());

        return command;
    }

    public static int Execute()
    {
        var cwd = Directory.GetCurrentDirectory();
        int filesCreated = 0;

        // Generate .github/copilot-instructions.md
        var githubDir = Path.Combine(cwd, ".github");
        var copilotPath = Path.Combine(githubDir, "copilot-instructions.md");
        if (File.Exists(copilotPath))
        {
            Console.Error.WriteLine($"Skipped: {GetRelativePath(cwd, copilotPath)} already exists");
        }
        else
        {
            Directory.CreateDirectory(githubDir);
            File.WriteAllText(copilotPath, GetInstructionContent());
            Console.WriteLine($"Created: {GetRelativePath(cwd, copilotPath)}");
            filesCreated++;
        }

        // Generate AGENTS.md
        var agentsPath = Path.Combine(cwd, "AGENTS.md");
        if (File.Exists(agentsPath))
        {
            Console.Error.WriteLine($"Skipped: AGENTS.md already exists");
        }
        else
        {
            File.WriteAllText(agentsPath, GetInstructionContent());
            Console.WriteLine($"Created: AGENTS.md");
            filesCreated++;
        }

        if (filesCreated > 0)
            Console.WriteLine($"\n{filesCreated} file(s) created. Agents will now discover cop language context automatically.");
        else
            Console.WriteLine("\nNo files created (all already exist).");

        return 0;
    }

    private static string GetRelativePath(string basePath, string fullPath)
    {
        return Path.GetRelativePath(basePath, fullPath);
    }

    internal static string GetInstructionContent()
    {
        return """
            # Cop Language Instructions

            This project uses **Cop** — a data processing language for writing static analysis rules, code checks, and report generation. Cop files use the `.cop` extension.

            ## Quick Reference

            ### Running Cop

            ```bash
            cop <file.cop>           # Run a .cop file
            cop <package-name>       # Run a package by name
            cop                      # Run all .cop files in current directory
            cop test                 # Run tests
            cop repl                 # Interactive REPL
            ```

            ### Getting Detailed Help

            ```bash
            cop help language        # Full language reference (syntax, types, operators)
            cop help <package>       # Package documentation (types, functions, examples)
            cop package list         # List all available packages
            ```

            **Always run `cop help language` before writing cop code** to get the full syntax reference.
            When using a package, run `cop help <package-name>` to see its types and API.

            ## Language Overview

            Cop is a declarative data processing language. The core pattern for writing rules is:

            ```
            import → define predicates → filter collections → produce output
            ```

            ### Key Syntax Rules

            - Strings use **single quotes**: `'hello'`
            - String interpolation: `'{item.Name} has {item.Count} methods'`
            - Styled output: `'{text@dim}'`, `'{text@red}'`
            - Comments: `#` for line comments, `##` for doc comments
            - No semicolons, no braces for blocks (except object literals)

            ### Core Pattern: Writing a Check

            ```cop
            import code
            import code-analysis

            # 1. Define a predicate (boolean filter)
            predicate isTooLong(Method) => Method.Statements.count() > 50

            # 2. Filter a collection
            let longMethods = Code.Methods:isTooLong

            # 3. Produce violations
            let violations = longMethods:toWarning('Method {item.Name} has too many statements')

            # 4. Output them
            CHECK violations
            ```

            ### Declarations

            | Keyword | Purpose | Example |
            |---------|---------|---------|
            | `import` | Import a package | `import code` |
            | `feed` | Declare package source | `feed 'github.com/owner/repo'` |
            | `let` | Declare a named value/list | `let Clients = Types:isClient` |
            | `predicate` | Boolean filter on items | `predicate isPublic(Type) => ...` |
            | `function` | Transform or compute | `function name(T) => expr` |
            | `type` | Object shape definition | `type Foo = { Name : string }` |
            | `enum` | Extensible enum | `enum Severity = error \| warning` |
            | `flags` | Bit flag constants | `flags Mod = Public \| Static` |
            | `command` | Named runnable entry point | `command main = CHECK(violations)` |
            | `foreach` | Iterate and output | `foreach items => '{item.Name}'` |
            | `test` | Test assertion | `test x = assert(expr)` |
            | `export` | Make visible to importers | `export predicate ...` |

            ### Filtering with `:`

            The colon operator filters collections or pipes values through functions:

            ```cop
            Types:isClient                    # filter Types where isClient is true
            Types:isClient:isPublic           # chained AND filters
            Statements:Kind:equals('call')    # field predicate
            someValue:myFunction              # pipe value through function
            ```

            ### Common String Predicates

            ```cop
            Name:startsWith('Get')
            Name:endsWith('Client')
            Name:contains('Test')
            Name:equals('Main')
            Name:matches('.*Service$')        # regex
            ```

            ### Collection Operations

            ```cop
            items.Count                       # number of items
            items.Select(item.Name)           # project to list of names
            items.Where(item.Age > 18)        # filter with expression
            items.OrderBy(item.Name)          # sort
            items:any(predicate)              # true if any match
            items:all(predicate)              # true if all match
            items:none(predicate)             # true if none match
            items:count(predicate)            # count matching
            ```

            ### Producing Violations (with code-analysis package)

            ```cop
            import code-analysis

            # Convert filtered items to violations:
            let v = filteredItems:toError('message with {item.Name}')
            let w = filteredItems:toWarning('message')
            let i = filteredItems:toInfo('message')

            # Output violations:
            CHECK v
            ```

            ## Common Packages

            | Package | Provides | Key Collections |
            |---------|----------|-----------------|
            | `code` | Source code analysis | Types, Methods, Statements, Lines, Files |
            | `code-analysis` | Violation type + CHECK | Violation, toError, toWarning, toInfo |
            | `files` | Filesystem analysis | Folders, Files |
            | `csharp` | C# language provider | csharp.types(), csharp.statements() |
            | `python` | Python language provider | python.types(), python.statements() |
            | `javascript` | JS/TS language provider | javascript.types(), javascript.statements() |

            ## Example: Complete Rule File

            ```cop
            feed 'github.com/KrzysztofCwalina/cop'
            import code
            import code-analysis
            import csharp

            # Flag methods longer than 50 statements
            predicate isTooLong(Method) => Method.Statements.count() > 50

            # Flag types with no documentation
            predicate undocumented(Type) => Type.Documented == false && Type:isPublic

            let longMethods = Code.Methods:isTooLong
                :toWarning('Method {item.Name} exceeds 50 statements ({item.Statements.count()})')

            let undocTypes = Code.Types:undocumented
                :toWarning('Public type {item.Name} is not documented')

            command main = CHECK(longMethods + undocTypes)
            ```

            ## Testing

            ```cop
            import code

            test has-types = assert(Code.Types.Count > 0)
            test no-long-methods = assert(Code.Methods:isTooLong.Count == 0)
            test has-public = assert(Code.Types:isPublic.Count > 0, 'Expected public types')
            ```

            Run with: `cop test`

            ## Tips for Agents

            1. **Always start with** `cop help language` to get the full syntax reference
            2. **Check package APIs** with `cop help <package-name>` before using a package
            3. **Use single quotes** for all strings (not double quotes)
            4. **Use `{item.Prop}`** for string interpolation in templates
            5. **Predicates are camelCase**, types are PascalCase, commands are UPPERCASE
            6. **Test with** `cop test` after writing rules
            7. **Validate syntax** with `cop syntax <file.cop>`
            """;
    }
}
