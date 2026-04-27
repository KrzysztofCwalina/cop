using Cop.Core;

namespace TypeSpecProvider;

/// <summary>
/// DataProvider for raw TypeSpec type graph.
/// Exposes Models, Operations, Interfaces, Enums, Unions, Scalars, Namespaces
/// as stride-based DataTables with shared UTF-8 string heap.
/// Supports RequestedCollections pruning and FilterEvaluator pushdown.
/// </summary>
public class TypeSpecRawProvider : DataProvider
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
                new() { Name = "TspProperty", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Type" },
                    new() { Name = "Optional", Type = "bool" },
                    new() { Name = "Default", Optional = true },
                    new() { Name = "Decorators", Type = "TspDecorator", Collection = true },
                ]},
                new() { Name = "TspModel", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Namespace", Optional = true },
                    new() { Name = "Properties", Type = "TspProperty", Collection = true },
                    new() { Name = "BaseModel", Optional = true },
                    new() { Name = "Decorators", Type = "TspDecorator", Collection = true },
                ]},
                new() { Name = "TspOperation", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Namespace", Optional = true },
                    new() { Name = "Interface", Optional = true },
                    new() { Name = "Parameters", Type = "TspProperty", Collection = true },
                    new() { Name = "ReturnType" },
                    new() { Name = "Decorators", Type = "TspDecorator", Collection = true },
                ]},
                new() { Name = "TspInterface", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Namespace", Optional = true },
                    new() { Name = "Operations", Type = "TspOperation", Collection = true },
                    new() { Name = "Decorators", Type = "TspDecorator", Collection = true },
                ]},
                new() { Name = "TspEnum", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Namespace", Optional = true },
                    new() { Name = "Members", Type = "TspEnumMember", Collection = true },
                    new() { Name = "Decorators", Type = "TspDecorator", Collection = true },
                ]},
                new() { Name = "TspEnumMember", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Value", Optional = true },
                    new() { Name = "Decorators", Type = "TspDecorator", Collection = true },
                ]},
                new() { Name = "TspUnion", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Namespace", Optional = true },
                    new() { Name = "Variants", Type = "TspUnionVariant", Collection = true },
                    new() { Name = "Decorators", Type = "TspDecorator", Collection = true },
                ]},
                new() { Name = "TspUnionVariant", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Type" },
                ]},
                new() { Name = "TspScalar", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Namespace", Optional = true },
                    new() { Name = "BaseScalar", Optional = true },
                    new() { Name = "Decorators", Type = "TspDecorator", Collection = true },
                ]},
                new() { Name = "TspNamespace", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "FullName" },
                    new() { Name = "Decorators", Type = "TspDecorator", Collection = true },
                ]},
            ],
            Collections =
            [
                new() { Name = "Models", ItemType = "TspModel" },
                new() { Name = "Operations", ItemType = "TspOperation" },
                new() { Name = "Interfaces", ItemType = "TspInterface" },
                new() { Name = "Enums", ItemType = "TspEnum" },
                new() { Name = "Unions", ItemType = "TspUnion" },
                new() { Name = "Scalars", ItemType = "TspScalar" },
                new() { Name = "Namespaces", ItemType = "TspNamespace" },
            ]
        };
    }

    public override DataStore QueryData(ProviderQuery query)
    {
        var spec = new TspSpec();
        if (query.RootPath is not null && Directory.Exists(query.RootPath))
            spec = TspParser.ParseFiles(query.RootPath);

        var requested = query.RequestedCollections;
        bool Want(string name) => requested is null || requested.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));

        var db = new DataStoreBuilder();

        // Child tables for string collections and object collections
        // TspDecorator: stride 2 (Name, Arguments-range)
        var decorators = db.AddTable("Decorators", "TspDecorator", 2);
        // TspDecorator.Arguments: stride 1 (string per row)
        var decArgs = db.AddTable("TspDecorator.Arguments", "string", 1);
        // TspProperty: stride 5 (Name, Type, Optional, Default, Decorators-range)
        var properties = db.AddTable("Properties", "TspProperty", 5);
        // TspEnumMember: stride 3 (Name, Value, Decorators-range)
        var enumMembers = db.AddTable("EnumMembers", "TspEnumMember", 3);
        // TspUnionVariant: stride 2 (Name, Type)
        var unionVariants = db.AddTable("UnionVariants", "TspUnionVariant", 2);
        // TspOperation (child table for Interface.Operations)
        var operations = db.AddTable("Operations", "TspOperation", 6);

        // Top-level collection tables
        var models = Want("Models") ? db.AddTable("Models", "TspModel", 5) : null;
        var interfaces = Want("Interfaces") ? db.AddTable("Interfaces", "TspInterface", 4) : null;
        var enums = Want("Enums") ? db.AddTable("Enums", "TspEnum", 4) : null;
        var unions = Want("Unions") ? db.AddTable("Unions", "TspUnion", 4) : null;
        var scalars = Want("Scalars") ? db.AddTable("Scalars", "TspScalar", 4) : null;
        var namespaces = Want("Namespaces") ? db.AddTable("Namespaces", "TspNamespace", 3) : null;

        // --- Populate ---

        // Models: Name(0), Namespace(1), Properties(2→Properties), BaseModel(3), Decorators(4→Decorators)
        if (models is not null)
        {
            foreach (var m in spec.Models)
            {
                if (!MatchesFilter(query.Filter, "Name", m.Name, "Namespace", m.Namespace))
                    continue;
                int row = models.AddRow();
                models.SetString(row, 0, m.Name);
                models.SetString(row, 1, m.Namespace);
                models.SetRange(row, 2, AddProperties(properties, decorators, decArgs, m.Properties));
                models.SetString(row, 3, m.BaseModel);
                models.SetRange(row, 4, AddDecorators(decorators, decArgs, m.Decorators));
            }
        }

        // Operations (top-level): Name(0), Namespace(1), Interface(2), Parameters(3→Properties), ReturnType(4), Decorators(5→Decorators)
        // Always populate since both top-level "Operations" and Interface.Operations share this table
        foreach (var o in spec.Operations)
        {
            if (Want("Operations") && !MatchesFilter(query.Filter, "Name", o.Name, "Namespace", o.Namespace))
                continue;
            int row = operations.AddRow();
            operations.SetString(row, 0, o.Name);
            operations.SetString(row, 1, o.Namespace);
            operations.SetString(row, 2, o.Interface);
            operations.SetRange(row, 3, AddProperties(properties, decorators, decArgs, o.Parameters));
            operations.SetString(row, 4, o.ReturnType);
            operations.SetRange(row, 5, AddDecorators(decorators, decArgs, o.Decorators));
        }

        // Interfaces: Name(0), Namespace(1), Operations(2→Operations), Decorators(3→Decorators)
        if (interfaces is not null)
        {
            foreach (var iface in spec.Interfaces)
            {
                if (!MatchesFilter(query.Filter, "Name", iface.Name, "Namespace", iface.Namespace))
                    continue;
                int row = interfaces.AddRow();
                interfaces.SetString(row, 0, iface.Name);
                interfaces.SetString(row, 1, iface.Namespace);
                // Add interface-local operations to the shared Operations table
                int opStart = operations.Count;
                foreach (var o in iface.Operations)
                {
                    int oRow = operations.AddRow();
                    operations.SetString(oRow, 0, o.Name);
                    operations.SetString(oRow, 1, o.Namespace);
                    operations.SetString(oRow, 2, o.Interface);
                    operations.SetRange(oRow, 3, AddProperties(properties, decorators, decArgs, o.Parameters));
                    operations.SetString(oRow, 4, o.ReturnType);
                    operations.SetRange(oRow, 5, AddDecorators(decorators, decArgs, o.Decorators));
                }
                interfaces.SetRange(row, 2, opStart, iface.Operations.Count);
                interfaces.SetRange(row, 3, AddDecorators(decorators, decArgs, iface.Decorators));
            }
        }

        // Enums: Name(0), Namespace(1), Members(2→EnumMembers), Decorators(3→Decorators)
        if (enums is not null)
        {
            foreach (var e in spec.Enums)
            {
                if (!MatchesFilter(query.Filter, "Name", e.Name, "Namespace", e.Namespace))
                    continue;
                int row = enums.AddRow();
                enums.SetString(row, 0, e.Name);
                enums.SetString(row, 1, e.Namespace);
                int memberStart = enumMembers.Count;
                foreach (var m in e.Members)
                {
                    int mRow = enumMembers.AddRow();
                    enumMembers.SetString(mRow, 0, m.Name);
                    enumMembers.SetString(mRow, 1, m.Value);
                    enumMembers.SetRange(mRow, 2, AddDecorators(decorators, decArgs, m.Decorators));
                }
                enums.SetRange(row, 2, memberStart, e.Members.Count);
                enums.SetRange(row, 3, AddDecorators(decorators, decArgs, e.Decorators));
            }
        }

        // Unions: Name(0), Namespace(1), Variants(2→UnionVariants), Decorators(3→Decorators)
        if (unions is not null)
        {
            foreach (var u in spec.Unions)
            {
                if (!MatchesFilter(query.Filter, "Name", u.Name, "Namespace", u.Namespace))
                    continue;
                int row = unions.AddRow();
                unions.SetString(row, 0, u.Name);
                unions.SetString(row, 1, u.Namespace);
                int varStart = unionVariants.Count;
                foreach (var v in u.Variants)
                {
                    int vRow = unionVariants.AddRow();
                    unionVariants.SetString(vRow, 0, v.Name);
                    unionVariants.SetString(vRow, 1, v.Type);
                }
                unions.SetRange(row, 2, varStart, u.Variants.Count);
                unions.SetRange(row, 3, AddDecorators(decorators, decArgs, u.Decorators));
            }
        }

        // Scalars: Name(0), Namespace(1), BaseScalar(2), Decorators(3→Decorators)
        if (scalars is not null)
        {
            foreach (var s in spec.Scalars)
            {
                if (!MatchesFilter(query.Filter, "Name", s.Name, "Namespace", s.Namespace))
                    continue;
                int row = scalars.AddRow();
                scalars.SetString(row, 0, s.Name);
                scalars.SetString(row, 1, s.Namespace);
                scalars.SetString(row, 2, s.BaseScalar);
                scalars.SetRange(row, 3, AddDecorators(decorators, decArgs, s.Decorators));
            }
        }

        // Namespaces: Name(0), FullName(1), Decorators(2→Decorators)
        if (namespaces is not null)
        {
            foreach (var n in spec.Namespaces)
            {
                if (!MatchesFilter(query.Filter, "Name", n.Name))
                    continue;
                int row = namespaces.AddRow();
                namespaces.SetString(row, 0, n.Name);
                namespaces.SetString(row, 1, n.FullName);
                namespaces.SetRange(row, 2, AddDecorators(decorators, decArgs, n.Decorators));
            }
        }

        return db.Build();
    }

    // --- Helper: add a list of TspDecorator rows, return (start, count) ---
    private static (int Start, int Count) AddDecorators(
        DataTableBuilder decorators, DataTableBuilder decArgs, List<TspDecorator> items)
    {
        int start = decorators.Count;
        foreach (var d in items)
        {
            int row = decorators.AddRow();
            decorators.SetString(row, 0, d.Name);
            // Arguments string collection
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

    // --- Helper: add a list of TspProperty rows, return (start, count) ---
    private static (int Start, int Count) AddProperties(
        DataTableBuilder properties, DataTableBuilder decorators, DataTableBuilder decArgs,
        List<TspProperty> items)
    {
        int start = properties.Count;
        foreach (var p in items)
        {
            int row = properties.AddRow();
            properties.SetString(row, 0, p.Name);
            properties.SetString(row, 1, p.Type);
            properties.SetBool(row, 2, p.Optional);
            properties.SetString(row, 3, p.Default);
            properties.SetRange(row, 4, AddDecorators(decorators, decArgs, p.Decorators));
        }
        return (start, items.Count);
    }

    /// <summary>
    /// Lightweight filter check using FilterEvaluator for top-level record filtering.
    /// Checks Name and optionally Namespace against the pushdown filter.
    /// </summary>
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
