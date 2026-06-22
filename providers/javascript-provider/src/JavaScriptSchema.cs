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
                    new ProviderPropertySchema { Name = "IsAbstract", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsGeneric", Type = "bool" },
                    new ProviderPropertySchema { Name = "HasImplements", Type = "bool" },
                ],
            },
            new()
            {
                Name = "JavaScriptMethod",
                Base = "Method",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsGenerator", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsArrow", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsGetter", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsSetter", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsConstructor", Type = "bool" },
                ],
            },
            new()
            {
                Name = "JavaScriptStatement",
                Base = "Statement",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsForOf", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsForIn", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsThrow", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsAwait", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsTryCatch", Type = "bool" },
                ],
            },
        };

        return new ProviderSchema { Types = types, Collections = baseSchema.Collections };
    }
}
