// Extends the shared source schema with Go-specific declaration facts.

using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Go-specific provider schema: the shared <see cref="CodeSchema"/> plus Go subtypes for
/// types, methods, and statements. The shared CodeSchema is left untouched.
/// </summary>
public static class GoSchema
{
    private static readonly ProviderSchema s_schema = Build();

    /// <summary>Serializes the Go provider schema as UTF-8 JSON.</summary>
    public static ReadOnlyMemory<byte> GetJson() => s_schema.ToJson();

    private static ProviderSchema Build() => CodeSchema.Extend(
        CodeSchema.BoolSubtype("GoType", "Type",
            "IsInterface", "IsStruct", "IsTypeAlias", "HasStructTags", "HasUnionTypeSet",
            "HasUnderlyingTypeTerms"),
        CodeSchema.BoolSubtype("GoMethod", "Method",
            "IsPointerReceiver", "HasNamedReturns", "IsVariadic", "IsGeneric"),
        CodeSchema.BoolSubtype("GoStatement", "Statement",
            "IsDefer", "IsGoroutine", "IsSelect", "IsRangeLoop", "IsTypeSwitch"));
}
