// Reads public and protected assembly API metadata without executing it.

using System.Reflection;
using Cop.Providers.SourceModel;
using CheckTypeKind = Cop.Providers.SourceModel.TypeKind;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Reads a .NET assembly (DLL) and extracts public API surface as TypeDeclarations.
/// Uses MetadataLoadContext for safe, read-only inspection without executing code.
/// All type names are fully qualified (from metadata), matching GenAPI output.
/// </summary>
public static class AssemblyApiReader
{
    /// <summary>
    /// Read the public API surface from a .NET assembly file.
    /// Returns a SourceFile containing TypeDeclarations with fully qualified names.
    /// </summary>
    public static SourceFile ReadAssembly(string dllPath)
    {
        dllPath = Path.GetFullPath(dllPath);
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException($"Assembly not found: {dllPath}");
        }

        MetadataAssemblyResolver resolver = FrameworkMetadata.CreateAssemblyResolver(dllPath);
        using MetadataLoadContext mlc = new MetadataLoadContext(resolver, coreAssemblyName: "System.Runtime");
        Assembly assembly = mlc.LoadFromAssemblyPath(dllPath);

        List<TypeDeclaration> types = new List<TypeDeclaration>();
        string? fileNamespace = null;

        foreach (Type type in assembly.GetExportedTypes())
        {
            if (type.DeclaringType != null)
            {
                continue; // skip nested (handled inside parent)
            }

            TypeDeclaration td = ConvertType(type, mlc);
            types.Add(td);
            fileNamespace ??= type.Namespace;
        }

