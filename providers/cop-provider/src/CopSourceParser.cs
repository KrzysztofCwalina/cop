using Cop.Lang;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Source parser for .cop files. Reuses the existing ScriptParser to parse
/// .cop syntax, then maps the AST to the shared code source model.
/// </summary>
public class CopSourceParser : ISourceParser
{
    public IReadOnlyList<string> Extensions => [".cop"];
    public string Language => "cop";

    public SourceFile? Parse(string filePath, string sourceText)
    {
        ScriptFile script;
        try
        {
            script = ScriptParser.Parse(sourceText, filePath);
        }
        catch (ParseException)
        {
            // Return a minimal source file so Lines/Files still work
            return new SourceFile(filePath, "cop", [], [], sourceText);
        }

        var types = new List<TypeDeclaration>();
        var statements = new List<StatementInfo>();
        var usings = new List<string>(script.Imports);

        // Map type definitions → TypeDeclaration
        foreach (var td in script.TypeDefinitions)
        {
            var modifiers = td.IsExported ? Modifier.Public : Modifier.None;
            var fields = td.Properties.Select(p =>
                new FieldDeclaration(p.Name, MakeTypeRef(p.TypeName, p.IsCollection), Modifier.Public, p.Line))
                .ToList();
            var baseTypes = td.BaseType is not null ? new List<string> { td.BaseType } : [];

            types.Add(new TypeDeclaration(td.Name, TypeKind.Class, modifiers,
                baseTypes, [], [], [], [], [], td.Line)
            {
                Fields = fields
            });
        }

        // Map flags definitions → TypeDeclaration (Enum)
        if (script.FlagsDefinitions is not null)
        {
            foreach (var fd in script.FlagsDefinitions)
            {
                var modifiers = fd.IsExported ? Modifier.Public : Modifier.None;
                types.Add(new TypeDeclaration(fd.Name, TypeKind.Enum, modifiers,
                    [], [], [], [], [], fd.Members, fd.Line));
            }
        }

        // Map enum definitions → TypeDeclaration (Enum)
        if (script.EnumDefinitions is not null)
        {
            foreach (var ed in script.EnumDefinitions)
            {
                var modifiers = ed.IsExported ? Modifier.Public : Modifier.None;
                types.Add(new TypeDeclaration(ed.Name, TypeKind.Enum, modifiers,
                    [], [], [], [], [], ed.Members, ed.Line));
            }
        }

        // Map predicates → MethodDeclaration (return type = bool)
        foreach (var pd in script.Predicates)
        {
            var modifiers = pd.IsExported ? Modifier.Public : Modifier.None;
            var param = new ParameterDeclaration(
                pd.ParameterType.ToLowerInvariant(), MakeTypeRef(pd.ParameterType), false, false, false, pd.Line);
            var retType = new TypeReference("bool", null, [], "bool");

            statements.Add(new StatementInfo("declaration", ["predicate"],
                pd.ParameterType, pd.Name, [], pd.Line, false));
        }

        // Map functions → MethodDeclaration
        foreach (var fn in script.Functions)
        {
            var modifiers = fn.IsExported ? Modifier.Public : Modifier.None;
            var retType = MakeTypeRef(fn.ReturnType);
            var parameters = fn.Parameters.Select(p =>
                new ParameterDeclaration(p.Name, MakeTypeRef(p.TypeName), false, false, false, fn.Line)).ToList();
            // Add the implicit input parameter
            parameters.Insert(0, new ParameterDeclaration(
                fn.InputType.ToLowerInvariant(), MakeTypeRef(fn.InputType), false, false, false, fn.Line));

            statements.Add(new StatementInfo("declaration", ["function"],
                fn.ReturnType, fn.Name, [], fn.Line, false));
        }

        // Map import statements
        foreach (var imp in script.Imports)
        {
            statements.Add(new StatementInfo("import", [], null, imp, [], 0, false));
        }

        // Map let declarations
        foreach (var let in script.LetDeclarations)
        {
            var keywords = new List<string> { "let" };
            if (let.IsExported) keywords.Add("export");
            statements.Add(new StatementInfo("declaration", keywords,
                null, let.Name, [], let.Line, false));
        }

        // Map commands → statements
        foreach (var cmd in script.Commands)
        {
            statements.Add(new StatementInfo("command", [],
                null, cmd.Name, [], cmd.Line, false));
        }

        return new SourceFile(filePath, "cop", types, statements, sourceText)
        {
            Usings = usings
        };
    }

    private static TypeReference MakeTypeRef(string typeName, bool isCollection = false)
    {
        if (isCollection)
        {
            var inner = new TypeReference(typeName, null, [], typeName);
            return new TypeReference("List", null, [inner], $"[{typeName}]");
        }
        return new TypeReference(typeName, null, [], typeName);
    }
}
