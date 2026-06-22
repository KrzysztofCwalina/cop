using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Go-specific provider schema: the shared <see cref="CodeSchema"/> plus Go subtypes for
/// types, methods, and statements. The shared CodeSchema is left untouched.
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
                    new ProviderPropertySchema { Name = "IsTypeAlias", Type = "bool" },
                    new ProviderPropertySchema { Name = "HasStructTags", Type = "bool" },
                    new ProviderPropertySchema { Name = "HasUnionTypeSet", Type = "bool" },
                    new ProviderPropertySchema { Name = "HasUnderlyingTypeTerms", Type = "bool" },
                ],
            },
            new()
            {
                Name = "GoMethod",
                Base = "Method",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsPointerReceiver", Type = "bool" },
                    new ProviderPropertySchema { Name = "HasNamedReturns", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsVariadic", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsGeneric", Type = "bool" },
                ],
            },
            new()
            {
                Name = "GoStatement",
                Base = "Statement",
                Properties =
                [
                    new ProviderPropertySchema { Name = "IsDefer", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsGoroutine", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsSelect", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsRangeLoop", Type = "bool" },
                    new ProviderPropertySchema { Name = "IsTypeSwitch", Type = "bool" },
                ],
            },
        };

        return new ProviderSchema { Types = types, Collections = baseSchema.Collections };
    }
}
