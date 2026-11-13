// Maps TypeScript compiler responses into the shared Cop source model.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal sealed class TypeScriptProjection(string? searchPath)
{
    private static readonly Lazy<string> s_scriptPath = new(() =>
    {
        using Stream stream = typeof(TypeScriptProjection).Assembly.GetManifestResourceStream(
            "Cop.Providers.src.typescript-projector.cjs")
            ?? throw new InvalidOperationException("The embedded TypeScript projector is missing.");
        using StreamReader reader = new StreamReader(stream);
        byte[] bytes = Encoding.UTF8.GetBytes(reader.ReadToEnd());
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cop", "helpers", "javascript");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, hash + ".cjs");
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return path;
        }

        string temporary = Path.Combine(directory, hash + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
        return path;
    });
    private readonly ConcurrentDictionary<string, Lazy<JavaScriptToolchain?>> _toolchains = new();
    private readonly ConcurrentDictionary<string, string> _unavailableCompilers = new();

    private JavaScriptToolchain? FindToolchain(string filePath)
    {
        string absolutePath = Path.GetFullPath(filePath);
        string directory = Path.GetDirectoryName(absolutePath)!;
        JavaScriptToolchain? toolchain = _toolchains.GetOrAdd(directory, _ => new(() =>
            JavaScriptToolchain.Discover(absolutePath, searchPath))).Value;
        return toolchain is not null && !_unavailableCompilers.ContainsKey(toolchain.CompilerPath) ? toolchain : null;
    }

    /// <summary>Gets the installed compiler identity, if available.</summary>
    public string? GetCacheIdentity(string filePath) => FindToolchain(filePath) is { } toolchain
        ? $"{toolchain.NodePath}:{toolchain.CompilerPath}" : null;

    /// <summary>Projects compiler syntax and binding facts for a file.</summary>
    public async ValueTask<SourceFile?> ParseAsync(
        string filePath, string sourceText, CancellationToken cancellationToken = default)
    {
        string absolutePath = Path.GetFullPath(filePath);
        JavaScriptToolchain? toolchain = FindToolchain(filePath);
        if (toolchain is null)
        {
            return null;
        }

        string request = JsonSerializer.Serialize(new { filePath = absolutePath, sourceText });
        try
        {
            FileProjection projection = await RequestAsync<FileProjection>(
                toolchain, request, cancellationToken).ConfigureAwait(false);
            return projection.ToSourceFile(filePath, sourceText);
        }
        catch (CompilerUnavailableException ex)
        {
            ReportUnavailable(toolchain, ex.Message);
            return null;
        }
    }

    /// <summary>Reads the compiler configuration governing a source file.</summary>
    public async ValueTask<ProjectConfiguration?> ReadProjectAsync(
        string filePath, CancellationToken cancellationToken = default)
    {
        JavaScriptToolchain? toolchain = FindToolchain(filePath);
        if (toolchain is null)
        {
            return null;
        }

        try
        {
            return await RequestAsync<ProjectConfiguration>(toolchain,
                JsonSerializer.Serialize(new { kind = "project", filePath = Path.GetFullPath(filePath) }),
                cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<T> RequestAsync<T>(
        JavaScriptToolchain toolchain, string request, CancellationToken cancellationToken)
    {
        ToolchainResult output = await ToolchainExecutionScope.RequestAsync(new ToolchainCommand(toolchain.NodePath, [s_scriptPath.Value, toolchain.CompilerPath], Environment.CurrentDirectory, new Dictionary<string, string?>
            {
                ["NODE_OPTIONS"] = null,
                ["NODE_PATH"] = null
            }), request, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (output.Status == ToolchainStatus.Unavailable)
        {
            throw new CompilerUnavailableException(output.Diagnostic ?? "Node.js is unavailable.");
        }

        if (output.Status != ToolchainStatus.Success)
        {
            throw new InvalidOperationException($"JavaScript/TypeScript compiler analysis failed: {output.Diagnostic} {output.StandardError}");
        }

        using JsonDocument document = JsonDocument.Parse(output.StandardOutput);
        if (document.RootElement.TryGetProperty("Unavailable", out JsonElement unavailable))
        {
            throw new CompilerUnavailableException(unavailable.GetString() ?? "Installed TypeScript is unusable.");
        }

        T data = JsonSerializer.Deserialize<T>(output.StandardOutput)
            ?? throw new InvalidDataException("The TypeScript compiler returned no projection.");
        return data;
    }

    private sealed class CompilerUnavailableException(string message) : Exception(message);

    internal sealed class ProjectConfiguration
    {
        /// <summary>Gets or sets the compiler-selected source files.</summary>
        public List<string> SourceFiles { get; set; } = [];
        /// <summary>Gets or sets referenced compiler project paths.</summary>
        public List<string> References { get; set; } = [];
        /// <summary>Gets or sets module path alias mappings.</summary>
        public Dictionary<string, string[]> PathAliases { get; set; } = [];
        /// <summary>Gets or sets the module resolution base directory.</summary>
        public string? BaseUrl { get; set; }
        /// <summary>Gets or sets compiler configuration diagnostics.</summary>
        public List<string> Errors { get; set; } = [];
    }

    private sealed class FileProjection
    {
        /// <summary>Gets or sets projected type declarations.</summary>
        public List<TypeProjection> Types { get; set; } = [];
        /// <summary>Gets or sets projected module-level methods.</summary>
        public List<MethodProjection> Methods { get; set; } = [];
        /// <summary>Gets or sets projected source statements.</summary>
        public List<StatementProjection> Statements { get; set; } = [];
        /// <summary>Gets or sets imported module names.</summary>
        public List<string> Usings { get; set; } = [];
        /// <summary>Gets or sets structured import binding facts.</summary>
        public List<ImportInfo> Imports { get; set; } = [];
        /// <summary>Gets or sets line numbers occupied by comments.</summary>
        public HashSet<int> CommentLines { get; set; } = [];
        /// <summary>Gets or sets compiler parsing diagnostics.</summary>
        public List<string> ParseErrors { get; set; } = [];

        /// <summary>Builds a source file and links method and parent IDs.</summary>
        public SourceFile ToSourceFile(string filePath, string sourceText)
        {
            Dictionary<int, MethodDeclaration> methods = new Dictionary<int, MethodDeclaration>();
            List<TypeDeclaration> types = Types.Select(t => t.ToType(methods)).ToList();
            List<MethodDeclaration> moduleMethods = Methods.Select(m => m.ToMethod(methods)).ToList();
            List<StatementInfo> statements = Statements.Select(s => s.ToStatement()).ToList();
            for (int i = 0; i < statements.Count; i++)
            {
                StatementProjection projection = Statements[i];
                StatementInfo statement = statements[i];
                if (projection.MethodId is int methodId)
                {
                    statement.Method = methods[methodId];
                    statement.Method.Statements.Add(statement);
                }
                if (projection.ParentId is int parentId)
                {
                    statement.Parent = statements[parentId];
                    statement.Parent.MutableChildren.Add(statement);
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
        /// <summary>Gets or sets the declared type name.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Gets or sets the shared-model type category.</summary>
        public string Kind { get; set; } = string.Empty;
        /// <summary>Gets or sets the enclosing namespace, if present.</summary>
        public string? Namespace { get; set; }
        /// <summary>Gets or sets the shared-model modifier flags.</summary>
        public int Modifiers { get; set; }
        /// <summary>Gets or sets declared base type names.</summary>
        public List<string> BaseTypes { get; set; } = [];
        /// <summary>Gets or sets attached decorator expressions.</summary>
        public List<string> Decorators { get; set; } = [];
        /// <summary>Gets or sets constructor projections.</summary>
        public List<MethodProjection> Constructors { get; set; } = [];
        /// <summary>Gets or sets member method projections.</summary>
        public List<MethodProjection> Methods { get; set; } = [];
        /// <summary>Gets or sets nested type projections.</summary>
        public List<TypeProjection> NestedTypes { get; set; } = [];
        /// <summary>Gets or sets enumeration member names.</summary>
        public List<string> EnumValues { get; set; } = [];
        /// <summary>Gets or sets field declarations.</summary>
        public List<FieldDeclaration> Fields { get; set; } = [];
        /// <summary>Gets or sets property declarations.</summary>
        public List<PropertyDeclaration> Properties { get; set; } = [];
        /// <summary>Gets or sets the declaration's starting line.</summary>
        public int Line { get; set; }
        /// <summary>Gets or sets whether documentation is attached.</summary>
        public bool HasDocComment { get; set; }
        /// <summary>Gets or sets attached documentation text.</summary>
        public string? DocComment { get; set; }
        /// <summary>Gets or sets whether the declaration is exported.</summary>
        public bool IsExported { get; set; }
        /// <summary>Gets or sets whether a base class is declared.</summary>
        public bool HasBaseClass { get; set; }
        /// <summary>Gets or sets whether the type is abstract.</summary>
        public bool IsAbstract { get; set; }
        /// <summary>Gets or sets whether type parameters are declared.</summary>
        public bool IsGeneric { get; set; }
        /// <summary>Gets or sets whether interfaces are implemented.</summary>
        public bool HasImplements { get; set; }
        /// <summary>Gets or sets implemented interface names.</summary>
        public List<string> Interfaces { get; set; } = [];
        /// <summary>Gets or sets the resolved type alias target.</summary>
        public TypeReference? AliasTarget { get; set; }
        /// <summary>Gets or sets declared type parameter names.</summary>
        public List<string> TypeParameters { get; set; } = [];
        /// <summary>Gets or sets the declaration's starting column.</summary>
        public int? Column { get; set; }
        /// <summary>Gets or sets the declaration's ending line.</summary>
        public int? EndLine { get; set; }
        /// <summary>Gets or sets the declaration's ending column.</summary>
        public int? EndColumn { get; set; }

        /// <summary>Builds a type and indexes its projected methods.</summary>
        public TypeDeclaration ToType(Dictionary<int, MethodDeclaration> methods) =>
            new JavaScriptTypeDeclaration(new TypeDeclaration(Name, Enum.Parse<TypeKind>(Kind), (Modifier)Modifiers, BaseTypes, Decorators,
                Constructors.Select(m => m.ToMethod(methods)).ToList(),
                Methods.Select(m => m.ToMethod(methods)).ToList(),
                NestedTypes.Select(t => t.ToType(methods)).ToList(), EnumValues, Line)
            {
                Fields = Fields, Properties = Properties, HasDocComment = HasDocComment, DocComment = DocComment,
                AliasTarget = AliasTarget, TypeParameters = TypeParameters, Interfaces = Interfaces, Namespace = Namespace,
                Column = Column, EndLine = EndLine, EndColumn = EndColumn
            })
            {
                IsExported = IsExported, HasBaseClass = HasBaseClass, IsAbstract = IsAbstract,
                IsGeneric = IsGeneric, HasImplements = HasImplements
            };
    }

    private sealed class MethodProjection
    {
        /// <summary>Gets or sets the ID used by statement method links.</summary>
        public int Id { get; set; }
        /// <summary>Gets or sets the declared method name.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Gets or sets the shared-model modifier flags.</summary>
        public int Modifiers { get; set; }
        /// <summary>Gets or sets attached decorator expressions.</summary>
        public List<string> Decorators { get; set; } = [];
        /// <summary>Gets or sets the resolved return type.</summary>
        public TypeReference? ReturnType { get; set; }
        /// <summary>Gets or sets parameter declarations.</summary>
        public List<ParameterDeclaration> Parameters { get; set; } = [];
        /// <summary>Gets or sets the declaration's starting line.</summary>
        public int Line { get; set; }
        /// <summary>Gets or sets whether documentation is attached.</summary>
        public bool HasDocComment { get; set; }
        /// <summary>Gets or sets attached documentation text.</summary>
        public string? DocComment { get; set; }
        /// <summary>Gets or sets whether this is a generator function.</summary>
        public bool IsGenerator { get; set; }
        /// <summary>Gets or sets whether this is an arrow function.</summary>
        public bool IsArrow { get; set; }
        /// <summary>Gets or sets whether this is a property getter.</summary>
        public bool IsGetter { get; set; }
        /// <summary>Gets or sets whether this is a property setter.</summary>
        public bool IsSetter { get; set; }
        /// <summary>Gets or sets declared type parameter names.</summary>
        public List<string> TypeParameters { get; set; } = [];
        /// <summary>Gets or sets the declaration's starting column.</summary>
        public int? Column { get; set; }
        /// <summary>Gets or sets the declaration's ending line.</summary>
        public int? EndLine { get; set; }
        /// <summary>Gets or sets the declaration's ending column.</summary>
        public int? EndColumn { get; set; }

        /// <summary>Builds and indexes the shared method declaration.</summary>
        public MethodDeclaration ToMethod(Dictionary<int, MethodDeclaration> methods)
        {
            JavaScriptMethodDeclaration method = new MethodDeclaration(Name, (Modifier)Modifiers, Decorators, ReturnType, Parameters, Line)
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
        /// <summary>Gets or sets the shared-model statement category.</summary>
        public string Kind { get; set; } = string.Empty;
        /// <summary>Gets or sets parsed statement keywords.</summary>
        public List<string> Keywords { get; set; } = [];
        /// <summary>Gets or sets the referenced type's name.</summary>
        public string? TypeName { get; set; }
        /// <summary>Gets or sets the resolved receiver type.</summary>
        public TypeReference? ReceiverType { get; set; }
        /// <summary>Gets or sets the resolved expression value type.</summary>
        public TypeReference? ValueType { get; set; }
        /// <summary>Gets or sets the callable bound by the compiler.</summary>
        public CallableReference? ReferencedCallable { get; set; }
        /// <summary>Gets or sets the referenced member's name.</summary>
        public string? MemberName { get; set; }
        /// <summary>Gets or sets source spellings of call arguments.</summary>
        public List<string> Arguments { get; set; } = [];
        /// <summary>Gets or sets the structured call target expression.</summary>
        public ExpressionInfo? CallTarget { get; set; }
        /// <summary>Gets or sets structured call argument expressions.</summary>
        public List<ExpressionInfo> ArgumentExpressions { get; set; } = [];
        /// <summary>Gets or sets the statement's starting line.</summary>
        public int Line { get; set; }
        /// <summary>Gets or sets the enclosing projected method's ID.</summary>
        public int? MethodId { get; set; }
        /// <summary>Gets or sets the enclosing statement's index.</summary>
        public int? ParentId { get; set; }
        /// <summary>Gets or sets whether an exception is rethrown.</summary>
        public bool HasRethrow { get; set; }
        /// <summary>Gets or sets whether this is an error handler.</summary>
        public bool IsErrorHandler { get; set; }
        /// <summary>Gets or sets whether the handler catches broadly.</summary>
        public bool IsGenericErrorHandler { get; set; }
        /// <summary>Gets or sets whether the statement body is braced.</summary>
        public bool IsBraced { get; set; }
        /// <summary>Gets or sets parsed environment access classification.</summary>
        public bool IsEnvironmentVariableAccess { get; set; }
        /// <summary>Gets or sets parsed environment read classification.</summary>
        public bool IsEnvironmentVariableRead { get; set; }
        /// <summary>Gets or sets the conservative original-throw proof.</summary>
        public bool IsOriginalExceptionAlwaysRethrown { get; set; }
        /// <summary>Gets or sets positive AbortError guard recognition.</summary>
        public bool IsCancellationHandler { get; set; }
        /// <summary>Gets or sets the conservative AbortError preservation proof.</summary>
        public bool IsCallerCancellationPreserved { get; set; }
        /// <summary>Gets or sets direct construction transfer classification.</summary>
        public bool IsOwnershipTransferred { get; set; }
        /// <summary>Gets or sets direct using initializer classification.</summary>
        public bool IsUsingResource { get; set; }
        /// <summary>Gets or sets the source condition expression.</summary>
        public string? Condition { get; set; }
        /// <summary>Gets or sets the source value expression.</summary>
        public string? Expression { get; set; }
        /// <summary>Gets or sets the structured value expression.</summary>
        public ExpressionInfo? ValueExpression { get; set; }
        /// <summary>Gets or sets interfaces of a constructed type.</summary>
        public List<string> ConstructedTypeInterfaces { get; set; } = [];
        /// <summary>Gets or sets the statement's starting column.</summary>
        public int? Column { get; set; }
        /// <summary>Gets or sets the statement's ending line.</summary>
        public int? EndLine { get; set; }
        /// <summary>Gets or sets the statement's ending column.</summary>
        public int? EndColumn { get; set; }

        /// <summary>Builds shared statement and expression facts.</summary>
        public StatementInfo ToStatement() =>
            new JavaScriptStatementInfo(new StatementInfo(Kind, Keywords, TypeName, MemberName, Arguments, Line, MethodId is not null))
            {
                CallTarget = CallTarget, ArgumentExpressions = ArgumentExpressions, HasRethrow = HasRethrow,
                IsErrorHandler = IsErrorHandler, IsGenericErrorHandler = IsGenericErrorHandler,
                IsBraced = IsBraced, Condition = Condition, Expression = Expression, ValueExpression = ValueExpression,
                Column = Column, EndLine = EndLine, EndColumn = EndColumn,
                ConstructedTypeInterfaces = ConstructedTypeInterfaces,
                ReceiverType = ReceiverType, ValueType = ValueType, ReferencedCallable = ReferencedCallable,
                IsEnvironmentVariableAccess = IsEnvironmentVariableAccess,
                IsEnvironmentVariableRead = IsEnvironmentVariableRead,
                IsOriginalExceptionAlwaysRethrown = IsOriginalExceptionAlwaysRethrown,
                IsCancellationHandler = IsCancellationHandler,
                IsCallerCancellationPreserved = IsCallerCancellationPreserved,
                IsOwnershipTransferred = IsOwnershipTransferred, IsUsingResource = IsUsingResource
            };
    }
}
