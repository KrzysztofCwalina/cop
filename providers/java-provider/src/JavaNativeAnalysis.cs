using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal static class JavaNativeAnalysis
{
    private static readonly ConcurrentDictionary<string, Lazy<string?>> Helpers = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions RequestJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Dictionary<string, string?> EnvironmentOverrides = new()
    {
        ["JAVA_TOOL_OPTIONS"] = null,
        ["JDK_JAVA_OPTIONS"] = null,
        ["_JAVA_OPTIONS"] = null,
        ["CLASSPATH"] = null,
        ["JDK_JAVAC_OPTIONS"] = null
    };
    public static string Identity(string? searchPath, bool injected)
    {
        if (injected) return "native-protocol";
        string? javac = ToolchainProcess.FindExecutable("javac", searchPath);
        if (javac is not null && JavaExecutable(javac) is not null)
        {
            string? helper = Helper(javac);
            if (helper is not null) return "jdk-tree:" + helper;
        }
        Warn(javac is null ? "JDK compiler toolchain is not installed" : "JDK native helper is unavailable");
        return "java-syntax-fallback";
    }

    public static SourceFile Enrich(SourceFile file, string? searchPath, Func<string, ToolchainResult>? request)
    {
        ToolchainResult result;
        if (request is not null) result = request(Request(file));
        else
        {
            string? javac = ToolchainProcess.FindExecutable("javac", searchPath);
            string? java = javac is null ? null : JavaExecutable(javac);
            if (java is null || javac is null)
            {
                Warn("JDK compiler toolchain is not installed");
                return file;
            }
            string? directory = Helper(javac);
            if (directory is null) { Warn("JDK cannot compile the bundled parse-only helper"); return file; }
            result = ToolchainExecutionScope.RequestAsync(java, ["-cp", directory, "CopJavaAnalysis"], directory, Request(file),
                timeout: TimeSpan.FromSeconds(30), environment: EnvironmentOverrides).GetAwaiter().GetResult();
        }
        if (result.Status != ToolchainStatus.Success)
            throw new InvalidOperationException($"Java native analysis {result.Status}: {result.Diagnostic ?? result.StandardError}");
        NativeResponse response;
        try { response = JsonSerializer.Deserialize<NativeResponse>(result.StandardOutput, Json) ?? throw new JsonException("null response"); }
        catch (JsonException ex) { throw new InvalidOperationException("Java native analysis returned invalid JSON.", ex); }
        if (response.Calls is null || response.Errors is null || response.Types is null || response.Imports is null)
            throw new InvalidOperationException("Java native analysis returned an incomplete response.");
        file = Declarations(file, response);
        ApplyCalls(file, response);
        if (response.Errors.Count == 0) file.ParseErrors.Clear();
        file.ParseErrors.AddRange(response.Errors);
        if (request is null) AttachSemantics(file, searchPath);
        return file;
    }

    private static void AttachSemantics(SourceFile file, string? searchPath)
    {
        var analysis = new Lazy<bool>(() =>
        {
            string javac = ToolchainProcess.FindExecutable("javac", searchPath)
                ?? throw new InvalidOperationException("Java semantic compiler disappeared after native parsing.");
            string java = JavaExecutable(javac)
                ?? throw new InvalidOperationException("Java semantic launcher is unavailable.");
            string helper = Helper(javac) ?? throw new InvalidOperationException("Java semantic helper is unavailable.");
            var context = JavaSemanticContext.Discover(file);
            string request = JsonSerializer.Serialize(new
            {
                Path = Convert.ToBase64String(Encoding.UTF8.GetBytes(context.Path)),
                Source = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.RawText)),
                Semantic = Convert.ToBase64String(Encoding.UTF8.GetBytes("true")),
                Sources = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", context.Sources))),
                Classpath = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(Path.PathSeparator, context.Classpath))),
                Sourcepath = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(Path.PathSeparator, context.Roots)))
            }, RequestJson);
            var result = ToolchainExecutionScope.RequestAsync(java, ["-cp", helper, "CopJavaAnalysis"], helper, request,
                timeout: TimeSpan.FromMinutes(2), environment: EnvironmentOverrides).GetAwaiter().GetResult();
            if (result.Status != ToolchainStatus.Success)
                throw new InvalidOperationException($"Java semantic analysis {result.Status}: {result.Diagnostic ?? result.StandardError}");
            var response = JsonSerializer.Deserialize<SemanticResponse>(result.StandardOutput, Json)
                ?? throw new InvalidOperationException("Java semantic analysis returned null.");
            if (response.Facts is null || response.Errors is null)
                throw new InvalidOperationException("Java semantic analysis omitted bindings or diagnostics.");
            foreach (string error in context.Errors.Concat(response.Errors)) ProviderErrors.Report(error);
            foreach (var fact in response.Facts)
            {
                var call = file.Statements.FirstOrDefault(s => s.Kind == "call" && s.Line == fact.Line && s.Column == fact.Column
                    && s.EndLine == fact.EndLine && s.EndColumn == fact.EndColumn);
                if (call is null) throw new InvalidOperationException("Java semantic binding has no matching source call.");
                call.ReceiverType = fact.ReceiverType;
                call.ValueType = fact.ValueType;
                call.ReferencedCallable = fact.Callable;
            }
            return true;
        });
        foreach (var statement in file.Statements)
            statement.DeferSemanticAnalysis(analysis);
    }

    private sealed record SemanticResponse(List<SemanticFact>? Facts, List<string>? Errors);
    private sealed record SemanticFact(int Line, int Column, int EndLine, int EndColumn, TypeReference? ReceiverType,
        TypeReference? ValueType, CallableReference? Callable);

    private static void Warn(string reason) =>
        ProviderWarnings.ReportOnce("java", "fallback", $"{reason}; using simpler Java syntax analysis (no type resolution).");

    private static string Request(SourceFile file) => JsonSerializer.Serialize(new
    {
        Path = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Path)),
        Source = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.RawText))
    }, RequestJson);

    private static string? JavaExecutable(string javac) => ToolchainProcess.FindExecutable(
        Path.Combine(Path.GetDirectoryName(javac)!, OperatingSystem.IsWindows() ? "java.exe" : "java"));

    private static string? Helper(string javac)
    {
        var executable = new FileInfo(javac);
        string key = javac + executable.Length + executable.LastWriteTimeUtc.Ticks;
        return Helpers.GetOrAdd(key, _ => new(() => Build(javac))).Value;
    }

    private static string? Build(string javac)
    {
        using var resource = typeof(JavaSourceParser).Assembly.GetManifestResourceStream("Cop.Java.Native")
            ?? throw new InvalidOperationException("Bundled Java helper resource is missing.");
        using var reader = new StreamReader(resource);
        string source = reader.ReadToEnd();
        var executable = new FileInfo(javac);
        string identity = source + javac + executable.Length + executable.LastWriteTimeUtc.Ticks
            + string.Join(";", EnvironmentOverrides.Select(p => $"{p.Key}={p.Value}"));
        string directory = Path.Combine(Path.GetTempPath(), "cop-native-java",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
        if (File.Exists(Path.Combine(directory, "CopJavaAnalysis.class"))) return directory;
        Directory.CreateDirectory(directory);
        string scratch = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string input = Path.Combine(scratch, "CopJavaAnalysis.java");
            File.WriteAllText(input, source, new UTF8Encoding(false));
            var result = ToolchainProcess.RunAsync(javac, ["-proc:none", "-encoding", "UTF-8", "-d", scratch, input], scratch,
                timeout: TimeSpan.FromSeconds(60), environment: EnvironmentOverrides).GetAwaiter().GetResult();
            if (result.Status != ToolchainStatus.Success)
            {
                ProviderWarnings.ReportOnce("java", "preflight", $"JDK native helper unavailable: {result.Diagnostic}\n{result.StandardError}");
                return null;
            }
            foreach (string output in Directory.GetFiles(scratch, "*.class").OrderBy(p => Path.GetFileName(p) == "CopJavaAnalysis.class"))
            {
                string destination = Path.Combine(directory, Path.GetFileName(output));
                try { File.Move(output, destination); }
                catch (IOException) when (File.Exists(destination)) { }
            }
            return directory;
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static SourceFile Declarations(SourceFile file, NativeResponse response)
    {
        IEnumerable<MethodDeclaration> Existing(TypeDeclaration type) =>
            type.Methods.Concat(type.Constructors).Concat(type.NestedTypes.SelectMany(Existing));
        var oldMethods = file.Types.SelectMany(Existing).ToList();
        MethodDeclaration Method(NativeMethod m)
        {
            var old = oldMethods.FirstOrDefault(o => o.Name == m.Name && o.Line == m.Line);
            var method = new JavaMethodDeclaration(m.Name, Modifiers(m.Modifiers), m.Decorators, m.ReturnTypeFact ?? Type(m.ReturnType),
                m.Parameters.Select(p => new ParameterDeclaration(p.Name, p.TypeFact ?? Type(p.Type)!, p.Variadic, false, false, p.Line)
                { Column = p.Column, EndLine = p.EndLine, EndColumn = p.EndColumn }).ToList(), m.Line,
                m.Modifiers.Contains("synchronized"), m.Modifiers.Contains("native"), m.Modifiers.Contains("default"),
                m.Modifiers.Contains("strictfp"), m.TypeParameters.Count > 0)
            {
                Column = m.Column,
                EndLine = m.EndLine,
                EndColumn = m.EndColumn,
                TypeParameters = m.TypeParameters,
                HasDocComment = old?.HasDocComment ?? false,
                Statements = old?.Statements ?? []
            };
            foreach (var statement in method.Statements) statement.Method = method;
            return method;
        }
        TypeDeclaration Declaration(NativeType t)
        {
            var methods = t.Methods.Select(Method).ToList();
            var old = file.Types.FirstOrDefault(o => o.Name == t.Name && o.Line == t.Line);
            return new TypeDeclaration(t.Name,
                t.Kind is "interface" or "annotation_type" ? TypeKind.Interface :
                t.Kind == "enum" ? TypeKind.Enum : t.Kind == "record" ? TypeKind.Struct : TypeKind.Class,
                Modifiers(t.Modifiers), t.BaseTypes, t.Decorators,
                methods.Where(m => m.Name == "<init>").ToList(), methods.Where(m => m.Name != "<init>").ToList(),
                t.NestedTypes.Select(Declaration).ToList(), t.EnumValues, t.Line)
            {
                Namespace = response.Namespace,
                Column = t.Column,
                EndLine = t.EndLine,
                EndColumn = t.EndColumn,
                TypeParameters = t.TypeParameters,
                HasDocComment = old?.HasDocComment ?? false,
                Fields = t.Fields.Select(f => new FieldDeclaration(f.Name, f.TypeFact ?? Type(f.Type)!, Modifiers(f.Modifiers), f.Line)).ToList()
            }.AsJava(t.Kind == "record", t.Kind == "enum", t.Modifiers.Contains("sealed"),
                t.Modifiers.Contains("non-sealed"), t.Modifiers.Contains("final"), t.TypeParameters.Count > 0);
        }
        return file with
        {
            Types = response.Types!.Select(Declaration).ToList(),
            Namespace = string.IsNullOrEmpty(response.Namespace) ? null : response.Namespace,
            Usings = response.Imports!,
            Imports = response.ImportFacts ?? file.Imports
        };
    }

    private static Modifier Modifiers(List<string> names)
    {
        var result = Modifier.None;
        foreach (var name in names)
            result |= name switch
            {
                "public" => Modifier.Public,
                "private" => Modifier.Private,
                "protected" => Modifier.Protected,
                "static" => Modifier.Static,
                "abstract" => Modifier.Abstract,
                "final" => Modifier.Sealed,
                _ => Modifier.None
            };
        return result;
    }
    private static TypeReference? Type(string? text) => string.IsNullOrEmpty(text) ? null : new(text, null, [], text);

    private static void ApplyCalls(SourceFile file, NativeResponse response)
    {
        if (response.Statements is null) throw new InvalidOperationException("Java native analysis omitted statement structure.");
        IEnumerable<MethodDeclaration> Members(TypeDeclaration type) =>
            type.Methods.Concat(type.Constructors).Concat(type.NestedTypes.SelectMany(Members));
        var methods = file.Types.SelectMany(Members).ToList();
        foreach (var method in methods) method.Statements = [];
        file.Statements.Clear();
        var nodes = new Dictionary<string, StatementInfo>();
        var parents = new List<(StatementInfo Node, string? Parent)>();
        void Add(string id, string? parent, int methodLine, int methodColumn, StatementInfo node)
        {
            if (!nodes.TryAdd(id, node)) throw new InvalidOperationException("Java native analysis returned duplicate syntax identities.");
            node.Method = methodLine > 0 ? methods.FirstOrDefault(m => m.Line == methodLine && m.Column == methodColumn) : null;
            node.Method?.Statements.Add(node);
            file.Statements.Add(node);
            parents.Add((node, parent));
        }
        foreach (var fact in response.Statements)
            Add(fact.ID, fact.ParentID, fact.MethodLine, fact.MethodColumn,
                new JavaStatementInfo(fact.Kind, [], string.IsNullOrEmpty(fact.TypeName) ? null : fact.TypeName, null, [], fact.Line, fact.MethodLine > 0)
                {
                    Column = fact.Column,
                    EndLine = fact.EndLine,
                    EndColumn = fact.EndColumn,
                    Condition = string.IsNullOrEmpty(fact.Condition) ? null : fact.Condition,
                    Expression = string.IsNullOrEmpty(fact.Expression) ? null : fact.Expression,
                    IsErrorHandler = fact.Kind == "catch",
                    IsGenericErrorHandler = fact.GenericError,
                    HasRethrow = fact.Rethrow,
                    IsEnhancedFor = fact.EnhancedFor,
                    IsTryWithResources = fact.TryWithResources
                });
        foreach (var fact in response.Calls!)
        {
            if (fact.Line <= 0 || fact.MemberName is null || fact.Arguments is null || fact.CallTarget is null || fact.ArgumentExpressions is null)
                throw new InvalidOperationException("Java native analysis returned an invalid call.");
            var call = new JavaStatementInfo("call", [], string.IsNullOrEmpty(fact.TypeName) ? null : fact.TypeName,
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
            if (!nodes.TryGetValue(parent, out var owner)) throw new InvalidOperationException("Java native syntax parent is missing.");
            node.Parent = owner;
            owner._children.Add(node);
        }
        file.Statements.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : Nullable.Compare(a.Column, b.Column));
    }

    private sealed record NativeResponse(List<NativeCall>? Calls, List<string>? Errors, List<NativeType>? Types,
        string? Namespace, List<string>? Imports, List<NativeStatement>? Statements, List<ImportInfo>? ImportFacts);
    private sealed record NativeCall(string ID, string? ParentID, int MethodLine, int MethodColumn, int Line, int Column, int EndLine, int EndColumn,
        string? TypeName, string? MemberName, List<string>? Arguments, ExpressionInfo? CallTarget, List<ExpressionInfo>? ArgumentExpressions);
    private sealed record NativeStatement(string ID, string? ParentID, int MethodLine, int MethodColumn, string Kind, string? TypeName, string? Condition,
        string? Expression, bool GenericError, bool Rethrow, bool EnhancedFor, bool TryWithResources,
        int Line, int Column, int EndLine, int EndColumn);
    private record NativeSpan(int Line, int Column, int EndLine, int EndColumn);
    private sealed record NativeParameter(string Name, string Type, bool Variadic, int Line, int Column, int EndLine, int EndColumn)
        : NativeSpan(Line, Column, EndLine, EndColumn)
    { public TypeReference? TypeFact { get; init; } }
    private sealed record NativeField(string Name, string Type, List<string> Modifiers, int Line, int Column, int EndLine, int EndColumn)
        : NativeSpan(Line, Column, EndLine, EndColumn)
    { public TypeReference? TypeFact { get; init; } }
    private sealed record NativeMethod(string Name, string? ReturnType, List<string> Modifiers, List<string> Decorators,
        List<NativeParameter> Parameters, List<string> TypeParameters, int Line, int Column, int EndLine, int EndColumn)
        : NativeSpan(Line, Column, EndLine, EndColumn)
    { public TypeReference? ReturnTypeFact { get; init; } }
    private sealed record NativeType(string Name, string Kind, List<string> Modifiers, List<string> Decorators, List<string> BaseTypes,
        List<string> TypeParameters, List<NativeField> Fields, List<NativeMethod> Methods, List<NativeType> NestedTypes,
        List<string> EnumValues, int Line, int Column, int EndLine, int EndColumn) : NativeSpan(Line, Column, EndLine, EndColumn);
}
