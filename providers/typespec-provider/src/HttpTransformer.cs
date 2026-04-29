namespace TypeSpecProvider;

/// <summary>
/// HTTP protocol graph types produced by the HTTP transformer.
/// Mirrors TypeSpec's @typespec/http layer.
/// </summary>
public class HttpSpec
{
    public List<HttpService> Services { get; set; } = [];
    public List<HttpOperation> Operations { get; set; } = [];
}

public class HttpService
{
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public List<HttpOperation> Operations { get; set; } = [];
    public string? Auth { get; set; }
}

public class HttpOperation
{
    public string Name { get; set; } = "";
    public string Verb { get; set; } = "";
    public string Path { get; set; } = "";
    public string UriTemplate { get; set; } = "";
    public List<HttpParameter> Parameters { get; set; } = [];
    public List<HttpResponse> Responses { get; set; } = [];
    public string? Interface { get; set; }
    public List<TspDecorator> Decorators { get; set; } = [];
}

public class HttpParameter
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string In { get; set; } = "query"; // path, query, header, cookie, body
    public bool Optional { get; set; }
    public string? Style { get; set; }
}

public class HttpResponse
{
    public string StatusCode { get; set; } = "200";
    public string? Description { get; set; }
    public string? Body { get; set; }
    public List<HttpHeader> Headers { get; set; } = [];
}

public class HttpHeader
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}

