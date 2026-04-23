using Cop.Core;

namespace Cop.Lang;

/// <summary>
/// Central registry for all cop type definitions. Pre-registers core primitives
/// and loads user/package-defined types from parsed cop files.
/// </summary>
public class TypeRegistry
{
    private readonly Dictionary<string, TypeDescriptor> _types = new();
    private readonly Dictionary<string, CollectionDeclaration> _collections = new();
    private readonly Dictionary<Type, string> _clrTypeMappings = new();
    private readonly Dictionary<string, Func<Document, List<object>>> _collectionExtractors = new();
    private readonly Dictionary<string, List<object>> _globalCollections = new();
    private readonly Dictionary<(string, string), List<object>> _extractorCache = new();

    public TypeRegistry()
    {
        RegisterCorePrimitives();
    }

    private void RegisterCorePrimitives()
    {
        Register(new TypeDescriptor("Object"));
        Register(new TypeDescriptor("string")
        {
            TextConverter = obj => obj?.ToString() ?? ""
        });
        Register(new TypeDescriptor("int")
        {
            TextConverter = obj => obj?.ToString() ?? "0"
        });
        Register(new TypeDescriptor("number")
        {
            TextConverter = obj => obj is double d ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0"
        });
        Register(new TypeDescriptor("bool")
        {
            TextConverter = obj => obj is true ? "true" : "false"
        });
        Register(new TypeDescriptor("byte")
        {
            TextConverter = obj => obj is byte b ? b.ToString() : "0"
        });
    }

    public void Register(TypeDescriptor descriptor)
    {
        _types[descriptor.Name] = descriptor;
    }

    public void RegisterCollection(CollectionDeclaration decl)
    {
        _collections[decl.Name] = decl;
    }

    /// <summary>
    /// Maps a CLR type to an cop type name for runtime type inference.
    /// </summary>
    public void RegisterClrType(Type clrType, string alanTypeName)
    {
        _clrTypeMappings[clrType] = alanTypeName;
    }

    /// <summary>
    /// Registers a function that extracts collection items from a document.
    /// </summary>
    public void RegisterCollectionExtractor(string collectionName, Func<Document, List<object>> extractor)
    {
        _collectionExtractors[collectionName] = extractor;
    }

    /// <summary>
    /// Returns the names of all registered per-document collection extractors.
    /// </summary>
    public IEnumerable<string> GetDocumentCollectionNames() => _collectionExtractors.Keys;

    public TypeDescriptor? GetType(string name) =>
        _types.TryGetValue(name, out var desc) ? desc : null;

    public CollectionDeclaration? GetCollection(string name) =>
        _collections.TryGetValue(name, out var decl) ? decl : null;

