using Cop.Core;

namespace Cop.Providers;

public static class CopSchema
{
    private static readonly ProviderSchema _schema = Build();

    public static ReadOnlyMemory<byte> GetJson() => _schema.ToJson();

    private static ProviderSchema Build()
    {
        var schema = CodeSchema.Get();
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
