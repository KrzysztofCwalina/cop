using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Go-specific provider schema: the shared <see cref="CodeSchema"/> plus the <c>GoType</c>
/// subtype of <c>Type</c>. The shared CodeSchema is left untouched.
/// </summary>
public static class GoSchema
{
    private static readonly ProviderSchema _schema = Build();

    public static ReadOnlyMemory<byte> GetJson() => _schema.ToJson();

    private static ProviderSchema Build()
    {
        var baseSchema = CodeSchema.Get();

        var types = new List<ProviderTypeSchema>(baseSchema.Types)
        {
            new()
            {
                Name = "GoType",
                Base = "Type",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsInterface", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsStruct", Type = "bool" },
                ],
            },
        };

        return new ProviderSchema { Types = types, Collections = baseSchema.Collections };
    }
}
