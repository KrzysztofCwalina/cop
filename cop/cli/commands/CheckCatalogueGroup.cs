// Groups catalogue checks into a folder/package tree.

namespace Cop.Cli.Commands;

internal sealed record CheckCatalogueGroup(
    string Id,
    string Name,
    string Origin,
    string Path,
    IReadOnlyList<CheckCatalogueGroup> Groups,
    IReadOnlyList<string> CheckIds)
{
    /// <summary>Groups checks by origin and folder into a tree.</summary>
    public static IReadOnlyList<CheckCatalogueGroup> Build(
        IReadOnlyList<CheckCatalogueDefinition> checks)
    {
        GroupBuilder root = new("", "", "", "");
        foreach (CheckCatalogueDefinition check in checks)
        {
            string[] parts = check.FilePath.Replace('\\', '/').Split('/');
            string[] folders = parts[..^1];
            if (check.Origin != "workspace")
            {
                folders = folders.Length > 0 && folders[0] == check.Origin
                    ? folders[1..]
                    : folders;
                if (folders.Length > 0 && folders[0] == "src")
                {
                    folders = folders[1..];
                }
                folders = [check.Origin, .. folders];
            }
            else if (folders.Length == 0)
            {
                folders = ["Repository"];
            }

            GroupBuilder group = root;
            for (int index = 0; index < folders.Length; index++)
            {
                string path = string.Join('/', folders[..(index + 1)]);
                string key = $"{check.Origin}:{path}";
                if (!group.Children.TryGetValue(key, out GroupBuilder? child))
                {
                    child = new GroupBuilder(key, folders[index], check.Origin, path);
                    group.Children.Add(key, child);
                }
                group = child;
            }
            group.CheckIds.Add(check.Id);
        }

        return root.Children.Values.Select(group => group.Build())
            .OrderBy(group => group.Origin == "workspace" ? 0 : 1)
            .ThenBy(group => group.Name, StringComparer.Ordinal)
            .ToList();
    }

    private sealed class GroupBuilder(string id, string name, string origin, string path)
    {
        public SortedDictionary<string, GroupBuilder> Children { get; } =
            new(StringComparer.Ordinal);
        public List<string> CheckIds { get; } = [];

        public CheckCatalogueGroup Build() => new(
            $"group:{id}", name, origin, path,
            Children.Values.Select(group => group.Build()).ToList(),
            CheckIds.Order(StringComparer.Ordinal).ToList());
    }
}
