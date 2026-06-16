using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Threading.Tasks;

namespace Cop.Providers;

/// <summary>
/// C# source code provider. Uses Roslyn semantic analysis for type resolution.
/// Creates a CSharpCompilation from all source files to provide accurate type information.
/// </summary>
public class CSharpProvider : DataProvider
{

    public override ReadOnlyMemory<byte> GetSchema() => CodeSchema.GetJson();

    public override RuntimeBindings GetRuntimeBindings() => CodeBindings.Build();

    public override object? Query(ProviderQuery query)
    {
        if (query.RootPath is null)
            return new Dictionary<string, List<object>>();

        var rootPath = query.RootPath;
        var excluded = query.ExcludedDirectories;

        // 1. Discover source files
        var filePaths = new List<string>();
        var parsers = new SourceParserRegistry();
        parsers.Register(new CSharpSourceParser());
        CodeCollectionBuilder.CollectSourceFiles(rootPath, parsers, excluded, filePaths);

        // Retry with backoff if 0 files found (handles antivirus interference)
        // Only retry if directory actually contains .cs files (not just other file types)
        if (filePaths.Count == 0 && Directory.Exists(rootPath))
        {
            bool hasCsFiles = Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories).Any();
            if (hasCsFiles)
            {
                int[] delays = [200, 1000, 3000];
                foreach (var delay in delays)
                {
                    Thread.Sleep(delay);
                    CodeCollectionBuilder.CollectSourceFiles(rootPath, parsers, excluded, filePaths);
                    if (filePaths.Count > 0) break;
                }
                if (filePaths.Count == 0)
                    throw new InvalidOperationException(
                        $"Provider scan found 0 source files in '{rootPath}' after 3 retries. " +
                        "This likely indicates filesystem interference (antivirus, file locks).");
            }
        }

