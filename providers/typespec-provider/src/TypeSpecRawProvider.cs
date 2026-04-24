using System.Text.Json;
using Cop.Core;

namespace TypeSpecProvider;

/// <summary>
/// DataProvider for raw TypeSpec type graph.
/// Exposes Models, Operations, Interfaces, Enums, Unions, Scalars.
/// </summary>
public class TypeSpecRawProvider : DataProvider
{
    public override ReadOnlyMemory<byte> GetSchema()
    {
        var schema = new
        {
            types = new object[]
            {
                new {
                    name = "TspDecorator",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Arguments", type = "string", collection = true },
                    }
                },
                new {
                    name = "TspProperty",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Type", type = "string" },
                        new { name = "Optional", type = "bool" },
                        new { name = "Default", type = "string", optional = true },
                        new { name = "Decorators", type = "TspDecorator", collection = true },
                    }
                },
                new {
                    name = "TspModel",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Namespace", type = "string", optional = true },
                        new { name = "Properties", type = "TspProperty", collection = true },
                        new { name = "BaseModel", type = "string", optional = true },
                        new { name = "Decorators", type = "TspDecorator", collection = true },
                    }
                },
                new {
                    name = "TspOperation",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Namespace", type = "string", optional = true },
                        new { name = "Interface", type = "string", optional = true },
                        new { name = "Parameters", type = "TspProperty", collection = true },
                        new { name = "ReturnType", type = "string" },
                        new { name = "Decorators", type = "TspDecorator", collection = true },
                    }
                },
                new {
                    name = "TspInterface",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Namespace", type = "string", optional = true },
                        new { name = "Operations", type = "TspOperation", collection = true },
                        new { name = "Decorators", type = "TspDecorator", collection = true },
                    }
                },
                new {
                    name = "TspEnum",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Namespace", type = "string", optional = true },
                        new { name = "Members", type = "TspEnumMember", collection = true },
                        new { name = "Decorators", type = "TspDecorator", collection = true },
                    }
                },
                new {
                    name = "TspEnumMember",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Value", type = "string", optional = true },
                        new { name = "Decorators", type = "TspDecorator", collection = true },
                    }
                },
                new {
                    name = "TspUnion",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Namespace", type = "string", optional = true },
                        new { name = "Variants", type = "TspUnionVariant", collection = true },
                        new { name = "Decorators", type = "TspDecorator", collection = true },
                    }
                },
                new {
                    name = "TspUnionVariant",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Type", type = "string" },
                    }
                },
                new {
                    name = "TspScalar",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Namespace", type = "string", optional = true },
                        new { name = "BaseScalar", type = "string", optional = true },
                        new { name = "Decorators", type = "TspDecorator", collection = true },
                    }
                },
                new {
                    name = "TspNamespace",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "FullName", type = "string" },
                        new { name = "Decorators", type = "TspDecorator", collection = true },
                    }
                },
            },
            collections = new object[]
            {
                new { name = "Models", itemType = "TspModel" },
                new { name = "Operations", itemType = "TspOperation" },
                new { name = "Interfaces", itemType = "TspInterface" },
                new { name = "Enums", itemType = "TspEnum" },
                new { name = "Unions", itemType = "TspUnion" },
                new { name = "Scalars", itemType = "TspScalar" },
                new { name = "Namespaces", itemType = "TspNamespace" },
            }
        };

        return JsonSerializer.SerializeToUtf8Bytes(schema, JsonOptions);
    }

    public override byte[] Query(ProviderQuery query)
    {
        var spec = new TspSpec();

        if (query.RootPath is not null && Directory.Exists(query.RootPath))
        {
            spec = TspParser.ParseFiles(query.RootPath);
        }

        var result = new Dictionary<string, object>
        {
            ["Models"] = spec.Models.Select(ModelToDict).ToList(),
            ["Operations"] = spec.Operations.Select(OperationToDict).ToList(),
            ["Interfaces"] = spec.Interfaces.Select(InterfaceToDict).ToList(),
            ["Enums"] = spec.Enums.Select(EnumToDict).ToList(),
            ["Unions"] = spec.Unions.Select(UnionToDict).ToList(),
            ["Scalars"] = spec.Scalars.Select(ScalarToDict).ToList(),
            ["Namespaces"] = spec.Namespaces.Select(n => new Dictionary<string, object?>
            {
                ["Name"] = n.Name,
                ["FullName"] = n.FullName,
                ["Decorators"] = n.Decorators.Select(DecoratorToDict).ToList(),
            }).ToList(),
        };

        return JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
    }

    private static Dictionary<string, object?> ModelToDict(TspModel m) => new()
    {
        ["Name"] = m.Name,
        ["Namespace"] = m.Namespace,
        ["Properties"] = m.Properties.Select(PropertyToDict).ToList(),
        ["BaseModel"] = m.BaseModel,
        ["Decorators"] = m.Decorators.Select(DecoratorToDict).ToList(),
    };

    private static Dictionary<string, object?> OperationToDict(TspOperation o) => new()
    {
        ["Name"] = o.Name,
        ["Namespace"] = o.Namespace,
        ["Interface"] = o.Interface,
        ["Parameters"] = o.Parameters.Select(PropertyToDict).ToList(),
        ["ReturnType"] = o.ReturnType,
        ["Decorators"] = o.Decorators.Select(DecoratorToDict).ToList(),
    };

    private static Dictionary<string, object?> InterfaceToDict(TspInterface i) => new()
    {
        ["Name"] = i.Name,
        ["Namespace"] = i.Namespace,
        ["Operations"] = i.Operations.Select(OperationToDict).ToList(),
        ["Decorators"] = i.Decorators.Select(DecoratorToDict).ToList(),
    };

    private static Dictionary<string, object?> EnumToDict(TspEnum e) => new()
    {
        ["Name"] = e.Name,
        ["Namespace"] = e.Namespace,
        ["Members"] = e.Members.Select(m => new Dictionary<string, object?>
        {
            ["Name"] = m.Name,
            ["Value"] = m.Value,
            ["Decorators"] = m.Decorators.Select(DecoratorToDict).ToList(),
        }).ToList(),
        ["Decorators"] = e.Decorators.Select(DecoratorToDict).ToList(),
    };

    private static Dictionary<string, object?> UnionToDict(TspUnion u) => new()
    {
        ["Name"] = u.Name,
        ["Namespace"] = u.Namespace,
        ["Variants"] = u.Variants.Select(v => new Dictionary<string, object?>
        {
            ["Name"] = v.Name,
            ["Type"] = v.Type,
        }).ToList(),
        ["Decorators"] = u.Decorators.Select(DecoratorToDict).ToList(),
    };

    private static Dictionary<string, object?> ScalarToDict(TspScalar s) => new()
    {
        ["Name"] = s.Name,
        ["Namespace"] = s.Namespace,
        ["BaseScalar"] = s.BaseScalar,
        ["Decorators"] = s.Decorators.Select(DecoratorToDict).ToList(),
    };

    private static Dictionary<string, object?> PropertyToDict(TspProperty p) => new()
    {
        ["Name"] = p.Name,
        ["Type"] = p.Type,
        ["Optional"] = p.Optional,
        ["Default"] = p.Default,
        ["Decorators"] = p.Decorators.Select(DecoratorToDict).ToList(),
    };

    private static Dictionary<string, object?> DecoratorToDict(TspDecorator d) => new()
    {
        ["Name"] = d.Name,
        ["Arguments"] = d.Arguments,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null, // PascalCase to match schema
        WriteIndented = false,
    };
}
