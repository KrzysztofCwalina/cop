// Extends the shared source schema with Rust declaration and statement facts.

using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Rust-specific provider schema. Extends the shared <see cref="CodeSchema"/> with the
/// <c>RustType</c> type (a subtype of <c>Type</c>) so the runtime type registry knows
/// <c>RustType &lt;: Type</c> and resolves its Rust-only fields. The shared CodeSchema is
/// left untouched — Rust specifics live only here.
/// </summary>
public static class RustSchema
{
    private static readonly ProviderSchema s_schema = Build();

    /// <summary>Serializes the Rust provider schema as UTF-8 JSON.</summary>
    public static ReadOnlyMemory<byte> GetJson() => s_schema.ToJson();

    private static ProviderSchema Build() => CodeSchema.Extend(
        CodeSchema.BoolSubtype("RustType", "Type",
            "IsTrait", "IsImpl", "IsUnsafe", "IsUnion", "IsTupleStruct", "IsUnitStruct", "IsNegativeImpl"),
        CodeSchema.BoolSubtype("RustMethod", "Method",
            "IsUnsafe", "IsConst", "IsExtern"),
        CodeSchema.BoolSubtype("RustStatement", "Statement",
            "IsMacroCall", "IsPanic"));
}
