using Cop.Core;
using Cop.Lang;

namespace SampleProvider;

/// <summary>
/// A minimal Cop data provider that demonstrates the plugin pattern.
/// This provider exposes a "Widgets" collection of Widget items
/// discovered from .widget files in the target directory.
///
/// To use in a .cop file:
///   import sample
///   foreach Widgets => '{item.Name} ({item.Category})'
/// </summary>
public class SampleProvider : DataProvider
{

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

    public override async ValueTask<object?> QueryAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Skip if the engine doesn't need Widgets
        if (query.Collection != null &&
            query.Collection != "Widgets")
            return new Dictionary<string, List<object>>();

        var widgets = new List<object>();
        var rootPath = query.RootPath ?? ".";

        if (Directory.Exists(rootPath))
        {
            foreach (var file in Directory.GetFiles(rootPath, "*.widget", SearchOption.AllDirectories))
            {
                // Read the file — each line is "key=value"
                var props = new Dictionary<string, string>();
                foreach (var line in await File.ReadAllLinesAsync(file, cancellationToken))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                        props[parts[0].Trim()] = parts[1].Trim();
                }

                widgets.Add(new DataObject("Widget", new Dictionary<string, object?>
                {
                    ["Name"] = props.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(file)),
                    ["Category"] = props.GetValueOrDefault("category", "uncategorized"),
                    ["Size"] = int.TryParse(props.GetValueOrDefault("size", "0"), out var s) ? (long)s : 0L,
                    ["FilePath"] = Path.GetRelativePath(rootPath, file).Replace('\\', '/'),
                }));
            }
        }

        return new Dictionary<string, List<object>> { ["Widgets"] = widgets };
    }

    public override string ToString() => "SampleProvider";
}
