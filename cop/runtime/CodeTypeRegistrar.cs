using Cop.Lang;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Registers CLR source model types (TypeDeclaration, MethodDeclaration, etc.)
/// as cop type accessors in the TypeRegistry. This is the ONLY place that maps
/// between CLR source model objects and cop type definitions from the code package.
/// </summary>
public static class CodeTypeRegistrar
{
    /// <summary>
    /// Registers all CLR type mappings, property accessors, and collection extractors
    /// for the code analysis source model.
    /// </summary>
    public static void Register(TypeRegistry registry)
    {
        RegisterTypeDescriptors(registry);
        RegisterClrTypeMappings(registry);
        RegisterPropertyAccessors(registry);
        RegisterCollectionExtractors(registry);
        RegisterMethodEvaluators(registry);
    }

    /// <summary>
    /// Creates TypeDescriptor entries for all code types so accessor registration works
    /// even without parsing code.cop. This is required for backward compatibility with
    /// .cop files that don't use 'import code'.
    /// </summary>
    private static void RegisterTypeDescriptors(TypeRegistry registry)
    {
        // Only register if not already loaded (e.g., via import code)
        if (!registry.HasType("Type"))
        {
            RegisterType(registry, "Type", [
                ("Name", "string", false, false), ("Kind", "string", false, false),
                ("Public", "bool", false, false), ("Sealed", "bool", false, false),
                ("Abstract", "bool", false, false), ("Static", "bool", false, false),
                ("BaseTypes", "string", false, true), ("Constructors", "Method", false, true),
                ("Methods", "Method", false, true), ("MethodNames", "string", false, true),
                ("NestedTypes", "Type", false, true),
                ("EnumValues", "string", false, true), ("Decorators", "string", false, true),
                ("Line", "int", false, false),
                ("File", "File", true, false),
                ("Source", "string", false, false),
            ]);
        }

        if (!registry.HasType("Method"))
        {
            RegisterType(registry, "Method", [
                ("Name", "string", false, false),
                ("Public", "bool", false, false), ("Protected", "bool", false, false),
                ("Private", "bool", false, false), ("Internal", "bool", false, false),
                ("Async", "bool", false, false), ("Static", "bool", false, false),
                ("Abstract", "bool", false, false), ("Virtual", "bool", false, false),
                ("Override", "bool", false, false),
                ("ReturnType", "TypeReference", true, false),
                ("Parameters", "Parameter", false, true),
                ("Statements", "Statement", false, true),
                ("Decorators", "string", false, true),
                ("Line", "int", false, false),
            ]);
        }

        if (!registry.HasType("Constructor"))
        {
            var ctorDesc = new TypeDescriptor("Constructor");
            ctorDesc.BaseType = registry.GetType("Method");
            registry.Register(ctorDesc);
        }

        if (!registry.HasType("Parameter"))
        {
            RegisterType(registry, "Parameter", [
                ("Name", "string", false, false),
                ("Type", "TypeReference", true, false),
                ("Variadic", "bool", false, false), ("Kwargs", "bool", false, false),
                ("Defaulted", "bool", false, false),
            ]);
        }

        if (!registry.HasType("TypeReference"))
        {
            RegisterType(registry, "TypeReference", [
                ("Name", "string", false, false), ("Namespace", "string", true, false),
                ("Generic", "bool", false, false),
                ("GenericArguments", "TypeReference", false, true),
                ("Length", "int", false, false),
            ]);
            registry.GetType("TypeReference").TextConverter = o => ((TypeReference)o).OriginalText;
        }

        if (!registry.HasType("Statement"))
        {
            RegisterType(registry, "Statement", [
                ("Kind", "string", false, false), ("Keywords", "string", false, true),
                ("TypeName", "string", true, false), ("MemberName", "string", true, false),
                ("Arguments", "string", false, true),
                ("Line", "int", false, false), ("InMethod", "bool", false, false),
                ("Rethrows", "bool", false, false),
                ("Generic", "bool", false, false),
                ("ErrorHandler", "bool", false, false),
                ("File", "File", true, false),
                ("Source", "string", false, false),
            ]);
        }

        if (!registry.HasType("Line"))
        {
            RegisterType(registry, "Line", [
                ("Text", "string", false, false), ("Number", "int", false, false),
                ("File", "File", true, false), ("Source", "string", false, false),
            ]);
            registry.GetType("Line").TextConverter = o => ((LineInfo)o).Text;
        }

        if (!registry.HasType("File"))
        {
            RegisterType(registry, "File", [
                ("Path", "string", false, false), ("Language", "string", true, false),
                ("Namespace", "string", true, false),
                ("Usings", "string", false, true), ("Types", "Type", false, true),
            ]);
        }

        // Register built-in collections
        if (!registry.HasCollection("Types"))
            registry.RegisterCollection(new CollectionDeclaration("Types", "Type", 0));
        if (!registry.HasCollection("Statements"))
            registry.RegisterCollection(new CollectionDeclaration("Statements", "Statement", 0));
        if (!registry.HasCollection("Lines"))
            registry.RegisterCollection(new CollectionDeclaration("Lines", "Line", 0));
        if (!registry.HasCollection("Files"))
            registry.RegisterCollection(new CollectionDeclaration("Files", "File", 0));
        if (!registry.HasCollection("Members"))
            registry.RegisterCollection(new CollectionDeclaration("Members", "Member", 0));

        if (!registry.HasType("Member"))
        {
            RegisterType(registry, "Member", [
                ("Name", "string", false, false),
                ("DeclaringType", "string", false, false),
                ("Line", "int", false, false),
            ]);
        }
    }

