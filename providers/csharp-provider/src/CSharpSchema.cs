// Extends the common code schema with C# declaration and control-flow facts.

using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// C#-specific provider schema. Extends the shared <see cref="CodeSchema"/> with the
/// <c>CSharpType</c> type (a subtype of <c>Type</c>) so the runtime type registry knows
/// <c>CSharpType &lt;: Type</c> and resolves its C#-only fields. The shared CodeSchema is
/// left untouched — C# specifics live only here.
/// </summary>
public static class CSharpSchema
{
    private static readonly ProviderSchema s_schema = Build();

    /// <summary>
    /// Serializes the provider schema for C#-specific source facts.
    /// </summary>
    public static ReadOnlyMemory<byte> GetJson() => s_schema.ToJson();

    private static ProviderSchema Build() => CodeSchema.Extend(
        CodeSchema.BoolSubtype("CSharpType", "Type",
            "IsRecord", "IsRecordStruct", "IsReadOnly", "IsRef", "IsFileLocal", "IsPartial",
            "HasPrimaryConstructor", "IsGeneric"),
        CodeSchema.BoolSubtype("CSharpMethod", "Method",
            "IsExtension", "IsPartial", "IsUnsafe", "IsExtern", "IsExpressionBodied", "IsGeneric"),
        CodeSchema.BoolSubtype("CSharpStatement", "Statement",
            "IsLock", "IsUnsafe", "IsFixed", "IsChecked", "IsUnchecked", "IsYield", "IsGoto",
            "IsAwaitForeach", "HasCatchFilter", "IsCancellationHandler", "IsCallerCancellationPreserved",
            "IsOriginalExceptionAlwaysRethrown", "HasTypeRestrictedCatchFilter", "IsOwnershipTransferred",
            "IsUsingResource"));
}
