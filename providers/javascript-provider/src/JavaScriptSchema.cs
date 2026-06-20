using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// JavaScript-specific provider schema: the shared <see cref="CodeSchema"/> plus the
/// <c>JavaScriptType</c> subtype of <c>Type</c>. The shared CodeSchema is left untouched.
/// </summary>
public static class JavaScriptSchema
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
                Name = "JavaScriptType",
                Base = "Type",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsExported", Type = "bool" },
                    new ProviderPropertySchema { Name = "HasBaseClass", Type = "bool" },
                ],
            },
        };

        return new ProviderSchema { Types = types, Collections = baseSchema.Collections };
    }
}
