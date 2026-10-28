using System.Collections.Concurrent;
using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>Discovers an already-installed compiler without invoking npm or assuming global require resolution.</summary>
public sealed record JavaScriptToolchain(string NodePath, string CompilerPath)
{
    public static JavaScriptToolchain? Discover(string sourcePath, string? searchPath = null)
    {
        var node = ToolchainProcess.FindExecutable("node", searchPath);
        if (node is null) return null;

        for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!);
            directory is not null; directory = directory.Parent)
        {
            var local = Path.Combine(directory.FullName, "node_modules", "typescript", "lib", "typescript.js");
            if (File.Exists(local)) return new(node, local);
        }

        // npm's Windows launchers sit beside node_modules; Unix tsc is normally a symlink
        // into ../lib/node_modules/typescript/bin/tsc (including version-manager installations).
        foreach (var entry in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var launcherName in OperatingSystem.IsWindows()
                ? new[] { "tsc.cmd", "tsc.ps1", "tsc.exe", "tsc" } : new[] { "tsc" })
            {
                var launcher = new FileInfo(Path.Combine(directory, launcherName));
                if (!launcher.Exists) continue;
                var target = launcher.ResolveLinkTarget(returnFinalTarget: true);
                var candidates = new[]
                {
                    Path.Combine(directory, "node_modules", "typescript", "lib", "typescript.js"),
                    Path.GetFullPath(Path.Combine(directory, "..", "lib", "node_modules", "typescript", "lib", "typescript.js")),
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target?.FullName ?? launcher.FullName)!,
                        "..", "lib", "typescript.js"))
                };
                foreach (var candidate in candidates)
                    if (File.Exists(candidate)) return new(node, candidate);
            }
        }
        return null;
    }
}

