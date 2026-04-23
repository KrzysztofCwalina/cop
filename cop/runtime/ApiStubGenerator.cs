using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Generates a C# stub listing from TypeDeclarations.
/// Output format matches Azure SDK GenAPI-style stub files:
/// namespace blocks, partial types, { throw null; } bodies, proper indentation.
/// </summary>
public static class ApiStubGenerator
{
    /// <summary>
    /// Generate a full C# stub listing from source files.
    /// Groups public types by namespace, sorts alphabetically.
    /// </summary>
    public static string Generate(IEnumerable<SourceFile> files)
    {
        // Collect all public types with their namespace
        var typesByNamespace = new SortedDictionary<string, List<TypeDeclaration>>();

        foreach (var file in files)
        {
            var ns = file.Namespace ?? "";
            foreach (var type in file.Types)
            {
                if (!type.IsPublic) continue;
                if (!typesByNamespace.TryGetValue(ns, out var list))
                {
                    list = [];
                    typesByNamespace[ns] = list;
                }
                list.Add(type);
            }
        }

        var sb = new System.Text.StringBuilder();
        var first = true;

        foreach (var (ns, types) in typesByNamespace)
        {
            if (!first) sb.AppendLine();
            first = false;

            types.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

            if (!string.IsNullOrEmpty(ns))
            {
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");
                foreach (var type in types)
                    WriteType(sb, type, indent: 1);
                sb.AppendLine("}");
            }
            else
            {
                foreach (var type in types)
                    WriteType(sb, type, indent: 0);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generate a stub listing from TypeDeclarations directly (e.g., from a DLL).
    /// </summary>
    public static string Generate(IEnumerable<TypeDeclaration> types, string? defaultNamespace = null)
    {
        var fakeFile = new SourceFile("", "csharp", types.ToList(), [], "")
        { Namespace = defaultNamespace };
        return Generate([fakeFile]);
    }

    private static void WriteType(System.Text.StringBuilder sb, TypeDeclaration type, int indent)
    {
        var pad = new string(' ', indent * 4);
        var memberPad = new string(' ', (indent + 1) * 4);

        // Type declaration line
        var entry = ApiEntry.ForType(type);
        sb.AppendLine($"{pad}{entry.StubLine}");
        sb.AppendLine($"{pad}{{");

        if (type.Kind == TypeKind.Enum)
        {
            foreach (var value in type.EnumValues)
                sb.AppendLine($"{memberPad}{value},");
        }
        else
        {
            var members = CollectMembers(type);
            foreach (var member in members)
                sb.AppendLine($"{memberPad}{member}");

            // Nested types
            foreach (var nested in type.NestedTypes.Where(n => n.IsPublic).OrderBy(n => n.Name))
                WriteType(sb, nested, indent + 1);
        }

        sb.AppendLine($"{pad}}}");
    }

    private static List<string> CollectMembers(TypeDeclaration type)
    {
        var members = new List<(string SortKey, string StubLine)>();

        // Fields (sorted by name)
        foreach (var field in type.Fields.Where(f => f.IsPublic).OrderBy(f => f.Name))
            members.Add(($"0_field_{field.Name}", ApiEntry.ForField(type, field).StubLine));

        // Constructors (sorted by parameter count)
        foreach (var ctor in type.Constructors.Where(c => c.IsPublic || c.IsProtected)
            .OrderBy(c => c.Parameters.Count))
            members.Add(($"1_ctor_{ctor.Parameters.Count}", ApiEntry.ForConstructor(type, ctor).StubLine));

        // Properties (sorted by name)
        foreach (var prop in type.Properties.Where(p => p.IsPublic || p.IsProtected).OrderBy(p => p.Name))
            members.Add(($"2_prop_{prop.Name}", ApiEntry.ForProperty(type, prop).StubLine));

        // Events (sorted by name)
        foreach (var evt in type.Events.Where(e => e.IsPublic || e.IsProtected).OrderBy(e => e.Name))
            members.Add(($"3_event_{evt.Name}", ApiEntry.ForEvent(type, evt).StubLine));

        // Methods (sorted by name, then parameter count)
        foreach (var method in type.Methods.Where(m => m.IsPublic || m.IsProtected)
            .OrderBy(m => m.Name).ThenBy(m => m.Parameters.Count))
            members.Add(($"4_method_{method.Name}_{method.Parameters.Count}", ApiEntry.ForMethod(type, method).StubLine));

        return members.Select(m => m.StubLine).ToList();
    }
}
