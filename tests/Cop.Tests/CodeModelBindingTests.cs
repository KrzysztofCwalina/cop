using Cop.Lang.Ast;
using Cop.Lang.Parser;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Phase 2 guard against the single most damaging type-model drift: a property declared in the code
/// model's .cop surface (or its provider schema) that has no runtime CLR accessor, so it silently
/// returns null at runtime. This is exactly the Parameter.Line bug. The code model is declared in
/// THREE places that must stay in sync — packages/core/code/src/*.cop (language surface),
/// CodeSchema.cs (provider schema), and CodeBindings.cs (CLR accessors). These tests fail if a
/// provider-backed property in any of the first two is not reachable through the third.
/// </summary>
[TestFixture]
public class CodeModelBindingTests
{
    private static string RepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    /// <summary>The cop type names that are backed by a CLR provider model object.</summary>
    private static HashSet<string> ProviderTypeNames()
        => CodeBindings.Build().ClrTypeMappings.Values.ToHashSet(StringComparer.Ordinal);

    private static bool HasAccessor(
        Dictionary<string, Dictionary<string, Func<object, object?>>> accessors,
        Dictionary<string, string?> baseOf,
        string typeName,
        string propName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? t = typeName;
        while (t is not null && seen.Add(t))
        {
            if (accessors.TryGetValue(t, out var a) && a.ContainsKey(propName)) return true;
            baseOf.TryGetValue(t, out t);
        }
        return false;
    }

    [Test]
    public void EveryCodeCopProviderProperty_IsReachableAtRuntime()
    {
        var providerTypes = ProviderTypeNames();
        var bindings = CodeBindings.Build();

        // Parse every .cop file in the code package and merge declared (non-computed) properties and
        // base types per type. Computed/trait properties (e.g. filePath => ...) have no CLR accessor.
        var props = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var baseOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        var srcDir = Path.Combine(RepoRoot(), "packages", "core", "code", "src");
        foreach (var file in Directory.GetFiles(srcDir, "*.cop"))
        {
            var module = CopParser.Parse(File.ReadAllText(file), file);
            foreach (var td in module.Declarations.OfType<TypeDecl>())
            {
                if (!props.TryGetValue(td.Name, out var list)) { list = []; props[td.Name] = list; }
                list.AddRange(td.Properties.Where(p => p.ComputedExpr is null).Select(p => p.Name));
                if (td.BaseType is not null) baseOf[td.Name] = td.BaseType;
            }
        }

        var failures = new List<string>();
        foreach (var (typeName, propNames) in props)
        {
            if (!providerTypes.Contains(typeName)) continue; // only CLR-backed types are checked
            foreach (var propName in propNames.Distinct())
            {
                if (!HasAccessor(bindings.Accessors, baseOf, typeName, propName))
                    failures.Add($"{typeName}.{propName}");
            }
        }

        Assert.That(failures, Is.Empty,
            "code.cop provider properties with NO runtime accessor (they will silently return null): "
            + string.Join(", ", failures.OrderBy(x => x)));
    }

    [Test]
    public void EveryProviderSchemaProperty_IsReachableAtRuntime()
    {
        var bindings = CodeBindings.Build();
        var schema = CodeSchema.Get();
        var baseOf = schema.Types.ToDictionary(t => t.Name, t => t.Base, StringComparer.Ordinal);

        var failures = new List<string>();
        foreach (var type in schema.Types)
            foreach (var prop in type.Properties)
                if (!HasAccessor(bindings.Accessors, baseOf, type.Name, prop.Name))
                    failures.Add($"{type.Name}.{prop.Name}");

        Assert.That(failures, Is.Empty,
            "CodeSchema properties with NO runtime accessor (they will silently return null): "
            + string.Join(", ", failures.OrderBy(x => x)));
    }

    [Test]
    public void CodeCop_And_CodeSchema_AgreeOnProviderProperties_ExceptDocumentedDifferences()
    {
        // code.cop (the language surface) and CodeSchema.cs (the provider schema) declare the same
        // provider types. They must agree on properties so `cop verify`/IntelliSense match runtime,
        // EXCEPT for these documented, intentional differences:
        //   - schema-only convenience booleans that code.cop expresses as predicates (isPublic, …)
        //   - cop-only properties that are not part of the binary provider schema
        var allowedSchemaOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "Type.IsSealed", "Type.IsAbstract", "Type.IsStatic", "Type.IsPublic",
            "Method.IsStatic", "Method.IsAbstract", "Method.IsPublic",
            "Field.IsStatic", "Field.IsPublic",
            "Property.IsStatic", "Property.IsAbstract", "Property.IsPublic",
            "Event.IsStatic", "Event.IsPublic",
            "TypeReference.Generic", "TypeReference.GenericArguments", "TypeReference.Length",
            "File.Projects",
            "Statement.Braced",
        };
        var allowedCopOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "Statement.CopIgnore", "Line.CopIgnore",
        };

        var providerTypes = ProviderTypeNames();
        var schema = CodeSchema.Get();
        var schemaProps = schema.Types.ToDictionary(
            t => t.Name,
            t => t.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        // Merge code.cop declared (non-computed) properties per type.
        var copProps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var srcDir = Path.Combine(RepoRoot(), "packages", "core", "code", "src");
        foreach (var file in Directory.GetFiles(srcDir, "*.cop"))
            foreach (var td in CopParser.Parse(File.ReadAllText(file), file).Declarations.OfType<TypeDecl>())
            {
                if (!copProps.TryGetValue(td.Name, out var set)) { set = new(StringComparer.Ordinal); copProps[td.Name] = set; }
                foreach (var p in td.Properties.Where(p => p.ComputedExpr is null)) set.Add(p.Name);
            }

        var unexpected = new List<string>();
        foreach (var typeName in providerTypes)
        {
            var inSchema = schemaProps.GetValueOrDefault(typeName) ?? [];
            var inCop = copProps.GetValueOrDefault(typeName) ?? [];

            foreach (var p in inSchema.Except(inCop))
                if (!allowedSchemaOnly.Contains($"{typeName}.{p}")) unexpected.Add($"schema-only {typeName}.{p}");
            foreach (var p in inCop.Except(inSchema))
                if (!allowedCopOnly.Contains($"{typeName}.{p}")) unexpected.Add($"cop-only {typeName}.{p}");
        }

        Assert.That(unexpected, Is.Empty,
            "code.cop and CodeSchema drifted on provider properties (add to code.cop/schema, or to the "
            + "documented allowlist if intentional): " + string.Join(", ", unexpected.OrderBy(x => x)));
    }
}
