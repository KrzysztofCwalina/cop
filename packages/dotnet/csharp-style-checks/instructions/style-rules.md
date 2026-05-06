# C# StyleCop Style Rules

Guidelines for writing C# code that conforms to StyleCop conventions used in Azure SDK and .NET libraries.

## Naming Conventions

- **PascalCase** for types, methods, properties, events, and namespaces: `UserService`, `GetItems()`
- **Interface names** start with `I` followed by an uppercase letter: `IRepository`, `IDisposable`
- **camelCase** for parameters and local variables: `userId`, `itemCount`
- **`_camelCase`** with underscore prefix for private instance fields: `_connection`, `_logger`
- **`s_camelCase`** for private static fields: `s_instance`
- Constants use **PascalCase**: `MaxRetryCount`, `DefaultTimeout`

## Spacing

- One space after keywords (`if`, `for`, `while`, `switch`, `return`)
- One space before and after binary operators (`=`, `+`, `==`, `&&`)
- No space between method name and opening parenthesis: `GetItems()`
- No space inside parentheses: `GetItems(id)` not `GetItems( id )`
- One space after commas in argument lists: `Method(a, b, c)`
- Single space after `//` in single-line comments: `// This is correct`
- No tabs — use spaces for indentation (4 spaces per level)
- No trailing whitespace at end of lines

## File Organization

- One type per file — each class, struct, interface, or enum in its own file
- One namespace per file
- File name matches the primary type name: `UserService.cs` contains `class UserService`
- Using directives at the top of the file, outside the namespace
- Order usings: System namespaces first, then third-party, then project-local

## Member Ordering

Within a type, order members by category and then by access level:

1. Constants and static readonly fields
2. Fields
3. Constructors
4. Properties
5. Methods
6. Nested types

Within each category, order by access: `public`, `internal`, `protected`, `private`.
Static members before instance members within the same access level.

## Access Modifiers

- Always declare access modifiers explicitly — do not rely on defaults
- Bad: `class UserService { }` — Good: `public class UserService { }`
- Bad: `string Name { get; }` — Good: `public string Name { get; }` or `private string Name { get; }`

## Brace Style

- Use Allman-style braces (opening brace on its own line):

```csharp
public void Process()
{
    if (condition)
    {
        DoWork();
    }
}
```

- Always use braces for `if`, `else`, `for`, `foreach`, `while`, `do` — even for single-line bodies
- No blank line after opening brace
- No blank line before closing brace
- Blank line after closing brace (before next element)

## Blank Lines

- One blank line between type members (methods, properties, constructors)
- No multiple consecutive blank lines
- No blank lines at the start or end of a file
- No blank line between a documentation comment and the element it documents

## Documentation

- All public types and members should have XML documentation comments
- Use `<summary>` for a brief description of the element
- Document parameters with `<param name="...">` tags
- Document return values with `<returns>` tags
- Documentation text should end with a period

```csharp
/// <summary>
/// Gets the user with the specified identifier.
/// </summary>
/// <param name="userId">The unique identifier of the user.</param>
/// <returns>The user, or <c>null</c> if not found.</returns>
public User? GetUser(string userId)
```
