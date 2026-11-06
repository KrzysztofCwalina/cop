// Merges compiler-derived Java type and symbol facts into parsed source files.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal static class JavaNativeAnalysis
{
    private static readonly ConcurrentDictionary<string, AsyncProviderValue<string?>> s_helpers = new();
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions s_requestJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Dictionary<string, string?> s_environmentOverrides = new()
    {
        ["JAVA_TOOL_OPTIONS"] = null,
        ["JDK_JAVA_OPTIONS"] = null,
        ["_JAVA_OPTIONS"] = null,
        ["CLASSPATH"] = null,
        ["JDK_JAVAC_OPTIONS"] = null
    };
    /// <summary>
    /// Identifies the native Java analysis configuration for source caching.
    /// </summary>
    public static string Identity(string? searchPath,
        Func<string, CancellationToken, ValueTask<ToolchainResult>>? request)
    {
        if (request is not null)
        {
            return "native-protocol";
        }

        string? javac = ToolchainProcess.FindExecutable("javac", searchPath);
        if (javac is not null && JavaExecutable(javac) is not null)
        {
            return "jdk-tree:" + javac;
        }
        return "java-syntax-fallback";
    }

    /// <summary>
    /// Adds native Java type and binding facts to a parsed source file.
    /// </summary>
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
            string? javac = ToolchainProcess.FindExecutable("javac", searchPath);
            string? java = javac is null ? null : JavaExecutable(javac);
            if (java is null || javac is null)
            {
                Warn("JDK compiler toolchain is not installed");
                return file;
            }
            string? directory = await HelperAsync(javac, cancellationToken).ConfigureAwait(false);
            if (directory is null) { Warn("JDK cannot compile the bundled parse-only helper"); return file; }
            result = await ToolchainExecutionScope.RequestAsync(
                new ToolchainCommand(java, ["-cp", directory, "CopJavaAnalysis"],
                    directory, s_environmentOverrides), Request(file),
                new ToolchainRunOptions { Timeout = TimeSpan.FromSeconds(30) },
                cancellationToken).ConfigureAwait(false);
        }
        if (result.Status != ToolchainStatus.Success)
        {
            throw new InvalidOperationException($"Java native analysis {result.Status}: {result.Diagnostic ?? result.StandardError}");
        }

        NativeResponse response;
        try { response = JsonSerializer.Deserialize<NativeResponse>(result.StandardOutput, s_json) ?? throw new JsonException("null response"); }
        catch (JsonException ex) { throw new InvalidOperationException("Java native analysis returned invalid JSON.", ex); }
        if (response.Calls is null || response.Errors is null || response.Types is null || response.Imports is null)
        {
            throw new InvalidOperationException("Java native analysis returned an incomplete response.");
        }

        file = Declarations(file, response);
        ApplyCalls(file, response);
        if (response.Errors.Count == 0)
        {
            file.ParseErrors.Clear();
        }

        file.ParseErrors.AddRange(response.Errors);
        if (request is null)
        {
            AttachSemantics(file, searchPath);
        }

        return file;
    }

    private static void AttachSemantics(SourceFile file, string? searchPath)
    {
        AsyncProviderValue<bool> analysis = new(async cancellationToken =>
        {
            string javac = ToolchainProcess.FindExecutable("javac", searchPath)
                ?? throw new InvalidOperationException("Java semantic compiler disappeared after native parsing.");
            string java = JavaExecutable(javac)
                ?? throw new InvalidOperationException("Java semantic launcher is unavailable.");
            string helper = await HelperAsync(javac, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Java semantic helper is unavailable.");
            JavaSemanticContext context = JavaSemanticContext.Discover(file);
            string request = JsonSerializer.Serialize(new
            {
                Path = Convert.ToBase64String(Encoding.UTF8.GetBytes(context.Path)),
                Source = Convert.ToBase64String(Encoding.UTF8.GetBytes(file.RawText)),
                Semantic = Convert.ToBase64String(Encoding.UTF8.GetBytes("true")),
                Sources = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", context.Sources))),
                Classpath = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(Path.PathSeparator, context.Classpath))),
                Sourcepath = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(Path.PathSeparator, context.Roots)))
            }, s_requestJson);
            ToolchainResult result = await ToolchainExecutionScope.RequestAsync(
                new ToolchainCommand(java, ["-cp", helper, "CopJavaAnalysis"],
                    helper, s_environmentOverrides), request,
                new ToolchainRunOptions { Timeout = TimeSpan.FromMinutes(2) },
                cancellationToken).ConfigureAwait(false);
            if (result.Status != ToolchainStatus.Success)
            {
                throw new InvalidOperationException($"Java semantic analysis {result.Status}: {result.Diagnostic ?? result.StandardError}");
            }

            SemanticResponse response = JsonSerializer.Deserialize<SemanticResponse>(result.StandardOutput, s_json)
                ?? throw new InvalidOperationException("Java semantic analysis returned null.");
            if (response.Facts is null || response.Errors is null)
            {
                throw new InvalidOperationException("Java semantic analysis omitted bindings or diagnostics.");
            }

            foreach (string error in context.Errors.Concat(response.Errors))
            {
                ProviderErrors.Report(error);
            }

            foreach (SemanticFact fact in response.Facts)
            {
                StatementInfo? call = file.Statements.FirstOrDefault(s => s.Kind == "call" && s.Line == fact.Line && s.Column == fact.Column
                    && s.EndLine == fact.EndLine && s.EndColumn == fact.EndColumn);
                if (call is null)
                {
                    throw new InvalidOperationException("Java semantic binding has no matching source call.");
                }

                call.ReceiverType = fact.ReceiverType;
                call.ValueType = fact.ValueType;
                call.ReferencedCallable = fact.Callable;
            }
            return true;
        });
        foreach (StatementInfo statement in file.Statements)
        {
            statement.DeferSemanticAnalysisAsync(analysis);
        }
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
    }, s_requestJson);

    private static string? JavaExecutable(string javac) => ToolchainProcess.FindExecutable(
        Path.Combine(Path.GetDirectoryName(javac)!, OperatingSystem.IsWindows() ? "java.exe" : "java"));

    private static ValueTask<string?> HelperAsync(string javac, CancellationToken cancellationToken)
    {
        FileInfo executable = new FileInfo(javac);
        string key = javac + executable.Length + executable.LastWriteTimeUtc.Ticks;
        return s_helpers.GetOrAdd(key, _ => new(token => BuildAsync(javac, token)))
            .GetValueAsync(cancellationToken);
    }

    private static async ValueTask<string?> BuildAsync(
        string javac, CancellationToken cancellationToken)
    {
        using Stream resource = typeof(JavaSourceParser).Assembly.GetManifestResourceStream("Cop.Java.Native")
            ?? throw new InvalidOperationException("Bundled Java helper resource is missing.");
        using StreamReader reader = new StreamReader(resource);
        string source = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        FileInfo executable = new FileInfo(javac);
        string identity = source + javac + executable.Length + executable.LastWriteTimeUtc.Ticks
            + string.Join(";", s_environmentOverrides.Select(p => $"{p.Key}={p.Value}"));
        string directory = Path.Combine(Path.GetTempPath(), "cop-native-java",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
        if (File.Exists(Path.Combine(directory, "CopJavaAnalysis.class")))
        {
            return directory;
        }

        Directory.CreateDirectory(directory);
        string scratch = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string input = Path.Combine(scratch, "CopJavaAnalysis.java");
            await File.WriteAllTextAsync(input, source, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            ToolchainResult result = await ToolchainProcess.RunAsync(
                new ToolchainCommand(javac, ["-proc:none", "-encoding", "UTF-8", "-d", scratch, input],
                    scratch, s_environmentOverrides),
                new ToolchainRunOptions { Timeout = TimeSpan.FromSeconds(60) },
                cancellationToken).ConfigureAwait(false);
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
        List<MethodDeclaration> oldMethods = file.Types.SelectMany(Existing).ToList();
        MethodDeclaration Method(NativeMethod m)
        {
            MethodDeclaration? old = oldMethods.FirstOrDefault(o => o.Name == m.Name && o.Line == m.Line);
            JavaMethodDeclaration method = new JavaMethodDeclaration(new MethodDeclaration(m.Name, Modifiers(m.Modifiers), m.Decorators, m.ReturnTypeFact ?? Type(m.ReturnType),
                m.Parameters.Select(p => new ParameterDeclaration(p.Name, p.TypeFact ?? Type(p.Type)!, p.Variadic, false, false, p.Line)
                { Column = p.Column, EndLine = p.EndLine, EndColumn = p.EndColumn }).ToList(), m.Line))
            {
                IsSynchronized = m.Modifiers.Contains("synchronized"),
                IsNative = m.Modifiers.Contains("native"),
                IsDefault = m.Modifiers.Contains("default"),
                IsStrictfp = m.Modifiers.Contains("strictfp"),
                IsGeneric = m.TypeParameters.Count > 0,
                Column = m.Column,
                EndLine = m.EndLine,
                EndColumn = m.EndColumn,
                TypeParameters = m.TypeParameters,
                HasDocComment = old?.HasDocComment ?? false,
                Statements = old?.Statements ?? []
            };
            foreach (StatementInfo statement in method.Statements)
            {
                statement.Method = method;
            }

            return method;
        }
        TypeDeclaration Declaration(NativeType t)
        {
            List<MethodDeclaration> methods = t.Methods.Select(Method).ToList();
            TypeDeclaration? old = file.Types.FirstOrDefault(o => o.Name == t.Name && o.Line == t.Line);
            return new JavaTypeDeclaration(new TypeDeclaration(t.Name,
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
            })
            {
                IsRecord = t.Kind == "record", IsEnum = t.Kind == "enum",
                HasSealedModifier = t.Modifiers.Contains("sealed"),
                IsNonSealed = t.Modifiers.Contains("non-sealed"),
                IsFinal = t.Modifiers.Contains("final"), IsGeneric = t.TypeParameters.Count > 0
            };
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
        Modifier result = Modifier.None;
        foreach (string name in names)
        {
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
        }

        return result;
    }
    private static TypeReference? Type(string? text) => string.IsNullOrEmpty(text) ? null : new(text, null, [], text);

    private static void ApplyCalls(SourceFile file, NativeResponse response)
    {
        if (response.Statements is null)
        {
            throw new InvalidOperationException("Java native analysis omitted statement structure.");
        }

        IEnumerable<MethodDeclaration> Members(TypeDeclaration type) =>
            type.Methods.Concat(type.Constructors).Concat(type.NestedTypes.SelectMany(Members));
        List<MethodDeclaration> methods = file.Types.SelectMany(Members).ToList();
        foreach (MethodDeclaration? method in methods)
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
                throw new InvalidOperationException("Java native analysis returned duplicate syntax identities.");
            }

            node.Method = methodLine > 0 ? methods.FirstOrDefault(m => m.Line == methodLine && m.Column == methodColumn) : null;
            node.Method?.Statements.Add(node);
            file.Statements.Add(node);
            parents.Add((node, parent));
        }
        foreach (NativeStatement fact in response.Statements)
        {
            Add(fact.ID, fact.ParentID, fact.MethodLine, fact.MethodColumn,
                new JavaStatementInfo(new StatementInfo(fact.Kind, [], string.IsNullOrEmpty(fact.TypeName) ? null : fact.TypeName, null, [], fact.Line, fact.MethodLine > 0))
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
        }

        foreach (NativeCall fact in response.Calls!)
        {
            if (fact.Line <= 0 || fact.MemberName is null || fact.Arguments is null || fact.CallTarget is null || fact.ArgumentExpressions is null)
            {
                throw new InvalidOperationException("Java native analysis returned an invalid call.");
            }

            JavaStatementInfo call = new JavaStatementInfo(new StatementInfo("call", [], string.IsNullOrEmpty(fact.TypeName) ? null : fact.TypeName,
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
                throw new InvalidOperationException("Java native syntax parent is missing.");
            }

            node.Parent = owner;
            owner.MutableChildren.Add(node);
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
    {
        /// <summary>Gets the compiler-resolved parameter type.</summary>
        public TypeReference? TypeFact { get; init; }
    }
    private sealed record NativeField(string Name, string Type, List<string> Modifiers, int Line, int Column, int EndLine, int EndColumn)
        : NativeSpan(Line, Column, EndLine, EndColumn)
    {
        /// <summary>Gets the compiler-resolved field type.</summary>
        public TypeReference? TypeFact { get; init; }
    }
    private sealed record NativeMethod(string Name, string? ReturnType, List<string> Modifiers, List<string> Decorators,
        List<NativeParameter> Parameters, List<string> TypeParameters, int Line, int Column, int EndLine, int EndColumn)
        : NativeSpan(Line, Column, EndLine, EndColumn)
    {
        /// <summary>Gets the compiler-resolved return type.</summary>
        public TypeReference? ReturnTypeFact { get; init; }
    }
    private sealed record NativeType(string Name, string Kind, List<string> Modifiers, List<string> Decorators, List<string> BaseTypes,
        List<string> TypeParameters, List<NativeField> Fields, List<NativeMethod> Methods, List<NativeType> NestedTypes,
        List<string> EnumValues, int Line, int Column, int EndLine, int EndColumn) : NativeSpan(Line, Column, EndLine, EndColumn);
}
