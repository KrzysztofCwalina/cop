// Provides demand-driven C# source facts and compiler binding.

using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cop.Providers;

/// <summary>
/// C# source code provider. Uses Roslyn semantic analysis for type resolution.
/// Creates a CSharpCompilation from all source files to provide accurate type information.
/// </summary>
public class CSharpProvider : DataProvider
{

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => CSharpSchema.GetJson();

    /// <inheritdoc />
    public override RuntimeBindings GetRuntimeBindings() => CSharpBindings.Build();

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, object?> GetQueryMetadata(ProviderQuery query) =>
        CodeBindings.SourceMetadata(query, ".cs");
    /// <inheritdoc />
    public override string QueryResultType => "Codebase";

    private readonly Dictionary<string, (List<SourceFile> Files, List<ProjectInfo> Projects)> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeferredSourceSnapshot> _deferredSnapshots = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override ValueTask<object?> QueryDeferredAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        if (query.RootPath is null)
        {
            return ValueTask.FromResult<object?>(new DeferredProviderResult(
                new Dictionary<string, IAsyncEnumerable<object>>()));
        }

        string root = Path.GetFullPath(query.RootPath);
        string key = root + "\n" +
            string.Join("\n", (query.ExcludedDirectories ?? new HashSet<string>()).Order(StringComparer.Ordinal));
        if (!_deferredSnapshots.TryGetValue(key, out DeferredSourceSnapshot? snapshot))
        {
            CSharpSourceParser parser = new CSharpSourceParser();
            SourceParserRegistry parsers = new SourceParserRegistry();
            parsers.Register(parser);
            ConcurrentDictionary<string, AsyncProviderValue<(SyntaxTree Tree, string Text)>> trees =
                new(StringComparer.Ordinal);
            int parsedCount = 0;
            CSharpSourceTextCache texts = new CSharpSourceTextCache(key);
            DeferredSourceSnapshot sources = null!;
            ValueTask<(SyntaxTree Tree, string Text)> SyntaxAsync(
                string path, CancellationToken token)
            {
                ExecutionStatistics.RecordInputFile(path);
                return trees.GetOrAdd(path, _ => new(async readToken =>
                {
                string text = await texts.ReadAsync(path, readToken).ConfigureAwait(false);
                (SyntaxTree, string text) parsed = (CSharpSyntaxTree.ParseText(
                    text, CSharpSourceParser.ParseOptions, path: path,
                    cancellationToken: readToken), text);
                if (Interlocked.Increment(ref parsedCount) == sources.Paths.Count)
                {
                    texts.Save(sources.Paths);
                }

                if (CopDiagnostics.IsTimingEnabled)
                {
                    Console.Error.WriteLine($"[provider-parse] csharp {Path.GetRelativePath(root, path)}");
                }

                return parsed;
                })).GetValueAsync(token);
            }
            Dictionary<SyntaxTree, CSharpCompilation>? readyCompilations = null;
            AsyncProviderValue<Dictionary<SyntaxTree, CSharpCompilation>> compilations =
                new(async token =>
            {
                List<SyntaxTree> allTrees = [];
                foreach (string path in sources.Paths)
                {
                    allTrees.Add((await SyntaxAsync(path, token).ConfigureAwait(false)).Tree);
                }
                Dictionary<SyntaxTree, CSharpCompilation> result =
                    await CSharpProjectCompilations.CreateAsync(
                        root, allTrees.ToArray(), query.ExcludedDirectories, GetReferences, token)
                        .ConfigureAwait(false);
                if (CopDiagnostics.IsTimingEnabled)
                {
                    Console.Error.WriteLine($"[provider-semantics] csharp compilation files={allTrees.Count}");
                }

                return readyCompilations = result;
            });
            sources = new(parsers, query, async (path, token) =>
            {
                (SyntaxTree tree, string text) = await SyntaxAsync(path, token).ConfigureAwait(false);
                SourceFile? file = parser.ParseWithSemantics(path, text, tree, model: null);
                AsyncProviderValue<bool> preparation = new(async prepareToken =>
                {
                    await compilations.GetValueAsync(prepareToken).ConfigureAwait(false);
                    return true;
                });
                file?.DeferSemanticAnalysis(() => CSharpSourceParser.EnrichWithSemantics(tree,
                    readyCompilations![tree].GetSemanticModel(tree), file.Types, file.Statements));
#pragma warning disable RSEXPERIMENTAL001 // Nullable-analysis control is experimental in the pinned Roslyn version.
                if (file is not null)
                {
                    CSharpSourceParser.DeferTypeNames(tree, file.Statements,
                        () => readyCompilations![tree].GetSemanticModel(tree, SemanticModelOptions.DisableNullableAnalysis),
                        () => readyCompilations![tree].GetSemanticModel(tree));
                    foreach (StatementInfo statement in file.Statements)
                    {
                        statement.DeferSemanticAnalysisAsync(preparation);
                        if (statement is CSharpStatementInfo { RequiresTypeNameAnalysis: true })
                        {
                            statement.DeferTypeNamePreparationAsync(preparation);
                        }
                    }
                    void Attach(TypeDeclaration type)
                    {
                        type.DeferSemanticAnalysisAsync(preparation);
                        foreach (TypeDeclaration nested in type.NestedTypes)
                        {
                            Attach(nested);
                        }
                    }
                    foreach (TypeDeclaration type in file.Types)
                    {
                        Attach(type);
                    }
                }
#pragma warning restore RSEXPERIMENTAL001
                return file;
            }, _ => ValueTask.FromResult(
                CSharpProjectDiscovery.Discover(root, query.ExcludedDirectories, sources.Paths)));
            snapshot = sources;
            _deferredSnapshots.Add(key, snapshot);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(snapshot.Query(query, GetRuntimeBindings()));
    }

