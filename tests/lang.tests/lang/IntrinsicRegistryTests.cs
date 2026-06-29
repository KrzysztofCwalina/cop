using NUnit.Framework;
using Cop.Lang;
using Cop.Lang.Interpreter;

namespace Cop.Tests.Lang;

/// <summary>
/// Guards the single IntrinsicRegistry and the consumers now derived from it. The registry is the
/// one place cop's built-in primitive predicates/transforms/properties are declared; editor
/// metadata, the type-checker's builtin-filter set, and the runtime's alias registrations all read
/// from it. These tests fail if the registry is internally inconsistent or a derived consumer drifts.
/// </summary>
[TestFixture]
public class IntrinsicRegistryTests
{
    [Test]
    public void Registry_HasNoDuplicateNameKindSurfaces()
    {
        var dupes = IntrinsicRegistry.All
            .GroupBy(o => (o.Name, o.Kind))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Name}/{g.Key.Kind}")
            .ToList();
        Assert.That(dupes, Is.Empty, "duplicate (name, kind) surfaces: " + string.Join(", ", dupes));
    }

    [Test]
    public void BuiltinFilterSet_CoversEveryPrimitivePredicateNameAndAlias()
    {
        var set = IntrinsicRegistry.NameSet(o => o.IsBuiltinFilter);

        foreach (var name in new[]
        {
            "startsWith", "endsWith", "contains", "containsAny", "equals", "notEquals", "matches",
            "sameAs", "empty", "in", "any", "all", "none", "count", "isSet", "isClear",
            "greaterThan", "lessThan", "greaterOrEqual", "lessOrEqual",
        })
            Assert.That(set, Does.Contain(name), name);

        foreach (var alias in new[] { "sw", "ew", "ct", "ca", "eq", "ne", "rx", "sm", "gt", "lt", "ge", "le" })
            Assert.That(set, Does.Contain(alias), alias);
    }

    [Test]
    public void StandardLibrary_RegistersShortAliasesFromRegistry()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);

        // These short forms used to be recognized only by pushdown/type-checking, not the runtime.
        foreach (var alias in new[] { "sw", "ew", "ct", "eq", "ne", "rx" })
            Assert.That(ffi.Resolve(alias), Is.Not.Null, $"alias '{alias}' must be registered in the FFI");

        // And they must resolve to the same function object as their canonical name.
        Assert.That(ffi.Resolve("sw"), Is.SameAs(ffi.Resolve("startsWith")));
        Assert.That(ffi.Resolve("eq"), Is.SameAs(ffi.Resolve("equals")));
    }

    [Test]
    public void LanguageMetadata_IsProjectedFromRegistry()
    {
        Assert.That(
            LanguageMetadata.StringPredicates.Select(e => e.Name).ToArray(),
            Is.EqualTo(IntrinsicRegistry.OfKind(IntrinsicKind.StringPredicate).Select(o => o.Name).ToArray()));
        Assert.That(
            LanguageMetadata.CollectionTransforms.Select(e => e.Name).ToArray(),
            Is.EqualTo(IntrinsicRegistry.OfKind(IntrinsicKind.CollectionTransform).Select(o => o.Name).ToArray()));
        Assert.That(
            LanguageMetadata.CollectionProperties.Select(e => e.Detail).ToArray(),
            Is.EqualTo(IntrinsicRegistry.OfKind(IntrinsicKind.CollectionProperty).Select(o => o.Detail).ToArray()));
    }
}
