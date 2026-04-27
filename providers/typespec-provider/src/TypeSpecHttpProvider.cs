using Cop.Core;

namespace TypeSpecProvider;

/// <summary>
/// DataProvider for HTTP protocol graph derived from TypeSpec.
/// Transforms raw TypeSpec AST through HTTP decorator interpretation.
/// Exposes HttpOperations, HttpServices with resolved verbs, paths, parameters.
/// Uses stride-based DataTables with shared UTF-8 string heap.
/// </summary>
public class TypeSpecHttpProvider : DataProvider
{
    public override DataFormat SupportedFormats => DataFormat.InMemoryDatabase;

    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();

    private static readonly ProviderSchema _schema = BuildSchema();

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                new() { Name = "TspDecorator", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Arguments", Collection = true },
                ]},
                new() { Name = "HttpParameter", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Type" },
                    new() { Name = "In" },
                    new() { Name = "Optional", Type = "bool" },
                    new() { Name = "Style", Optional = true },
                ]},
                new() { Name = "HttpHeader", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Type" },
                ]},
                new() { Name = "HttpResponse", Properties =
                [
                    new() { Name = "StatusCode" },
                    new() { Name = "Description", Optional = true },
                    new() { Name = "Body", Optional = true },
                    new() { Name = "Headers", Type = "HttpHeader", Collection = true },
                ]},
                new() { Name = "HttpOperation", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Verb" },
                    new() { Name = "Path" },
                    new() { Name = "UriTemplate" },
                    new() { Name = "Parameters", Type = "HttpParameter", Collection = true },
                    new() { Name = "Responses", Type = "HttpResponse", Collection = true },
                    new() { Name = "Interface", Optional = true },
                    new() { Name = "Decorators", Type = "TspDecorator", Collection = true },
                ]},
                new() { Name = "HttpService", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Namespace" },
                    new() { Name = "Operations", Type = "HttpOperation", Collection = true },
                    new() { Name = "Auth", Optional = true },
                ]},
            ],
            Collections =
            [
                new() { Name = "Operations", ItemType = "HttpOperation" },
                new() { Name = "Services", ItemType = "HttpService" },
            ]
        };
    }

    public override DataStore QueryData(ProviderQuery query)
    {
        var rawSpec = new TspSpec();
        if (query.RootPath is not null && Directory.Exists(query.RootPath))
            rawSpec = TspParser.ParseFiles(query.RootPath);

        var httpSpec = HttpTransformer.Transform(rawSpec);
        var requested = query.RequestedCollections;
        bool Want(string name) => requested is null || requested.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));

        var db = new DataStoreBuilder();

        // Child tables
        // TspDecorator: stride 2 (Name, Arguments-range)
        var decorators = db.AddTable("Decorators", "TspDecorator", 2);
        // TspDecorator.Arguments: stride 1
        var decArgs = db.AddTable("TspDecorator.Arguments", "string", 1);
        // HttpParameter: stride 5 (Name, Type, In, Optional, Style)
        var parameters = db.AddTable("Parameters", "HttpParameter", 5);
        // HttpHeader: stride 2 (Name, Type)
        var headers = db.AddTable("Headers", "HttpHeader", 2);
        // HttpResponse: stride 4 (StatusCode, Description, Body, Headers-range)
        var responses = db.AddTable("Responses", "HttpResponse", 4);
        // HttpOperation: stride 8 (Name, Verb, Path, UriTemplate, Parameters-range, Responses-range, Interface, Decorators-range)
        var operations = db.AddTable("Operations", "HttpOperation", 8);

        // Top-level tables
        var services = Want("Services") ? db.AddTable("Services", "HttpService", 4) : null;

        // Populate operations (used both top-level and as child of Services)
        if (Want("Operations"))
        {
            foreach (var o in httpSpec.Operations)
            {
                if (!MatchesFilter(query.Filter, "Name", o.Name, "Verb", o.Verb))
                    continue;
                AddOperation(operations, parameters, responses, headers, decorators, decArgs, o);
            }
        }

        // Services: Name(0), Namespace(1), Operations(2→Operations), Auth(3)
        if (services is not null)
        {
            foreach (var s in httpSpec.Services)
            {
                if (!MatchesFilter(query.Filter, "Name", s.Name, "Namespace", s.Namespace))
                    continue;
                int row = services.AddRow();
                services.SetString(row, 0, s.Name);
                services.SetString(row, 1, s.Namespace);
                int opStart = operations.Count;
                foreach (var o in s.Operations)
                    AddOperation(operations, parameters, responses, headers, decorators, decArgs, o);
                services.SetRange(row, 2, opStart, s.Operations.Count);
                services.SetString(row, 3, s.Auth);
            }
        }

        return db.Build();
    }

    private static void AddOperation(
        DataTableBuilder operations, DataTableBuilder parameters, DataTableBuilder responses,
        DataTableBuilder headers, DataTableBuilder decorators, DataTableBuilder decArgs,
        HttpOperation o)
    {
        int row = operations.AddRow();
        operations.SetString(row, 0, o.Name);
        operations.SetString(row, 1, o.Verb);
        operations.SetString(row, 2, o.Path);
        operations.SetString(row, 3, o.UriTemplate);

        // Parameters
        int paramStart = parameters.Count;
        foreach (var p in o.Parameters)
        {
            int pRow = parameters.AddRow();
            parameters.SetString(pRow, 0, p.Name);
            parameters.SetString(pRow, 1, p.Type);
            parameters.SetString(pRow, 2, p.In);
            parameters.SetBool(pRow, 3, p.Optional);
            parameters.SetString(pRow, 4, p.Style);
        }
        operations.SetRange(row, 4, paramStart, o.Parameters.Count);

        // Responses
        int respStart = responses.Count;
        foreach (var r in o.Responses)
        {
            int rRow = responses.AddRow();
            responses.SetString(rRow, 0, r.StatusCode);
            responses.SetString(rRow, 1, r.Description);
            responses.SetString(rRow, 2, r.Body);
            int hdrStart = headers.Count;
            foreach (var h in r.Headers)
            {
                int hRow = headers.AddRow();
                headers.SetString(hRow, 0, h.Name);
                headers.SetString(hRow, 1, h.Type);
            }
            responses.SetRange(rRow, 3, hdrStart, r.Headers.Count);
        }
        operations.SetRange(row, 5, respStart, o.Responses.Count);

        operations.SetString(row, 6, o.Interface);
        operations.SetRange(row, 7, AddDecorators(decorators, decArgs, o.Decorators));
    }

    private static (int Start, int Count) AddDecorators(
        DataTableBuilder decorators, DataTableBuilder decArgs, List<TspDecorator> items)
    {
        int start = decorators.Count;
        foreach (var d in items)
        {
            int row = decorators.AddRow();
            decorators.SetString(row, 0, d.Name);
            int argStart = decArgs.Count;
            foreach (var a in d.Arguments)
            {
                int aRow = decArgs.AddRow();
                decArgs.SetString(aRow, 0, a);
            }
            decorators.SetRange(row, 1, argStart, d.Arguments.Count);
        }
        return (start, items.Count);
    }

    private static bool MatchesFilter(FilterExpression? filter, string prop1, string? val1,
        string? prop2 = null, string? val2 = null)
    {
        if (filter is null) return true;
        return FilterEvaluator.Matches(filter, prop => prop switch
        {
            _ when prop == prop1 => (object?)val1,
            _ when prop == prop2 => (object?)val2,
            _ => null
        });
    }
}
