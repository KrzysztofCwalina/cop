# C# Library Design Guidance

## API Surface Design

**Minimize Public Surface**
- Expose only types and members that form your intended public contract
- Use `internal` by default; make public only what's essential
- Avoid exposing implementation details, collection internals, or framework dependencies
- Consider using the `[EditorBrowsable(EditorBrowsableState.Never)]` attribute for members that must be public but shouldn't appear in IntelliSense

**Seal Classes by Default**
- Mark classes `sealed` unless inheritance is an explicit design goal
- Sealed classes are more maintainable—you control all subclass behavior
- If you anticipate subclassing, document the protected contract clearly
- Use abstract base classes when defining extension points

**Prefer Interfaces**
- Design abstractions around interfaces, not base classes
- Interfaces enable composition, multiple inheritance of type, and better testability
- Keep interfaces focused and cohesive (e.g., `IEnumerable`, `IDisposable`)

## Versioning

**Semantic Versioning**
- Follow SemVer: MAJOR.MINOR.PATCH (e.g., 2.1.3)
- MAJOR: Breaking changes
- MINOR: New backward-compatible features
- PATCH: Bug fixes

**Assembly Versioning**
- Set `AssemblyVersion` only on major version changes to avoid binding redirects
- Set `AssemblyFileVersion` and `InformationalVersion` to full SemVer
- Example: AssemblyVersion=2.0.0.0, FileVersion=2.1.3.0, InformationalVersion=2.1.3

**Backward Compatibility**
- Never remove public members—deprecate them with `[Obsolete]` instead
- Never change method signatures in breaking ways
- Never change the meaning of parameters or return values
- Use overloads to add optional parameters rather than breaking existing ones

## Packaging

**NuGet Conventions**
- Use `<PackageId>` matching the namespace root (e.g., Company.Library)
- Include meaningful `<Description>` and `<Authors>` metadata
- Add `<RepositoryUrl>` pointing to source control
- Set `<PackageProjectUrl>` to documentation
- Use `<LicenseExpression>` (e.g., "Apache-2.0", "MIT")

**Package Metadata**
- Include `<Version>` matching your versioning scheme
- Document breaking changes in `<PackageReleaseNotes>`
- Add relevant `<PackageTags>` for discoverability
- Set `<Nullable>enable</Nullable>` for modern null-safety

**README**
- Create a concise README.md in the package root
- Include basic usage example, installation instructions, and license
- Link to full documentation for complex libraries

## Dependency Management

**Minimize Dependencies**
- Reduce external dependencies—each adds maintenance burden
- Audit transitive dependencies; watch for version conflicts
- Prefer `Target Frameworks` like `net6.0` and `netstandard2.1` over multiple frameworks

**Use Abstractions**
- Depend on interfaces, not concrete implementations
- Allow consumers to substitute their own implementations
- Example: accept `ILogger` rather than `ConcreteLogger`

**Avoid Diamond Dependencies**
- If two of your dependencies depend on conflicting versions of a third library, your package becomes problematic
- Document minimum version requirements clearly
- Test against realistic dependency trees

## XML Documentation

**All Public Members**
- Every public type, method, property, and event must have XML documentation
- Use `<summary>`, `<param>`, `<returns>`, `<exception>` tags
- Keep summaries concise; use `<remarks>` for detailed explanation

**Examples and Remarks**
- Include `<example>` for complex APIs or common use cases
- Document exceptions in `<exception>` tags so consumers know what to handle
- Use `<see cref="..."/>` and `<seealso cref="..."/>` for cross-references

Example:
```csharp
/// <summary>
/// Computes the hash of the provided data.
/// </summary>
/// <param name="data">The input bytes to hash.</param>
/// <returns>A 32-character hexadecimal string.</returns>
/// <exception cref="ArgumentNullException">Thrown if data is null.</exception>
public string ComputeHash(byte[] data)
{
    // ...
}
```

## Obsoleting APIs

**ObsoleteAttribute Usage**
- Apply `[Obsolete("message", false)]` to deprecate APIs; don't remove them immediately
- Set the second parameter to `true` only when removing in a major version
- Provide clear migration guidance in the obsolete message

Example:
```csharp
[Obsolete("Use ComputeHashAsync instead for better performance.", false)]
public string ComputeHash(byte[] data)
{
    // ...
}
```

**Migration Paths**
- Document the replacement API clearly
- If possible, provide helper methods to ease migration
- Give at least one full minor version (2-4 weeks) before removing deprecated APIs
