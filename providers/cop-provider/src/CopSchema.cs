// Declares the provider schema for Cop-specific function facts.

using Cop.Core;

namespace Cop.Providers;

/// <summary>Describes the runtime schema for Cop-specific source declarations.</summary>
public static class CopSchema
{
    private static readonly ProviderSchema s_schema = Build();

    /// <summary>Serializes the Cop provider schema as UTF-8 JSON.</summary>
    public static ReadOnlyMemory<byte> GetJson() => s_schema.ToJson();

    private static ProviderSchema Build()
    {
        ProviderSchema schema = CodeSchema.Get();
        return new ProviderSchema
        {
            Types =
            [
                .. schema.Types,
                new ProviderTypeSchema
                {
                    Name = "CopFunction",
                    Base = "Method",
                    Properties =
                    [
                        new ProviderPropertySchema { Name = "IsIntrinsic", Type = "bool" },
                        new ProviderPropertySchema { Name = "IsPredicate", Type = "bool" },
                        new ProviderPropertySchema { Name = "IsCommand", Type = "bool" },
                        new ProviderPropertySchema { Name = "IsExported", Type = "bool" },
                        new ProviderPropertySchema { Name = "HasGuard", Type = "bool" }
                    ]
                }
            ],
            Collections = schema.Collections
        };
    }
}
