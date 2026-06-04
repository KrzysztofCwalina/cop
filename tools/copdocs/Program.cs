using CopDocs;

// copdocs - generates docs/reference.html from package .cop files
//
// Usage: copdocs <package-dir>... [--output <path>] [--overrides <path>]
//
// Each <package-dir> is scanned recursively for packages (dirs with a manifest .md).
// --output defaults to docs/reference.html relative to the first package root.
// --overrides points to a JSON file with supplementary metadata (overviews, samples, etc.)

var packageDirs = new List<string>();
string? outputPath = null;
string? overridesPath = null;
string? repoUrl = null;

// Parse arguments
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--output" && i + 1 < args.Length)
        outputPath = args[++i];
    else if (args[i] == "--overrides" && i + 1 < args.Length)
        overridesPath = args[++i];
    else if (args[i] == "--repo-url" && i + 1 < args.Length)
        repoUrl = args[++i];
    else if (!args[i].StartsWith("--"))
        packageDirs.Add(args[i]);
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
var discoveredPackages = new List<(string Dir, string Id, string Category)>();

// Detect repo root and source URL for linking
string? repoRoot = null;
string? repoBaseUrl = null;
var firstDir = Path.GetFullPath(packageDirs[0]);
try
{
    var dir = new DirectoryInfo(firstDir);
    while (dir != null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
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
            var gitConfigPath = Path.Combine(repoRoot, ".git", "config");
            if (File.Exists(gitConfigPath))
            {
                var gitConfig = File.ReadAllText(gitConfigPath);
                var match = System.Text.RegularExpressions.Regex.Match(gitConfig, @"url\s*=\s*(https://github\.com/[^\s]+?)(?:\.git)?$", System.Text.RegularExpressions.RegexOptions.Multiline);
                if (match.Success)
                    repoBaseUrl = match.Groups[1].Value + "/blob/master";
            }
        }
        catch { /* ignore */ }
    }
}

var extractor = new PackageExtractor(repoBaseUrl, repoRoot);

foreach (var dir in packageDirs)
{
    var fullDir = Path.GetFullPath(dir);
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
    var json = File.ReadAllText(overridesPath);
    var options = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };
    overrides = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, PackageEntry>>(json, options);
    Console.Error.WriteLine($"Loaded overrides for {overrides?.Count ?? 0} packages");
}

// Extract data from all packages
var referenceData = new ReferenceData();
var categoryOrder = new[] { "Core", "Code", ".NET", "Python", "JavaScript", "Cop", "TypeSpec", "Misc" };
var categoryPackages = categoryOrder.ToDictionary(c => c, _ => new List<string>());

foreach (var (pkgDir, pkgId, category) in discoveredPackages.OrderBy(p => p.Id))
{
    Console.Error.WriteLine($"  Extracting: {pkgId} ({category})");
    var entry = extractor.Extract(pkgDir);

    // Apply overrides
    if (overrides != null && overrides.TryGetValue(pkgId, out var overrideEntry))
    {
        MergeOverrides(entry, overrideEntry);
    }

    referenceData.Packages[pkgId] = entry;

    if (categoryPackages.ContainsKey(category))
        categoryPackages[category].Add(pkgId);
    else
        categoryPackages["Misc"].Add(pkgId);
}

// Build categories (skip empty ones)
foreach (var catName in categoryOrder)
{
    if (categoryPackages[catName].Count > 0)
    {
        referenceData.Categories.Add(new Category
        {
            Name = catName,
            PackageIds = categoryPackages[catName]
        });
    }
}

// Determine output path
outputPath ??= "docs/reference.html";
outputPath = Path.GetFullPath(outputPath);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

// Generate HTML
var generator = new HtmlGenerator();
generator.Generate(referenceData, outputPath);

