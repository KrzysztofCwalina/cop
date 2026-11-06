// Generates C# declarations from canonical API entries.

using System.Text;
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
        SortedDictionary<string, List<TypeDeclaration>> typesByNamespace = new SortedDictionary<string, List<TypeDeclaration>>();

        foreach (SourceFile file in files)
        {
            string ns = file.Namespace ?? string.Empty;
            foreach (TypeDeclaration type in file.Types)
            {
                if (!type.IsPublic)
                {
                    continue;
                }

                if (!typesByNamespace.TryGetValue(ns, out List<TypeDeclaration>? list))
                {
                    list = [];
                    typesByNamespace[ns] = list;
                }
                list.Add(type);
            }
        }

        StringBuilder sb = new System.Text.StringBuilder();
        bool first = true;

        foreach ((string? ns, List<TypeDeclaration>? types) in typesByNamespace)
        {
            if (!first)
            {
                sb.AppendLine();
            }

            first = false;

            types.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

            if (!string.IsNullOrEmpty(ns))
            {
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");
                foreach (TypeDeclaration type in types)
                {
                    WriteType(sb, type, indent: 1);
                }

                sb.AppendLine("}");
            }
            else
            {
                foreach (TypeDeclaration type in types)
                {
                    WriteType(sb, type, indent: 0);
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generate a stub listing from TypeDeclarations directly (e.g., from a DLL).
    /// </summary>
    public static string Generate(IEnumerable<TypeDeclaration> types, string? defaultNamespace = null)
    {
        SourceFile fakeFile = new SourceFile(string.Empty, "csharp", types.ToList(), [], string.Empty)
        { Namespace = defaultNamespace };
        return Generate([fakeFile]);
    }

    private static void WriteType(System.Text.StringBuilder sb, TypeDeclaration type, int indent)
    {
        string pad = new string(' ', indent * 4);
        string memberPad = new string(' ', (indent + 1) * 4);

        // Type declaration line
        ApiEntry entry = ApiEntry.ForType(type);
        sb.AppendLine($"{pad}{entry.ApiAsText}");
        sb.AppendLine($"{pad}{{");

        if (type.Kind == TypeKind.Enum)
        {
            foreach (string value in type.EnumValues)
            {
                sb.AppendLine($"{memberPad}{value},");
            }
        }
        else
        {
            List<string> members = CollectMembers(type);
            foreach (string member in members)
            {
                sb.AppendLine($"{memberPad}{member}");
            }

            // Nested types
            foreach (TypeDeclaration? nested in type.NestedTypes.Where(n => n.IsPublic).OrderBy(n => n.Name))
            {
                WriteType(sb, nested, indent + 1);
            }
        }

        sb.AppendLine($"{pad}}}");
    }

    private static List<string> CollectMembers(TypeDeclaration type)
    {
        List<(string SortKey, string ApiAsText)> members = new List<(string SortKey, string ApiAsText)>();

        // Fields (sorted by name)
        foreach (FieldDeclaration? field in type.Fields.Where(f => f.IsPublic).OrderBy(f => f.Name))
        {
            members.Add(($"0_field_{field.Name}", ApiEntry.ForField(type, field).ApiAsText));
        }

        // Constructors (sorted by parameter count)
        foreach (MethodDeclaration? ctor in type.Constructors.Where(c => c.IsPublic || c.IsProtected)
            .OrderBy(c => c.Parameters.Count))
        {
            members.Add(($"1_ctor_{ctor.Parameters.Count}", ApiEntry.ForConstructor(type, ctor).ApiAsText));
        }

        // Properties (sorted by name)
        foreach (PropertyDeclaration? prop in type.Properties.Where(p => p.IsPublic || p.IsProtected).OrderBy(p => p.Name))
        {
            members.Add(($"2_prop_{prop.Name}", ApiEntry.ForProperty(type, prop).ApiAsText));
        }

        // Events (sorted by name)
        foreach (EventDeclaration? evt in type.Events.Where(e => e.IsPublic || e.IsProtected).OrderBy(e => e.Name))
        {
            members.Add(($"3_event_{evt.Name}", ApiEntry.ForEvent(type, evt).ApiAsText));
        }

        // Methods (sorted by name, then parameter count)
        foreach (MethodDeclaration? method in type.Methods.Where(m => m.IsPublic || m.IsProtected)
            .OrderBy(m => m.Name).ThenBy(m => m.Parameters.Count))
        {
            members.Add(($"4_method_{method.Name}_{method.Parameters.Count}", ApiEntry.ForMethod(type, method).ApiAsText));
        }

        return members.Select(m => m.ApiAsText).ToList();
    }
}