/// <summary>
/// Transforms a raw TypeSpec type graph into an HTTP protocol graph.
/// Interprets @get/@put/@post/@patch/@delete/@head, @route, @path, @query, @header, @body, @statusCode decorators.
/// </summary>
public class HttpTransformer
{
    private static readonly HashSet<string> HttpVerbDecorators = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "put", "post", "patch", "delete", "head",
        "Http.get", "Http.put", "Http.post", "Http.patch", "Http.delete", "Http.head",
    };

    public static HttpSpec Transform(TspSpec spec)
    {
        var httpSpec = new HttpSpec();

        // Resolve interface extends — copy operations from base interfaces into derived ones
        ResolveInterfaceExtends(spec);

        // Build namespace→route mapping from @route decorators on namespaces/interfaces
        var routeMap = BuildRouteMap(spec);

        // Apply @autoRoute heuristic — compute routes from model names + @key properties
        ApplyAutoRoute(spec, routeMap);

        // Process all operations (both top-level and inside interfaces)
        var allOperations = new List<(TspOperation Op, string? ContainerRoute)>();

        foreach (var op in spec.Operations)
        {
            var containerRoute = ResolveContainerRoute(op.Namespace, null, routeMap);
            allOperations.Add((op, containerRoute));
        }

        foreach (var iface in spec.Interfaces)
        {
            // Skip template interfaces — their ops exist only to be inherited via extends
            if (iface.TemplateParameters.Count > 0) continue;

            var containerRoute = ResolveContainerRoute(iface.Namespace, iface.Name, routeMap);
            foreach (var op in iface.Operations)
            {
                allOperations.Add((op, containerRoute));
            }
        }

        foreach (var (op, containerRoute) in allOperations)
        {
            var httpOp = TransformOperation(op, containerRoute);
            if (httpOp is not null)
            {
                httpSpec.Operations.Add(httpOp);
            }
        }

        // Group operations into services by namespace
        var serviceGroups = httpSpec.Operations
            .GroupBy(o => o.Interface ?? "default")
            .ToList();

        foreach (var group in serviceGroups)
        {
            var firstOp = group.First();
            httpSpec.Services.Add(new HttpService
            {
                Name = group.Key,
                Namespace = group.Key,
                Operations = group.ToList(),
            });
        }

        return httpSpec;
    }

    private static HttpOperation? TransformOperation(TspOperation op, string? containerRoute)
    {
        var verb = ResolveVerb(op);
        if (verb is null) return null; // Not an HTTP operation

        var opRoute = GetDecoratorArg(op.Decorators, "route");
        var path = BuildPath(containerRoute, opRoute, op);

        var httpOp = new HttpOperation
        {
            Name = op.Name,
            Verb = verb,
            Path = path,
            UriTemplate = path,
            Interface = op.Interface,
            Decorators = op.Decorators,
        };

        // Classify parameters
        foreach (var param in op.Parameters)
        {
            if (param.Name.StartsWith("...")) continue; // Skip spread params for now

            var httpParam = ClassifyParameter(param, path);
            httpOp.Parameters.Add(httpParam);
        }

        // Process return type as response
        var responses = BuildResponses(op);
        httpOp.Responses.AddRange(responses);

        return httpOp;
    }

    private static string? ResolveVerb(TspOperation op)
    {
        foreach (var dec in op.Decorators)
        {
            var name = dec.Name.Split('.').Last().ToLowerInvariant();
            if (name is "get" or "put" or "post" or "patch" or "delete" or "head")
                return name;
        }
        return null;
    }

    private static string BuildPath(string? containerRoute, string? opRoute, TspOperation op)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(containerRoute))
            parts.Add(containerRoute.TrimEnd('/'));

        if (!string.IsNullOrEmpty(opRoute))
            parts.Add(opRoute.TrimStart('/'));

        var basePath = parts.Count > 0 ? string.Join("/", parts) : "";

        // Add path parameters from the operation if not already in route
        foreach (var param in op.Parameters)
        {
            if (param.Name.StartsWith("...")) continue;
            if (HasDecorator(param.Decorators, "path"))
            {
                var placeholder = $"{{{param.Name}}}";
                if (!basePath.Contains(placeholder))
                    basePath = basePath.TrimEnd('/') + "/" + placeholder;
            }
        }

        if (!basePath.StartsWith("/"))
            basePath = "/" + basePath;

        return basePath;
    }

    private static HttpParameter ClassifyParameter(TspProperty param, string path)
    {
        string location = "query"; // default

        if (HasDecorator(param.Decorators, "path"))
            location = "path";
        else if (HasDecorator(param.Decorators, "query"))
            location = "query";
        else if (HasDecorator(param.Decorators, "header"))
            location = "header";
        else if (HasDecorator(param.Decorators, "body"))
            location = "body";
        else if (HasDecorator(param.Decorators, "cookie"))
            location = "cookie";
        else if (path.Contains($"{{{param.Name}}}"))
            location = "path"; // implicit path param

        return new HttpParameter
        {
            Name = param.Name,
            Type = param.Type,
            In = location,
            Optional = param.Optional,
        };
    }

    private static List<HttpResponse> BuildResponses(TspOperation op)
    {
        var responses = new List<HttpResponse>();

        if (op.ReturnType != "void")
        {
            // Parse union return types: A | B | C
            var returnParts = op.ReturnType.Split('|', StringSplitOptions.TrimEntries);
            foreach (var part in returnParts)
            {
                var response = new HttpResponse { Body = part };

                // Try to detect status codes from type names
                if (part.Contains("Error") || part.Contains("error"))
                    response.StatusCode = "default";
                else if (part.Contains("NotModified") || part.Contains("304"))
                    response.StatusCode = "304";
                else if (part.Contains("Created") || part.Contains("201"))
                    response.StatusCode = "201";
                else if (part.Contains("NoContent") || part.Contains("204"))
                    response.StatusCode = "204";
                else
                    response.StatusCode = "200";

                responses.Add(response);
            }
        }

        return responses;
    }

    private static Dictionary<string, string> BuildRouteMap(TspSpec spec)
    {
        var routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ns in spec.Namespaces)
        {
            var route = GetDecoratorArg(ns.Decorators, "route");
            if (route is not null)
            {
                routes[ns.FullName] = route;
                routes[ns.Name] = route;
            }
        }

        // Check interfaces for @route
        foreach (var iface in spec.Interfaces)
        {
            var route = GetDecoratorArg(iface.Decorators, "route");
            if (route is not null)
            {
                var key = iface.Namespace is not null ? $"{iface.Namespace}.{iface.Name}" : iface.Name;
                routes[key] = route;
                routes[iface.Name] = route;
            }
        }

        // Check models that might be used as namespace containers with @route
        foreach (var model in spec.Models)
        {
            var route = GetDecoratorArg(model.Decorators, "route");
            if (route is not null)
            {
                var key = model.Namespace is not null ? $"{model.Namespace}.{model.Name}" : model.Name;
                routes[key] = route;
                routes[model.Name] = route;
            }
        }

        return routes;
    }

    private static string? ResolveContainerRoute(string? ns, string? ifaceName, Dictionary<string, string> routeMap)
    {
        // Try interface-specific route first
        if (ifaceName is not null)
        {
            if (ns is not null && routeMap.TryGetValue($"{ns}.{ifaceName}", out var r1))
                return r1;
            if (routeMap.TryGetValue(ifaceName, out var r2))
                return r2;
        }

        // Try namespace parts from most specific to least
        if (ns is not null)
        {
            var parts = ns.Split('.');
            for (int i = parts.Length; i > 0; i--)
            {
                var prefix = string.Join(".", parts.Take(i));
                if (routeMap.TryGetValue(prefix, out var r))
                    return r;
                // Also try just the last part (e.g., "Pets" from "PetStore.Pets")
                if (i == parts.Length && routeMap.TryGetValue(parts[^1], out var r3))
                    return r3;
            }
        }

        return null;
    }

    private static string? GetDecoratorArg(List<TspDecorator> decorators, string name)
    {
        foreach (var dec in decorators)
        {
            var decName = dec.Name.Split('.').Last();
            if (string.Equals(decName, name, StringComparison.OrdinalIgnoreCase) && dec.Arguments.Count > 0)
                return dec.Arguments[0];
        }
        return null;
    }

    private static bool HasDecorator(List<TspDecorator> decorators, string name)
    {
        foreach (var dec in decorators)
        {
            var decName = dec.Name.Split('.').Last();
            if (string.Equals(decName, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Applies @autoRoute heuristic: for interfaces with @autoRoute (and no explicit @route),
    /// computes routes from the resource model name and its @key properties.
    /// Standard convention: model name → pluralized, lowercased → route segment;
    /// @key properties → path parameters on operations that reference them.
    /// </summary>
    private static void ApplyAutoRoute(TspSpec spec, Dictionary<string, string> routeMap)
    {
        // Build model lookup
        var modelMap = new Dictionary<string, TspModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in spec.Models)
        {
            modelMap[model.Name] = model;
            if (model.Namespace is not null)
                modelMap[$"{model.Namespace}.{model.Name}"] = model;
        }

        foreach (var iface in spec.Interfaces)
        {
            if (!HasDecorator(iface.Decorators, "autoRoute")) continue;

            // Skip if interface already has an explicit @route
            var ifaceKey = iface.Namespace is not null ? $"{iface.Namespace}.{iface.Name}" : iface.Name;
            if (routeMap.ContainsKey(ifaceKey) || routeMap.ContainsKey(iface.Name)) continue;

            // Find the resource model — from extends type args
            var resourceModel = FindResourceModel(iface, modelMap);
            if (resourceModel is null) continue;

            // Compute route segment from model name
            var routeSegment = "/" + Pluralize(resourceModel.Name).ToLowerInvariant();

            // Register in route map
            routeMap[ifaceKey] = routeSegment;
            routeMap[iface.Name] = routeSegment;

            // Find @key properties on the resource model (including inherited from base)
            var keyProps = GetKeyProperties(resourceModel, modelMap);

            // Inject @path decorator on operation params that match @key property names
            if (keyProps.Count > 0)
            {
                foreach (var op in iface.Operations)
                {
                    InjectKeyPathParams(op, keyProps);
                }
            }
        }

        // Also check namespaces with @autoRoute
        foreach (var ns in spec.Namespaces)
        {
            if (!HasDecorator(ns.Decorators, "autoRoute")) continue;
            if (routeMap.ContainsKey(ns.FullName) || routeMap.ContainsKey(ns.Name)) continue;

            // For namespace-level @autoRoute, we need to find the model from operations
            // This is less common; skip for now — interface-level is the primary pattern
        }
    }

    /// <summary>
    /// Finds the resource model for an interface by examining its extends type arguments
    /// or the parameter/return types of its operations.
    /// </summary>
    private static TspModel? FindResourceModel(TspInterface iface, Dictionary<string, TspModel> modelMap)
    {
        // Strategy 1: Look at extends type arguments (most common pattern)
        // e.g., "interface Customers extends ResourceOperations<Customer> {}"
        foreach (var extendsRef in iface.Extends)
        {
            var (_, typeArgs) = ParseExtendsReference(extendsRef);
            foreach (var arg in typeArgs)
            {
                // Strip array/optional suffixes
                var cleanName = arg.TrimEnd('[', ']', '?');
                if (modelMap.TryGetValue(cleanName, out var model))
                    return model;
            }
        }

        // Strategy 2: Look at operation return types and parameter types
        foreach (var op in iface.Operations)
        {
            // Check return type (strip union parts, arrays)
            var returnParts = op.ReturnType.Split('|', StringSplitOptions.TrimEntries);
            foreach (var part in returnParts)
            {
                var cleanName = part.TrimEnd('[', ']', '?');
                if (modelMap.TryGetValue(cleanName, out var model))
                    return model;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets @key properties from a model and its base model chain.
    /// </summary>
    private static List<TspProperty> GetKeyProperties(TspModel model, Dictionary<string, TspModel> modelMap)
    {
        var keys = new List<TspProperty>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = model;

        while (current is not null && visited.Add(current.Name))
        {
            foreach (var prop in current.Properties)
            {
                if (HasDecorator(prop.Decorators, "key"))
                    keys.Add(prop);
            }

            // Follow base model chain
            if (current.BaseModel is not null && modelMap.TryGetValue(current.BaseModel, out var baseModel))
                current = baseModel;
            else
                break;
        }

        return keys;
    }

    /// <summary>
    /// Injects @path decorator on operation parameters that match @key property names
    /// but don't already have a @path decorator.
    /// </summary>
    private static void InjectKeyPathParams(TspOperation op, List<TspProperty> keyProps)
    {
        foreach (var keyProp in keyProps)
        {
            foreach (var param in op.Parameters)
            {
                if (string.Equals(param.Name, keyProp.Name, StringComparison.OrdinalIgnoreCase)
                    && !HasDecorator(param.Decorators, "path"))
                {
                    param.Decorators.Add(new TspDecorator { Name = "path" });
                }
            }
        }
    }

    /// <summary>
    /// Simple English pluralization heuristic for model names.
    /// </summary>
    private static string Pluralize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        // Common irregular plurals
        var irregulars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["person"] = "people",
            ["child"] = "children",
            ["man"] = "men",
            ["woman"] = "women",
            ["mouse"] = "mice",
            ["goose"] = "geese",
            ["ox"] = "oxen",
            ["datum"] = "data",
            ["index"] = "indices",
            ["matrix"] = "matrices",
            ["vertex"] = "vertices",
            ["radius"] = "radii",
            ["status"] = "statuses",
        };

        if (irregulars.TryGetValue(name, out var irregular))
            return irregular;

        // Words ending in s, ss, sh, ch, x, z → add "es"
        if (name.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("ss", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("sh", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("x", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("z", StringComparison.OrdinalIgnoreCase))
            return name + "es";

        // Words ending in consonant + y → replace y with "ies"
        if (name.EndsWith("y", StringComparison.OrdinalIgnoreCase) && name.Length > 1)
        {
            var beforeY = name[^2];
            if (!"aeiouAEIOU".Contains(beforeY))
                return name[..^1] + "ies";
        }

        // Default: add "s"
        return name + "s";
    }

    /// <summary>
    /// Resolves interface extends chains: copies operations from base interfaces
    /// into derived interfaces, substituting template type parameters.
    /// For example, given:
    ///   interface ResourceOps&lt;T&gt; { @get op list(): T[]; }
    ///   interface Customers extends ResourceOps&lt;Customer&gt; {}
    /// Customers gets a "list" operation returning "Customer[]".
    /// </summary>
    private static void ResolveInterfaceExtends(TspSpec spec)
    {
        // Build lookup: interface name → TspInterface
        var ifaceMap = new Dictionary<string, TspInterface>(StringComparer.OrdinalIgnoreCase);
        foreach (var iface in spec.Interfaces)
        {
            ifaceMap[iface.Name] = iface;
            if (iface.Namespace is not null)
                ifaceMap[$"{iface.Namespace}.{iface.Name}"] = iface;
        }

        // Track which interfaces have been resolved to handle chains
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var iface in spec.Interfaces)
        {
            ResolveExtendsRecursive(iface, ifaceMap, resolved, depth: 0);
        }
    }

    private static void ResolveExtendsRecursive(
        TspInterface iface,
        Dictionary<string, TspInterface> ifaceMap,
        HashSet<string> resolved,
        int depth)
    {
        if (resolved.Contains(iface.Name)) return;
        if (depth > 10) return; // guard against circular extends

        foreach (var extendsRef in iface.Extends)
        {
            // Parse "BaseInterface<Arg1, Arg2>" into name and type arguments
            var (baseName, typeArgs) = ParseExtendsReference(extendsRef);

            if (!ifaceMap.TryGetValue(baseName, out var baseIface))
                continue;

            // Resolve the base first (handles multi-level chains)
            ResolveExtendsRecursive(baseIface, ifaceMap, resolved, depth + 1);

            // Build template substitution map: T → Customer, U → string, etc.
            var substitutions = new Dictionary<string, string>();
            for (int i = 0; i < Math.Min(baseIface.TemplateParameters.Count, typeArgs.Count); i++)
            {
                substitutions[baseIface.TemplateParameters[i]] = typeArgs[i];
            }

            // Clone operations from base into this interface
            foreach (var baseOp in baseIface.Operations)
            {
                var clonedOp = CloneOperation(baseOp, iface.Name, iface.Namespace, substitutions);
                iface.Operations.Add(clonedOp);
            }

            // Inherit decorators from the base interface (e.g., @route)
            // Only add decorators that the derived interface doesn't already have
            foreach (var baseDec in baseIface.Decorators)
            {
                if (!HasDecorator(iface.Decorators, baseDec.Name.Split('.').Last()))
                {
                    iface.Decorators.Add(baseDec);
                }
            }
        }

        resolved.Add(iface.Name);
    }

    /// <summary>
    /// Parses "ResourceOps&lt;Customer, string&gt;" into ("ResourceOps", ["Customer", "string"]).
    /// </summary>
    private static (string Name, List<string> TypeArgs) ParseExtendsReference(string reference)
    {
        var ltIndex = reference.IndexOf('<');
        if (ltIndex < 0)
            return (reference.Trim(), []);

        var name = reference[..ltIndex].Trim();
        var argsStr = reference[(ltIndex + 1)..];

        // Strip trailing >
        if (argsStr.EndsWith(">"))
            argsStr = argsStr[..^1];

        // Split on commas, respecting nested generics
        var args = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < argsStr.Length; i++)
        {
            if (argsStr[i] == '<') depth++;
            else if (argsStr[i] == '>') depth--;
            else if (argsStr[i] == ',' && depth == 0)
            {
                args.Add(argsStr[start..i].Trim());
                start = i + 1;
            }
        }
        args.Add(argsStr[start..].Trim());

        return (name, args);
    }

    private static TspOperation CloneOperation(
        TspOperation source,
        string newInterfaceName,
        string? newNamespace,
        Dictionary<string, string> substitutions)
    {
        return new TspOperation
        {
            Name = source.Name,
            Namespace = newNamespace,
            Interface = newInterfaceName,
            ReturnType = SubstituteType(source.ReturnType, substitutions),
            Decorators = source.Decorators.Select(d => new TspDecorator
            {
                Name = d.Name,
                Arguments = new List<string>(d.Arguments),
            }).ToList(),
            Parameters = source.Parameters.Select(p => new TspProperty
            {
                Name = p.Name,
                Type = SubstituteType(p.Type, substitutions),
                Optional = p.Optional,
                Default = p.Default,
                Decorators = p.Decorators.Select(d => new TspDecorator
                {
                    Name = d.Name,
                    Arguments = new List<string>(d.Arguments),
                }).ToList(),
            }).ToList(),
        };
    }

    /// <summary>
    /// Substitutes template type parameters in a type reference.
    /// E.g., "T[]" with {T → Customer} becomes "Customer[]",
    /// "T | Error" becomes "Customer | Error".
    /// </summary>
    private static string SubstituteType(string type, Dictionary<string, string> substitutions)
    {
        if (substitutions.Count == 0) return type;

        foreach (var (param, replacement) in substitutions)
        {
            // Replace whole-word occurrences of the template parameter
            // Handle: T, T[], T | Error, Map<string, T>
            type = System.Text.RegularExpressions.Regex.Replace(
                type,
                @$"\b{System.Text.RegularExpressions.Regex.Escape(param)}\b",
                replacement);
        }

        return type;
    }
}
