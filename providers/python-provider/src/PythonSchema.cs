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
                    new ProviderPropertySchema { Name = "IsAbstract", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsNamedTuple", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsProtocol", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsException", Type = "bool" },
                    new ProviderPropertySchema { Name = "HasSlots", Type = "bool" },
                ],
            },
            new()
            {
                Name = "PythonMethod",
                Base = "Method",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsStaticmethod", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsClassmethod", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsProperty", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsGenerator", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsAbstractMethod", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsDunder", Type = "bool" },
                ],
            },
            new()
            {
                Name = "PythonStatement",
                Base = "Statement",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsWith", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsAsyncWith", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsRaise", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsAssert", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsComprehension", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsGlobal", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsNonlocal", Type = "bool" },
                ],
            },
        };

        return new ProviderSchema { Types = types, Collections = baseSchema.Collections };
    }
}
