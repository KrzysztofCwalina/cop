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
    private static readonly ProviderSchema _schema = Build();

    public static ReadOnlyMemory<byte> GetJson() => _schema.ToJson();

    private static ProviderSchema Build()
    {
        var baseSchema = CodeSchema.Get();

        var types = new List<ProviderTypeSchema>(baseSchema.Types)
        {
            new()
            {
                Name = "CSharpType",
                Base = "Type",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsRecord", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsPartial", Type = "bool" },
                ],
            },
        };

        return new ProviderSchema { Types = types, Collections = baseSchema.Collections };
    }
}
