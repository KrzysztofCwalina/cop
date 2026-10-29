using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal static class GoNativeAnalysis
{
    private static readonly ConcurrentDictionary<string, Lazy<string?>> Helpers = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, string?> EnvironmentOverrides = new()
    {
        ["GOTOOLCHAIN"] = "local",
        ["GOWORK"] = "off",
        ["GOFLAGS"] = "",
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
    public static string Identity(string? searchPath, bool injected)
    {
        if (injected) return "native-protocol";
        string? go = ToolchainProcess.FindExecutable("go", searchPath);
        if (go is not null)
        {
            string? helper = Helper(go);
            if (helper is not null) return "go-ast:" + helper;
        }
        Warn("", go is null ? "Go toolchain is not installed" : "Go native helper is unavailable");
        return "go-syntax-fallback";
    }

    public static SourceFile Enrich(SourceFile file, string? searchPath, Func<string, ToolchainResult>? request)
    {
        ToolchainResult result;
        if (request is not null)
            result = request(Request(file));
        else
        {
            string? go = ToolchainProcess.FindExecutable("go", searchPath);
            if (go is null) { Warn(file.Path, "Go toolchain is not installed"); return file; }
            string? helper = Helper(go);
            if (helper is null) { Warn(file.Path, "Go toolchain cannot compile the bundled parse-only helper"); return file; }
            result = ToolchainExecutionScope.RequestAsync(helper, [], Path.GetDirectoryName(helper)!, Request(file),
                timeout: TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }
        if (result.Status != ToolchainStatus.Success)
            throw new InvalidOperationException($"Go native analysis {result.Status}: {result.Diagnostic ?? result.StandardError}");
        NativeResponse response;
        try
        {
            response = JsonSerializer.Deserialize<NativeResponse>(result.StandardOutput, Json)
                ?? throw new JsonException("null response");
        }
        catch (JsonException ex) { throw new InvalidOperationException("Go native analysis returned invalid JSON.", ex); }
        if (response.Calls is null || response.Errors is null)
            throw new InvalidOperationException("Go native analysis returned an incomplete response.");
        file = Declarations(file, response);
        Apply(file, response);
        if (response.Errors.Count == 0) file.ParseErrors.Clear();
        file.ParseErrors.AddRange(response.Errors);
        if (request is null)
        {
            string go = ToolchainProcess.FindExecutable("go", searchPath)!;
            GoSemanticAnalysis.Attach(file, go, Helper(go)!);
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

    private static string? Helper(string go)
    {
        var executable = new FileInfo(go);
        string key = go + executable.Length + executable.LastWriteTimeUtc.Ticks;
        return Helpers.GetOrAdd(key, _ => new(() => Build(go))).Value;
    }

    private static string? Build(string go)
    {
        using var resource = typeof(GoSourceParser).Assembly.GetManifestResourceStream("Cop.Go.Native")
            ?? throw new InvalidOperationException("Bundled Go helper resource is missing.");
        using var reader = new StreamReader(resource);
        string source = reader.ReadToEnd();
        using var semantics = typeof(GoSourceParser).Assembly.GetManifestResourceStream("Cop.Go.Semantics")
            ?? throw new InvalidOperationException("Bundled Go semantics resource is missing.");
        using var semanticReader = new StreamReader(semantics);
        string semanticSource = semanticReader.ReadToEnd();
        var executable = new FileInfo(go);
        string identity = source + semanticSource + go + executable.Length + executable.LastWriteTimeUtc.Ticks
            + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            + string.Join(";", EnvironmentOverrides.Select(p => $"{p.Key}={p.Value}"));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        string directory = Path.Combine(Path.GetTempPath(), "cop-native-go", hash);
        string binary = Path.Combine(directory, OperatingSystem.IsWindows() ? "analysis.exe" : "analysis");
        if (File.Exists(binary)) return binary;
        Directory.CreateDirectory(directory);
        string scratch = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string input = Path.Combine(scratch, "analysis.go");
            string output = Path.Combine(scratch, Path.GetFileName(binary));
            File.WriteAllText(input, source, new UTF8Encoding(false));
            string semanticInput = Path.Combine(scratch, "semantics.go");
            File.WriteAllText(semanticInput, semanticSource, new UTF8Encoding(false));
            var result = ToolchainProcess.RunAsync(go, ["build", "-o", output, input, semanticInput], scratch,
                timeout: TimeSpan.FromMinutes(3), environment: EnvironmentOverrides).GetAwaiter().GetResult();
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
        if (response.Statements is null) throw new InvalidOperationException("Go native analysis omitted statement structure.");
        var methods = file.Types.SelectMany(t => t.Methods).Concat(file.Methods).Distinct().ToList();
        foreach (var method in methods) method.Statements = [];
        file.Statements.Clear();
        var nodes = new Dictionary<string, StatementInfo>();
        var parents = new List<(StatementInfo Node, string? Parent)>();
        void Add(string id, string? parent, int methodLine, int methodColumn, StatementInfo node)
        {
            if (!nodes.TryAdd(id, node)) throw new InvalidOperationException("Go native analysis returned duplicate syntax identities.");
            node.Method = methodLine > 0 ? methods.FirstOrDefault(m => m.Line == methodLine && m.Column == methodColumn) : null;
            node.Method?.Statements.Add(node);
            file.Statements.Add(node);
            parents.Add((node, parent));
        }
        foreach (var fact in response.Statements)
            Add(fact.ID, fact.ParentID, fact.MethodLine, fact.MethodColumn,
                new GoStatementInfo(fact.Kind, [], null, Null(fact.MemberName), [], fact.Line, fact.MethodLine > 0)
                {
                    Column = fact.Column,
                    EndLine = fact.EndLine,
                    EndColumn = fact.EndColumn,
                    Condition = Null(fact.Condition),
                    Expression = Null(fact.Expression),
                    IsErrorHandler = fact.ErrorHandler,
                    IsGenericErrorHandler = fact.Kind == "catch"
                });
        foreach (var fact in response.Calls!)
        {
            if (fact.Line <= 0 || fact.MemberName is null || fact.Arguments is null ||
                fact.CallTarget is null || fact.ArgumentExpressions is null)
                throw new InvalidOperationException("Go native analysis returned an invalid call.");
            var call = new GoStatementInfo("call", [], string.IsNullOrEmpty(fact.TypeName) ? null : fact.TypeName,
                fact.MemberName, fact.Arguments, fact.Line, fact.MethodLine > 0)
            {
                Column = fact.Column,
                EndLine = fact.EndLine,
                EndColumn = fact.EndColumn,
                CallTarget = fact.CallTarget,
                ArgumentExpressions = fact.ArgumentExpressions
            };
            Add(fact.ID, fact.ParentID, fact.MethodLine, fact.MethodColumn, call);
        }
        foreach (var (node, parent) in parents)
        {
            if (string.IsNullOrEmpty(parent)) continue;
            if (!nodes.TryGetValue(parent, out var owner)) throw new InvalidOperationException("Go native syntax parent is missing.");
            node.Parent = owner;
            owner._children.Add(node);
        }
        file.Statements.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : Nullable.Compare(a.Column, b.Column));
    }

    private static SourceFile Declarations(SourceFile file, NativeResponse response)
    {
        if (response.Types is null || response.Methods is null || response.Imports is null)
            throw new InvalidOperationException("Go native analysis omitted declarations.");
        var oldMethods = file.Types.SelectMany(t => t.Methods).Concat(file.Methods).Distinct().ToList();
        MethodDeclaration Method(NativeMethod m)
        {
            var old = oldMethods.FirstOrDefault(o => o.Name == m.Name && o.ReceiverType == Null(m.Receiver));
            var method = new MethodDeclaration(m.Name, Visibility(m.Name), [], m.ReturnTypeFact ?? Type(m.ReturnType),
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
            foreach (var statement in method.Statements) statement.Method = method;
            return method;
        }
        var methods = response.Methods.Select(Method).ToList();
        var types = response.Types.Select(t => new TypeDeclaration(t.Name,
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
        }.AsGo(t.Kind == "interface", t.Kind == "struct", t.Kind == "alias", t.StructTags, t.Union, t.Underlying))
            .Cast<TypeDeclaration>().ToList();
        foreach (var method in methods.Where(m => m.ReceiverType is not null))
            types.FirstOrDefault(t => t.Name == method.ReceiverType)?.Methods.Add(method);
        var functions = methods.Where(m => m.ReceiverType is null).ToList();
        if (functions.Count > 0)
            types.Add(new TypeDeclaration(Path.GetFileNameWithoutExtension(file.Path) + " (functions)", TypeKind.Class,
                Modifier.Public, [], [], [], functions, [], [], 0).AsGo());
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
    { public TypeReference? TypeFact { get; init; } }
    private sealed record NativeField(string Name, string Type, int Line, int Column, int EndLine, int EndColumn)
        : NativeSpan(Line, Column, EndLine, EndColumn)
    { public TypeReference? TypeFact { get; init; } }
    private sealed record NativeMethod(string Name, string? Receiver, string? ReturnType, List<NativeParameter> Parameters,
        List<string> TypeParameters, bool PointerReceiver, bool NamedReturns, bool Documented,
        int Line, int Column, int EndLine, int EndColumn) : NativeSpan(Line, Column, EndLine, EndColumn)
    { public TypeReference? ReturnTypeFact { get; init; } }
    private sealed record NativeType(string Name, string Kind, string? AliasTarget, List<string> BaseTypes,
        List<string> TypeParameters, List<NativeField> Fields, List<NativeMethod> Methods, bool StructTags, bool Union,
        bool Underlying, bool Documented, int Line, int Column, int EndLine, int EndColumn) : NativeSpan(Line, Column, EndLine, EndColumn)
    { public TypeReference? AliasTargetFact { get; init; } }
    private sealed record NativeCall(string ID, string? ParentID, int MethodLine, int MethodColumn, int Line, int Column, int EndLine, int EndColumn,
        string? TypeName, string? MemberName, List<string>? Arguments, ExpressionInfo? CallTarget, List<ExpressionInfo>? ArgumentExpressions);
    private sealed record NativeStatement(string ID, string? ParentID, int MethodLine, int MethodColumn, string Kind, string? Condition,
        string? Expression, bool ErrorHandler, string? MemberName, int Line, int Column, int EndLine, int EndColumn);
}
