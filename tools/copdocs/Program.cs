// Generates the package reference application from package source and documentation.

using System.Text.Json;
using System.Text.RegularExpressions;

using Cop.Tools.Docs;

// copdocs - generates docs/reference.html from package .cop files
//
// Usage: copdocs <package-dir>... [--output <path>] [--overrides <path>]
//
// Each <package-dir> is scanned recursively for packages (dirs with a manifest .md).
// --output defaults to docs/reference.html relative to the first package root.
// --overrides points to a JSON file with supplementary metadata (overviews, samples, etc.)

List<string> packageDirs = new List<string>();
string? outputPath = null;
string? overridesPath = null;
string? repoUrl = null;

// Parse arguments
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--output" && i + 1 < args.Length)
    {
        outputPath = args[++i];
    }
    else if (args[i] == "--overrides" && i + 1 < args.Length)
    {
        overridesPath = args[++i];
    }
    else if (args[i] == "--repo-url" && i + 1 < args.Length)
    {
        repoUrl = args[++i];
    }
    else if (!args[i].StartsWith("--"))
    {
        packageDirs.Add(args[i]);
    }
}

if (packageDirs.Count == 0)
{
    Console.Error.WriteLine("Usage: copdocs <package-dir>... [--output <path>] [--overrides <path>]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  <package-dir>  One or more directories to scan for packages.");
    Console.Error.WriteLine("                 A package is a directory with a <name>.md manifest and src/*.cop files.");
    Console.Error.WriteLine("  --output       Output HTML file path (default: docs/reference.html)");
    Console.Error.WriteLine("  --overrides    JSON file with supplementary metadata (overviews, samples, descriptions)");
    return 1;
}

// Discover all packages
List<(string Dir, string Id, string Category)> discoveredPackages = new List<(string Dir, string Id, string Category)>();

// Detect repo root and source URL for linking
string? repoRoot = null;
string? repoBaseUrl = null;
string firstDir = Path.GetFullPath(packageDirs[0]);
try
{
    DirectoryInfo? dir = new DirectoryInfo(firstDir);
    while (dir != null)
    {
        string gitPath = Path.Combine(dir.FullName, ".git");
        if (Directory.Exists(gitPath) || File.Exists(gitPath))
        {
            repoRoot = dir.FullName;
            break;
        }
        dir = dir.Parent;
    }
}
catch { /* ignore */ }

if (repoRoot != null)
{
    if (repoUrl != null)
    {
        // User-provided URL: use as blob base
        repoBaseUrl = repoUrl.TrimEnd('/') + "/blob/master";
    }
    else
    {
        // Auto-detect from git remote
        try
        {
            string gitConfigPath = Path.Combine(repoRoot, ".git", "config");
            if (File.Exists(gitConfigPath))
            {
                string gitConfig = File.ReadAllText(gitConfigPath);
                Match match = System.Text.RegularExpressions.Regex.Match(gitConfig, @"url\s*=\s*(https://github\.com/[^\s]+?)(?:\.git)?$", System.Text.RegularExpressions.RegexOptions.Multiline);
                if (match.Success)
                {
                    repoBaseUrl = match.Groups[1].Value + "/blob/master";
                }
            }
        }
        catch { /* ignore */ }
    }
}

PackageExtractor extractor = new PackageExtractor(repoBaseUrl, repoRoot);

foreach (string dir in packageDirs)
{
    string fullDir = Path.GetFullPath(dir);
    if (!Directory.Exists(fullDir))
    {
        Console.Error.WriteLine($"Warning: Directory not found: {fullDir}");
        continue;
    }
    DiscoverPackages(fullDir, discoveredPackages, extractor);
}

if (discoveredPackages.Count == 0)
{
    Console.Error.WriteLine("Error: No packages found in the specified directories.");
    return 1;
}

// Load overrides if specified
Dictionary<string, PackageEntry>? overrides = null;
if (overridesPath != null && File.Exists(overridesPath))
{
    string json = File.ReadAllText(overridesPath);
    JsonSerializerOptions options = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };
    overrides = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, PackageEntry>>(json, options);
    Console.Error.WriteLine($"Loaded overrides for {overrides?.Count ?? 0} packages");
}

// Extract data from all packages
ReferenceData referenceData = new ReferenceData();

// Categories mirror the top-level folders under packages/. Preferred groups are listed first
// in a natural reading order; any other discovered folder is appended alphabetically.
string[] preferredOrder = new[] { "Core", "Languages", "Formats", "Checks", "Tools" };
Dictionary<string, List<string>> categoryPackages = new Dictionary<string, List<string>>();

foreach ((string? pkgDir, string? pkgId, string? category) in discoveredPackages.OrderBy(p => p.Id))
{
    Console.Error.WriteLine($"  Extracting: {pkgId} ({category})");
    PackageEntry entry = extractor.Extract(pkgDir);

    // Apply overrides
    if (overrides != null && overrides.TryGetValue(pkgId, out PackageEntry? overrideEntry))
    {
        MergeOverrides(entry, overrideEntry);
    }

    referenceData.Packages[pkgId] = entry;

    if (!categoryPackages.TryGetValue(category, out List<string>? list))
    {
        categoryPackages[category] = list = [];
    }

    list.Add(pkgId);
}

// Build categories: preferred folders first, then any others alphabetically.
List<string> orderedCategories = preferredOrder
    .Where(categoryPackages.ContainsKey)
    .Concat(categoryPackages.Keys.Where(k => !preferredOrder.Contains(k)).OrderBy(k => k))
    .ToList();

foreach (string? catName in orderedCategories)
{
    referenceData.Categories.Add(new Category
    {
        Name = catName,
        PackageIds = categoryPackages[catName]
    });
}

