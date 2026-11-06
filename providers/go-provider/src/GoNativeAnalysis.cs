// Merges native Go compiler type and symbol facts into parsed source files.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal static class GoNativeAnalysis
{
    private static readonly ConcurrentDictionary<string, AsyncProviderValue<string?>> s_helpers = new();
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, string?> s_environmentOverrides = new()
    {
        ["GOTOOLCHAIN"] = "local",
        ["GOWORK"] = "off",
        ["GOFLAGS"] = string.Empty,
        ["GO111MODULE"] = "off",
        ["GOENV"] = "off",
        ["CGO_ENABLED"] = "0",
        ["GOOS"] = null,
        ["GOARCH"] = null,
        ["GOROOT"] = null,
        ["GOEXPERIMENT"] = null,
        ["GOPROXY"] = "off",
        ["GOSUMDB"] = "off"
    };
    /// <summary>
    /// Identifies the native Go analysis configuration for source caching.
    /// </summary>
    public static string Identity(string? searchPath,
        Func<string, CancellationToken, ValueTask<ToolchainResult>>? request)
    {
        if (request is not null)
        {
            return "native-protocol";
        }

        string? go = ToolchainProcess.FindExecutable("go", searchPath);
        return go is null ? "go-syntax-fallback" : "go-ast:" + go;
    }

    /// <summary>Adds native Go type and binding facts to a parsed source file.</summary>
    public static async ValueTask<SourceFile> EnrichAsync(
        SourceFile file, string? searchPath,
        Func<string, CancellationToken, ValueTask<ToolchainResult>>? request,
        CancellationToken cancellationToken)
    {
        ToolchainResult result;
        if (request is not null)
        {
            result = await request(Request(file), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string? go = ToolchainProcess.FindExecutable("go", searchPath);
            if (go is null) { Warn(file.Path, "Go toolchain is not installed"); return file; }
            string? helper = await HelperAsync(go, cancellationToken).ConfigureAwait(false);
            if (helper is null) { Warn(file.Path, "Go toolchain cannot compile the bundled parse-only helper"); return file; }
            result = await ToolchainExecutionScope.RequestAsync(
                new ToolchainCommand(helper, [], Path.GetDirectoryName(helper)!),
                Request(file), new ToolchainRunOptions { Timeout = TimeSpan.FromSeconds(30) },
                cancellationToken).ConfigureAwait(false);
        }
        if (result.Status != ToolchainStatus.Success)
        {
            throw new InvalidOperationException($"Go native analysis {result.Status}: {result.Diagnostic ?? result.StandardError}");
        }

        NativeResponse response;
        try
        {
            response = JsonSerializer.Deserialize<NativeResponse>(result.StandardOutput, s_json)
                ?? throw new JsonException("null response");
        }
        catch (JsonException ex) { throw new InvalidOperationException("Go native analysis returned invalid JSON.", ex); }
        if (response.Calls is null || response.Errors is null)
        {
            throw new InvalidOperationException("Go native analysis returned an incomplete response.");
        }

        file = Declarations(file, response);
        Apply(file, response);
        if (response.Errors.Count == 0)
        {
            file.ParseErrors.Clear();
        }

        file.ParseErrors.AddRange(response.Errors);
        if (request is null)
        {
            string go = ToolchainProcess.FindExecutable("go", searchPath)!;
            string helper = await HelperAsync(go, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Go semantic helper is unavailable.");
            GoSemanticAnalysis.Attach(file, go, helper);
        }
        return file;
    }

    private static void Warn(string path, string reason) =>
        ProviderWarnings.ReportOnce("go", "fallback", $"{reason}; using simpler Go syntax analysis (no type resolution).");

    private static string Request(SourceFile file) => JsonSerializer.Serialize(new
    {
        Path = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Path)),
        Source = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.RawText))
    });

    private static ValueTask<string?> HelperAsync(string go, CancellationToken cancellationToken)
    {
        FileInfo executable = new FileInfo(go);
        string key = go + executable.Length + executable.LastWriteTimeUtc.Ticks;
        return s_helpers.GetOrAdd(key, _ => new(token => BuildAsync(go, token)))
            .GetValueAsync(cancellationToken);
    }

    private static async ValueTask<string?> BuildAsync(string go, CancellationToken cancellationToken)
    {
        using Stream resource = typeof(GoSourceParser).Assembly.GetManifestResourceStream("Cop.Go.Native")
            ?? throw new InvalidOperationException("Bundled Go helper resource is missing.");
        using StreamReader reader = new StreamReader(resource);
        string source = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        using Stream semantics = typeof(GoSourceParser).Assembly.GetManifestResourceStream("Cop.Go.Semantics")
            ?? throw new InvalidOperationException("Bundled Go semantics resource is missing.");
        using StreamReader semanticReader = new StreamReader(semantics);
        string semanticSource = await semanticReader.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        FileInfo executable = new FileInfo(go);
        string identity = source + semanticSource + go + executable.Length + executable.LastWriteTimeUtc.Ticks
            + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            + string.Join(";", s_environmentOverrides.Select(p => $"{p.Key}={p.Value}"));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        string directory = Path.Combine(Path.GetTempPath(), "cop-native-go", hash);
        string binary = Path.Combine(directory, OperatingSystem.IsWindows() ? "analysis.exe" : "analysis");
        if (File.Exists(binary))
        {
            return binary;
        }

        Directory.CreateDirectory(directory);
        string scratch = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string input = Path.Combine(scratch, "analysis.go");
            string output = Path.Combine(scratch, Path.GetFileName(binary));
            await File.WriteAllTextAsync(input, source, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            string semanticInput = Path.Combine(scratch, "semantics.go");
            await File.WriteAllTextAsync(
                semanticInput, semanticSource, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            ToolchainResult result = await ToolchainProcess.RunAsync(
                new ToolchainCommand(go, ["build", "-o", output, input, semanticInput],
                    scratch, s_environmentOverrides),
                new ToolchainRunOptions { Timeout = TimeSpan.FromMinutes(3) }, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status != ToolchainStatus.Success)
            {
                ProviderWarnings.ReportOnce("go", "preflight", $"Go native helper unavailable: {result.Diagnostic}\n{result.StandardError}");
                return null;
            }
            try { File.Move(output, binary); }
            catch (IOException) when (File.Exists(binary)) { }
            return binary;
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static void Apply(SourceFile file, NativeResponse response)
    {
        if (response.Statements is null)
        {
            throw new InvalidOperationException("Go native analysis omitted statement structure.");
        }

        List<MethodDeclaration> methods = file.Types.SelectMany(t => t.Methods).Concat(file.Methods).Distinct().ToList();
        foreach (MethodDeclaration method in methods)
        {
            method.Statements = [];
        }

        file.Statements.Clear();
        Dictionary<string, StatementInfo> nodes = new Dictionary<string, StatementInfo>();
        List<(StatementInfo Node, string? Parent)> parents = new List<(StatementInfo Node, string? Parent)>();
        void Add(string id, string? parent, int methodLine, int methodColumn, StatementInfo node)
        {
            if (!nodes.TryAdd(id, node))
            {
                throw new InvalidOperationException("Go native analysis returned duplicate syntax identities.");
            }

            node.Method = methodLine > 0 ? methods.FirstOrDefault(m => m.Line == methodLine && m.Column == methodColumn) : null;
            node.Method?.Statements.Add(node);
            file.Statements.Add(node);
            parents.Add((node, parent));
        }
        foreach (NativeStatement fact in response.Statements)
        {
            Add(fact.ID, fact.ParentID, fact.MethodLine, fact.MethodColumn,
                new GoStatementInfo(new StatementInfo(fact.Kind, [], null, Null(fact.MemberName), [], fact.Line, fact.MethodLine > 0))
                {
                    Column = fact.Column,
                    EndLine = fact.EndLine,
                    EndColumn = fact.EndColumn,
                    Condition = Null(fact.Condition),
                    Expression = Null(fact.Expression),
                    IsErrorHandler = fact.ErrorHandler,
                    IsGenericErrorHandler = fact.Kind == "catch"
                });
        }

        foreach (NativeCall fact in response.Calls!)
        {
            if (fact.Line <= 0 || fact.MemberName is null || fact.Arguments is null ||
                fact.CallTarget is null || fact.ArgumentExpressions is null)
            {
                throw new InvalidOperationException("Go native analysis returned an invalid call.");
            }

            GoStatementInfo call = new GoStatementInfo(new StatementInfo("call", [], string.IsNullOrEmpty(fact.TypeName) ? null : fact.TypeName,
                fact.MemberName, fact.Arguments, fact.Line, fact.MethodLine > 0))
            {
                Column = fact.Column,
                EndLine = fact.EndLine,
                EndColumn = fact.EndColumn,
                CallTarget = fact.CallTarget,
                ArgumentExpressions = fact.ArgumentExpressions
            };
            Add(fact.ID, fact.ParentID, fact.MethodLine, fact.MethodColumn, call);
        }
        foreach ((StatementInfo? node, string? parent) in parents)
        {
            if (string.IsNullOrEmpty(parent))
            {
                continue;
            }

            if (!nodes.TryGetValue(parent, out StatementInfo? owner))
            {
                throw new InvalidOperationException("Go native syntax parent is missing.");
            }

            node.Parent = owner;
            owner.MutableChildren.Add(node);
        }
        file.Statements.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : Nullable.Compare(a.Column, b.Column));
    }

    private static SourceFile Declarations(SourceFile file, NativeResponse response)
    {
        if (response.Types is null || response.Methods is null || response.Imports is null)
        {
            throw new InvalidOperationException("Go native analysis omitted declarations.");
        }

        List<MethodDeclaration> oldMethods = file.Types.SelectMany(t => t.Methods).Concat(file.Methods).Distinct().ToList();
        MethodDeclaration Method(NativeMethod m)
        {
            MethodDeclaration? old = oldMethods.FirstOrDefault(o => o.Name == m.Name && o.ReceiverType == Null(m.Receiver));
            GoMethodDeclaration method = new MethodDeclaration(m.Name, Visibility(m.Name), [], m.ReturnTypeFact ?? Type(m.ReturnType),
                m.Parameters.Select(p => new ParameterDeclaration(p.Name, p.TypeFact ?? Type(p.Type)!, p.Variadic, false, false, p.Line)
                { Column = p.Column, EndLine = p.EndLine, EndColumn = p.EndColumn }).ToList(), m.Line)
            {
                Column = m.Column,
                EndLine = m.EndLine,
                EndColumn = m.EndColumn,
                ReceiverType = Null(m.Receiver),
                TypeParameters = m.TypeParameters,
                HasDocComment = m.Documented,
                Statements = old?.Statements ?? []
            }.AsGo(m.PointerReceiver, m.NamedReturns, m.Parameters.Any(p => p.Variadic), m.TypeParameters.Count > 0);
            foreach (StatementInfo statement in method.Statements)
            {
                statement.Method = method;
            }

            return method;
        }
        List<MethodDeclaration> methods = response.Methods.Select(Method).ToList();
        List<TypeDeclaration> types = response.Types.Select(t => new GoTypeDeclaration(new TypeDeclaration(t.Name,
            t.Kind == "alias" ? TypeKind.Alias : t.Kind == "interface" ? TypeKind.Interface : TypeKind.Struct,
            Visibility(t.Name), t.BaseTypes, [], [], t.Methods.Select(Method).ToList(), [], [], t.Line)
        {
            Column = t.Column,
            EndLine = t.EndLine,
            EndColumn = t.EndColumn,
            AliasTarget = t.AliasTargetFact ?? Type(t.AliasTarget),
            TypeParameters = t.TypeParameters,
            HasDocComment = t.Documented,
            Fields = t.Fields.Select(f => new FieldDeclaration(f.Name, f.TypeFact ?? Type(f.Type)!, Visibility(f.Name), f.Line)).ToList()
        })
        {
            IsInterface = t.Kind == "interface", IsStruct = t.Kind == "struct",
            IsTypeAlias = t.Kind == "alias", HasStructTags = t.StructTags,
            HasUnionTypeSet = t.Union, HasUnderlyingTypeTerms = t.Underlying
        })
            .Cast<TypeDeclaration>().ToList();
        foreach (MethodDeclaration? method in methods.Where(m => m.ReceiverType is not null))
        {
            types.FirstOrDefault(t => t.Name == method.ReceiverType)?.Methods.Add(method);
        }

        List<MethodDeclaration> functions = methods.Where(m => m.ReceiverType is null).ToList();
        if (functions.Count > 0)
        {
            types.Add(new TypeDeclaration(Path.GetFileNameWithoutExtension(file.Path) + " (functions)", TypeKind.Class,
                Modifier.Public, [], [], [], functions, [], [], 0).AsGo());
        }

        return file with
        {
            Types = types,
            Methods = methods.Where(m => m.ReceiverType is not null).ToList(),
            Namespace = response.Namespace,
            Usings = response.Imports,
            Imports = response.ImportFacts ?? file.Imports
        };
    }

    private static string? Null(string? text) => string.IsNullOrEmpty(text) ? null : text;
    private static TypeReference? Type(string? text) => string.IsNullOrEmpty(text) ? null : new(text, null, [], text);
    private static Modifier Visibility(string name) => name.Length > 0 && char.IsUpper(name[0]) ? Modifier.Public : Modifier.Private;

    private sealed record NativeResponse(List<NativeCall>? Calls, List<string>? Errors, List<NativeType>? Types,
        List<NativeMethod>? Methods, string? Namespace, List<string>? Imports, List<NativeStatement>? Statements,
        List<ImportInfo>? ImportFacts);
    private record NativeSpan(int Line, int Column, int EndLine, int EndColumn);
    private sealed record NativeParameter(string Name, string Type, bool Variadic, int Line, int Column, int EndLine, int EndColumn)
        : NativeSpan(Line, Column, EndLine, EndColumn)
    {
        /// <summary>Gets the compiler-resolved parameter type.</summary>
        public TypeReference? TypeFact { get; init; }
    }
    private sealed record NativeField(string Name, string Type, int Line, int Column, int EndLine, int EndColumn)
        : NativeSpan(Line, Column, EndLine, EndColumn)
    {
        /// <summary>Gets the compiler-resolved field type.</summary>
        public TypeReference? TypeFact { get; init; }
    }
    private sealed record NativeMethod(string Name, string? Receiver, string? ReturnType, List<NativeParameter> Parameters,
        List<string> TypeParameters, bool PointerReceiver, bool NamedReturns, bool Documented,
        int Line, int Column, int EndLine, int EndColumn) : NativeSpan(Line, Column, EndLine, EndColumn)
    {
        /// <summary>Gets the compiler-resolved return type.</summary>
        public TypeReference? ReturnTypeFact { get; init; }
    }
    private sealed record NativeType(string Name, string Kind, string? AliasTarget, List<string> BaseTypes,
        List<string> TypeParameters, List<NativeField> Fields, List<NativeMethod> Methods, bool StructTags, bool Union,
        bool Underlying, bool Documented, int Line, int Column, int EndLine, int EndColumn) : NativeSpan(Line, Column, EndLine, EndColumn)
    {
        /// <summary>Gets the compiler-resolved alias target.</summary>
        public TypeReference? AliasTargetFact { get; init; }
    }
    private sealed record NativeCall(string ID, string? ParentID, int MethodLine, int MethodColumn, int Line, int Column, int EndLine, int EndColumn,
        string? TypeName, string? MemberName, List<string>? Arguments, ExpressionInfo? CallTarget, List<ExpressionInfo>? ArgumentExpressions);
    private sealed record NativeStatement(string ID, string? ParentID, int MethodLine, int MethodColumn, string Kind, string? Condition,
        string? Expression, bool ErrorHandler, string? MemberName, int Line, int Column, int EndLine, int EndColumn);
}
