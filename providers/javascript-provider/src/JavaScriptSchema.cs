// Extends the shared source schema with JavaScript declaration and statement facts.

using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// JavaScript-specific provider schema: the shared <see cref="CodeSchema"/> plus the
/// <c>JavaScriptType</c> subtype of <c>Type</c>. The shared CodeSchema is left untouched.
/// </summary>
public static class JavaScriptSchema
{
    private static readonly ProviderSchema s_schema = Build();

    /// <summary>Serializes the JavaScript provider schema as UTF-8 JSON.</summary>
    public static ReadOnlyMemory<byte> GetJson() => s_schema.ToJson();

    private static ProviderSchema Build() => CodeSchema.Extend(
        CodeSchema.BoolSubtype("JavaScriptType", "Type",
            "IsExported", "HasBaseClass", "IsAbstract", "IsGeneric", "HasImplements"),
        CodeSchema.BoolSubtype("JavaScriptMethod", "Method",
            "IsGenerator", "IsArrow", "IsGetter", "IsSetter", "IsConstructor"),
        CodeSchema.BoolSubtype("JavaScriptStatement", "Statement",
            "IsForOf", "IsForIn", "IsThrow", "IsAwait", "IsTryCatch", "IsYield", "IsAwaitForeach",
            "IsEnvironmentVariableAccess", "IsEnvironmentVariableRead", "IsOriginalExceptionAlwaysRethrown",
            "IsCancellationHandler", "IsCallerCancellationPreserved", "IsOwnershipTransferred",
            "IsUsingResource"));
}
