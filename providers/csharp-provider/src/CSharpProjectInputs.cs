using System.Xml.Linq;
using Cop.Core;

namespace Cop.Providers;

internal static class CSharpProjectInputs
{
    private static bool IsInTarget(XElement element) =>
        element.AncestorsAndSelf().Any(parent => parent.Name.LocalName == "Target");

    private static bool HasExpression(string value) =>
        value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal)
        || value.Contains("%(", StringComparison.Ordinal);

    internal static List<string> UnsupportedSettings(XDocument? document)
    {
        var elements = document?.Descendants().ToList() ?? [];
        var settings = new List<string>();
        if (elements.Any(e => e.Name.LocalName == "TargetFrameworks"
            && e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length > 1))
            settings.Add("multiple target frameworks (<TargetFrameworks>)");
        if (elements.Any(e => e.Name.LocalName == "Import" && !IsInTarget(e)))
            settings.Add("imported build files (<Import>)");
        if (elements.Any(e => !IsInTarget(e) && e.Attribute("Condition") is not null))
            settings.Add("conditional build settings (Condition attributes)");
        if (elements.Any(e => e.Name.LocalName == "Compile" && IsInTarget(e)))
            settings.Add("build-generated source-file rules (<Target>/<Compile>)");
        if (elements.Any(e => IsInTarget(e) && e.Name.LocalName is
            "DefineConstants" or "ImplicitUsings" or "Using" or "Reference" or "ProjectReference" or "PackageReference"
            or "TargetFramework" or "TargetFrameworks" or "EnableDefaultItems" or "EnableDefaultCompileItems"
            or "DefaultItemExcludes" or "DefaultExcludesInProjectFolder"))
            settings.Add("build-generated analysis settings (<Target>)");
        if (elements.Any(e => !IsInTarget(e) && e.Name.LocalName == "Compile"
            && e.Attributes().Any(a => a.Name.LocalName is "Include" or "Exclude" or "Remove" or "Update"
                && HasExpression(a.Value))))
            settings.Add("source-file rules with unevaluated expressions (<Compile>)");
        if (elements.Any(e => !IsInTarget(e)
            && e.Name.LocalName is "EnableDefaultItems" or "EnableDefaultCompileItems" or "DefaultItemExcludes" or "DefaultExcludesInProjectFolder"
            && HasExpression(e.Value)))
            settings.Add("source-file defaults with unevaluated expressions");
        return settings;
    }

    internal static List<string>? Resolve(string project, XDocument? document, IReadOnlyList<string> sourceFiles, string root)
    {
        if (document is null || UnsupportedSettings(document).Any(setting =>
            setting != "multiple target frameworks (<TargetFrameworks>)"))
            return null;

        var directory = Path.GetDirectoryName(project)!;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var elements = document.Descendants().Where(e => !IsInTarget(e)).ToList();
        string? Property(string name) => elements.LastOrDefault(e =>
            e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim();
        bool Matches(string path, string patterns) => patterns.Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(pattern => PathGlob.IsMatch(path, Path.GetFullPath(
                pattern.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar), directory),
                OperatingSystem.IsWindows()));

        var selected = new HashSet<string>(comparer);
        if (!string.Equals(Property("EnableDefaultItems"), "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Property("EnableDefaultCompileItems"), "false", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var path in sourceFiles)
            {
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                    continue;
                var parts = relative.Split('/');
                if (parts.SkipLast(1).Any(part => comparer.Equals(part, "bin") || comparer.Equals(part, "obj")
                    || part.StartsWith('.')))
                    continue;
                if (Matches(path, Property("DefaultItemExcludes") ?? "")
                    || Matches(path, Property("DefaultExcludesInProjectFolder") ?? ""))
                    continue;
                selected.Add(path);
            }
        }

        foreach (var item in elements.Where(e => e.Name.LocalName == "Compile"))
        {
            if (item.Attribute("Include")?.Value is { } include)
            {
                foreach (var pattern in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var absolute = Path.GetFullPath(pattern.Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar), directory);
                    int wildcard = absolute.IndexOfAny(['*', '?']);
                    var prefix = wildcard < 0 ? absolute : absolute[..wildcard];
                    var sourceDirectory = prefix.EndsWith(Path.DirectorySeparatorChar)
                        ? prefix : Path.GetDirectoryName(prefix)!;
                    var relative = Path.GetRelativePath(root, sourceDirectory);
                    if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        || Path.IsPathRooted(relative))
                        ProviderWarnings.ReportOnce("csharp", project,
                            $"Project '{project}' includes sources outside the analysis target '{root}' (<Compile Include=\"{pattern}\">). " +
                            "Those files are not read, so project symbols may be incomplete. " +
                            "Run with '-t' pointing to a common parent directory that contains the project and its linked sources.");
                }
                foreach (var path in sourceFiles.Where(path => Matches(path, include)
                    && !Matches(path, item.Attribute("Exclude")?.Value ?? "")))
                    selected.Add(path);
            }
            if (item.Attribute("Remove")?.Value is { } remove)
                selected.RemoveWhere(path => Matches(path, remove));
        }
        return selected.Order(comparer).ToList();
    }
}
