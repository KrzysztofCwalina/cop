// Extends the shared source schema with Python declaration and statement facts.

using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Python-specific provider schema: the shared <see cref="CodeSchema"/> plus the
/// <c>PythonType</c> subtype of <c>Type</c>. The shared CodeSchema is left untouched.
/// </summary>
public static class PythonSchema
{
    private static readonly ProviderSchema s_schema = Build();

    /// <summary>Serializes the Python provider schema as UTF-8 JSON.</summary>
    public static ReadOnlyMemory<byte> GetJson() => s_schema.ToJson();

    private static ProviderSchema Build() => CodeSchema.Extend(
        CodeSchema.BoolSubtype("PythonType", "Type",
            "IsDataclass", "IsEnum", "IsAbstract", "IsNamedTuple", "IsProtocol", "IsException", "HasSlots"),
        CodeSchema.BoolSubtype("PythonMethod", "Method",
            "IsStaticmethod", "IsClassmethod", "IsProperty", "IsGenerator", "IsAbstractMethod", "IsDunder"),
        CodeSchema.BoolSubtype("PythonStatement", "Statement",
            "IsWith", "IsAsyncWith", "IsRaise", "IsAssert", "IsComprehension", "IsGlobal", "IsNonlocal",
            "IsYield", "IsAsyncFor", "IsUsingResource", "IsOwnershipTransferred",
            "IsOriginalExceptionAlwaysRethrown", "IsCancellationHandler", "IsCallerCancellationPreserved",
            "IsEnvironmentVariableAccess", "IsEnvironmentVariableRead"));
}
