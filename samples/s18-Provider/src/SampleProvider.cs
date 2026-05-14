using Cop.Core;

namespace SampleProvider;

/// <summary>
/// A minimal Cop data provider that demonstrates the plugin pattern.
/// This provider exposes a "Widgets" collection of Widget items
/// discovered from .widget files in the target directory.
///
/// To use in a .cop file:
///   import sample
///   foreach Widgets   # Widgets is explicitly exported by the sample package
///       '{Widget.Name} ({Widget.Category})'
/// </summary>
public class SampleProvider : DataProvider
{
    public override DataFormat SupportedFormats => DataFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema()
    {
        var schema = new ProviderSchema
        {
            Types =
            [
                new() { Name = "Widget", Properties =
                [
                    new() { Name = "Name" },
                    new() { Name = "Category" },
                    new() { Name = "Size", Type = "int" },
                    new() { Name = "FilePath" },
                ]}
            ],
            Collections =
            [
                new() { Name = "Widgets", ItemType = "Widget" }
            ]
        };
        return schema.ToJson();
    }

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query)
    {
        // Skip if the engine doesn't need Widgets
        if (query.RequestedCollections != null &&
            !query.RequestedCollections.Contains("Widgets"))
            return new();

        var widgets = new List<object>();
        var rootPath = query.RootPath ?? ".";

        if (Directory.Exists(rootPath))
        {
            foreach (var file in Directory.GetFiles(rootPath, "*.widget", SearchOption.AllDirectories))
            {
                // Read the file — each line is "key=value"
                var props = new Dictionary<string, string>();
                foreach (var line in File.ReadAllLines(file))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                        props[parts[0].Trim()] = parts[1].Trim();
                }

                widgets.Add(new Dictionary<string, object?>
                {
                    ["Name"] = props.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(file)),
                    ["Category"] = props.GetValueOrDefault("category", "uncategorized"),
                    ["Size"] = int.TryParse(props.GetValueOrDefault("size", "0"), out var s) ? (long)s : 0L,
                    ["FilePath"] = Path.GetRelativePath(rootPath, file).Replace('\\', '/'),
                });
            }
        }

        return new Dictionary<string, List<object>> { ["Widgets"] = widgets };
    }

    public override string ToString() => "SampleProvider";
}