    /// <inheritdoc />
    public override Dictionary<string,
        Func<List<object?>, CancellationToken, ValueTask<object?>>>? GetFunctions() => new()
    {
        ["toCodebase"] = (args, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<object?>(ToCodebase(args.Count > 0 ? args[0] : null));
        }
    };

    /// <inheritdoc />
    public override Dictionary<string,
        Func<List<object?>, CancellationToken, ValueTask<object?>>>? GetFunctions(
        AnalysisContext context) => new()
    {
        ["toCodebase"] = (args, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<object?>(ToCodebase(
                args.Count > 0 ? args[0] : null,
                args.Count > 1 && args[1] is string path
                    ? Path.GetFullPath(path, context.RootPath) : null));
        }
    };

    /// <inheritdoc />
    public override async ValueTask<object?> QueryAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        if (query.RootPath is null)
        {
            return new Dictionary<string, List<object>>();
        }

        string key = Path.GetFullPath(query.RootPath) + "\n" +
            string.Join("\n", (query.ExcludedDirectories ?? new HashSet<string>()).Order(StringComparer.Ordinal));
        if (!_snapshots.TryGetValue(key, out (List<SourceFile> Files, List<ProjectInfo> Projects) snapshot))
        {
            List<SourceFile> files = await LoadSourceFilesAsync(query, cancellationToken)
                .ConfigureAwait(false);
            List<ProjectInfo> projects = CSharpProjectDiscovery.Discover(query.RootPath, query.ExcludedDirectories,
                files.Select(file => file.FullPath).ToArray());
            LinkFilesToProjects(new Dictionary<string, List<object>> { ["Files"] = files.Cast<object>().ToList() },
                projects, query.RootPath);
            snapshot = (files, projects);
            _snapshots.Add(key, snapshot);
        }
        Dictionary<string, List<object>> collections = CodeCollectionBuilder.ExtractCollections(snapshot.Files, query.Collection, query.CollectionFilters, query.Filter);
        if (query.Collection is null or "Projects")
        {
            collections["Projects"] = snapshot.Projects.Cast<object>().ToList();
        }