        // 2. Parse syntax trees and read source text
        var syntaxTrees = new List<(SyntaxTree Tree, string FilePath, string Text)>();
        var fileErrors = new List<string>();
        foreach (var filePath in filePaths)
        {
            if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                var tree = CSharpSyntaxTree.ParseText(text, path: filePath);
                syntaxTrees.Add((tree, filePath, text));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                fileErrors.Add($"Failed to read '{filePath}': {ex.Message}");
            }
        }

        if (fileErrors.Count > 0)
        {
            foreach (var err in fileErrors)
                Console.Error.WriteLine(err);
            if (syntaxTrees.Count == 0)
                throw new InvalidOperationException(
                    $"Failed to read all {fileErrors.Count} source file(s). First error: {fileErrors[0]}");
        }

        // 3. Create Roslyn compilation with framework + project references
        var references = GetReferences(rootPath);
        var compilation = CSharpCompilation.Create(
            "CopAnalysis",
            syntaxTrees.Select(t => t.Tree),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        // 4. Parse each file using its semantic model. Files are independent and the
        //    Roslyn compilation is immutable, so GetSemanticModel + extraction run in
        //    parallel. Semantic binding over every file is the dominant analysis cost.
        var parsedFiles = new SourceFile?[syntaxTrees.Count];
        Parallel.For(0, syntaxTrees.Count, i =>
        {
            var (tree, filePath, text) = syntaxTrees[i];
            SemanticModel? model = null;
            try { model = compilation.GetSemanticModel(tree); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"Warning: Semantic analysis failed for '{filePath}': {ex.Message}");
            }

            // CSharpSourceParser is stateless (all helpers static); a fresh instance
            // per file keeps the parallel work trivially thread-safe.
            var sourceFile = new CSharpSourceParser().ParseWithSemantics(filePath, text, tree, model);
            if (sourceFile is null) return;

            var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            var normalized = sourceFile with { Path = relativePath };
            LinkReferences(normalized); // mutates only this file — safe in parallel
            parsedFiles[i] = normalized;
        });

        var sourceFiles = new List<SourceFile>(syntaxTrees.Count);
        foreach (var sf in parsedFiles)
            if (sf is not null) sourceFiles.Add(sf);

        sourceFiles.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));

        // 5. Build collections
        var collections = CodeCollectionBuilder.ExtractCollections(sourceFiles, query.Collection, query.CollectionFilters);

        // 6. Discover projects and link files
        var projects = CSharpProjectDiscovery.Discover(rootPath, excluded);
        LinkFilesToProjects(collections, projects, rootPath);
        if (query.Collection == null || query.Collection == "Projects")
            collections["Projects"] = projects.Cast<object>().ToList();

        return collections;
    }

    public override void RegisterCapabilities(TypeRegistry registry, string rootPath)
    {
        registry.RegisterDocumentLoader(path =>
        {
            var sourceFile = AssemblyApiReader.ReadAssembly(path);
            for (int i = 0; i < sourceFile.Types.Count; i++)
                sourceFile.Types[i] = sourceFile.Types[i] with { File = sourceFile };
            return [new Document(path, sourceFile.Language, sourceFile)];
        });
    }

    private static List<MetadataReference> GetReferences(string rootPath)
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Framework references — load all DLLs from the runtime directory
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        if (Directory.Exists(runtimeDir))
        {
            foreach (var dll in Directory.EnumerateFiles(runtimeDir, "*.dll"))
            {
                if (seen.Add(Path.GetFileName(dll)))
                {
                    try { references.Add(MetadataReference.CreateFromFile(dll)); }
                    catch { }
                }
            }
        }

        // Project dependency references — resolve from project.assets.json
        try
        {
            var assetsPath = FindProjectAssets(rootPath);
            if (assetsPath != null)
                LoadPackageReferences(assetsPath, references, seen);
        }
        catch { }

        return references;
    }

    /// <summary>
    /// Finds project.assets.json by checking obj/ in the project directory,
    /// or artifacts/obj/{ProjectName}/ for repos with redirected output.
    /// </summary>
    private static string? FindProjectAssets(string rootPath)
    {
        // Find .csproj in rootPath or parent
        string? csprojDir = null;
        string? csprojName = null;

        var csproj = Directory.EnumerateFiles(rootPath, "*.csproj").FirstOrDefault();
        if (csproj != null)
        {
            csprojDir = rootPath;
            csprojName = Path.GetFileNameWithoutExtension(csproj);
        }
        else
        {
            var parent = Directory.GetParent(rootPath);
            if (parent != null)
            {
                csproj = Directory.EnumerateFiles(parent.FullName, "*.csproj").FirstOrDefault();
                if (csproj != null)
                {
                    csprojDir = parent.FullName;
                    csprojName = Path.GetFileNameWithoutExtension(csproj);
                }
            }
        }

        if (csprojDir == null || csprojName == null) return null;

        // Standard location: obj/project.assets.json
        var standard = Path.Combine(csprojDir, "obj", "project.assets.json");
        if (File.Exists(standard)) return standard;

        // Redirected output (e.g., Azure SDK): artifacts/obj/{ProjectName}/project.assets.json
        // Walk up looking for artifacts/obj/{ProjectName}/
        var dir = csprojDir;
        for (int i = 0; i < 6; i++)
        {
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
            var candidate = Path.Combine(dir, "artifacts", "obj", csprojName, "project.assets.json");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Parses project.assets.json to resolve package DLL paths from the NuGet cache.
    /// </summary>
    private static void LoadPackageReferences(string assetsPath, List<MetadataReference> references, HashSet<string> seen)
    {
        var json = File.ReadAllText(assetsPath);
        var doc = System.Text.Json.JsonDocument.Parse(json);

        // Get package folder paths
        var packageFolders = new List<string>();
        if (doc.RootElement.TryGetProperty("packageFolders", out var folders))
        {
            foreach (var folder in folders.EnumerateObject())
                packageFolders.Add(folder.Name);
        }
        if (packageFolders.Count == 0) return;

        // Get library paths (package-id/version → path in cache)
        var libraryPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("libraries", out var libraries))
        {
            foreach (var lib in libraries.EnumerateObject())
            {
                if (lib.Value.TryGetProperty("path", out var pathProp))
                    libraryPaths[lib.Name] = pathProp.GetString() ?? lib.Name;
            }
        }

        // Get compile assets from the first target framework
        if (!doc.RootElement.TryGetProperty("targets", out var targets)) return;
        var firstTarget = targets.EnumerateObject().FirstOrDefault();

        foreach (var package in firstTarget.Value.EnumerateObject())
        {
            if (!package.Value.TryGetProperty("compile", out var compile)) continue;
            if (!libraryPaths.TryGetValue(package.Name, out var libPath)) continue;

            foreach (var asset in compile.EnumerateObject())
            {
                var relativeDll = asset.Name;
                if (!relativeDll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (relativeDll.Contains("_._")) continue;

                // Resolve full path: packageFolder + libraryPath + assetPath
                foreach (var folder in packageFolders)
                {
                    var fullPath = Path.Combine(folder, libPath, relativeDll);
                    if (File.Exists(fullPath) && seen.Add(Path.GetFileName(fullPath)))
                    {
                        try { references.Add(MetadataReference.CreateFromFile(fullPath)); }
                        catch { }
                        break;
                    }
                }
            }
        }
    }

    private static void LinkReferences(SourceFile file)
    {
        for (int i = 0; i < file.Statements.Count; i++)
        {
            file.Statements[i].File = file;
            var stmtLine = file.Statements[i].Line;
            if (stmtLine >= 2 && file.CommentLines.Contains(stmtLine - 1))
            {
                var prevLineText = file.Lines[stmtLine - 2];
                var idx = prevLineText.IndexOf("cop-ignore:", StringComparison.Ordinal);
                if (idx >= 0)
                    file.Statements[i].CopIgnore = prevLineText[(idx + "cop-ignore:".Length)..].Trim();
            }
        }

        for (int i = 0; i < file.Types.Count; i++)
            file.Types[i] = file.Types[i] with { File = file };

        for (int i = 0; i < file.Regions.Count; i++)
        {
            if (file.Regions[i].File is null)
                file.Regions[i] = file.Regions[i] with { File = file };
        }
    }

    private static void LinkFilesToProjects(Dictionary<string, List<object>> collections, List<ProjectInfo> projects, string rootPath)
    {
        if (!collections.TryGetValue("Files", out var files) || files.Count == 0)
            return;

        var projectDirs = new List<(string Dir, string Name)>();
        foreach (var proj in projects)
        {
           var projFilePath = Path.Combine(rootPath, proj.Path.Replace('/', '\\'));
           var projDir = Path.GetDirectoryName(projFilePath);
           if (projDir is not null)
           {
               var relDir = Path.GetRelativePath(rootPath, projDir).Replace('\\', '/');
               if (!relDir.EndsWith('/')) relDir += '/';
               projectDirs.Add((relDir, proj.Name));
           }
        }

        foreach (var fileObj in files)
        {
           var file = (SourceFile)fileObj;
           var filePath = file.Path;
           foreach (var (dir, name) in projectDirs)
           {
               if (filePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                   file.Projects.Add(name);
           }
        }
    }
}