    private static void RegisterType(TypeRegistry registry, string name,
        List<(string Name, string TypeName, bool IsOptional, bool IsCollection)> properties)
    {
        var desc = new TypeDescriptor(name);
        foreach (var (propName, typeName, isOptional, isCollection) in properties)
            desc.Properties[propName] = new PropertyDescriptor(propName, typeName, isOptional, isCollection);
        registry.Register(desc);
    }

    private static void RegisterClrTypeMappings(TypeRegistry registry)
    {
        registry.RegisterClrType(typeof(TypeDeclaration), "Type");
        registry.RegisterClrType(typeof(MethodDeclaration), "Method");
        registry.RegisterClrType(typeof(ParameterDeclaration), "Parameter");
        registry.RegisterClrType(typeof(TypeReference), "TypeReference");
        registry.RegisterClrType(typeof(StatementInfo), "Statement");
        registry.RegisterClrType(typeof(LineInfo), "Line");
        registry.RegisterClrType(typeof(SourceFile), "File");
        registry.RegisterClrType(typeof(MemberInfo), "Member");
    }

    private static void RegisterPropertyAccessors(TypeRegistry registry)
    {
        registry.RegisterAccessors("Type", new()
        {
            ["Name"] = o => ((TypeDeclaration)o).Name,
            ["Kind"] = o => ((TypeDeclaration)o).Kind.ToString(),
            ["Public"] = o => (object)((TypeDeclaration)o).IsPublic,
            ["Sealed"] = o => (object)((TypeDeclaration)o).IsSealed,
            ["Abstract"] = o => (object)((TypeDeclaration)o).IsAbstract,
            ["Static"] = o => (object)((TypeDeclaration)o).IsStatic,
            ["Constructors"] = o => (object)((TypeDeclaration)o).Constructors,
            ["Methods"] = o => (object)((TypeDeclaration)o).Methods,
            ["NestedTypes"] = o => (object)((TypeDeclaration)o).NestedTypes,
            ["EnumValues"] = o => (object)((TypeDeclaration)o).EnumValues,
            ["BaseTypes"] = o => (object)((TypeDeclaration)o).BaseTypes,
            ["Decorators"] = o => (object)((TypeDeclaration)o).Decorators,
            ["Line"] = o => (object)((TypeDeclaration)o).Line,
            ["MethodNames"] = o => (object)((TypeDeclaration)o).Methods.Select(m => m.Name).ToList(),
            ["File"] = o => ((TypeDeclaration)o).File,
            ["Source"] = o => ((TypeDeclaration)o).Source,
        });

        registry.RegisterAccessors("Method", new()
        {
            ["Name"] = o => ((MethodDeclaration)o).Name,
            ["Public"] = o => (object)((MethodDeclaration)o).IsPublic,
            ["Protected"] = o => (object)((MethodDeclaration)o).IsProtected,
            ["Private"] = o => (object)((MethodDeclaration)o).IsPrivate,
            ["Internal"] = o => (object)((MethodDeclaration)o).IsInternal,
            ["Async"] = o => (object)((MethodDeclaration)o).IsAsync,
            ["Static"] = o => (object)((MethodDeclaration)o).IsStatic,
            ["Abstract"] = o => (object)((MethodDeclaration)o).IsAbstract,
            ["Virtual"] = o => (object)((MethodDeclaration)o).IsVirtual,
            ["Override"] = o => (object)((MethodDeclaration)o).IsOverride,
            ["Parameters"] = o => (object)((MethodDeclaration)o).Parameters,
            ["Statements"] = o => (object)((MethodDeclaration)o).Statements,
            ["Decorators"] = o => (object)((MethodDeclaration)o).Decorators,
            ["ReturnType"] = o => (object?)((MethodDeclaration)o).ReturnType,
            ["Line"] = o => (object)((MethodDeclaration)o).Line,
        });

        registry.RegisterAccessors("Parameter", new()
        {
            ["Name"] = o => ((ParameterDeclaration)o).Name,
            ["Type"] = o => (object?)((ParameterDeclaration)o).Type,
            ["Variadic"] = o => (object)((ParameterDeclaration)o).IsVariadic,
            ["Kwargs"] = o => (object)((ParameterDeclaration)o).IsKwargs,
            ["Defaulted"] = o => (object)((ParameterDeclaration)o).HasDefaultValue,
        });

        registry.RegisterAccessors("TypeReference", new()
        {
            ["Name"] = o => ((TypeReference)o).Name,
            ["Namespace"] = o => ((TypeReference)o).Namespace,
            ["Generic"] = o => (object)((TypeReference)o).IsGeneric,
            ["GenericArguments"] = o => (object)((TypeReference)o).GenericArguments,
            ["Length"] = o => (object)((TypeReference)o).OriginalText.Length,
        });

        registry.RegisterAccessors("Statement", new()
        {
            ["Kind"] = o => ((StatementInfo)o).Kind,
            ["Keywords"] = o => (object)((StatementInfo)o).Keywords,
            ["TypeName"] = o => ((StatementInfo)o).TypeName,
            ["MemberName"] = o => ((StatementInfo)o).MemberName,
            ["Arguments"] = o => (object)((StatementInfo)o).Arguments,
            ["Line"] = o => (object)((StatementInfo)o).Line,
            ["InMethod"] = o => (object)((StatementInfo)o).IsInMethod,
            ["Rethrows"] = o => (object)((StatementInfo)o).HasRethrow,
            ["Generic"] = o => (object)((StatementInfo)o).IsGenericErrorHandler,
            ["ErrorHandler"] = o => (object)((StatementInfo)o).IsErrorHandler,
            ["File"] = o => ((StatementInfo)o).File,
            ["Source"] = o => ((StatementInfo)o).Source,
        });

        registry.RegisterAccessors("Line", new()
        {
            ["Text"] = o => ((LineInfo)o).Text,
            ["Number"] = o => (object)((LineInfo)o).Number,
            ["File"] = o => ((LineInfo)o).File,
            ["Source"] = o => ((LineInfo)o).Source,
        });

        registry.RegisterAccessors("File", new()
        {
            ["Path"] = o => ((SourceFile)o).Path,
            ["Language"] = o => ((SourceFile)o).Language,
            ["Namespace"] = o => ((SourceFile)o).Namespace,
            ["Usings"] = o => (object)((SourceFile)o).Usings,
            ["Types"] = o => (object)((SourceFile)o).Types,
        });

        registry.RegisterAccessors("Member", new()
        {
            ["Name"] = o => ((MemberInfo)o).Name,
            ["DeclaringType"] = o => ((MemberInfo)o).DeclaringType,
            ["Line"] = o => (object)((MemberInfo)o).Line,
        });
    }

    private static void RegisterCollectionExtractors(TypeRegistry registry)
    {
        registry.RegisterCollectionExtractor("Types",
            doc => doc.As<SourceFile>().Types.Cast<object>().ToList());

        registry.RegisterCollectionExtractor("Statements",
            doc => doc.As<SourceFile>().Statements.Cast<object>().ToList());

        registry.RegisterCollectionExtractor("Lines",
            doc => {
                var file = doc.As<SourceFile>();
                return file.Lines.Select((text, i) => (object)new LineInfo(text, i + 1) { File = file }).ToList();
            });

        registry.RegisterCollectionExtractor("Files",
            doc => [(object)doc.As<SourceFile>()]);

        registry.RegisterCollectionExtractor("Members",
            doc => doc.As<SourceFile>().Types.SelectMany(t =>
                t.Methods.Select(m => (object)new MemberInfo(m.Name, t.Name, m.Line))
            ).ToList());
    }

    private static void RegisterMethodEvaluators(TypeRegistry registry)
    {
        registry.RegisterMethodEvaluator("Type", "inheritsFrom",
            (target, args) => ((TypeDeclaration)target).InheritsFrom(args[0]?.ToString() ?? ""));
    }
}