        return new SourceFile(
            Path: Path.GetFileName(dllPath),
            Language: "csharp",
            Types: types,
            Statements: [],
            RawText: "")
        { Namespace = fileNamespace };
    }

    private static TypeDeclaration ConvertType(Type type, MetadataLoadContext mlc)
    {
        CheckTypeKind kind = GetTypeKind(type);
        Modifier modifiers = GetModifiers(type);
        List<string> baseTypes = GetBaseTypes(type);
        string name = FormatTypeName(type);

        List<MethodDeclaration> constructors = new List<MethodDeclaration>();
        List<MethodDeclaration> methods = new List<MethodDeclaration>();
        List<PropertyDeclaration> properties = new List<PropertyDeclaration>();
        List<EventDeclaration> events = new List<EventDeclaration>();
        List<FieldDeclaration> fields = new List<FieldDeclaration>();
        List<TypeDeclaration> nestedTypes = new List<TypeDeclaration>();
        List<string> enumValues = new List<string>();

        if (kind == CheckTypeKind.Enum)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                enumValues.Add(field.Name);
            }
        }
        else
        {
            const BindingFlags apiFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (ConstructorInfo ctor in type.GetConstructors(apiFlags))
            {
                if (!IsVisibleApi(ctor))
                {
                    continue;
                }

                constructors.Add(ConvertConstructor(ctor));
            }

            foreach (MethodInfo method in type.GetMethods(apiFlags))
            {
                if (!IsVisibleApi(method) || (method.IsSpecialName && !method.Name.StartsWith("op_", StringComparison.Ordinal)))
                {
                    continue;
                }

                methods.Add(ConvertMethod(method));
            }

            foreach (PropertyInfo prop in type.GetProperties(apiFlags))
            {
                if (prop.GetAccessors(true).Any(IsVisibleApi))
                {
                    properties.Add(ConvertProperty(prop));
                }
            }

            foreach (EventInfo evt in type.GetEvents(apiFlags))
            {
                if (evt.GetAddMethod(true) is { } add && IsVisibleApi(add))
                {
                    events.Add(ConvertEvent(evt));
                }
            }

            foreach (FieldInfo field in type.GetFields(apiFlags))
            {
                if (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly)
                {
                    fields.Add(ConvertField(field));
                }
            }

            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem)
                {
                    nestedTypes.Add(ConvertType(nested, mlc));
                }
            }
        }

        return new TypeDeclaration(
            Name: name,
            Kind: kind,
            Modifiers: modifiers,
            BaseTypes: baseTypes,
            Decorators: [],
            Constructors: constructors,
            Methods: methods,
            NestedTypes: nestedTypes,
            EnumValues: enumValues,
            Line: 0)
        {
            Properties = properties,
            Events = events,
            Fields = fields,
            Namespace = type.Namespace,
            TypeParameters = type.IsGenericTypeDefinition ? type.GetGenericArguments().Select(p => p.Name).ToList() : []
        };
    }

    private static string FormatTypeName(Type type)
    {
        string fullName = type.FullName ?? type.Name;
        if (!type.IsGenericType)
        {
            return fullName;
        }

        string baseName = fullName.Contains('`')
            ? fullName[..fullName.IndexOf('`')]
            : fullName;
        IEnumerable<string> args = type.GetGenericArguments().Select(FormatTypeRef);
        return $"{baseName}<{string.Join(", ", args)}>";
    }

    private static string FormatTypeRef(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        string name = type.FullName ?? type.Name;
        if (!type.IsGenericType)
        {
            return name;
        }

        string baseName = name.Contains('`')
            ? name[..name.IndexOf('`')]
            : name;
        IEnumerable<string> args = type.GetGenericArguments().Select(FormatTypeRef);
        return $"{baseName}<{string.Join(", ", args)}>";
    }

    private static CheckTypeKind GetTypeKind(Type type)
    {
        if (type.IsEnum)
        {
            return CheckTypeKind.Enum;
        }

        if (type.IsInterface)
        {
            return CheckTypeKind.Interface;
        }

        if (type.IsValueType)
        {
            return CheckTypeKind.Struct;
        }

        return CheckTypeKind.Class;
    }

    private static Modifier GetModifiers(Type type)
    {
        Modifier mods = Modifier.None;
        if (type.IsPublic || type.IsNestedPublic)
        {
            mods |= Modifier.Public;
        }

        if (type.IsNestedFamily || type.IsNestedFamORAssem)
        {
            mods |= Modifier.Protected;
        }

        if (type.IsSealed && !type.IsValueType && !type.IsEnum)
        {
            mods |= Modifier.Sealed;
        }

        if (type.IsAbstract && !type.IsInterface && !type.IsSealed)
        {
            mods |= Modifier.Abstract;
        }
        // Static classes are abstract + sealed in metadata
        if (type.IsAbstract && type.IsSealed)
        {
            mods = (mods & ~Modifier.Abstract & ~Modifier.Sealed) | Modifier.Public | Modifier.Static;
        }

        return mods;
    }

    private static List<string> GetBaseTypes(Type type)
    {
        List<string> bases = new List<string>();
        if (type.BaseType != null && type.BaseType.FullName != "System.Object"
            && type.BaseType.FullName != "System.ValueType" && type.BaseType.FullName != "System.Enum")
        {
            bases.Add(FormatTypeRef(type.BaseType));
        }
        foreach (Type iface in type.GetInterfaces())
        {
            // Only include directly declared interfaces
            if (type.BaseType != null && type.BaseType.GetInterfaces().Contains(iface))
            {
                continue;
            }

            bases.Add(FormatTypeRef(iface));
        }
        return bases;
    }

    private static bool IsVisibleApi(MethodBase method) =>
        method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static MethodDeclaration ConvertConstructor(ConstructorInfo ctor)
    {
        return new MethodDeclaration(
            Name: ".ctor",
            Modifiers: GetMethodModifiers(ctor),
            Decorators: [],
            ReturnType: null,
            Parameters: ctor.GetParameters().Select(ConvertParameter).ToList(),
            Line: 0);
    }

    private static MethodDeclaration ConvertMethod(MethodInfo method)
    {
        string returnType = FormatTypeRef(method.ReturnType);
        return new MethodDeclaration(
            Name: FormatMethodName(method),
            Modifiers: GetMethodModifiers(method),
            Decorators: [],
            ReturnType: new TypeReference(method.ReturnType.Name, method.ReturnType.Namespace, [], returnType),
            Parameters: method.GetParameters().Select(ConvertParameter).ToList(),
            Line: 0)
        {
            TypeParameters = method.IsGenericMethodDefinition ? method.GetGenericArguments().Select(p => p.Name).ToList() : []
        };
    }

    private static string FormatMethodName(MethodInfo method)
    {
        if (!method.IsGenericMethod)
        {
            return method.Name;
        }

        IEnumerable<string> args = method.GetGenericArguments().Select(a => a.Name);
        return $"{method.Name}<{string.Join(", ", args)}>";
    }

    private static Modifier GetMethodModifiers(MethodBase method)
    {
        Modifier mods = Modifier.None;
        if (method.IsPublic)
        {
            mods |= Modifier.Public;
        }
        else if (method.IsFamily || method.IsFamilyOrAssembly)
        {
            mods |= Modifier.Protected;
        }

        if (method.IsStatic)
        {
            mods |= Modifier.Static;
        }

        if (method.IsAbstract)
        {
            mods |= Modifier.Abstract;
        }

        if (method.IsVirtual && !method.IsAbstract && !method.IsFinal)
        {
            // Check if this is an override or a new virtual
            if (method is MethodInfo mi && mi.GetBaseDefinition() != mi)
            {
                mods |= Modifier.Override;
            }
            else
            {
                mods |= Modifier.Virtual;
            }
        }
        return mods;
    }

    private static PropertyDeclaration ConvertProperty(PropertyInfo prop)
    {
        MethodInfo? getter = prop.GetGetMethod(true);
        MethodInfo? setter = prop.GetSetMethod(true);
        if (getter is not null && !IsVisibleApi(getter))
        {
            getter = null;
        }

        if (setter is not null && !IsVisibleApi(setter))
        {
            setter = null;
        }

        Modifier mods = Modifier.None;

        MethodInfo? accessorMethod = getter ?? setter;
        if (accessorMethod != null)
        {
            mods = GetMethodModifiers(accessorMethod);
        }

        string typeRef = FormatTypeRef(prop.PropertyType);
        return new PropertyDeclaration(
            Name: prop.Name,
            Type: new TypeReference(prop.PropertyType.Name, prop.PropertyType.Namespace, [], typeRef),
            Modifiers: mods,
            Line: 0)
        { HasGetter = getter != null, HasSetter = setter != null };
    }

    private static EventDeclaration ConvertEvent(EventInfo evt)
    {
        Modifier mods = Modifier.None;
        MethodInfo? addMethod = evt.GetAddMethod(true);
        if (addMethod != null)
        {
            mods = GetMethodModifiers(addMethod);
        }

        Type? handlerType = evt.EventHandlerType;
        string typeRef = handlerType != null ? FormatTypeRef(handlerType) : "EventHandler";
        return new EventDeclaration(
            Name: evt.Name,
            Type: handlerType != null
                ? new TypeReference(handlerType.Name, handlerType.Namespace, [], typeRef)
                : new TypeReference("EventHandler", "System", [], "EventHandler"),
            Modifiers: mods,
            Line: 0);
    }

    private static FieldDeclaration ConvertField(FieldInfo field)
    {
        Modifier mods = Modifier.None;
        if (field.IsPublic)
        {
            mods |= Modifier.Public;
        }
        else if (field.IsFamily || field.IsFamilyOrAssembly)
        {
            mods |= Modifier.Protected;
        }

        if (field.IsStatic)
        {
            mods |= Modifier.Static;
        }

        if (field.IsInitOnly)
        {
            mods |= Modifier.Readonly;
        }

        if (field.IsLiteral)
        {
            mods |= Modifier.Const;
        }

        string typeRef = FormatTypeRef(field.FieldType);
        return new FieldDeclaration(
            Name: field.Name,
            Type: new TypeReference(field.FieldType.Name, field.FieldType.Namespace, [], typeRef),
            Modifiers: mods,
            Line: 0);
    }

    private static ParameterDeclaration ConvertParameter(ParameterInfo param)
    {
        string typeRef = param.ParameterType != null ? FormatTypeRef(param.ParameterType) : "object";
        return new ParameterDeclaration(
            Name: param.Name ?? $"arg{param.Position}",
            Type: param.ParameterType != null
                ? new TypeReference(param.ParameterType.Name, param.ParameterType.Namespace, [], typeRef)
                : new TypeReference("object", "System", [], "object"),
            IsVariadic: false,
            IsKwargs: false,
            HasDefaultValue: param.HasDefaultValue,
            Line: 0)
        { DefaultValueText = param.HasDefaultValue ? FormatDefaultValue(param) : null, IsOptional = param.IsOptional };
    }

    private static string FormatDefaultValue(ParameterInfo param)
    {
        if (param.DefaultValue == null)
        {
            return "null";
        }

        if (param.DefaultValue is bool b)
        {
            return b ? "true" : "false";
        }

        if (param.DefaultValue is string s)
        {
            return $"\"{s}\"";
        }

        if (param.ParameterType?.IsEnum == true)
        {
            return $"({FormatTypeRef(param.ParameterType)}){param.DefaultValue}";
        }

        return param.DefaultValue.ToString() ?? "default";
    }
}
