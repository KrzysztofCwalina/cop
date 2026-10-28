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
            throw new FileNotFoundException($"Assembly not found: {dllPath}");

        var resolver = CreateResolver(dllPath);
        using var mlc = new MetadataLoadContext(resolver);
        var assembly = mlc.LoadFromAssemblyPath(dllPath);

        var types = new List<TypeDeclaration>();
        string? fileNamespace = null;

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.DeclaringType != null) continue; // skip nested (handled inside parent)
            var td = ConvertType(type, mlc);
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

    private static PathAssemblyResolver CreateResolver(string dllPath)
    {
        var directory = Path.GetDirectoryName(dllPath)!;
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(directory, "*.dll"))
            paths.Add(f);
        foreach (var f in Directory.GetFiles(runtimeDir, "*.dll"))
            paths.Add(f);

        return new PathAssemblyResolver(paths);
    }

    private static TypeDeclaration ConvertType(Type type, MetadataLoadContext mlc)
    {
        var kind = GetTypeKind(type);
        var modifiers = GetModifiers(type);
        var baseTypes = GetBaseTypes(type);
        var name = FormatTypeName(type);

        var constructors = new List<MethodDeclaration>();
        var methods = new List<MethodDeclaration>();
        var properties = new List<PropertyDeclaration>();
        var events = new List<EventDeclaration>();
        var fields = new List<FieldDeclaration>();
        var nestedTypes = new List<TypeDeclaration>();
        var enumValues = new List<string>();

        if (kind == CheckTypeKind.Enum)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                enumValues.Add(field.Name);
        }
        else
        {
            const BindingFlags apiFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var ctor in type.GetConstructors(apiFlags))
            {
                if (!IsVisibleApi(ctor)) continue;
                constructors.Add(ConvertConstructor(ctor));
            }

            foreach (var method in type.GetMethods(apiFlags))
            {
                if (!IsVisibleApi(method) || (method.IsSpecialName && !method.Name.StartsWith("op_", StringComparison.Ordinal))) continue;
                methods.Add(ConvertMethod(method));
            }

            foreach (var prop in type.GetProperties(apiFlags))
                if (prop.GetAccessors(true).Any(IsVisibleApi)) properties.Add(ConvertProperty(prop));

            foreach (var evt in type.GetEvents(apiFlags))
                if (evt.GetAddMethod(true) is { } add && IsVisibleApi(add)) events.Add(ConvertEvent(evt));

            foreach (var field in type.GetFields(apiFlags))
                if (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly) fields.Add(ConvertField(field));

            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem)
                    nestedTypes.Add(ConvertType(nested, mlc));
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
        var fullName = type.FullName ?? type.Name;
        if (!type.IsGenericType)
            return fullName;
        var baseName = fullName.Contains('`')
            ? fullName[..fullName.IndexOf('`')]
            : fullName;
        var args = type.GetGenericArguments().Select(FormatTypeRef);
        return $"{baseName}<{string.Join(", ", args)}>";
    }

    private static string FormatTypeRef(Type type)
    {
        if (type.IsGenericParameter) return type.Name;
        var name = type.FullName ?? type.Name;
        if (!type.IsGenericType) return name;

        var baseName = name.Contains('`')
            ? name[..name.IndexOf('`')]
            : name;
        var args = type.GetGenericArguments().Select(FormatTypeRef);
        return $"{baseName}<{string.Join(", ", args)}>";
    }

    private static CheckTypeKind GetTypeKind(Type type)
    {
        if (type.IsEnum) return CheckTypeKind.Enum;
        if (type.IsInterface) return CheckTypeKind.Interface;
        if (type.IsValueType) return CheckTypeKind.Struct;
        return CheckTypeKind.Class;
    }

    private static Modifier GetModifiers(Type type)
    {
        var mods = Modifier.None;
        if (type.IsPublic || type.IsNestedPublic) mods |= Modifier.Public;
        if (type.IsNestedFamily || type.IsNestedFamORAssem) mods |= Modifier.Protected;
        if (type.IsSealed && !type.IsValueType && !type.IsEnum) mods |= Modifier.Sealed;
        if (type.IsAbstract && !type.IsInterface && !type.IsSealed) mods |= Modifier.Abstract;
        // Static classes are abstract + sealed in metadata
        if (type.IsAbstract && type.IsSealed) mods = (mods & ~Modifier.Abstract & ~Modifier.Sealed) | Modifier.Public | Modifier.Static;
        return mods;
    }

    private static List<string> GetBaseTypes(Type type)
    {
        var bases = new List<string>();
        if (type.BaseType != null && type.BaseType.FullName != "System.Object"
            && type.BaseType.FullName != "System.ValueType" && type.BaseType.FullName != "System.Enum")
        {
            bases.Add(FormatTypeRef(type.BaseType));
        }
        foreach (var iface in type.GetInterfaces())
        {
            // Only include directly declared interfaces
            if (type.BaseType != null && type.BaseType.GetInterfaces().Contains(iface)) continue;
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
        var returnType = FormatTypeRef(method.ReturnType);
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
        if (!method.IsGenericMethod) return method.Name;
        var args = method.GetGenericArguments().Select(a => a.Name);
        return $"{method.Name}<{string.Join(", ", args)}>";
    }

    private static Modifier GetMethodModifiers(MethodBase method)
    {
        var mods = Modifier.None;
        if (method.IsPublic) mods |= Modifier.Public;
        else if (method.IsFamily || method.IsFamilyOrAssembly) mods |= Modifier.Protected;
        if (method.IsStatic) mods |= Modifier.Static;
        if (method.IsAbstract) mods |= Modifier.Abstract;
        if (method.IsVirtual && !method.IsAbstract && !method.IsFinal)
        {
            // Check if this is an override or a new virtual
            if (method is MethodInfo mi && mi.GetBaseDefinition() != mi)
                mods |= Modifier.Override;
            else
                mods |= Modifier.Virtual;
        }
        return mods;
    }

    private static PropertyDeclaration ConvertProperty(PropertyInfo prop)
    {
        var getter = prop.GetGetMethod(true);
        var setter = prop.GetSetMethod(true);
        if (getter is not null && !IsVisibleApi(getter)) getter = null;
        if (setter is not null && !IsVisibleApi(setter)) setter = null;
        var mods = Modifier.None;

        var accessorMethod = getter ?? setter;
        if (accessorMethod != null)
            mods = GetMethodModifiers(accessorMethod);

        var typeRef = FormatTypeRef(prop.PropertyType);
        return new PropertyDeclaration(
            Name: prop.Name,
            Type: new TypeReference(prop.PropertyType.Name, prop.PropertyType.Namespace, [], typeRef),
            Modifiers: mods,
            Line: 0)
        { HasGetter = getter != null, HasSetter = setter != null };
    }

    private static EventDeclaration ConvertEvent(EventInfo evt)
    {
        var mods = Modifier.None;
        var addMethod = evt.GetAddMethod(true);
        if (addMethod != null)
            mods = GetMethodModifiers(addMethod);

        var handlerType = evt.EventHandlerType;
        var typeRef = handlerType != null ? FormatTypeRef(handlerType) : "EventHandler";
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
        var mods = Modifier.None;
        if (field.IsPublic) mods |= Modifier.Public;
        else if (field.IsFamily || field.IsFamilyOrAssembly) mods |= Modifier.Protected;
        if (field.IsStatic) mods |= Modifier.Static;
        if (field.IsInitOnly) mods |= Modifier.Readonly;
        if (field.IsLiteral) mods |= Modifier.Const;

        var typeRef = FormatTypeRef(field.FieldType);
        return new FieldDeclaration(
            Name: field.Name,
            Type: new TypeReference(field.FieldType.Name, field.FieldType.Namespace, [], typeRef),
            Modifiers: mods,
            Line: 0);
    }

    private static ParameterDeclaration ConvertParameter(ParameterInfo param)
    {
        var typeRef = param.ParameterType != null ? FormatTypeRef(param.ParameterType) : "object";
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
            return "null";
        if (param.DefaultValue is bool b)
            return b ? "true" : "false";
        if (param.DefaultValue is string s)
            return $"\"{s}\"";
        if (param.ParameterType?.IsEnum == true)
            return $"({FormatTypeRef(param.ParameterType)}){param.DefaultValue}";
        return param.DefaultValue.ToString() ?? "default";
    }
}
