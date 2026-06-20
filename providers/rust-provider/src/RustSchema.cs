using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Rust-specific provider schema. Extends the shared <see cref="CodeSchema"/> with the
/// <c>RustType</c> type (a subtype of <c>Type</c>) so the runtime type registry knows
/// <c>RustType &lt;: Type</c> and resolves its Rust-only fields. The shared CodeSchema is
/// left untouched — Rust specifics live only here.
/// </summary>
public static class RustSchema
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
                Name = "RustType",
                Base = "Type",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsTrait", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsImpl", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsUnsafe", Type = "bool" },
                ],
            },
        };

        return new ProviderSchema { Types = types, Collections = baseSchema.Collections };
    }
}