var totalChecks = referenceData.Packages.Values.Sum(p => p.Checks.Count);
var totalTypes = referenceData.Packages.Values.Sum(p => p.Types.Count);
var totalPredicates = referenceData.Packages.Values.Sum(p => p.Predicates.Count);

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
        var manifest = extractor.GetManifest(rootDir);
        var id = manifest.Name ?? Path.GetFileName(rootDir);
        var category = PackageExtractor.GetCategory(rootDir, manifest);
        packages.Add((rootDir, id, category));
        return;
    }

    // Otherwise scan subdirectories
    foreach (var subDir in Directory.GetDirectories(rootDir))
    {
        if (IsPackageDir(subDir))
        {
            var manifest = extractor.GetManifest(subDir);
            var id = manifest.Name ?? Path.GetFileName(subDir);
            var category = PackageExtractor.GetCategory(subDir, manifest);
            packages.Add((subDir, id, category));
        }
        else
        {
            // Recurse one more level (for packages/dotnet/csharp-checks/ structure)
            foreach (var nestedDir in Directory.GetDirectories(subDir))
            {
                if (IsPackageDir(nestedDir))
                {
                    var manifest = extractor.GetManifest(nestedDir);
                    var id = manifest.Name ?? Path.GetFileName(nestedDir);
                    var category = PackageExtractor.GetCategory(nestedDir, manifest);
                    packages.Add((nestedDir, id, category));
                }
            }
        }
    }
}

static bool IsPackageDir(string dir)
{
    var dirName = Path.GetFileName(dir);
    // Skip hidden/special directories
    if (dirName.StartsWith(".")) return false;

    // A package has cop.json
    return File.Exists(Path.Combine(dir, Cop.Core.PackageMetadata.MetadataFileName));
}

static void MergeOverrides(PackageEntry target, PackageEntry overrideEntry)
{
    if (!string.IsNullOrEmpty(overrideEntry.Overview))
        target.Overview = overrideEntry.Overview;
    if (!string.IsNullOrEmpty(overrideEntry.Label))
        target.Label = overrideEntry.Label;
    if (overrideEntry.Samples.Count > 0)
        target.Samples = overrideEntry.Samples;
    if (overrideEntry.Collections.Count > 0)
        target.Collections = overrideEntry.Collections;

    // Merge type descriptions from overrides
    foreach (var (typeName, typeOverride) in overrideEntry.Types)
    {
        if (target.Types.TryGetValue(typeName, out var existingType))
        {
            if (!string.IsNullOrEmpty(typeOverride.Desc))
                existingType.Desc = typeOverride.Desc;
            // Merge prop descriptions
            foreach (var propOverride in typeOverride.Props)
            {
                var existingProp = existingType.Props.FirstOrDefault(p => p.Name == propOverride.Name);
                if (existingProp != null && !string.IsNullOrEmpty(propOverride.Desc))
                    existingProp.Desc = propOverride.Desc;
            }
        }
        else
        {
            target.Types[typeName] = typeOverride;
        }
    }

    // Merge predicate descriptions
    foreach (var predOverride in overrideEntry.Predicates)
    {
        var existing = target.Predicates.FirstOrDefault(p => p.Name == predOverride.Name);
        if (existing != null && !string.IsNullOrEmpty(predOverride.Desc))
            existing.Desc = predOverride.Desc;
        else if (existing == null)
            target.Predicates.Add(predOverride);
    }

    // Merge function descriptions
    foreach (var fnOverride in overrideEntry.Functions)
    {
        var existing = target.Functions.FirstOrDefault(f => f.Name == fnOverride.Name);
        if (existing != null && !string.IsNullOrEmpty(fnOverride.Desc))
            existing.Desc = fnOverride.Desc;
        else if (existing == null)
            target.Functions.Add(fnOverride);
    }

    // Merge check descriptions
    foreach (var checkOverride in overrideEntry.Checks)
    {
        var existing = target.Checks.FirstOrDefault(c => c.Name == checkOverride.Name);
        if (existing != null && !string.IsNullOrEmpty(checkOverride.Desc))
            existing.Desc = checkOverride.Desc;
        else if (existing == null)
            target.Checks.Add(checkOverride);
    }

    // Merge command descriptions
    foreach (var cmdOverride in overrideEntry.Commands)
    {
        var existing = target.Commands.FirstOrDefault(c => c.Name == cmdOverride.Name);
        if (existing != null && !string.IsNullOrEmpty(cmdOverride.Desc))
            existing.Desc = cmdOverride.Desc;
        else if (existing == null)
            target.Commands.Add(cmdOverride);
    }
}
