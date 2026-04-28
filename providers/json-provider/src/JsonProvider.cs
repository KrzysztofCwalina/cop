using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Provider for JSON file parsing. Registers the "json" file parser
/// so that Parse('file.json', [Type]) works in .cop files.
/// Must be imported with: import json
/// </summary>
public class JsonProvider : DataProvider, ICapabilityProvider
{
    public override DataFormat SupportedFormats => DataFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema() => new ProviderSchema().ToJson();

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query) => new();

    public void RegisterCapabilities(TypeRegistry registry, string rootPath)
    {
        registry.RegisterFileParser("json", (filePath, typeName) =>
        {
            var fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(rootPath, filePath);
            if (!File.Exists(fullPath))
                throw new InvalidOperationException($"Parse() file not found: {fullPath}");

            var schema = registry.ExportTypeAsSchema(typeName);
            var items = JsonCollectionDeserializer.DeserializeArray(File.ReadAllBytes(fullPath), typeName, schema);
            JsonCollectionDeserializer.RegisterScriptObjectAccessors(registry, schema);
            return items;
        });
    }

    public override string ToString() => "JsonProvider";
}
