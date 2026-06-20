using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Java-specific provider schema: the shared <see cref="CodeSchema"/> plus the
/// <c>JavaType</c> subtype of <c>Type</c> (so the runtime knows <c>JavaType &lt;: Type</c>).
/// The shared CodeSchema is left untouched.
/// </summary>
public static class JavaSchema
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
                Name = "JavaType",
                Base = "Type",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsRecord", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsEnum", Type = "bool" },
                ],
            },
        };

        return new ProviderSchema { Types = types, Collections = baseSchema.Collections };
    }
}
