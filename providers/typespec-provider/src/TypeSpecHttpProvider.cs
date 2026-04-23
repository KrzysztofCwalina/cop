using System.Text.Json;
using Cop.Core;

namespace TypeSpecProvider;

/// <summary>
/// CopProvider for HTTP protocol graph derived from TypeSpec.
/// Transforms raw TypeSpec AST through HTTP decorator interpretation.
/// Exposes HttpOperations, HttpServices with resolved verbs, paths, parameters.
/// </summary>
public class TypeSpecHttpProvider : CopProvider
{
    public override byte[] GetSchema()
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
                    name = "HttpParameter",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Type", type = "string" },
                        new { name = "In", type = "string" },
                        new { name = "Optional", type = "bool" },
                        new { name = "Style", type = "string", optional = true },
                    }
                },
                new {
                    name = "HttpHeader",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Type", type = "string" },
                    }
                },
                new {
                    name = "HttpResponse",
                    properties = new object[]
                    {
                        new { name = "StatusCode", type = "string" },
                        new { name = "Description", type = "string", optional = true },
                        new { name = "Body", type = "string", optional = true },
                        new { name = "Headers", type = "HttpHeader", collection = true },
                    }
                },
                new {
                    name = "HttpOperation",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Verb", type = "string" },
                        new { name = "Path", type = "string" },
                        new { name = "UriTemplate", type = "string" },
                        new { name = "Parameters", type = "HttpParameter", collection = true },
                        new { name = "Responses", type = "HttpResponse", collection = true },
                        new { name = "Interface", type = "string", optional = true },
                        new { name = "Decorators", type = "TspDecorator", collection = true },
                    }
                },
                new {
                    name = "HttpService",
                    properties = new object[]
                    {
                        new { name = "Name", type = "string" },
                        new { name = "Namespace", type = "string" },
                        new { name = "Operations", type = "HttpOperation", collection = true },
                        new { name = "Auth", type = "string", optional = true },
                    }
                },
            },
            collections = new object[]
            {
                new { name = "Operations", itemType = "HttpOperation" },
                new { name = "Services", itemType = "HttpService" },
            }
        };

        return JsonSerializer.SerializeToUtf8Bytes(schema, JsonOptions);
    }

    public override byte[] QueryJson(ProviderQuery query)
    {
        var rawSpec = new TspSpec();

        if (query.CodebasePath is not null && Directory.Exists(query.CodebasePath))
        {
            rawSpec = TspParser.ParseFiles(query.CodebasePath);
        }

        var httpSpec = HttpTransformer.Transform(rawSpec);

        var result = new Dictionary<string, object>
        {
            ["Operations"] = httpSpec.Operations.Select(OpToDict).ToList(),
            ["Services"] = httpSpec.Services.Select(SvcToDict).ToList(),
        };

        return JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
    }

    private static Dictionary<string, object?> OpToDict(HttpOperation o) => new()
    {
        ["Name"] = o.Name,
        ["Verb"] = o.Verb,
        ["Path"] = o.Path,
        ["UriTemplate"] = o.UriTemplate,
        ["Parameters"] = o.Parameters.Select(ParamToDict).ToList(),
        ["Responses"] = o.Responses.Select(RespToDict).ToList(),
        ["Interface"] = o.Interface,
        ["Decorators"] = o.Decorators.Select(DecToDict).ToList(),
    };

    private static Dictionary<string, object?> SvcToDict(HttpService s) => new()
    {
        ["Name"] = s.Name,
        ["Namespace"] = s.Namespace,
        ["Operations"] = s.Operations.Select(OpToDict).ToList(),
        ["Auth"] = s.Auth,
    };

    private static Dictionary<string, object?> ParamToDict(HttpParameter p) => new()
    {
        ["Name"] = p.Name,
        ["Type"] = p.Type,
        ["In"] = p.In,
        ["Optional"] = p.Optional,
        ["Style"] = p.Style,
    };

    private static Dictionary<string, object?> RespToDict(HttpResponse r) => new()
    {
        ["StatusCode"] = r.StatusCode,
        ["Description"] = r.Description,
        ["Body"] = r.Body,
        ["Headers"] = r.Headers.Select(h => new Dictionary<string, object?>
        {
            ["Name"] = h.Name,
            ["Type"] = h.Type,
        }).ToList(),
    };

    private static Dictionary<string, object?> DecToDict(TspDecorator d) => new()
    {
        ["Name"] = d.Name,
        ["Arguments"] = d.Arguments,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };
}
