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

    private static ProviderSchema Build()
    {
        ProviderSchema baseSchema = CodeSchema.Get();

        List<ProviderTypeSchema> types = new List<ProviderTypeSchema>(baseSchema.Types)
        {
            new()
            {
                Name = "JavaType",
                Base = "Type",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsRecord", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsEnum", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsSealed", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsNonSealed", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsFinal", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsGeneric", Type = "bool" },
                ],
            },
            new()
            {
                Name = "JavaMethod",
                Base = "Method",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsSynchronized", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsNative", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsDefault", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsStrictfp", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsGeneric", Type = "bool" },
                ],
            },
            new()
            {
                Name = "JavaStatement",
                Base = "Statement",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsSynchronized", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsTryWithResources", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsEnhancedFor", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsThrow", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsAssert", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsOriginalExceptionAlwaysRethrown", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsUsingResource", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsOwnershipTransferred", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsCancellationHandler", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsCallerCancellationPreserved", Type = "bool" },
                ],
            },
        };

        return new ProviderSchema { Types = types, Collections = baseSchema.Collections };
    }
}
