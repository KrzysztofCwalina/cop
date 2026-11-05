// Interprets TypeSpec HTTP decorators to produce service and operation metadata.

namespace TypeSpecProvider;

/// <summary>
/// HTTP protocol graph types produced by the HTTP transformer.
/// Mirrors TypeSpec's @typespec/http layer.
/// </summary>
public class HttpSpec
{
    /// <summary>
    /// Gets or sets the service groupings derived from HTTP operations.
    /// </summary>
    public List<HttpService> Services { get; set; } = [];
    /// <summary>Gets or sets all transformed HTTP operations.</summary>
    public List<HttpOperation> Operations { get; set; } = [];
}

/// <summary>Groups related HTTP operations under a service identity.</summary>
public class HttpService
{
    /// <summary>Gets or sets the service's display name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the scope used to group the service's operations.</summary>
    public string Namespace { get; set; } = "";
    /// <summary>Gets or sets the HTTP operations belonging to the service.</summary>
    public List<HttpOperation> Operations { get; set; } = [];
    /// <summary>
    /// Gets or sets the service authentication description, if available.
    /// </summary>
    public string? Auth { get; set; }
}

/// <summary>Describes the HTTP binding of a TypeSpec operation.</summary>
public class HttpOperation
{
    /// <summary>Gets or sets the source operation's name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the HTTP request method.</summary>
    public string Verb { get; set; } = "";
    /// <summary>Gets or sets the operation's resolved route path.</summary>
    public string Path { get; set; } = "";
    /// <summary>
    /// Gets or sets the URI template including route parameter placeholders.
    /// </summary>
    public string UriTemplate { get; set; } = "";
    /// <summary>Gets or sets the operation's HTTP parameter bindings.</summary>
    public List<HttpParameter> Parameters { get; set; } = [];
    /// <summary>Gets or sets the operation's declared HTTP responses.</summary>
    public List<HttpResponse> Responses { get; set; } = [];
    /// <summary>Gets or sets the source interface name, if any.</summary>
    public string? Interface { get; set; }
    /// <summary>Gets or sets decorators retained from the source operation.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}

/// <summary>Describes how an operation parameter is carried in an HTTP request.</summary>
public class HttpParameter
{
    /// <summary>Gets or sets the HTTP parameter's name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the parameter's TypeSpec type expression.</summary>
    public string Type { get; set; } = "";
    /// <summary>
    /// Gets or sets its location: path, query, header, cookie, or body.
    /// </summary>
    public string In { get; set; } = "query"; // path, query, header, cookie, body
    /// <summary>Gets or sets whether the parameter may be omitted.</summary>
    public bool Optional { get; set; }
    /// <summary>Gets or sets the parameter serialization style, if specified.</summary>
    public string? Style { get; set; }
}

/// <summary>Describes an operation's response status, body type, and headers.</summary>
public class HttpResponse
{
    /// <summary>
    /// Gets or sets the response status code or status-code expression.
    /// </summary>
    public string StatusCode { get; set; } = "200";
    /// <summary>Gets or sets the response documentation, if available.</summary>
    public string? Description { get; set; }
    /// <summary>
    /// Gets or sets the response body's TypeSpec type expression, if any.
    /// </summary>
    public string? Body { get; set; }
    /// <summary>Gets or sets the declared response header bindings.</summary>
    public List<HttpHeader> Headers { get; set; } = [];
}

/// <summary>Describes a named HTTP response header and its value type.</summary>
public class HttpHeader
{
    /// <summary>Gets or sets the header's HTTP field name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the header value's TypeSpec type expression.</summary>
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

