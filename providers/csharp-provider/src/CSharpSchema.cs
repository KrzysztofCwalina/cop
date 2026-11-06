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

    private static ProviderSchema Build()
    {
        ProviderSchema baseSchema = CodeSchema.Get();

        List<ProviderTypeSchema> types = new List<ProviderTypeSchema>(baseSchema.Types)
        {
            new()
            {
                Name = "CSharpType",
                Base = "Type",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsRecord", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsRecordStruct", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsReadOnly", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsRef", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsFileLocal", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsPartial", Type = "bool" },
                    new ProviderPropertySchema { Name = "HasPrimaryConstructor", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsGeneric", Type = "bool" },
                ],
            },
            new()
            {
                Name = "CSharpMethod",
                Base = "Method",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsExtension", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsPartial", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsUnsafe", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsExtern", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsExpressionBodied", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsGeneric", Type = "bool" },
                ],
            },
            new()
            {
                Name = "CSharpStatement",
                Base = "Statement",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsLock", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsUnsafe", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsFixed", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsChecked", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsUnchecked", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsYield", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsGoto", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsAwaitForeach", Type = "bool" },
                    new ProviderPropertySchema { Name = "HasCatchFilter", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsCancellationHandler", Type = "bool" },
                    new ProviderPropertySchema
                    {
                        Name = "IsCallerCancellationPreserved", Type = "bool"
                    },
                    new ProviderPropertySchema
                    {
                        Name = "IsOriginalExceptionAlwaysRethrown", Type = "bool"
                    },
                    new ProviderPropertySchema
                    {
                        Name = "HasTypeRestrictedCatchFilter", Type = "bool"
                    },
                    new ProviderPropertySchema { Name = "IsOwnershipTransferred", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsUsingResource", Type = "bool" },
                ],
            },
        };

        return new ProviderSchema { Types = types, Collections = baseSchema.Collections };
    }
}