// Determine output path
outputPath ??= "docs/reference.html";
outputPath = Path.GetFullPath(outputPath);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

// Generate HTML
HtmlGenerator generator = new HtmlGenerator();
generator.Generate(referenceData, outputPath);

int totalChecks = referenceData.Packages.Values.Sum(p => p.Checks.Count);
int totalTypes = referenceData.Packages.Values.Sum(p => p.Types.Count);
int totalPredicates = referenceData.Packages.Values.Sum(p => p.Predicates.Count);

Console.Error.WriteLine();
Console.Error.WriteLine($"Generated: {outputPath}");
Console.Error.WriteLine($"  {referenceData.Packages.Count} packages, {totalTypes} types, {totalPredicates} predicates, {totalChecks} checks");
return 0;

// === Helper methods ===

static void DiscoverPackages(string rootDir, List<(string Dir, string Id, string Category)> packages, PackageExtractor extractor)
{
    // Check if rootDir itself is a package
    if (IsPackageDir(rootDir))
    {
        PackageExtractor.PackageManifest manifest = extractor.GetManifest(rootDir);
        string id = manifest.Name ?? Path.GetFileName(rootDir);
        string category = PackageExtractor.GetCategory(rootDir);
        packages.Add((rootDir, id, category));
        return;
    }

    // Otherwise scan subdirectories
    foreach (string subDir in Directory.GetDirectories(rootDir))
    {
        if (IsPackageDir(subDir))
        {
            PackageExtractor.PackageManifest manifest = extractor.GetManifest(subDir);
            string id = manifest.Name ?? Path.GetFileName(subDir);
            string category = PackageExtractor.GetCategory(subDir);
            packages.Add((subDir, id, category));
        }
        else
        {
            // Recurse one more level (for packages/dotnet/csharp-checks/ structure)
            foreach (string nestedDir in Directory.GetDirectories(subDir))
            {
                if (IsPackageDir(nestedDir))
                {
                    PackageExtractor.PackageManifest manifest = extractor.GetManifest(nestedDir);
                    string id = manifest.Name ?? Path.GetFileName(nestedDir);
                    string category = PackageExtractor.GetCategory(nestedDir);
                    packages.Add((nestedDir, id, category));
                }
            }
        }
    }
}

static bool IsPackageDir(string dir)
{
    string dirName = Path.GetFileName(dir);
    // Skip hidden/special directories
    if (dirName.StartsWith("."))
    {
        return false;
    }

    // A package has cop.json
    return File.Exists(Path.Combine(dir, Cop.Core.PackageMetadata.MetadataFileName));
}

static void MergeOverrides(PackageEntry target, PackageEntry overrideEntry)
{
    if (!string.IsNullOrEmpty(overrideEntry.Overview))
    {
        target.Overview = overrideEntry.Overview;
    }

    if (!string.IsNullOrEmpty(overrideEntry.Label))
    {
        target.Label = overrideEntry.Label;
    }

    if (overrideEntry.Samples.Count > 0)
    {
        target.Samples = overrideEntry.Samples;
    }

    if (overrideEntry.Collections.Count > 0)
    {
        target.Collections = overrideEntry.Collections;
    }

    // Merge type descriptions from overrides
    foreach ((string? typeName, TypeEntry? typeOverride) in overrideEntry.Types)
    {
        if (target.Types.TryGetValue(typeName, out TypeEntry? existingType))
        {
            if (!string.IsNullOrEmpty(typeOverride.Desc))
            {
                existingType.Desc = typeOverride.Desc;
            }
            // Merge prop descriptions
            foreach (PropEntry propOverride in typeOverride.Props)
            {
                PropEntry? existingProp = existingType.Props.FirstOrDefault(p => p.Name == propOverride.Name);
                if (existingProp != null && !string.IsNullOrEmpty(propOverride.Desc))
                {
                    existingProp.Desc = propOverride.Desc;
                }
            }
        }
        else
        {
            target.Types[typeName] = typeOverride;
        }
    }

    // Merge predicate descriptions
    foreach (PredicateEntry predOverride in overrideEntry.Predicates)
    {
        PredicateEntry? existing = target.Predicates.FirstOrDefault(p => p.Name == predOverride.Name);
        if (existing != null && !string.IsNullOrEmpty(predOverride.Desc))
        {
            existing.Desc = predOverride.Desc;
        }
        else if (existing == null)
        {
            target.Predicates.Add(predOverride);
        }
    }

    // Merge function descriptions
    foreach (FunctionEntry fnOverride in overrideEntry.Functions)
    {
        FunctionEntry? existing = target.Functions.FirstOrDefault(f => f.Name == fnOverride.Name);
        if (existing != null && !string.IsNullOrEmpty(fnOverride.Desc))
        {
            existing.Desc = fnOverride.Desc;
        }
        else if (existing == null)
        {
            target.Functions.Add(fnOverride);
        }
    }

    // Merge check descriptions
    foreach (CheckEntry checkOverride in overrideEntry.Checks)
    {
        CheckEntry? existing = target.Checks.FirstOrDefault(c => c.Name == checkOverride.Name);
        if (existing != null && !string.IsNullOrEmpty(checkOverride.Desc))
        {
            existing.Desc = checkOverride.Desc;
        }
        else if (existing == null)
        {
            target.Checks.Add(checkOverride);
        }
    }

    // Merge command descriptions
    foreach (CommandEntry cmdOverride in overrideEntry.Commands)
    {
        CommandEntry? existing = target.Commands.FirstOrDefault(c => c.Name == cmdOverride.Name);
        if (existing != null && !string.IsNullOrEmpty(cmdOverride.Desc))
        {
            existing.Desc = cmdOverride.Desc;
        }
        else if (existing == null)
        {
            target.Commands.Add(cmdOverride);
        }
    }
}