    /// <summary>
    /// Resolves interface inheritance and projects HTTP decorator bindings.
    /// </summary>
    public static HttpSpec Transform(TspSpec spec)
    {
        HttpSpec httpSpec = new HttpSpec();

        // Resolve interface extends — copy operations from base interfaces into derived ones
        ResolveInterfaceExtends(spec);

        // Build namespace→route mapping from @route decorators on namespaces/interfaces
        Dictionary<string, string> routeMap = BuildRouteMap(spec);

        // Apply @autoRoute heuristic — compute routes from model names + @key properties
        ApplyAutoRoute(spec, routeMap);

        // Process all operations (both top-level and inside interfaces)
        List<(TspOperation Op, string? ContainerRoute)> allOperations = new List<(TspOperation Op, string? ContainerRoute)>();

        foreach (TspOperation op in spec.Operations)
        {
            string? containerRoute = ResolveContainerRoute(op.Namespace, null, routeMap);
            allOperations.Add((op, containerRoute));
        }

        foreach (TspInterface iface in spec.Interfaces)
        {
            // Skip template interfaces — their ops exist only to be inherited via extends
            if (iface.TemplateParameters.Count > 0)
            {
                continue;
            }

            string? containerRoute = ResolveContainerRoute(iface.Namespace, iface.Name, routeMap);
            foreach (TspOperation op in iface.Operations)
            {
                allOperations.Add((op, containerRoute));
            }
        }

        foreach ((TspOperation? op, string? containerRoute) in allOperations)
        {
            HttpOperation? httpOp = TransformOperation(op, containerRoute);
            if (httpOp is not null)
            {
                httpSpec.Operations.Add(httpOp);
            }
        }

        // Group operations into services by namespace
        List<IGrouping<string, HttpOperation>> serviceGroups = httpSpec.Operations
            .GroupBy(o => o.Interface ?? "default")
            .ToList();

        foreach (IGrouping<string, HttpOperation> group in serviceGroups)
        {
            HttpOperation firstOp = group.First();
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
        string? verb = ResolveVerb(op);
        if (verb is null)
        {
            return null; // Not an HTTP operation
        }

        string? opRoute = GetDecoratorArg(op.Decorators, "route");
        string path = BuildPath(containerRoute, opRoute, op);

        HttpOperation httpOp = new HttpOperation
        {
            Name = op.Name,
            Verb = verb,
            Path = path,
            UriTemplate = path,
            Interface = op.Interface,
            Decorators = op.Decorators,
        };

        // Classify parameters
        foreach (TspProperty param in op.Parameters)
        {
            if (param.Name.StartsWith("..."))
            {
                continue; // Skip spread params for now
            }

            HttpParameter httpParam = ClassifyParameter(param, path);
            httpOp.Parameters.Add(httpParam);
        }

        // Process return type as response
        List<HttpResponse> responses = BuildResponses(op);
        httpOp.Responses.AddRange(responses);

        return httpOp;
    }

    private static string? ResolveVerb(TspOperation op)
    {
        foreach (TspDecorator dec in op.Decorators)
        {
            string name = dec.Name.Split('.').Last().ToLowerInvariant();
            if (name is "get" or "put" or "post" or "patch" or "delete" or "head")
            {
                return name;
            }
        }
        return null;
    }

    private static string BuildPath(string? containerRoute, string? opRoute, TspOperation op)
    {
        List<string> parts = new List<string>();

        if (!string.IsNullOrEmpty(containerRoute))
        {
            parts.Add(containerRoute.TrimEnd('/'));
        }

        if (!string.IsNullOrEmpty(opRoute))
        {
            parts.Add(opRoute.TrimStart('/'));
        }

        string basePath = parts.Count > 0 ? string.Join("/", parts) : "";

        // Add path parameters from the operation if not already in route
        foreach (TspProperty param in op.Parameters)
        {
            if (param.Name.StartsWith("..."))
            {
                continue;
            }

            if (HasDecorator(param.Decorators, "path"))
            {
                string placeholder = $"{{{param.Name}}}";
                if (!basePath.Contains(placeholder))
                {
                    basePath = basePath.TrimEnd('/') + "/" + placeholder;
                }
            }
        }

        if (!basePath.StartsWith("/"))
        {
            basePath = "/" + basePath;
        }

        return basePath;
    }

    private static HttpParameter ClassifyParameter(TspProperty param, string path)
    {
        string location = "query"; // default

        if (HasDecorator(param.Decorators, "path"))
        {
            location = "path";
        }
        else if (HasDecorator(param.Decorators, "query"))
        {
            location = "query";
        }
        else if (HasDecorator(param.Decorators, "header"))
        {
            location = "header";
        }
        else if (HasDecorator(param.Decorators, "body"))
        {
            location = "body";
        }
        else if (HasDecorator(param.Decorators, "cookie"))
        {
            location = "cookie";
        }
        else if (path.Contains($"{{{param.Name}}}"))
        {
            location = "path"; // implicit path param
        }

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
        List<HttpResponse> responses = new List<HttpResponse>();

        if (op.ReturnType != "void")
        {
            // Parse union return types: A | B | C
            string[] returnParts = op.ReturnType.Split('|', StringSplitOptions.TrimEntries);
            foreach (string part in returnParts)
            {
                HttpResponse response = new HttpResponse { Body = part };

                // Try to detect status codes from type names
                if (part.Contains("Error") || part.Contains("error"))
                {
                    response.StatusCode = "default";
                }
                else if (part.Contains("NotModified") || part.Contains("304"))
                {
                    response.StatusCode = "304";
                }
                else if (part.Contains("Created") || part.Contains("201"))
                {
                    response.StatusCode = "201";
                }
                else if (part.Contains("NoContent") || part.Contains("204"))
                {
                    response.StatusCode = "204";
                }
                else
                {
                    response.StatusCode = "200";
                }

                responses.Add(response);
            }
        }

        return responses;
    }

    private static Dictionary<string, string> BuildRouteMap(TspSpec spec)
    {
        Dictionary<string, string> routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (TspNamespace ns in spec.Namespaces)
        {
            string? route = GetDecoratorArg(ns.Decorators, "route");
            if (route is not null)
            {
                routes[ns.FullName] = route;
                routes[ns.Name] = route;
            }
        }

        // Check interfaces for @route
        foreach (TspInterface iface in spec.Interfaces)
        {
            string? route = GetDecoratorArg(iface.Decorators, "route");
            if (route is not null)
            {
                string key = iface.Namespace is not null ? $"{iface.Namespace}.{iface.Name}" : iface.Name;
                routes[key] = route;
                routes[iface.Name] = route;
            }
        }

        // Check models that might be used as namespace containers with @route
        foreach (TspModel model in spec.Models)
        {
            string? route = GetDecoratorArg(model.Decorators, "route");
            if (route is not null)
            {
                string key = model.Namespace is not null ? $"{model.Namespace}.{model.Name}" : model.Name;
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
            if (ns is not null && routeMap.TryGetValue($"{ns}.{ifaceName}", out string? r1))
            {
                return r1;
            }

            if (routeMap.TryGetValue(ifaceName, out string? r2))
            {
                return r2;
            }
        }

        // Try namespace parts from most specific to least
        if (ns is not null)
        {
            string[] parts = ns.Split('.');
            for (int i = parts.Length; i > 0; i--)
            {
                string prefix = string.Join(".", parts.Take(i));
                if (routeMap.TryGetValue(prefix, out string? r))
                {
                    return r;
                }
                // Also try just the last part (e.g., "Pets" from "PetStore.Pets")
                if (i == parts.Length && routeMap.TryGetValue(parts[^1], out string? r3))
                {
                    return r3;
                }
            }
        }

        return null;
    }

    private static string? GetDecoratorArg(List<TspDecorator> decorators, string name)
    {
        foreach (TspDecorator dec in decorators)
        {
            string decName = dec.Name.Split('.').Last();
            if (string.Equals(decName, name, StringComparison.OrdinalIgnoreCase) && dec.Arguments.Count > 0)
            {
                return dec.Arguments[0];
            }
        }
        return null;
    }

    private static bool HasDecorator(List<TspDecorator> decorators, string name)
    {
        foreach (TspDecorator dec in decorators)
        {
            string decName = dec.Name.Split('.').Last();
            if (string.Equals(decName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
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
        Dictionary<string, TspModel> modelMap = new Dictionary<string, TspModel>(StringComparer.OrdinalIgnoreCase);
        foreach (TspModel model in spec.Models)
        {
            modelMap[model.Name] = model;
            if (model.Namespace is not null)
            {
                modelMap[$"{model.Namespace}.{model.Name}"] = model;
            }
        }

        foreach (TspInterface iface in spec.Interfaces)
        {
            if (!HasDecorator(iface.Decorators, "autoRoute"))
            {
                continue;
            }

            // Skip if interface already has an explicit @route
            string ifaceKey = iface.Namespace is not null ? $"{iface.Namespace}.{iface.Name}" : iface.Name;
            if (routeMap.ContainsKey(ifaceKey) || routeMap.ContainsKey(iface.Name))
            {
                continue;
            }

            // Find the resource model — from extends type args
            TspModel? resourceModel = FindResourceModel(iface, modelMap);
            if (resourceModel is null)
            {
                continue;
            }

            // Compute route segment from model name
            string routeSegment = "/" + Pluralize(resourceModel.Name).ToLowerInvariant();

            // Register in route map
            routeMap[ifaceKey] = routeSegment;
            routeMap[iface.Name] = routeSegment;

            // Find @key properties on the resource model (including inherited from base)
            List<TspProperty> keyProps = GetKeyProperties(resourceModel, modelMap);

            // Inject @path decorator on operation params that match @key property names
            if (keyProps.Count > 0)
            {
                foreach (TspOperation op in iface.Operations)
                {
                    InjectKeyPathParams(op, keyProps);
                }
            }
        }

        // Also check namespaces with @autoRoute
        foreach (TspNamespace ns in spec.Namespaces)
        {
            if (!HasDecorator(ns.Decorators, "autoRoute"))
            {
                continue;
            }

            if (routeMap.ContainsKey(ns.FullName) || routeMap.ContainsKey(ns.Name))
            {
                continue;
            }

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
        foreach (string extendsRef in iface.Extends)
        {
            (string _, List<string>? typeArgs) = ParseExtendsReference(extendsRef);
            foreach (string arg in typeArgs)
            {
                // Strip array/optional suffixes
                string cleanName = arg.TrimEnd('[', ']', '?');
                if (modelMap.TryGetValue(cleanName, out TspModel? model))
                {
                    return model;
                }
            }
        }

        // Strategy 2: Look at operation return types and parameter types
        foreach (TspOperation op in iface.Operations)
        {
            // Check return type (strip union parts, arrays)
            string[] returnParts = op.ReturnType.Split('|', StringSplitOptions.TrimEntries);
            foreach (string part in returnParts)
            {
                string cleanName = part.TrimEnd('[', ']', '?');
                if (modelMap.TryGetValue(cleanName, out TspModel? model))
                {
                    return model;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets @key properties from a model and its base model chain.
    /// </summary>
    private static List<TspProperty> GetKeyProperties(TspModel model, Dictionary<string, TspModel> modelMap)
    {
        List<TspProperty> keys = new List<TspProperty>();
        HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TspModel? current = model;

        while (current is not null && visited.Add(current.Name))
        {
            foreach (TspProperty prop in current.Properties)
            {
                if (HasDecorator(prop.Decorators, "key"))
                {
                    keys.Add(prop);
                }
            }

            // Follow base model chain
            if (current.BaseModel is not null && modelMap.TryGetValue(current.BaseModel, out TspModel? baseModel))
            {
                current = baseModel;
            }
            else
            {
                break;
            }
        }

        return keys;
    }

    /// <summary>
    /// Injects @path decorator on operation parameters that match @key property names
    /// but don't already have a @path decorator.
    /// </summary>
    private static void InjectKeyPathParams(TspOperation op, List<TspProperty> keyProps)
    {
        foreach (TspProperty keyProp in keyProps)
        {
            foreach (TspProperty param in op.Parameters)
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
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        // Common irregular plurals
        Dictionary<string, string> irregulars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

        if (irregulars.TryGetValue(name, out string? irregular))
        {
            return irregular;
        }

        // Words ending in s, ss, sh, ch, x, z → add "es"
        if (name.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("ss", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("sh", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("x", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("z", StringComparison.OrdinalIgnoreCase))
        {
            return name + "es";
        }

        // Words ending in consonant + y → replace y with "ies"
        if (name.EndsWith("y", StringComparison.OrdinalIgnoreCase) && name.Length > 1)
        {
            char beforeY = name[^2];
            if (!"aeiouAEIOU".Contains(beforeY))
            {
                return name[..^1] + "ies";
            }
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
        Dictionary<string, TspInterface> ifaceMap = new Dictionary<string, TspInterface>(StringComparer.OrdinalIgnoreCase);
        foreach (TspInterface iface in spec.Interfaces)
        {
            ifaceMap[iface.Name] = iface;
            if (iface.Namespace is not null)
            {
                ifaceMap[$"{iface.Namespace}.{iface.Name}"] = iface;
            }
        }

        // Track which interfaces have been resolved to handle chains
        HashSet<string> resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (TspInterface iface in spec.Interfaces)
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
        if (resolved.Contains(iface.Name))
        {
            return;
        }

        if (depth > 10)
        {
            return; // guard against circular extends
        }

        foreach (string extendsRef in iface.Extends)
        {
            // Parse "BaseInterface<Arg1, Arg2>" into name and type arguments
            (string? baseName, List<string>? typeArgs) = ParseExtendsReference(extendsRef);

            if (!ifaceMap.TryGetValue(baseName, out TspInterface? baseIface))
            {
                continue;
            }

            // Resolve the base first (handles multi-level chains)
            ResolveExtendsRecursive(baseIface, ifaceMap, resolved, depth + 1);

            // Build template substitution map: T → Customer, U → string, etc.
            Dictionary<string, string> substitutions = new Dictionary<string, string>();
            for (int i = 0; i < Math.Min(baseIface.TemplateParameters.Count, typeArgs.Count); i++)
            {
                substitutions[baseIface.TemplateParameters[i]] = typeArgs[i];
            }

            // Clone operations from base into this interface
            foreach (TspOperation baseOp in baseIface.Operations)
            {
                TspOperation clonedOp = CloneOperation(baseOp, iface.Name, iface.Namespace, substitutions);
                iface.Operations.Add(clonedOp);
            }

            // Inherit decorators from the base interface (e.g., @route)
            // Only add decorators that the derived interface doesn't already have
            foreach (TspDecorator baseDec in baseIface.Decorators)
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
        int ltIndex = reference.IndexOf('<');
        if (ltIndex < 0)
        {
            return (reference.Trim(), []);
        }

        string name = reference[..ltIndex].Trim();
        string argsStr = reference[(ltIndex + 1)..];

        // Strip trailing >
        if (argsStr.EndsWith(">"))
        {
            argsStr = argsStr[..^1];
        }

        // Split on commas, respecting nested generics
        List<string> args = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < argsStr.Length; i++)
        {
            if (argsStr[i] == '<')
            {
                depth++;
            }
            else if (argsStr[i] == '>')
            {
                depth--;
            }
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
        if (substitutions.Count == 0)
        {
            return type;
        }

        foreach ((string? param, string? replacement) in substitutions)
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
