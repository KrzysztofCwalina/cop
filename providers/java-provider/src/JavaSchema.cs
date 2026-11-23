// Extends the shared source schema with Java-specific declaration facts.

using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Java-specific provider schema: the shared <see cref="CodeSchema"/> plus the
/// <c>JavaType</c> subtype of <c>Type</c> (so the runtime knows <c>JavaType &lt;: Type</c>).
/// The shared CodeSchema is left untouched.
/// </summary>
public static class JavaSchema
{
    private static readonly ProviderSchema s_schema = Build();

    /// <summary>Serializes the Java provider schema as UTF-8 JSON.</summary>
    public static ReadOnlyMemory<byte> GetJson() => s_schema.ToJson();

    private static ProviderSchema Build() => CodeSchema.Extend(
        CodeSchema.BoolSubtype("JavaType", "Type",
            "IsRecord", "IsEnum", "IsSealed", "IsNonSealed", "IsFinal", "IsGeneric"),
        CodeSchema.BoolSubtype("JavaMethod", "Method",
            "IsSynchronized", "IsNative", "IsDefault", "IsStrictfp", "IsGeneric"),
        CodeSchema.BoolSubtype("JavaStatement", "Statement",
            "IsSynchronized", "IsTryWithResources", "IsEnhancedFor", "IsThrow", "IsAssert",
            "IsOriginalExceptionAlwaysRethrown", "IsUsingResource", "IsOwnershipTransferred",
            "IsCancellationHandler", "IsCallerCancellationPreserved"));
}
