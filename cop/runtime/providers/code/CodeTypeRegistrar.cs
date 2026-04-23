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
                ("Documented", "bool", false, false),
                ("Fields", "Field", false, true),
                ("Properties", "Property", false, true),
                ("Events", "Event", false, true),
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
                ("Documented", "bool", false, false),
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
                ("DefaultValue", "string", true, false),
            ]);
        }

        if (!registry.HasType("Field"))
        {
            RegisterType(registry, "Field", [
                ("Name", "string", false, false),
                ("Type", "TypeReference", true, false),
                ("Public", "bool", false, false), ("Private", "bool", false, false),
                ("Protected", "bool", false, false), ("Internal", "bool", false, false),
                ("Static", "bool", false, false), ("Readonly", "bool", false, false),
                ("Const", "bool", false, false),
                ("Line", "int", false, false),
            ]);
        }

        if (!registry.HasType("Property"))
        {
            RegisterType(registry, "Property", [
                ("Name", "string", false, false),
                ("Type", "TypeReference", true, false),
                ("Public", "bool", false, false), ("Protected", "bool", false, false),
                ("Private", "bool", false, false), ("Internal", "bool", false, false),
                ("Static", "bool", false, false), ("Abstract", "bool", false, false),
                ("Virtual", "bool", false, false), ("Override", "bool", false, false),
                ("HasGetter", "bool", false, false), ("HasSetter", "bool", false, false),
                ("Documented", "bool", false, false),
                ("Line", "int", false, false),
            ]);
        }

        if (!registry.HasType("Event"))
        {
            RegisterType(registry, "Event", [
                ("Name", "string", false, false),
                ("Type", "TypeReference", true, false),
                ("Public", "bool", false, false), ("Protected", "bool", false, false),
                ("Private", "bool", false, false), ("Internal", "bool", false, false),
                ("Static", "bool", false, false),
                ("Line", "int", false, false),
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
                ("Method", "Method", true, false),
                ("Parent", "Statement", true, false),
                ("Children", "Statement", false, true),
                ("Ancestors", "Statement", false, true),
                ("Condition", "string", true, false),
                ("Expression", "string", true, false),
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

        if (!registry.HasType("Api"))
        {
            RegisterType(registry, "Api", [
                ("Kind", "string", false, false),
                ("TypeName", "string", false, false),
                ("MemberName", "string", false, false),
                ("Signature", "string", false, false),
                ("ApiAsText", "string", false, false),
                ("Line", "int", false, false),
                ("File", "File", true, false),
                ("Source", "string", false, false),
            ]);
            registry.GetType("Api").TextConverter = o => ((ApiEntry)o).Signature;
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
        if (!registry.HasCollection("Api"))
            registry.RegisterCollection(new CollectionDeclaration("Api", "Api", 0));

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
        registry.RegisterClrType(typeof(ApiEntry), "Api");
        registry.RegisterClrType(typeof(FieldDeclaration), "Field");
        registry.RegisterClrType(typeof(PropertyDeclaration), "Property");
        registry.RegisterClrType(typeof(EventDeclaration), "Event");
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
            ["Documented"] = o => (object)((TypeDeclaration)o).HasDocComment,
            ["Fields"] = o => (object)((TypeDeclaration)o).Fields,
            ["Properties"] = o => (object)((TypeDeclaration)o).Properties,
            ["Events"] = o => (object)((TypeDeclaration)o).Events,
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
            ["Documented"] = o => (object)((MethodDeclaration)o).HasDocComment,
        });

        registry.RegisterAccessors("Parameter", new()
        {
            ["Name"] = o => ((ParameterDeclaration)o).Name,
            ["Type"] = o => (object?)((ParameterDeclaration)o).Type,
            ["Variadic"] = o => (object)((ParameterDeclaration)o).IsVariadic,
            ["Kwargs"] = o => (object)((ParameterDeclaration)o).IsKwargs,
            ["Defaulted"] = o => (object)((ParameterDeclaration)o).HasDefaultValue,
            ["DefaultValue"] = o => ((ParameterDeclaration)o).DefaultValueText,
        });

        registry.RegisterAccessors("Field", new()
        {
            ["Name"] = o => ((FieldDeclaration)o).Name,
            ["Type"] = o => (object?)((FieldDeclaration)o).Type,
            ["Public"] = o => (object)((FieldDeclaration)o).IsPublic,
            ["Private"] = o => (object)((FieldDeclaration)o).IsPrivate,
            ["Protected"] = o => (object)((FieldDeclaration)o).IsProtected,
            ["Internal"] = o => (object)((FieldDeclaration)o).IsInternal,
            ["Static"] = o => (object)((FieldDeclaration)o).IsStatic,
            ["Readonly"] = o => (object)((FieldDeclaration)o).IsReadonly,
            ["Const"] = o => (object)((FieldDeclaration)o).IsConst,
            ["Line"] = o => (object)((FieldDeclaration)o).Line,
        });

        registry.RegisterAccessors("Property", new()
        {
            ["Name"] = o => ((PropertyDeclaration)o).Name,
            ["Type"] = o => (object?)((PropertyDeclaration)o).Type,
            ["Public"] = o => (object)((PropertyDeclaration)o).IsPublic,
            ["Protected"] = o => (object)((PropertyDeclaration)o).IsProtected,
            ["Private"] = o => (object)((PropertyDeclaration)o).IsPrivate,
            ["Internal"] = o => (object)((PropertyDeclaration)o).IsInternal,
            ["Static"] = o => (object)((PropertyDeclaration)o).IsStatic,
            ["Abstract"] = o => (object)((PropertyDeclaration)o).IsAbstract,
            ["Virtual"] = o => (object)((PropertyDeclaration)o).IsVirtual,
            ["Override"] = o => (object)((PropertyDeclaration)o).IsOverride,
            ["HasGetter"] = o => (object)((PropertyDeclaration)o).HasGetter,
            ["HasSetter"] = o => (object)((PropertyDeclaration)o).HasSetter,
            ["Documented"] = o => (object)((PropertyDeclaration)o).HasDocComment,
            ["Line"] = o => (object)((PropertyDeclaration)o).Line,
        });

        registry.RegisterAccessors("Event", new()
        {
            ["Name"] = o => ((EventDeclaration)o).Name,
            ["Type"] = o => (object?)((EventDeclaration)o).Type,
            ["Public"] = o => (object)((EventDeclaration)o).IsPublic,
            ["Protected"] = o => (object)((EventDeclaration)o).IsProtected,
            ["Private"] = o => (object)((EventDeclaration)o).IsPrivate,
            ["Internal"] = o => (object)((EventDeclaration)o).IsInternal,
            ["Static"] = o => (object)((EventDeclaration)o).IsStatic,
            ["Line"] = o => (object)((EventDeclaration)o).Line,
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
            ["Method"] = o => ((StatementInfo)o).Method,
            ["Parent"] = o => ((StatementInfo)o).Parent,
            ["Children"] = o => (object)((StatementInfo)o)._children,
            ["Ancestors"] = o => (object)((StatementInfo)o).GetAncestors(),
            ["Condition"] = o => ((StatementInfo)o).Condition,
            ["Expression"] = o => ((StatementInfo)o).Expression,
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

        registry.RegisterAccessors("Api", new()
        {
            ["Kind"] = o => ((ApiEntry)o).Kind,
            ["TypeName"] = o => ((ApiEntry)o).TypeName,
            ["MemberName"] = o => ((ApiEntry)o).MemberName,
            ["Signature"] = o => ((ApiEntry)o).Signature,
            ["ApiAsText"] = o => ((ApiEntry)o).ApiAsText,
            ["Line"] = o => (object)((ApiEntry)o).Line,
            ["File"] = o => ((ApiEntry)o).File,
            ["Source"] = o => ((ApiEntry)o).Source,
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
            {
                var members = new List<object>();
                members.AddRange(t.Methods.Select(m => new MemberInfo(m.Name, t.Name, m.Line)));
                members.AddRange(t.Properties.Select(p => new MemberInfo(p.Name, t.Name, p.Line)));
                members.AddRange(t.Events.Select(e => new MemberInfo(e.Name, t.Name, e.Line)));
                members.AddRange(t.Fields.Select(f => new MemberInfo(f.Name, t.Name, f.Line)));
                return members;
            }).ToList());

        registry.RegisterCollectionExtractor("Api",
            doc =>
            {
                var file = doc.As<SourceFile>();
                var entries = new List<object>();
                foreach (var type in file.Types)
                {
                    if (!type.IsPublic) continue;

                    // Type-level entry
                    entries.Add(ApiEntry.ForType(type));

                    // Enum values
                    if (type.Kind == TypeKind.Enum)
                    {
                        foreach (var value in type.EnumValues)
                            entries.Add(ApiEntry.ForEnumValue(type, value));
                        continue;
                    }

                    // Constructors
                    foreach (var ctor in type.Constructors)
                    {
                        if (ctor.IsPublic || ctor.IsProtected)
                            entries.Add(ApiEntry.ForConstructor(type, ctor));
                    }

                    // Methods
                    foreach (var method in type.Methods)
                    {
                        if (method.IsPublic || method.IsProtected)
                            entries.Add(ApiEntry.ForMethod(type, method));
                    }

                    // Properties
                    foreach (var prop in type.Properties)
                    {
                        if (prop.IsPublic || prop.IsProtected)
                            entries.Add(ApiEntry.ForProperty(type, prop));
                    }

                    // Events
                    foreach (var evt in type.Events)
                    {
                        if (evt.IsPublic || evt.IsProtected)
                            entries.Add(ApiEntry.ForEvent(type, evt));
                    }

                    // Fields
                    foreach (var field in type.Fields)
                    {
                        if (field.IsPublic || field.IsProtected)
                            entries.Add(ApiEntry.ForField(type, field));
                    }
                }
                return entries;
            });
    }

    private static void RegisterMethodEvaluators(TypeRegistry registry)
    {
        registry.RegisterMethodEvaluator("Type", "inheritsFrom",
            (target, args) => ((TypeDeclaration)target).InheritsFrom(args[0]?.ToString() ?? ""));
    }
}
