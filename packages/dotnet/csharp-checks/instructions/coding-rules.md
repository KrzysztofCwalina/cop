# C# Coding Standards and Best Practices

## Naming Conventions

### Types and Methods
- Use **PascalCase** for all public types, classes, interfaces, methods, and properties
- Examples: `public class UserService`, `public string GetUserName()`, `public interface IRepository`
- Maintain consistency across public APIs; it aids discoverability and IDE autocomplete

### Local Variables and Parameters
- Use **camelCase** for local variables and method parameters
- Example: `var userName = GetName(userId);`
- Enhances readability and distinguishes scope immediately

### Private Fields
- Use **_camelCase** with leading underscore for private instance fields
- Example: `private string _userName;`
- Explicitly declares field as private; improves code clarity

### Constants
- Use **UPPER_SNAKE_CASE** for compile-time constants
- Example: `private const int DEFAULT_TIMEOUT = 5000;`

---

## Nullable Reference Types

- **Always enable** nullable reference types in your `.csproj`:
  ```xml
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  ```
- Annotate return types and parameters explicitly: `public string? GetValue()` vs `public string GetValue()`
- Non-nullable types indicate values that are guaranteed not null; nullable types permit null
- **Avoid the null-forgiving operator (`!`)** except in rare cases where you're certain of non-nullability
- Using the null-forgiving operator too freely defeats the purpose of nullable reference types

---

## Async/Await Patterns

- Use **async all the way**: if a method calls an async operation, make it async
- Avoid `Task.Wait()` or `Task.Result` in application code; they can cause deadlocks
- Add `ConfigureAwait(false)` in library code to avoid capturing synchronization context:
  ```csharp
  public async Task<User> GetUserAsync(int id)
  {
      var response = await _httpClient.GetAsync($"/api/users/{id}").ConfigureAwait(false);
      return await response.Content.ReadAsAsync<User>().ConfigureAwait(false);
  }
  ```
- Always accept `CancellationToken` parameters in long-running async methods for proper cancellation support
- Example: `public async Task<List<Item>> FetchItemsAsync(CancellationToken ct = default)`

---

## LINQ Usage

- **Prefer method syntax** over query syntax for consistency
  - Bad: `var users = from u in context.Users where u.IsActive select u;`
  - Good: `var users = context.Users.Where(u => u.IsActive);`
- Avoid excessive method chaining on a single line; break into multiple lines for readability
- **Materialize collections** when needed for multiple iterations:
  ```csharp
  var activeUsers = users.Where(u => u.IsActive).ToList();  // Materialize once
  var count = activeUsers.Count;
  var first = activeUsers.FirstOrDefault();
  ```
- Use `ToList()` or `ToArray()` explicitly when you need to consume the entire sequence

---

## Error Handling

- Use exceptions for **exceptional cases**, not control flow
  - Bad: `try { return int.Parse(input); } catch { return 0; }`
  - Good: `if (int.TryParse(input, out var result)) { return result; }`
- Use **specific catch blocks** instead of generic `catch (Exception)`
  ```csharp
  try { /* code */ }
  catch (ArgumentNullException ex) { /* handle specific error */ }
  catch (InvalidOperationException ex) { /* handle specific error */ }
  catch (Exception ex) { /* log or rethrow */ }
  ```
- **Never use empty catch blocks**; always log or rethrow
- Use `throw;` to preserve stack trace when rethrowing

---

## Modern C# Features

- Use **file-scoped namespaces** (C# 10+) instead of traditional braces:
  ```csharp
  namespace MyApp.Services;
  
  public class UserService { }  // No nested braces needed
  ```
- Use **expression-bodied members** for simple one-liners:
  ```csharp
  public string GetFullName() => $"{FirstName} {LastName}";
  public bool IsActive => Status == UserStatus.Active;
  ```
- Use **target-typed `new` expressions** (C# 9+) to reduce verbosity:
  ```csharp
  User user = new() { Name = "John", Age = 30 };  // Type inferred from context
  ```
- Use **records** (C# 9+) for immutable data types:
  ```csharp
  public record UserDto(int Id, string Name, string Email);
  ```

---

## .NET 10 Target Framework

- Target **.NET 10** for all new projects unless otherwise specified
- Leverage latest language features and performance improvements
- Keep dependencies updated; use `dotnet package update --check-outdated` regularly
- Ensure your code compiles without warnings

---

## Code Organization

- One public type per file (with rare exceptions for nested types)
- Place using statements at the top; order: system namespaces first, then third-party, then local
- Use regions sparingly; well-organized methods eliminate the need for regions
- Keep methods focused and under 30 lines; extract complex logic into helper methods

---

## StyleCop Conventions

### Spacing

- One space after keywords (`if`, `for`, `while`, `switch`, `return`)
- One space before and after binary operators (`=`, `+`, `==`, `&&`)
- No space between method name and opening parenthesis: `GetItems()`
- No space inside parentheses: `GetItems(id)` not `GetItems( id )`
- One space after commas in argument lists: `Method(a, b, c)`
- Single space after `//` in single-line comments: `// This is correct`
- No tabs — use spaces for indentation (4 spaces per level)
- No trailing whitespace at end of lines

### Member Ordering

Within a type, order members by category and then by access level:

1. Constants and static readonly fields
2. Fields
3. Constructors
4. Properties
5. Methods
6. Nested types

Within each category, order by access: `public`, `internal`, `protected`, `private`.
Static members before instance members within the same access level.

### Access Modifiers

- Always declare access modifiers explicitly — do not rely on defaults
- Bad: `class UserService { }` — Good: `public class UserService { }`
- Bad: `string Name { get; }` — Good: `public string Name { get; }` or `private string Name { get; }`

### Brace Style

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

### Blank Lines

- One blank line between type members (methods, properties, constructors)
- No multiple consecutive blank lines
- No blank lines at the start or end of a file
- No blank line between a documentation comment and the element it documents

### Documentation

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
