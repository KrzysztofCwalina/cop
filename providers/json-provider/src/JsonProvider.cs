using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Provider for JSON file parsing. Exposes json.Parse(path, typeName) function
/// that parses JSON files into typed collections.
/// Must be imported with: import json
/// </summary>
public class JsonProvider : ObjectProvider, ICapabilityProvider
{
    public override ObjectFormat SupportedFormats => ObjectFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema() => new ProviderSchema().ToJson();

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query) => new();

    public void RegisterCapabilities(TypeRegistry registry, string rootPath)
    {
        // Register json.Parse(path, typeName) as a provider function
        registry.RegisterProviderFunction("json", "Parse", args =>
        {
            if (args.Count < 2)
                throw new InvalidOperationException("json.Parse requires 2 arguments: json.Parse('file.json', 'TypeName')");

            var filePath = args[0]?.ToString()
                ?? throw new InvalidOperationException("json.Parse: file path cannot be null");
            var typeName = args[1]?.ToString()
                ?? throw new InvalidOperationException("json.Parse: type name cannot be null");

            var fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(rootPath, filePath);
            if (!File.Exists(fullPath))
                throw new InvalidOperationException($"json.Parse: file not found: {fullPath}");

            var schema = registry.ExportTypeAsSchema(typeName);
            var items = JsonCollectionDeserializer.DeserializeArray(File.ReadAllBytes(fullPath), typeName, schema);
            JsonCollectionDeserializer.RegisterDataObjectAccessors(registry, schema);

            // Return as a list (provider functions return object?)
            return Task.FromResult<object?>(items);
        });

        // Keep file parser registration for backward compatibility during transition
        registry.RegisterFileParser("json", (filePath, typeName) =>
        {
            var fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(rootPath, filePath);
            if (!File.Exists(fullPath))
                throw new InvalidOperationException($"Parse() file not found: {fullPath}");

            var schema = registry.ExportTypeAsSchema(typeName);
            var items = JsonCollectionDeserializer.DeserializeArray(File.ReadAllBytes(fullPath), typeName, schema);
            JsonCollectionDeserializer.RegisterDataObjectAccessors(registry, schema);
            return items;
        });
    }

    public override string ToString() => "JsonProvider";
}
