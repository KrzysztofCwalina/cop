using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Python-specific provider schema: the shared <see cref="CodeSchema"/> plus the
/// <c>PythonType</c> subtype of <c>Type</c>. The shared CodeSchema is left untouched.
/// </summary>
public static class PythonSchema
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
                Name = "PythonType",
                Base = "Type",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsDataclass", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsEnum", Type = "bool" },
                ],
            },
        };

        return new ProviderSchema { Types = types, Collections = baseSchema.Collections };
    }
}
