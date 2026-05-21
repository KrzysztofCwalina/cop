using Cop.Core;

namespace Cop.Lang;

/// <summary>
/// Result of resolving a collection name. Handles bare names (may be ambiguous)
/// and qualified names (e.g., csharp.Types).
/// </summary>
public abstract record CollectionResolution
{
    public sealed record Found(List<object> Items) : CollectionResolution;
    public sealed record NotFound() : CollectionResolution;
    public sealed record Ambiguous(List<string> Namespaces, string CollectionName) : CollectionResolution;
}

/// <summary>
/// Central registry for all cop type definitions. Pre-registers core primitives
/// and loads user/package-defined types from parsed cop files.
/// </summary>
public class TypeRegistry
{
    private readonly Dictionary<string, TypeDescriptor> _types = new();
    private readonly Dictionary<string, CollectionDeclaration> _collections = new();
    private readonly Dictionary<string, FlagsDefinition> _flagsTypes = new();
    private readonly Dictionary<string, int> _flagsConstants = new();
    private readonly Dictionary<string, List<string>> _flagsMemberOwners = new();
    private readonly Dictionary<string, EnumDefinition> _enumTypes = new();
    private readonly Dictionary<string, string> _enumConstants = new();
    private readonly Dictionary<string, List<string>> _enumMemberOwners = new();
    private readonly Dictionary<Type, string> _clrTypeMappings = new();
    private readonly Dictionary<string, Func<Document, List<object>>> _collectionExtractors = new();
    private readonly Dictionary<string, List<object>> _globalCollections = new();
    private readonly Dictionary<string, Dictionary<string, List<object>>> _nsCollections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, string), List<object>> _extractorCache = new();
    private readonly Dictionary<string, Func<string, string, List<object>>> _fileParsers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, SinkProvider>> _nsSinks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StreamProvider> _streamingSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Func<List<object?>, Task<object?>>>> _nsProviderFunctions = new(StringComparer.Ordinal);
    private Func<string, List<Document>>? _documentLoader;

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
        Register(new TypeDescriptor("bytes")
        {
            TextConverter = obj => obj is byte[] arr ? $"[{arr.Length} bytes]" : obj?.ToString() ?? "[]"
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
    /// Loads flags definitions from a parsed .cop file, registering each member as a named constant.
    /// Members get power-of-2 values: first=1, second=2, third=4, etc.
    /// </summary>
    public List<string> LoadFlagsDefinitions(IEnumerable<FlagsDefinition> flagsDefs)
    {
        var errors = new List<string>();
        foreach (var fd in flagsDefs)
        {
            if (_flagsTypes.ContainsKey(fd.Name))
            {
                errors.Add($"line {fd.Line}: duplicate flags definition '{fd.Name}'");
                continue;
            }
            _flagsTypes[fd.Name] = fd;
        }
        return errors;
    }

    /// <summary>
    /// Tries to resolve a bare identifier as a flags constant.
    /// Returns the integer bit value, or null if not a known flags member.
    /// </summary>
    public int? TryResolveFlagsConstant(string name) =>
        _flagsConstants.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Returns the FlagsDefinition for a named flags type, or null if not found.
    /// </summary>
    public FlagsDefinition? GetFlagsType(string name) =>
        _flagsTypes.TryGetValue(name, out var fd) ? fd : null;

    /// <summary>
    /// Returns true if the given type name is a registered flags type.
    /// </summary>
    public bool IsFlagsType(string typeName) => _flagsTypes.ContainsKey(typeName);

    /// <summary>
    /// Resolves a qualified flags constant (e.g., "Modifier", "Public" → 1).
    /// Returns the bit value, or null if the type or member is not found.
    /// </summary>
    public int? TryResolveQualifiedFlagsConstant(string typeName, string memberName)
    {
        if (!_flagsTypes.TryGetValue(typeName, out var fd)) return null;
        int bit = 1;
        foreach (var member in fd.Members)
        {
            if (member == memberName) return bit;
            bit <<= 1;
        }
        return null;
    }

    /// <summary>
    /// Returns the list of flags type names that define a given member, for error messages.
    /// Returns null if the member is not known.
    /// </summary>
    public List<string>? GetFlagsMemberOwners(string memberName) =>
        _flagsMemberOwners.TryGetValue(memberName, out var owners) ? owners : null;

    /// <summary>
    /// Loads extensible enum definitions. Members resolve to their string name.
    /// </summary>
    public List<string> LoadEnumDefinitions(IEnumerable<EnumDefinition> enumDefs)
    {
        var errors = new List<string>();
        foreach (var ed in enumDefs)
        {
            if (_enumTypes.ContainsKey(ed.Name))
            {
                errors.Add($"line {ed.Line}: duplicate enum definition '{ed.Name}'");
                continue;
            }
            _enumTypes[ed.Name] = ed;
        }
        return errors;
    }

    /// <summary>
    /// Loads type import declarations. Promotes all members of the named flags or enum type
    /// to global scope as bare identifiers (e.g., <c>export Modifier</c> makes Public, Static, etc. available).
    /// Must be called AFTER LoadFlagsDefinitions and LoadEnumDefinitions.
    /// </summary>
    public List<string> LoadTypeImports(IEnumerable<TypeImportDeclaration> typeImports)
    {
        var errors = new List<string>();
        foreach (var ti in typeImports)
        {
            if (_flagsTypes.TryGetValue(ti.TypeName, out var fd))
            {
                int bit = 1;
                foreach (var member in fd.Members)
                {
                    if (!_flagsMemberOwners.TryGetValue(member, out var owners))
                    {
                        owners = [];
                        _flagsMemberOwners[member] = owners;
                    }
                    owners.Add(fd.Name);

                    if (owners.Count == 1)
                        _flagsConstants[member] = bit;
                    else
                        _flagsConstants.Remove(member); // ambiguous

                    bit <<= 1;
                }
            }
            else if (_enumTypes.TryGetValue(ti.TypeName, out var ed))
            {
                foreach (var member in ed.Members)
                {
                    // Cross-kind collision: enum member conflicts with a flags constant
                    if (_flagsMemberOwners.ContainsKey(member))
                    {
                        errors.Add($"line {ti.Line}: enum member '{member}' conflicts with a flags constant");
                        continue;
                    }

                    if (!_enumMemberOwners.TryGetValue(member, out var owners))
                    {
                        owners = [];
                        _enumMemberOwners[member] = owners;
                    }
                    owners.Add(ed.Name);

                    if (owners.Count == 1)
                        _enumConstants[member] = member;
                    else
                        _enumConstants.Remove(member); // ambiguous
                }
            }
            else
            {
                errors.Add($"line {ti.Line}: unknown flags or enum type '{ti.TypeName}' in type import");
            }
        }
        return errors;
    }

    /// <summary>
    /// Tries to resolve a bare identifier as an enum constant.
    /// Returns the string value (member name), or null if not a known enum member.
    /// </summary>
    public string? TryResolveEnumConstant(string name) =>
        _enumConstants.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Returns the EnumDefinition for a named enum type, or null if not found.
    /// </summary>
    public EnumDefinition? GetEnumType(string name) =>
        _enumTypes.TryGetValue(name, out var ed) ? ed : null;

    /// <summary>
    /// Returns true if the given type name is a registered enum type.
    /// </summary>
    public bool IsEnumType(string typeName) => _enumTypes.ContainsKey(typeName);

    /// <summary>
    /// Resolves a qualified enum constant (e.g., "TypeKind", "Class" → "Class").
    /// Returns the member string, or null if the type or member is not found.
    /// </summary>
    public string? TryResolveQualifiedEnumConstant(string typeName, string memberName)
    {
        if (!_enumTypes.TryGetValue(typeName, out var ed)) return null;
        return ed.Members.Contains(memberName) ? memberName : null;
    }

    /// <summary>
    /// Returns the list of enum type names that define a given member, for error messages.
    /// Returns null if the member is not known.
    /// </summary>
    public List<string>? GetEnumMemberOwners(string memberName) =>
        _enumMemberOwners.TryGetValue(memberName, out var owners) ? owners : null;

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
    /// full PredicateEvaluator/DataObject pipeline for significantly faster execution.
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
    /// Used for runtime-computed collections (Parse results, temp aggregated collections).
    /// These are NOT namespace-qualified and take priority over namespaced collections.
    /// </summary>
    public void RegisterGlobalCollection(string name, List<object> items)
    {
        _globalCollections[name] = items;
    }

    /// <summary>
    /// Appends items to a namespace-qualified global collection.
    /// Used by provider loading — each provider registers under its package name.
    /// </summary>
    public void AppendNamespacedCollection(string ns, string collName, List<object> items)
    {
        if (!_nsCollections.TryGetValue(ns, out var nsDict))
        {
            nsDict = new(StringComparer.Ordinal);
            _nsCollections[ns] = nsDict;
        }
        if (nsDict.TryGetValue(collName, out var existing))
            existing.AddRange(items);
        else
            nsDict[collName] = new List<object>(items);
    }

    /// <summary>
    /// Resolves a collection name (bare or qualified) to items.
    /// Resolution order: flat globals first, then namespaced collections.
    /// Bare names that exist in multiple namespaces produce Ambiguous.
    /// </summary>
    public CollectionResolution ResolveCollection(string name)
    {
        // Flat globals (runtime-computed) always win — they are local scope
        if (_globalCollections.TryGetValue(name, out var flatItems))
            return new CollectionResolution.Found(flatItems);

        // Qualified name: "csharp.Types"
        var dotIndex = name.IndexOf('.');
        if (dotIndex > 0)
        {
            var ns = name[..dotIndex];
            var collName = name[(dotIndex + 1)..];
            if (_nsCollections.TryGetValue(ns, out var nsDict)
                && nsDict.TryGetValue(collName, out var nsItems))
                return new CollectionResolution.Found(nsItems);

            return new CollectionResolution.NotFound();
        }

        // No bare-name fallback — collections must be qualified (csharp.Types)
        // or explicitly exported via `export let Types = ...` in the provider package.
        return new CollectionResolution.NotFound();
    }

    /// <summary>
    /// Gets items from a global collection by name. For backward compatibility.
    /// Checks flat globals first, then resolves namespaced collections (bare or qualified).
    /// Returns null if not found. Throws if ambiguous.
    /// </summary>
    public List<object>? GetGlobalCollectionItems(string name)
    {
        var resolution = ResolveCollection(name);
        return resolution switch
        {
            CollectionResolution.Found f => f.Items,
            CollectionResolution.Ambiguous a => throw new AmbiguousCollectionException(
                $"'{a.CollectionName}' is ambiguous between: {string.Join(", ", a.Namespaces.Select(n => $"{n}.{a.CollectionName}"))}. Use a qualified name."),
            _ => null
        };
    }

    /// <summary>
    /// Returns true if the named collection exists (flat or namespaced qualified).
    /// </summary>
    public bool IsGlobalCollection(string name)
    {
        if (_globalCollections.ContainsKey(name))
            return true;

        var dotIndex = name.IndexOf('.');
        if (dotIndex > 0)
        {
            var ns = name[..dotIndex];
            var collName = name[(dotIndex + 1)..];
            return _nsCollections.TryGetValue(ns, out var nsDict) && nsDict.ContainsKey(collName);
        }

        // No bare-name fallback — must be qualified or in flat globals
        return false;
    }

    /// <summary>
    /// Resolves a bare collection name to its provider namespace.
    /// Returns null — bare-name resolution is no longer supported.
    /// Collections must be qualified (csharp.Types) or explicitly exported.
    /// </summary>
    public string? ResolveCollectionNamespace(string collectionName)
    {
        return null;
    }

    /// <summary>
    /// Removes a flat global collection registration.
    /// </summary>
    public void UnregisterGlobalCollection(string name) => _globalCollections.Remove(name);

    /// <summary>
    /// Registers a sink under a namespace (e.g., "http", "Send").
    /// </summary>
    public void RegisterSink(string ns, SinkProvider sink)
    {
        if (!_nsSinks.TryGetValue(ns, out var nsDict))
        {
            nsDict = new(StringComparer.Ordinal);
            _nsSinks[ns] = nsDict;
        }
        nsDict[sink.Name] = sink;
    }

    /// <summary>
    /// Resolves a sink by qualified or bare name.
    /// Qualified: "http.Send" → namespace "http", name "Send".
    /// Bare: "WriteLine" → searches all namespaces, returns null if ambiguous.
    /// </summary>
    public SinkProvider? ResolveSink(string name)
    {
        var dotIndex = name.IndexOf('.');
        if (dotIndex > 0)
        {
            var ns = name[..dotIndex];
            var sinkName = name[(dotIndex + 1)..];
            if (_nsSinks.TryGetValue(ns, out var nsDict) && nsDict.TryGetValue(sinkName, out var sink))
                return sink;
            return null;
        }

        // Bare name: search all namespaces
        SinkProvider? found = null;
        foreach (var nsDict in _nsSinks.Values)
        {
            if (nsDict.TryGetValue(name, out var sink))
            {
                if (found != null) return null; // ambiguous
                found = sink;
            }
        }
        return found;
    }

    /// <summary>
    /// Returns all sinks registered under a namespace, or null if none found.
    /// </summary>
    public Dictionary<string, SinkProvider>? GetNamespaceSinks(string ns)
    {
        return _nsSinks.TryGetValue(ns, out var nsDict) ? nsDict : null;
    }

    /// <summary>
    /// Registers a streaming source provider under a qualified name (e.g., "http.Requests").
    /// </summary>
    public void RegisterStreamingSource(string qualifiedName, StreamProvider source)
    {
        _streamingSources[qualifiedName] = source;
    }

    /// <summary>
    /// Resolves a streaming source provider by qualified or bare name.
    /// Returns null if not found.
    /// </summary>
    public StreamProvider? ResolveStreamingSource(string name)
    {
        if (_streamingSources.TryGetValue(name, out var source))
            return source;

        // Try bare name match
        foreach (var (key, src) in _streamingSources)
        {
            var dot = key.LastIndexOf('.');
            if (dot >= 0 && string.Equals(key[(dot + 1)..], name, StringComparison.Ordinal))
                return src;
        }
        return null;
    }

    /// <summary>
    /// Returns true if the named collection is a streaming source.
    /// </summary>
    public bool IsStreamingCollection(string name) => ResolveStreamingSource(name) != null;

    /// <summary>
    /// Returns all registered streaming source names (for diagnostics).
    /// </summary>
    public IEnumerable<string> GetStreamingSourceNames() => _streamingSources.Keys;

    /// <summary>
    /// Registers a provider function under a namespace (e.g., "http", "Post").
    /// Provider functions are async and accept evaluated arguments.
    /// Called as namespace.FunctionName(args) in cop scripts.
    /// </summary>
    public void RegisterProviderFunction(string ns, string name, Func<List<object?>, Task<object?>> func)
    {
        if (!_nsProviderFunctions.TryGetValue(ns, out var nsDict))
        {
            nsDict = new(StringComparer.OrdinalIgnoreCase);
            _nsProviderFunctions[ns] = nsDict;
        }
        nsDict[name] = func;
    }

    /// <summary>
    /// Resolves a provider function by namespace and name.
    /// Returns null if the namespace or function is not registered.
    /// </summary>
    public Func<List<object?>, Task<object?>>? ResolveProviderFunction(string ns, string name)
    {
        if (_nsProviderFunctions.TryGetValue(ns, out var nsDict) && nsDict.TryGetValue(name, out var func))
            return func;
        return null;
    }

    /// <summary>
    /// Returns true if the given name is a registered provider namespace that has functions.
    /// Used by the evaluator to resolve bare identifiers like "http" as namespace references.
    /// </summary>
    public bool IsProviderFunctionNamespace(string name)
        => _nsProviderFunctions.ContainsKey(name);

    /// <summary>
    /// Gets the names of all registered global collections (flat + namespaced bare names).
    /// For aggregate count computation. Returns bare names for unambiguous collections,
    /// and qualified names for all namespaced collections.
    /// </summary>
    public IEnumerable<string> GetGlobalCollectionNames()
    {
        // Flat globals
        foreach (var name in _globalCollections.Keys)
            yield return name;

        // Track bare name → namespaces for ambiguity detection
        var bareNames = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (ns, nsDict) in _nsCollections)
        {
            foreach (var collName in nsDict.Keys)
            {
                // Always yield qualified name
                yield return $"{ns}.{collName}";

                if (!bareNames.TryGetValue(collName, out var nsList))
                {
                    nsList = [];
                    bareNames[collName] = nsList;
                }
                nsList.Add(ns);
            }
        }

        // Yield bare names only when unambiguous and not already in flat globals
        foreach (var (collName, nsList) in bareNames)
        {
            if (nsList.Count == 1 && !_globalCollections.ContainsKey(collName))
                yield return collName;
        }
    }

    /// <summary>
    /// Gets all known collection names: global collections, namespaced collections,
    /// and registered collection declarations. For REPL completions.
    /// </summary>
    public List<string> GetAllCollectionNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in GetGlobalCollectionNames())
            names.Add(n);
        foreach (var c in _collections.Keys)
            names.Add(c);
        return [.. names.OrderBy(n => n)];
    }

    /// <summary>
    /// Gets all registered provider namespace names (e.g., "csharp", "files").
    /// </summary>
    public List<string> GetProviderNamespaces()
    {
        return [.. _nsCollections.Keys.OrderBy(n => n)];
    }

    /// <summary>
    /// Gets collection names registered under a specific provider namespace.
    /// Returns empty list if the namespace is not found.
    /// </summary>
    public List<string> GetNamespaceCollections(string ns)
    {
        if (_nsCollections.TryGetValue(ns, out var nsDict))
            return [.. nsDict.Keys.OrderBy(n => n)];
        return [];
    }

    /// <summary>
    /// Registers a document loader for Load('path') calls.
    /// Typically registered by the code provider for assembly loading.
    /// </summary>
    public void RegisterDocumentLoader(Func<string, List<Document>> loader) => _documentLoader = loader;

    /// <summary>
    /// Gets the registered document loader, or null if none is registered.
    /// </summary>
    public Func<string, List<Document>>? DocumentLoader => _documentLoader;

    /// <summary>
    /// Registers a file parser for a given file extension (e.g., "json", "csv").
    /// Parsers are used by Parse('file.ext', [Type]) in the interpreter.
    /// </summary>
    public void RegisterFileParser(string extension, Func<string, string, List<object>> parser)
        => _fileParsers[extension] = parser;

    /// <summary>
    /// Attempts to parse a file using a registered parser for its extension.
    /// Returns null if no parser is registered for the file's extension.
    /// </summary>
    public List<object>? TryParseFile(string filePath, string typeName)
    {
        var ext = Path.GetExtension(filePath).TrimStart('.');
        if (_fileParsers.TryGetValue(ext, out var parser))
            return parser(filePath, typeName);
        return null;
    }

    /// <summary>
    /// Exports a user-defined type as a ProviderSchema for use by file parsers.
    /// </summary>
    public ProviderSchema ExportTypeAsSchema(string typeName)
    {
        var typeDesc = GetType(typeName)
            ?? throw new InvalidOperationException($"Type '{typeName}' is not defined. Add: type {typeName} = {{ ... }}");

        var schema = new ProviderSchema();
        var typeSchema = new ProviderTypeSchema { Name = typeName };

        foreach (var (propName, prop) in typeDesc.Properties)
        {
            typeSchema.Properties.Add(new ProviderPropertySchema
            {
                Name = propName,
                Type = prop.TypeName ?? "string",
                Optional = prop.IsOptional,
                Collection = prop.IsCollection
            });
        }

        schema.Types.Add(typeSchema);
        return schema;
    }

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
                // If the existing type was registered by a provider schema,
                // merge new properties from the .cop definition rather than erroring.
                var existing = _types[td.Name];
                if (existing.IsProviderType)
                {
                    foreach (var prop in td.Properties)
                    {
                        if (!existing.Properties.ContainsKey(prop.Name))
                            existing.Properties[prop.Name] = new PropertyDescriptor(
                                prop.Name, prop.TypeName, prop.IsOptional, prop.IsCollection);
                    }
                    continue;
                }

                errors.Add($"line {td.Line}: duplicate type definition '{td.Name}'");
                continue;
            }

            var descriptor = new TypeDescriptor(td.Name);
            foreach (var prop in td.Properties)
            {
                ValidatePropertyCasing(td.Name, prop.Name);
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
    internal string? InferTypeName(object? value)
    {
        if (value is null) return null;
        if (value is DataObject ao)
            return ao.TypeName;
        if (value is RecordView v)
            return v.Table.TypeName;
        return _clrTypeMappings.TryGetValue(value.GetType(), out var name) ? name : null;
    }

    /// <summary>
    /// Gets the item type name for a registered collection, or null if not found.
    /// Handles qualified names by stripping the namespace prefix.
    /// </summary>
    public string? GetCollectionItemType(string collectionName)
    {
        if (_collections.TryGetValue(collectionName, out var decl))
            return decl.ItemType;

        // Qualified name: "csharp.Types" → look up "Types"
        var dotIndex = collectionName.IndexOf('.');
        if (dotIndex > 0)
        {
            var bareName = collectionName[(dotIndex + 1)..];
            if (_collections.TryGetValue(bareName, out decl))
                return decl.ItemType;
        }

        return null;
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

    /// <summary>
    /// Registers type descriptors and collection declarations from a <see cref="ProviderSchema"/>.
    /// Does NOT register property accessors — the caller is responsible for registering
    /// CLR-based accessors via <see cref="RegisterAccessors"/> for Objects-format providers,
    /// or DataObject-based accessors for JSON-format providers.
    /// </summary>
    public void RegisterProviderSchema(ProviderSchema schema)
    {
        foreach (var ts in schema.Types)
        {
            if (HasType(ts.Name)) continue;
            var desc = new TypeDescriptor(ts.Name) { IsProviderType = true };
            foreach (var ps in ts.Properties)
            {
                ValidatePropertyCasing(ts.Name, ps.Name);
                desc.Properties[ps.Name] = new PropertyDescriptor(ps.Name, ps.Type, ps.Optional, ps.Collection);
            }
            Register(desc);
        }

        // Resolve base types
        foreach (var ts in schema.Types)
        {
            if (ts.Base is null) continue;
            var desc = GetType(ts.Name);
            var baseDesc = GetType(ts.Base);
            if (desc is not null && baseDesc is not null && desc.BaseType is null)
                desc.BaseType = baseDesc;
        }

        // Register collections
        foreach (var cs in schema.Collections)
        {
            if (!HasCollection(cs.Name))
                RegisterCollection(new CollectionDeclaration(cs.Name, cs.ItemType, 0));
        }
    }

    private static void ValidatePropertyCasing(string typeName, string propName)
    {
        if (propName.Length > 0 && char.IsLower(propName[0]))
            throw new InvalidOperationException(
                $"Property '{propName}' on type '{typeName}' must be PascalCase. " +
                $"Use a predicate for boolean queries (e.g., predicate {propName}({typeName}) => ...).");
    }

    /// <summary>
    /// Auto-generates slot-based property accessors for all types in a provider schema.
    /// Each property accessor reads from the corresponding <see cref="RecordView"/> slot,
    /// decoding strings from the shared UTF-8 string heap. Slot index = property order in schema.
    /// </summary>
    public void RegisterDataTableAccessors(ProviderSchema schema)
    {
        foreach (var ts in schema.Types)
        {
            var accessors = new Dictionary<string, Func<object, object?>>();
            for (int slot = 0; slot < ts.Properties.Count; slot++)
            {
                var prop = ts.Properties[slot];
                int capturedSlot = slot; // capture for closure
                bool isPrimitive = prop.Type is "string" or "int" or "bool";

                if (!prop.Collection && isPrimitive)
                {
                    // Scalar primitive — works for both required and optional
                    accessors[prop.Name] = prop.Type switch
                    {
                        "string" => obj =>
                        {
                            var v = (RecordView)obj;
                            return v.Table.GetString(v.Index, capturedSlot);
                        },
                        "int" => obj =>
                        {
                            var v = (RecordView)obj;
                            return (object)v.Table.GetInt32(v.Index, capturedSlot);
                        },
                        "bool" => obj =>
                        {
                            var v = (RecordView)obj;
                            return (object)v.Table.GetBool(v.Index, capturedSlot);
                        },
                        _ => throw new InvalidOperationException()
                    };
                }
                else
                {
                    // Collection or object-reference — raw long placeholder, wired later
                    accessors[prop.Name] = obj =>
                    {
                        var v = (RecordView)obj;
                        return (object)v.Table.GetInt64(v.Index, capturedSlot);
                    };
                }
            }
            RegisterAccessors(ts.Name, accessors);
        }
    }

    /// <summary>
    /// Wires collection and reference property accessors using the actual DataStore tables.
    /// Call after <see cref="RegisterDataTableAccessors"/> and after the DataStore is built.
    /// Replaces raw long accessors with proper RecordView-returning accessors for
    /// collection (range → List&lt;RecordView&gt;) and reference (index → RecordView) properties.
    /// </summary>
    public void WireDataStoreAccessors(ProviderSchema schema, DataStore store)
    {
        // Build lookups: by type name (for object refs) and by table name (for string collections)
        var tableByType = new Dictionary<string, DataTable>();
        var tableByName = new Dictionary<string, DataTable>();
        foreach (var (name, table) in store.Tables)
        {
            tableByName[name] = table;
            tableByType[table.TypeName] = table;
        }

        foreach (var ts in schema.Types)
        {
            var desc = GetType(ts.Name);
            if (desc is null) continue;

            for (int slot = 0; slot < ts.Properties.Count; slot++)
            {
                var prop = ts.Properties[slot];
                int capturedSlot = slot;
                bool isPrimitive = prop.Type is "string" or "int" or "bool";

                if (prop.Collection)
                {
                    if (isPrimitive)
                    {
                        // String/int/bool collection: range into a child table named "{Type}.{Prop}"
                        string childTableName = $"{ts.Name}.{prop.Name}";
                        if (tableByName.TryGetValue(childTableName, out var strTable))
                        {
                            var capturedChild = strTable;
                            desc.Properties[prop.Name].Accessor = prop.Type switch
                            {
                                "string" => obj =>
                                {
                                    var v = (RecordView)obj;
                                    var (start, count) = v.Table.GetRange(v.Index, capturedSlot);
                                    var items = new List<object>(count);
                                    for (int i = 0; i < count; i++)
                                        items.Add(capturedChild.GetString(start + i, 0));
                                    return items;
                                },
                                "int" => obj =>
                                {
                                    var v = (RecordView)obj;
                                    var (start, count) = v.Table.GetRange(v.Index, capturedSlot);
                                    var items = new List<object>(count);
                                    for (int i = 0; i < count; i++)
                                        items.Add(capturedChild.GetInt32(start + i, 0));
                                    return items;
                                },
                                "bool" => obj =>
                                {
                                    var v = (RecordView)obj;
                                    var (start, count) = v.Table.GetRange(v.Index, capturedSlot);
                                    var items = new List<object>(count);
                                    for (int i = 0; i < count; i++)
                                        items.Add(capturedChild.GetBool(start + i, 0));
                                    return items;
                                },
                                _ => throw new InvalidOperationException()
                            };
                        }
                    }
                    else
                    {
                        // Object collection: range reference into a child table by type name
                        if (tableByType.TryGetValue(prop.Type, out var childTable))
                        {
                            var capturedChild = childTable;
                            desc.Properties[prop.Name].Accessor = obj =>
                            {
                                var v = (RecordView)obj;
                                var (start, count) = v.Table.GetRange(v.Index, capturedSlot);
                                var items = new List<object>(count);
                                for (int i = 0; i < count; i++)
                                    items.Add(new RecordView(capturedChild, start + i));
                                return items;
                            };
                        }
                    }
                }
                else if (!isPrimitive)
                {
                    // Optional object reference: index into a child table (-1 = null)
                    if (tableByType.TryGetValue(prop.Type, out var refTable))
                    {
                        var capturedRef = refTable;
                        desc.Properties[prop.Name].Accessor = obj =>
                        {
                            var v = (RecordView)obj;
                            int idx = v.Table.GetRef(v.Index, capturedSlot);
                            return idx < 0 ? null : new RecordView(capturedRef, idx);
                        };
                    }
                }
            }
        }
    }
}