    /// <summary>
    /// Gets collection items from a document using a registered extractor.
    /// Results are cached per (collectionName, documentPath) so extractors
    /// run at most once per document per collection.
    /// Returns null if no extractor is registered for this collection.
    /// </summary>
    public List<object>? GetCollectionItems(string collectionName, Document document)
    {
        if (!_collectionExtractors.TryGetValue(collectionName, out var extractor))
            return null;

        var cacheKey = (collectionName, document.Path);
        if (_extractorCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = extractor(document);
        _extractorCache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// Gets collection items with pushdown filter support.
    /// First retrieves unfiltered items (cached), then applies the filter
    /// using registered property accessors for the collection's item type.
    /// </summary>
    public List<object>? GetCollectionItems(string collectionName, Document document, Core.FilterExpression? filter)
    {
        var items = GetCollectionItems(collectionName, document);
        if (items is null || filter is null) return items;

        var itemTypeName = GetCollectionItemType(collectionName);
        return itemTypeName is not null ? ApplyPushdownFilter(itemTypeName, items, filter) : items;
    }

    /// <summary>
    /// Gets items from a global collection with pushdown filter support.
    /// </summary>
    public List<object>? GetGlobalCollectionItems(string name, Core.FilterExpression? filter)
    {
        var items = GetGlobalCollectionItems(name);
        if (items is null || filter is null) return items;

        var itemTypeName = GetCollectionItemType(name);
        return itemTypeName is not null ? ApplyPushdownFilter(itemTypeName, items, filter) : items;
    }

    /// <summary>
    /// Applies a pushdown filter to items using registered property accessors.
    /// This evaluates filter conditions natively on CLR objects, bypassing the
    /// full PredicateEvaluator/ScriptObject pipeline for significantly faster execution.
    /// </summary>
    public List<object> ApplyPushdownFilter(string itemTypeName, List<object> items, Core.FilterExpression filter)
    {
        var accessors = GetAccessors(itemTypeName);
        if (accessors is null || accessors.Count == 0) return items;

        var predicate = FilterCompiler.Compile(filter, accessors);
        return items.Where(predicate).ToList();
    }

    /// <summary>
    /// Gets all registered property accessors for a type name.
    /// Returns null if the type is not registered or has no accessors.
    /// </summary>
    public Dictionary<string, Func<object, object?>>? GetAccessors(string typeName)
    {
        if (!_types.TryGetValue(typeName, out var descriptor)) return null;

        var accessors = new Dictionary<string, Func<object, object?>>();
        foreach (var (name, prop) in descriptor.Properties)
        {
            if (prop.Accessor is not null)
                accessors[name] = prop.Accessor;
        }

        return accessors.Count > 0 ? accessors : null;
    }

    /// <summary>
    /// Registers a pre-computed global collection (not tied to any document).
    /// </summary>
    public void RegisterGlobalCollection(string name, List<object> items)
    {
        _globalCollections[name] = items;
    }

    /// <summary>
    /// Gets items from a registered global collection, or null if not found.
    /// </summary>
    public List<object>? GetGlobalCollectionItems(string name)
    {
        return _globalCollections.TryGetValue(name, out var items) ? items : null;
    }

    /// <summary>
    /// Returns true if the named collection is a registered global collection.
    /// </summary>
    public bool IsGlobalCollection(string name) => _globalCollections.ContainsKey(name);

    /// <summary>
    /// Removes a global collection registration.
    /// </summary>
    public void UnregisterGlobalCollection(string name) => _globalCollections.Remove(name);

    public bool HasType(string name) => _types.ContainsKey(name);

    public bool HasCollection(string name) => _collections.ContainsKey(name);

    private static bool IsCorePrimitive(string name) =>
        name is "Object" or "string" or "int" or "number" or "bool" or "byte";

    public IEnumerable<TypeDescriptor> AllTypes => _types.Values;
    public IEnumerable<CollectionDeclaration> AllCollections => _collections.Values;

    /// <summary>
    /// Loads type definitions from a parsed cop file into the registry.
    /// Resolves base types and validates no duplicate properties in subtypes.
    /// </summary>
    public List<string> LoadTypeDefinitions(IEnumerable<TypeDefinition> typeDefs)
    {
        var errors = new List<string>();

        // First pass: create all descriptors
        foreach (var td in typeDefs)
        {
            if (_types.ContainsKey(td.Name) && !IsCorePrimitive(td.Name))
            {
                errors.Add($"line {td.Line}: duplicate type definition '{td.Name}'");
                continue;
            }

            var descriptor = new TypeDescriptor(td.Name);
            foreach (var prop in td.Properties)
            {
                descriptor.Properties[prop.Name] = new PropertyDescriptor(
                    prop.Name, prop.TypeName, prop.IsOptional, prop.IsCollection);
            }
            _types[td.Name] = descriptor;
        }

        // Second pass: resolve base types and check for duplicate properties
        foreach (var td in typeDefs)
        {
            if (td.BaseType is null) continue;
            if (!_types.TryGetValue(td.Name, out var descriptor)) continue;

            if (!_types.TryGetValue(td.BaseType, out var baseDescriptor))
            {
                errors.Add($"line {td.Line}: base type '{td.BaseType}' not found for '{td.Name}'");
                continue;
            }

            // Check for inheritance cycles
            if (HasCycle(td.Name, td.BaseType))
            {
                errors.Add($"line {td.Line}: inheritance cycle detected for '{td.Name}'");
                continue;
            }

            // Check for duplicate properties (subtypes cannot override parent properties)
            foreach (var prop in td.Properties)
            {
                if (baseDescriptor.GetProperty(prop.Name) is not null)
                {
                    errors.Add($"line {prop.Line}: property '{prop.Name}' already defined in base type '{td.BaseType}'");
                }
            }

            descriptor.BaseType = baseDescriptor;
        }

        return errors;
    }

    /// <summary>
    /// Registers CLR accessor delegates for properties of a named type.
    /// </summary>
    public void RegisterAccessors(string typeName, Dictionary<string, Func<object, object?>> accessors)
    {
        if (!_types.TryGetValue(typeName, out var descriptor)) return;

        foreach (var (propName, accessor) in accessors)
        {
            if (descriptor.Properties.TryGetValue(propName, out var prop))
            {
                prop.Accessor = accessor;
            }
        }
    }

    /// <summary>
    /// Registers a method evaluator on a type descriptor.
    /// </summary>
    public void RegisterMethodEvaluator(string typeName, string methodName, Func<object, List<object?>, object?> evaluator)
    {
        if (_types.TryGetValue(typeName, out var descriptor))
            descriptor.MethodEvaluators[methodName] = evaluator;
    }

    /// <summary>
    /// Tries to evaluate a registered method on a value. Returns null if no evaluator found.
    /// Walks up the type hierarchy to find inherited method evaluators.
    /// </summary>
    public object? TryEvaluateMethod(object target, string methodName, List<object?> args)
    {
        var typeName = InferTypeName(target);
        if (typeName is null) return null;

        var desc = GetType(typeName);
        while (desc is not null)
        {
            if (desc.MethodEvaluators.TryGetValue(methodName, out var evaluator))
                return evaluator(target, args);
            desc = desc.BaseType;
        }
        return null;
    }

    /// <summary>
    /// Registers Program type descriptor and accessors (general-purpose built-in).
    /// </summary>
    public void RegisterProgramType()
    {
        if (!HasType("Program"))
        {
            var desc = new TypeDescriptor("Program");
            desc.Properties["Args"] = new PropertyDescriptor("Args", "string", false, true);
            Register(desc);
        }
        RegisterClrType(typeof(ProgramInfo), "Program");
        RegisterAccessors("Program", new()
        {
            ["Args"] = o => (object)((ProgramInfo)o).Args,
        });
    }

    /// <summary>
    /// Converts a value to its textual representation using the type's TextConverter.
    /// </summary>
    public string ConvertToText(object? value)
    {
        if (value is null) return "null";
        if (value is string s) return s;
        if (value is bool b) return b ? "true" : "false";
        if (value is int i) return i.ToString();
        if (value is double d) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value is byte by) return by.ToString();

        // Try to find a type descriptor for this CLR type
        var typeName = InferTypeName(value);
        if (typeName is not null && _types.TryGetValue(typeName, out var desc) && desc.TextConverter is not null)
            return desc.TextConverter(value);

        return value.ToString() ?? "";
    }

    /// <summary>
    /// Converts a value to text if it has a registered TextConverter. Returns null for unregistered types.
    /// </summary>
    public string? ConvertToTextIfRegistered(object? value)
    {
        if (value is null || value is string) return null;
        var typeName = InferTypeName(value);
        if (typeName is not null && _types.TryGetValue(typeName, out var desc) && desc.TextConverter is not null)
            return desc.TextConverter(value);
        return null;
    }

    /// <summary>
    /// Infers the cop type name from a CLR object using registered CLR type mappings.
    /// </summary>
    internal string? InferTypeName(object value)
    {
        if (value is ScriptObject ao)
            return ao.TypeName;
        return _clrTypeMappings.TryGetValue(value.GetType(), out var name) ? name : null;
    }

    /// <summary>
    /// Gets the item type name for a registered collection, or null if not found.
    /// </summary>
    public string? GetCollectionItemType(string collectionName)
    {
        return _collections.TryGetValue(collectionName, out var decl) ? decl.ItemType : null;
    }

    private bool HasCycle(string typeName, string baseTypeName)
    {
        var visited = new HashSet<string> { typeName };
        var current = baseTypeName;
        while (current is not null)
        {
            if (!visited.Add(current)) return true;
            current = _types.TryGetValue(current, out var desc) ? desc.BaseType?.Name : null;
        }
        return false;
    }
}