        return ApplyQueryFilters(query, collections);
    }

    private static async ValueTask<List<SourceFile>> LoadSourceFilesAsync(
        ProviderQuery query, CancellationToken cancellationToken)
    {
        string rootPath = query.RootPath!;
        IReadOnlySet<string>? excluded = query.ExcludedDirectories;

        // Optional phase timing for diagnosing slow analyses: enabled at -dd.
        Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        bool _diag = Cop.Core.CopDiagnostics.IsTimingEnabled;
        void Log(string m) { if (_diag) { Console.Error.WriteLine($"[csharp] {m} (+{_sw.ElapsedMilliseconds}ms)"); } }

        // 1. Discover source files
        List<string> filePaths = new List<string>();
        SourceParserRegistry parsers = new SourceParserRegistry();
        parsers.Register(new CSharpSourceParser());
        CodeCollectionBuilder.CollectSourceFiles(rootPath, parsers, excluded, filePaths);
        Log($"discovered {filePaths.Count} source files");

        // Retry with backoff if 0 files found (handles antivirus interference)
        // Only retry if directory actually contains .cs files (not just other file types)
        if (filePaths.Count == 0 && Directory.Exists(rootPath))
        {
            bool hasCsFiles = Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories).Any();
            if (hasCsFiles)
            {
                int[] delays = [200, 1000, 3000];
                foreach (int delay in delays)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    CodeCollectionBuilder.CollectSourceFiles(rootPath, parsers, excluded, filePaths);
                    if (filePaths.Count > 0)
                    {
                        break;
                    }
                }
                if (filePaths.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Provider scan found 0 source files in '{rootPath}' after 3 retries. " +
                        "This likely indicates filesystem interference (antivirus, file locks).");
                }
            }
        }

        // 2. Read + parse syntax trees. Unlike GetSemanticModel (step 4), parsing is a
        //    pure, independent, per-file operation with no shared Roslyn state, so it is
        //    safe to parallelize — and on large repos (e.g. azure-sdk-for-net, with big
        //    auto-generated files) reading+parsing is the dominant cost, not binding.
        List<string> csFiles = filePaths.Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();

        // Large analyses (e.g. an entire monorepo) take minutes; tell the user so a long
        // run doesn't look like a hang. Scoping with -t <subdir> is much faster.
        if (csFiles.Count > 5000)
        {
            Console.Error.WriteLine(
                $"Analyzing {csFiles.Count:N0} C# files — this can take several minutes. " +
                "Narrow the scope with -t <subdir> for faster runs, or pass -dd for progress.");
        }

        (SyntaxTree Tree, string FilePath, string Text)?[] parsedTrees = new (SyntaxTree Tree, string FilePath, string Text)?[csFiles.Count];
        ConcurrentBag<string> fileErrors = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(Enumerable.Range(0, csFiles.Count),
            cancellationToken, async (i, token) =>
        {
            string filePath = csFiles[i];
            try
            {
                using FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader reader = new StreamReader(stream);
                string text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                SyntaxTree tree = CSharpSyntaxTree.ParseText(
                    text, CSharpSourceParser.ParseOptions, path: filePath, cancellationToken: token);
                parsedTrees[i] = (tree, filePath, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                fileErrors.Add($"Failed to read '{filePath}': {ex.Message}");
            }
        }).ConfigureAwait(false);

        List<(SyntaxTree Tree, string FilePath, string Text)> syntaxTrees = new List<(SyntaxTree Tree, string FilePath, string Text)>(csFiles.Count);
        foreach ((SyntaxTree Tree, string FilePath, string Text)? p in parsedTrees)
        {
            if (p is not null)
            {
                syntaxTrees.Add(p.Value);
            }
        }

        if (fileErrors.Count > 0)
        {
            foreach (string err in fileErrors)
            {
                Console.Error.WriteLine(err);
            }

            if (syntaxTrees.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Failed to read all {fileErrors.Count} source file(s). First error: {fileErrors.First()}");
            }
        }
        Log($"parsed {syntaxTrees.Count} syntax trees");

        // 3. Create Roslyn compilation with framework + project references
        Dictionary<SyntaxTree, CSharpCompilation> compilations =
            await CSharpProjectCompilations.CreateAsync(rootPath,
                syntaxTrees.Select(t => t.Tree), excluded, GetReferences, cancellationToken)
                .ConfigureAwait(false);
        Log("created compilation");

        // 4. Parse each file using its semantic model.
        //
        // NOTE: This is intentionally SEQUENTIAL. Parallelizing GetSemanticModel across
        // a single shared CSharpCompilation looks like a win on small repos but is
        // pathological at scale: the first semantic-model access lazily builds the
        // compilation's declaration/symbol tables, and doing that under heavy parallel
        // contention on a large solution (e.g. azure-sdk-for-net) causes lock contention
        // and memory thrashing that can hang for many minutes. Keep it sequential.
        List<SourceFile> sourceFiles = new List<SourceFile>(syntaxTrees.Count);
        CSharpSourceParser parser = new CSharpSourceParser();
        int _n = 0;
        foreach ((SyntaxTree? tree, string? filePath, string? text) in syntaxTrees)
        {
            SemanticModel model;
            try
            {
                model = compilations[tree].GetSemanticModel(tree);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Semantic analysis failed for '{filePath}': {ex.Message}", ex);
            }

            SourceFile? sourceFile = parser.ParseWithSemantics(filePath, text, tree, model);
            if (++_n % 500 == 0)
            {
                Log($"semantic-extracted {_n}/{syntaxTrees.Count} files");
            }

            if (sourceFile is null)
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            SourceFile normalized = sourceFile with
            {
                Path = relativePath, FullPath = Path.GetFullPath(filePath), SourceRoot = Path.GetFullPath(rootPath)
            };
            LinkReferences(normalized);
            sourceFiles.Add(normalized);

            // Surface Roslyn syntax errors so a malformed file is reported, not silently modelled as
            // partial/incorrect data. The engine drains the shared sink after querying and fails the run.
            foreach (string parseError in normalized.ParseErrors)
            {
                Cop.Core.ProviderErrors.Report(parseError);
            }
        }

        sourceFiles.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));
        Log($"semantic extraction complete ({sourceFiles.Count} files)");

        return sourceFiles;
    }

    private static DataObject ToCodebase(object? value, string? sourceRoot = null)
    {
        if (value is System.Collections.IList { Count: > 0 } list)
        {
            value = list[0];
        }

        Dictionary<string, object?> fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (string collection in s_codebaseCollectionNames)
        {
            fields[collection] = value is DataObject data ? WrapProviderValue(data.GetField(collection)) ?? new List<object>() : new List<object>();
        }

        fields["SourceScopes"] = sourceRoot is null ? new List<object>() : new List<object>
        {
            new DataObject("SourceScope", new Dictionary<string, object?>
            {
                ["RootPath"] = sourceRoot,
                ["Extensions"] = new[] { ".cs" }
            })
        };
        return new DataObject("Codebase", fields);
    }

    private static object? WrapProviderValue(object? value)
    {
        if (value is null or string or bool or int or long or double or float or decimal)
        {
            return value;
        }

        if (value is DataObject)
        {
            return value;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            List<object?> items = new List<object?>();
            foreach (object? item in enumerable)
            {
                items.Add(WrapProviderValue(item));
            }

            return items;
        }

        Type clrType = value.GetType();
        if (!s_providerBindings.ClrTypeMappings.TryGetValue(clrType, out string? typeName)
            || !s_providerBindings.Accessors.TryGetValue(typeName, out Dictionary<string, Func<object, object?>>? accessors))
        {
            return value;
        }

        return new DataObject(typeName).WithAsyncFieldResolver(async (fieldName, token) =>
        {
            string resolvedName = fieldName;
            if (!accessors.TryGetValue(fieldName, out Func<object, object?>? accessor))
            {
                foreach (KeyValuePair<string, Func<object, object?>> candidate in accessors)
                {
                    if (!candidate.Key.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    accessor = candidate.Value;
                    resolvedName = candidate.Key;
                    break;
                }
            }
            if (s_providerBindings.AsyncAccessors.TryGetValue(typeName, out Dictionary<string,
                    Func<object, CancellationToken, ValueTask<object?>>>? asyncFields)
                && asyncFields.TryGetValue(resolvedName,
                    out Func<object, CancellationToken, ValueTask<object?>>? asyncRead))
            {
                return WrapProviderValue(await asyncRead(value, token).ConfigureAwait(false));
            }
            return accessor is null ? null : WrapProviderValue(accessor(value));
        });
    }

    private static readonly string[] s_codebaseCollectionNames =
    [
        "Files",
        "Types",
        "Statements",
        "Methods",
        "Calls",
        "Lines",
        "Regions",
        "Projects"
    ];

    private static readonly RuntimeBindings s_providerBindings = CSharpBindings.Build();

    /// <inheritdoc />
    public override void RegisterCapabilities(TypeRegistry registry, string rootPath)
    {
        registry.RegisterDocumentLoader(path =>
        {
            SourceFile sourceFile = AssemblyApiReader.ReadAssembly(path);
            for (int i = 0; i < sourceFile.Types.Count; i++)
            {
                sourceFile.Types[i].File = sourceFile;
            }

            return [new Document(path, sourceFile.Language, sourceFile)];
        });
    }

    private static List<MetadataReference> GetReferences(string rootPath)
    {
        List<MetadataReference> references = FrameworkMetadata.CompilationReferences.Cast<MetadataReference>().ToList();
        HashSet<string> seen = references.Select(reference => Path.GetFileName(reference.Display!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Project dependency references — resolve from project.assets.json
        try
        {
            string? assetsPath = FindProjectAssets(rootPath);
            if (assetsPath != null)
            {
                LoadPackageReferences(assetsPath, references, seen);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ProviderWarnings.ReportOnce("csharp", rootPath, $"Cannot read restored C# references under '{rootPath}': {ex.Message}; semantic resolution may be incomplete.");
        }

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

        string? csproj = Directory.EnumerateFiles(rootPath, "*.csproj").FirstOrDefault();
        if (csproj != null)
        {
            csprojDir = rootPath;
            csprojName = Path.GetFileNameWithoutExtension(csproj);
        }
        else
        {
            DirectoryInfo? parent = Directory.GetParent(rootPath);
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

        if (csprojDir == null || csprojName == null)
        {
            return null;
        }

        // Standard location: obj/project.assets.json
        string standard = Path.Combine(csprojDir, "obj", "project.assets.json");
        if (File.Exists(standard))
        {
            return standard;
        }

        // Redirected output (e.g., Azure SDK): artifacts/obj/{ProjectName}/project.assets.json
        // Walk up looking for artifacts/obj/{ProjectName}/
        string dir = csprojDir;
        for (int i = 0; i < 6; i++)
        {
            DirectoryInfo? parent = Directory.GetParent(dir);
            if (parent == null)
            {
                break;
            }

            dir = parent.FullName;
            string candidate = Path.Combine(dir, "artifacts", "obj", csprojName, "project.assets.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses project.assets.json to resolve package DLL paths from the NuGet cache.
    /// </summary>
    private static void LoadPackageReferences(string assetsPath, List<MetadataReference> references, HashSet<string> seen)
    {
        string json = File.ReadAllText(assetsPath);
        using JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);

        // Get package folder paths
        List<string> packageFolders = new List<string>();
        if (doc.RootElement.TryGetProperty("packageFolders", out JsonElement folders))
        {
            foreach (JsonProperty folder in folders.EnumerateObject())
            {
                packageFolders.Add(folder.Name);
            }
        }
        if (packageFolders.Count == 0)
        {
            return;
        }

        // Get library paths (package-id/version → path in cache)
        Dictionary<string, string> libraryPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("libraries", out JsonElement libraries))
        {
            foreach (JsonProperty lib in libraries.EnumerateObject())
            {
                if (lib.Value.TryGetProperty("path", out JsonElement pathProp))
                {
                    libraryPaths[lib.Name] = pathProp.GetString() ?? lib.Name;
                }
            }
        }

        // Get compile assets from the first target framework
        if (!doc.RootElement.TryGetProperty("targets", out JsonElement targets))
        {
            return;
        }

        JsonProperty firstTarget = targets.EnumerateObject().FirstOrDefault();
        if (firstTarget.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty package in firstTarget.Value.EnumerateObject())
        {
            if (!package.Value.TryGetProperty("compile", out JsonElement compile))
            {
                continue;
            }

            if (!libraryPaths.TryGetValue(package.Name, out string? libPath))
            {
                continue;
            }

            foreach (JsonProperty asset in compile.EnumerateObject())
            {
                string relativeDll = asset.Name;
                if (!relativeDll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (relativeDll.Contains("_._"))
                {
                    continue;
                }

                // Resolve full path: packageFolder + libraryPath + assetPath
                foreach (string folder in packageFolders)
                {
                    string fullPath = Path.Combine(folder, libPath, relativeDll);
                    if (File.Exists(fullPath) && seen.Add(Path.GetFileName(fullPath)))
                    {
                        try { references.Add(MetadataReference.CreateFromFile(fullPath)); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
                        {
                            ProviderWarnings.ReportOnce("csharp", fullPath, $"Cannot load metadata reference '{fullPath}': {ex.Message}");
                        }
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
            int stmtLine = file.Statements[i].Line;
            if (stmtLine >= 2 && file.CommentLines.Contains(stmtLine - 1))
            {
                string prevLineText = file.Lines[stmtLine - 2];
                int idx = prevLineText.IndexOf("cop-ignore:", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    file.Statements[i].CopIgnore = prevLineText[(idx + "cop-ignore:".Length)..].Trim();
                }
            }
        }

        for (int i = 0; i < file.Types.Count; i++)
        {
            file.Types[i].File = file;
        }

        LinkMethodFiles(file.Types, file);

        for (int i = 0; i < file.Regions.Count; i++)
        {
            if (file.Regions[i].File is null)
            {
                file.Regions[i] = file.Regions[i] with { File = file };
            }
        }
    }

    private static void LinkMethodFiles(IEnumerable<TypeDeclaration> types, SourceFile file)
    {
        foreach (TypeDeclaration type in types)
        {
            type.File = file;
            foreach (MethodDeclaration method in type.Methods)
            {
                method.File = file;
            }

            foreach (MethodDeclaration ctor in type.Constructors)
            {
                ctor.File = file;
            }

            LinkMethodFiles(type.NestedTypes, file);
        }
    }

    private static void LinkFilesToProjects(Dictionary<string, List<object>> collections, List<ProjectInfo> projects, string rootPath)
    {
        if (!collections.TryGetValue("Files", out List<object>? files) || files.Count == 0)
        {
            return;
        }

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        List<(string Dir, string Name, HashSet<string>? Files)> projectDirs = new List<(string Dir, string Name, HashSet<string>? Files)>();
        foreach (ProjectInfo proj in projects)
        {
            string projFilePath = Path.Combine(rootPath, proj.Path.Replace('/', Path.DirectorySeparatorChar));
            string? projDir = Path.GetDirectoryName(projFilePath);
           if (projDir is not null)
           {
                string relDir = Path.GetRelativePath(rootPath, projDir).Replace('\\', '/');
               if (relDir == ".")
                {
                    relDir = string.Empty;
                }
                else if (!relDir.EndsWith('/'))
                {
                    relDir += '/';
                }

                projectDirs.Add((relDir, proj.Name,
                   proj.SourceFiles is null ? null : new HashSet<string>(proj.SourceFiles, comparer)));
           }
        }

        foreach (object fileObj in files)
        {
            SourceFile file = (SourceFile)fileObj;
            string filePath = file.Path;
           foreach ((string? dir, string? name, HashSet<string>? sources) in projectDirs)
           {
               if (sources is not null ? sources.Contains(file.FullPath) : filePath.StartsWith(dir, comparison))
                {
                    file.Projects.Add(name);
                }
            }
        }
    }
}