internal sealed class TypeScriptProjection(string? searchPath)
{
    private static readonly Lazy<string> Script = new(() =>
    {
        using var stream = typeof(TypeScriptProjection).Assembly.GetManifestResourceStream(
            "Cop.Providers.src.typescript-projector.cjs")
            ?? throw new InvalidOperationException("The embedded TypeScript projector is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    private readonly ConcurrentDictionary<string, Lazy<JavaScriptToolchain?>> _toolchains = new();
    private readonly ConcurrentDictionary<string, string> _unavailableCompilers = new();

    private JavaScriptToolchain? FindToolchain(string filePath)
    {
        var absolutePath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(absolutePath)!;
        var toolchain = _toolchains.GetOrAdd(directory, _ => new(() =>
            JavaScriptToolchain.Discover(absolutePath, searchPath))).Value;
        return toolchain is not null && !_unavailableCompilers.ContainsKey(toolchain.CompilerPath) ? toolchain : null;
    }

    public string? GetCacheIdentity(string filePath) => FindToolchain(filePath) is { } toolchain
        ? $"{toolchain.NodePath}:{toolchain.CompilerPath}" : null;

    public SourceFile? Parse(string filePath, string sourceText)
    {
        var absolutePath = Path.GetFullPath(filePath);
        var toolchain = FindToolchain(filePath);
        if (toolchain is null) return null;
        var request = JsonSerializer.Serialize(new { filePath = absolutePath, sourceText });
        try { return Request<FileProjection>(toolchain, request).ToSourceFile(filePath, sourceText); }
        catch (CompilerUnavailableException ex)
        {
            ReportUnavailable(toolchain, ex.Message);
            return null;
        }
    }

    public ProjectConfiguration? ReadProject(string filePath)
    {
        var toolchain = FindToolchain(filePath);
        if (toolchain is null) return null;
        try
        {
            return Request<ProjectConfiguration>(toolchain,
                JsonSerializer.Serialize(new { kind = "project", filePath = Path.GetFullPath(filePath) }));
        }
        catch (CompilerUnavailableException ex)
        {
            ReportUnavailable(toolchain, ex.Message);
            return null;
        }
    }

    private void ReportUnavailable(JavaScriptToolchain toolchain, string diagnostic)
    {
        _unavailableCompilers.TryAdd(toolchain.CompilerPath, diagnostic);
        ProviderWarnings.ReportOnce("javascript", "typescript-load", diagnostic + " Using simpler syntax analysis.");
    }

    private static T Request<T>(JavaScriptToolchain toolchain, string request)
    {
        var output = ToolchainExecutionScope.RequestAsync(toolchain.NodePath, ["-e", Script.Value, toolchain.CompilerPath],
            Environment.CurrentDirectory, request)
            .GetAwaiter().GetResult();
        if (output.Status == ToolchainStatus.Unavailable)
            throw new CompilerUnavailableException(output.Diagnostic ?? "Node.js is unavailable.");
        if (output.Status != ToolchainStatus.Success)
            throw new InvalidOperationException($"JavaScript/TypeScript compiler analysis failed: {output.Diagnostic} {output.StandardError}");
        using var document = JsonDocument.Parse(output.StandardOutput);
        if (document.RootElement.TryGetProperty("Unavailable", out var unavailable))
            throw new CompilerUnavailableException(unavailable.GetString() ?? "Installed TypeScript is unusable.");
        var data = JsonSerializer.Deserialize<T>(output.StandardOutput)
            ?? throw new InvalidDataException("The TypeScript compiler returned no projection.");
        return data;
    }

    private sealed class CompilerUnavailableException(string message) : Exception(message);

    internal sealed class ProjectConfiguration
    {
        public List<string> SourceFiles { get; set; } = [];
        public List<string> References { get; set; } = [];
        public Dictionary<string, string[]> PathAliases { get; set; } = [];
        public string? BaseUrl { get; set; }
        public List<string> Errors { get; set; } = [];
    }

    private sealed class FileProjection
    {
        public List<TypeProjection> Types { get; set; } = [];
        public List<MethodProjection> Methods { get; set; } = [];
        public List<StatementProjection> Statements { get; set; } = [];
        public List<string> Usings { get; set; } = [];
        public List<ImportInfo> Imports { get; set; } = [];
        public HashSet<int> CommentLines { get; set; } = [];
        public List<string> ParseErrors { get; set; } = [];

        public SourceFile ToSourceFile(string filePath, string sourceText)
        {
            var methods = new Dictionary<int, MethodDeclaration>();
            var types = Types.Select(t => t.ToType(methods)).ToList();
            var moduleMethods = Methods.Select(m => m.ToMethod(methods)).ToList();
            var statements = Statements.Select(s => s.ToStatement()).ToList();
            for (int i = 0; i < statements.Count; i++)
            {
                var projection = Statements[i];
                var statement = statements[i];
                if (projection.MethodId is int methodId)
                {
                    statement.Method = methods[methodId];
                    statement.Method.Statements.Add(statement);
                }
                if (projection.ParentId is int parentId)
                {
                    statement.Parent = statements[parentId];
                    statement.Parent._children.Add(statement);
                }
            }
            return new(filePath, "javascript", types, statements, sourceText)
            {
                Methods = moduleMethods, Usings = Usings, Imports = Imports,
                CommentLines = CommentLines, ParseErrors = ParseErrors
            };
        }
    }

    private sealed class TypeProjection
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? Namespace { get; set; }
        public int Modifiers { get; set; }
        public List<string> BaseTypes { get; set; } = [];
        public List<string> Decorators { get; set; } = [];
        public List<MethodProjection> Constructors { get; set; } = [];
        public List<MethodProjection> Methods { get; set; } = [];
        public List<TypeProjection> NestedTypes { get; set; } = [];
        public List<string> EnumValues { get; set; } = [];
        public List<FieldDeclaration> Fields { get; set; } = [];
        public List<PropertyDeclaration> Properties { get; set; } = [];
        public int Line { get; set; }
        public bool HasDocComment { get; set; }
        public string? DocComment { get; set; }
        public bool IsExported { get; set; }
        public bool HasBaseClass { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsGeneric { get; set; }
        public bool HasImplements { get; set; }
        public List<string> Interfaces { get; set; } = [];
        public TypeReference? AliasTarget { get; set; }
        public List<string> TypeParameters { get; set; } = [];
        public int? Column { get; set; }
        public int? EndLine { get; set; }
        public int? EndColumn { get; set; }

        public TypeDeclaration ToType(Dictionary<int, MethodDeclaration> methods) =>
            new TypeDeclaration(Name, Enum.Parse<TypeKind>(Kind), (Modifier)Modifiers, BaseTypes, Decorators,
                Constructors.Select(m => m.ToMethod(methods)).ToList(),
                Methods.Select(m => m.ToMethod(methods)).ToList(),
                NestedTypes.Select(t => t.ToType(methods)).ToList(), EnumValues, Line)
            {
                Fields = Fields, Properties = Properties, HasDocComment = HasDocComment, DocComment = DocComment,
                AliasTarget = AliasTarget, TypeParameters = TypeParameters, Interfaces = Interfaces, Namespace = Namespace,
                Column = Column, EndLine = EndLine, EndColumn = EndColumn
            }.AsJavaScript(IsExported, HasBaseClass, IsAbstract, IsGeneric, HasImplements);
    }

    private sealed class MethodProjection
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Modifiers { get; set; }
        public List<string> Decorators { get; set; } = [];
        public TypeReference? ReturnType { get; set; }
        public List<ParameterDeclaration> Parameters { get; set; } = [];
        public int Line { get; set; }
        public bool HasDocComment { get; set; }
        public string? DocComment { get; set; }
        public bool IsGenerator { get; set; }
        public bool IsArrow { get; set; }
        public bool IsGetter { get; set; }
        public bool IsSetter { get; set; }
        public List<string> TypeParameters { get; set; } = [];
        public int? Column { get; set; }
        public int? EndLine { get; set; }
        public int? EndColumn { get; set; }

        public MethodDeclaration ToMethod(Dictionary<int, MethodDeclaration> methods)
        {
            var method = new MethodDeclaration(Name, (Modifier)Modifiers, Decorators, ReturnType, Parameters, Line)
            {
                HasDocComment = HasDocComment, DocComment = DocComment, TypeParameters = TypeParameters,
                Column = Column, EndLine = EndLine, EndColumn = EndColumn
            }.AsJavaScript(IsGenerator, IsArrow, IsGetter, IsSetter);
            methods.Add(Id, method);
            return method;
        }
    }

    private sealed class StatementProjection
    {
        public string Kind { get; set; } = "";
        public List<string> Keywords { get; set; } = [];
        public string? TypeName { get; set; }
        public string? MemberName { get; set; }
        public List<string> Arguments { get; set; } = [];
        public ExpressionInfo? CallTarget { get; set; }
        public List<ExpressionInfo> ArgumentExpressions { get; set; } = [];
        public int Line { get; set; }
        public int? MethodId { get; set; }
        public int? ParentId { get; set; }
        public bool HasRethrow { get; set; }
        public bool IsErrorHandler { get; set; }
        public bool IsGenericErrorHandler { get; set; }
        public bool IsBraced { get; set; }
        public string? Condition { get; set; }
        public string? Expression { get; set; }
        public ExpressionInfo? ValueExpression { get; set; }
        public List<string> ConstructedTypeInterfaces { get; set; } = [];
        public int? Column { get; set; }
        public int? EndLine { get; set; }
        public int? EndColumn { get; set; }

        public StatementInfo ToStatement() =>
            new JavaScriptStatementInfo(Kind, Keywords, TypeName, MemberName, Arguments, Line, MethodId is not null)
            {
                CallTarget = CallTarget, ArgumentExpressions = ArgumentExpressions, HasRethrow = HasRethrow,
                IsErrorHandler = IsErrorHandler, IsGenericErrorHandler = IsGenericErrorHandler,
                IsBraced = IsBraced, Condition = Condition, Expression = Expression, ValueExpression = ValueExpression,
                Column = Column, EndLine = EndLine, EndColumn = EndColumn,
                ConstructedTypeInterfaces = ConstructedTypeInterfaces
            };
    }
}
