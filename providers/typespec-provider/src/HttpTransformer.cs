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

        // Build namespace→route mapping from @route decorators on namespaces/interfaces
        var routeMap = BuildRouteMap(spec);

        // Process all operations (both top-level and inside interfaces)
        var allOperations = new List<(TspOperation Op, string? ContainerRoute)>();

        foreach (var op in spec.Operations)
        {
            var containerRoute = ResolveContainerRoute(op.Namespace, null, routeMap);
            allOperations.Add((op, containerRoute));
        }

        foreach (var iface in spec.Interfaces)
        {
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
}
