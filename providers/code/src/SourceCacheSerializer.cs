// Serializes source models while preserving declaration and statement identity.

using System.IO.Compression;
using System.Text;
using System.Reflection;
using System.Runtime.Loader;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

internal static class SourceCacheSerializer
{
    private static readonly byte[] Magic = [(byte)'C', (byte)'O', (byte)'P', (byte)'C'];
    private const int FormatVersion = 12;

    public static void Save(string cachePath, byte[] fingerprint, List<SourceFile> sourceFiles)
    {
        string? directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream fileStream = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None);
        Save(fileStream, fingerprint, sourceFiles);
    }

    internal static void Save(Stream stream, byte[] fingerprint, List<SourceFile> sourceFiles)
    {
        using GZipStream gzip = new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true);
        using CacheWriter writer = new CacheWriter(gzip);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(fingerprint.Length);
        writer.Write(fingerprint);
        WriteList(writer, sourceFiles, WriteSourceFile);
    }

    public static List<SourceFile>? TryLoad(string cachePath, byte[] fingerprint)
        => TryLoad(cachePath, fingerprint, typeof(SourceCacheSerializer).Assembly);

    public static List<SourceFile>? TryLoad(string cachePath, byte[] fingerprint, Assembly parserAssembly)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            using FileStream fileStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryLoad(fileStream, fingerprint, parserAssembly);
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static List<SourceFile>? TryLoad(Stream stream, byte[] fingerprint, Assembly parserAssembly)
    {
        try
        {
            using GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            using CacheReader reader = new CacheReader(gzip,
                AssemblyLoadContext.GetLoadContext(parserAssembly) ?? AssemblyLoadContext.Default);

            byte[] magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !magic.SequenceEqual(Magic))
            {
                return null;
            }

            int version = reader.ReadInt32();
            if (version != FormatVersion)
            {
                return null;
            }

            byte[] cachedFingerprint = ReadBytes(reader, nameof(fingerprint));
            if (!cachedFingerprint.SequenceEqual(fingerprint))
            {
                return null;
            }

            return ReadList(reader, ReadSourceFile, "SourceFiles");
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class CacheReader(Stream input, AssemblyLoadContext context)
        : BinaryReader(input, Encoding.UTF8, leaveOpen: false)
    {
        public AssemblyLoadContext Context { get; } = context;
        public Dictionary<int, StatementInfo> Statements { get; } = [];
        public Dictionary<int, MethodDeclaration> Methods { get; } = [];
        public Dictionary<int, TypeDeclaration> Types { get; } = [];
        public Dictionary<int, ExpressionInfo> Expressions { get; } = [];
    }

    private sealed class CacheWriter(Stream output) : BinaryWriter(output, Encoding.UTF8, leaveOpen: false)
    {
        public Dictionary<StatementInfo, int> Statements { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<MethodDeclaration, int> Methods { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<TypeDeclaration, int> Types { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<ExpressionInfo, int> Expressions { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private static void WriteSourceFile(BinaryWriter writer, SourceFile sourceFile)
    {
        CacheWriter graph = (CacheWriter)writer;
        graph.Statements.Clear();
        graph.Methods.Clear();
        graph.Types.Clear();
        graph.Expressions.Clear();
        writer.Write(sourceFile.Path);
        writer.Write(sourceFile.Language);
        WriteList(writer, sourceFile.Types, WriteTypeDeclaration);
        WriteList(writer, sourceFile.Statements, WriteStatementInfo);
        writer.Write(sourceFile.RawText);
        WriteList(writer, sourceFile.Usings, static (w, value) => w.Write(value));
        WriteNullableString(writer, sourceFile.Namespace);
        WriteList(writer, sourceFile.Regions, WriteRegionInfo);
        WriteHashSet(writer, sourceFile.CommentLines);
        WriteList(writer, sourceFile.Comments, static (w, comment) =>
        {
            w.Write(comment.Kind);
            w.Write(comment.Content);
            w.Write(comment.Line);
        });
        WriteList(writer, sourceFile.Methods, WriteMethodDeclaration);
        WriteList(writer, sourceFile.Imports, WriteImport);
        WriteList(writer, sourceFile.ParseErrors, static (w, error) => w.Write(error));
        List<StatementInfo> statements = graph.Statements.Keys.ToList();
        HashSet<object?> seen = statements.ToHashSet(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < statements.Count; i++)
        {
            StatementInfo statement = statements[i];
            if (statement.Parent is not null && seen.Add(statement.Parent))
            {
                statements.Add(statement.Parent);
            }

            foreach (StatementInfo? related in statement.Children.Concat(statement.Method?.Statements ?? []))
            {
                if (seen.Add(related))
                {
                    statements.Add(related);
                }
            }
        }
        WriteList(writer, statements, WriteStatementInfo);
        WriteList(writer, statements.Where(s => s.Method is not null).Select(s => s.Method!)
            .Distinct<MethodDeclaration>(ReferenceEqualityComparer.Instance).ToList(), WriteMethodDeclaration);
        writer.Write(graph.Statements.Count);
        foreach ((StatementInfo? statement, int id) in graph.Statements)
        {
            writer.Write(id);
            writer.Write(statement.Parent is null ? -1 : graph.Statements[statement.Parent]);
            writer.Write(statement.Method is null ? -1 : graph.Methods[statement.Method]);
        }
    }

    private static SourceFile ReadSourceFile(BinaryReader reader)
    {
        CacheReader graph = (CacheReader)reader;
        graph.Statements.Clear();
        graph.Methods.Clear();
        graph.Types.Clear();
        graph.Expressions.Clear();
        string path = reader.ReadString();
        string language = reader.ReadString();
        List<TypeDeclaration> types = ReadList(reader, ReadTypeDeclaration, "SourceFile.Types");
        List<StatementInfo> statements = ReadList(reader, ReadStatementInfo, "SourceFile.Statements");
        string rawText = reader.ReadString();
        List<string> usings = ReadList(reader, static r => r.ReadString(), "SourceFile.Usings");
        string? @namespace = ReadNullableString(reader);
        List<RegionInfo> regions = ReadList(reader, ReadRegionInfo, "SourceFile.Regions");
        HashSet<int> commentLines = ReadHashSet(reader, "SourceFile.CommentLines");
        List<CommentInfo> comments = ReadList(reader,
            static r => new CommentInfo(r.ReadString(), r.ReadString(), r.ReadInt32()),
            "SourceFile.Comments");
        List<MethodDeclaration> methods = ReadList(reader, ReadMethodDeclaration, "SourceFile.Methods");
        List<ImportInfo> imports = ReadList(reader, ReadImport, "SourceFile.Imports");
        List<string> errors = ReadList(reader, static r => r.ReadString(), "SourceFile.ParseErrors");
        _ = ReadList(reader, ReadStatementInfo, "SourceFile.ParentStatements");
        _ = ReadList(reader, ReadMethodDeclaration, "SourceFile.StatementMethods");
        int count = ReadCount(reader, "SourceFile.Graph");
        if (count != graph.Statements.Count)
        {
            throw new InvalidDataException("Invalid statement graph size.");
        }

        HashSet<int> linked = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int statementId = reader.ReadInt32();
            int parentId = reader.ReadInt32();
            int methodIndex = reader.ReadInt32();
            if (!linked.Add(statementId) || !graph.Statements.TryGetValue(statementId, out StatementInfo? statement)
                || parentId < -1 || (parentId >= 0 && !graph.Statements.ContainsKey(parentId))
                || methodIndex < -1 || (methodIndex >= 0 && !graph.Methods.ContainsKey(methodIndex)))
            {
                throw new InvalidDataException("Invalid statement graph reference.");
            }

            statement.Parent = parentId < 0 ? null : graph.Statements[parentId];
            statement.Method = methodIndex < 0 ? null : graph.Methods[methodIndex];
        }

        return new SourceFile(path, language, types, statements, rawText)
        {
            Usings = usings,
            Namespace = @namespace,
            Regions = regions,
            CommentLines = commentLines,
            Comments = comments,
            Methods = methods,
            Imports = imports,
            ParseErrors = errors
        };
    }

    private static void WriteTypeDeclaration(BinaryWriter writer, TypeDeclaration type)
    {
        CacheWriter graph = (CacheWriter)writer;
        if (graph.Types.TryGetValue(type, out int existing))
        {
            writer.Write(existing);
            writer.Write(false);
            return;
        }
        writer.Write(graph.Types.Count);
        writer.Write(true);
        graph.Types.Add(type, graph.Types.Count);
        writer.Write(type.Name);
        writer.Write((int)type.Kind);
        writer.Write((int)type.Modifiers);
        WriteList(writer, type.BaseTypes, static (w, value) => w.Write(value));
        WriteList(writer, type.Decorators, static (w, value) => w.Write(value));
        WriteList(writer, type.Constructors, WriteMethodDeclaration);
        WriteList(writer, type.DeclaredMethods, WriteMethodDeclaration);
        WriteList(writer, type.NestedTypes, WriteTypeDeclaration);
        WriteList(writer, type.EnumValues, static (w, value) => w.Write(value));
        writer.Write(type.Line);
        writer.Write(type.HasDocComment);
        WriteNullableString(writer, type.DocComment);
        WriteList(writer, type.Fields, WriteFieldDeclaration);
        WriteList(writer, type.Properties, WritePropertyDeclaration);
        WriteList(writer, type.Events, WriteEventDeclaration);
        WriteNullableTypeReference(writer, type.AliasTarget);
        WriteList(writer, type.TypeParameters, static (w, value) => w.Write(value));
        WriteSpan(writer, type.Column, type.EndLine, type.EndColumn);
        WriteNullableString(writer, type.Namespace);
        WriteList(writer, type.CachedInterfaces, static (w, value) => w.Write(value));

        // Language-specific subtype tag + flags (e.g. RustType). Null for base types.
        string? languageTag = type.LanguageTag;
        WriteNullableString(writer, languageTag);
        if (languageTag is not null)
        {
            IReadOnlyList<KeyValuePair<string, bool>> flags = type.LanguageFlags ?? [];
            writer.Write(flags.Count);
            foreach (KeyValuePair<string, bool> flag in flags)
            {
                writer.Write(flag.Key);
                writer.Write(flag.Value);
            }
        }
    }

    private static TypeDeclaration ReadTypeDeclaration(BinaryReader reader)
    {
        CacheReader graph = (CacheReader)reader;
        int id = reader.ReadInt32();
        if (!reader.ReadBoolean())
        {
            return graph.Types.TryGetValue(id, out TypeDeclaration? existing) ? existing
                : throw new InvalidDataException("Invalid type reference.");
        }

        TypeDeclaration type = new TypeDeclaration(
            reader.ReadString(),
            (TypeKind)reader.ReadInt32(),
            (Modifier)reader.ReadInt32(),
            ReadList(reader, static r => r.ReadString(), "TypeDeclaration.BaseTypes"),
            ReadList(reader, static r => r.ReadString(), "TypeDeclaration.Decorators"),
            ReadList(reader, ReadMethodDeclaration, "TypeDeclaration.Constructors"),
            ReadList(reader, ReadMethodDeclaration, "TypeDeclaration.Methods"),
            ReadList(reader, ReadTypeDeclaration, "TypeDeclaration.NestedTypes"),
            ReadList(reader, static r => r.ReadString(), "TypeDeclaration.EnumValues"),
            reader.ReadInt32())
        {
            HasDocComment = reader.ReadBoolean(),
            DocComment = ReadNullableString(reader),
            Fields = ReadList(reader, ReadFieldDeclaration, "TypeDeclaration.Fields"),
            Properties = ReadList(reader, ReadPropertyDeclaration, "TypeDeclaration.Properties"),
            Events = ReadList(reader, ReadEventDeclaration, "TypeDeclaration.Events"),
            AliasTarget = ReadNullableTypeReference(reader),
            TypeParameters = ReadList(reader, static r => r.ReadString(), "TypeDeclaration.TypeParameters"),
            Column = ReadNullableInt(reader),
            EndLine = ReadNullableInt(reader),
            EndColumn = ReadNullableInt(reader),
            Namespace = ReadNullableString(reader),
            Interfaces = ReadList(reader, static r => r.ReadString(), "TypeDeclaration.Interfaces")
        };

        // Language-specific subtype tag + flags — reconstruct the subtype if registered.
        string? languageTag = ReadNullableString(reader);
        if (languageTag is not null)
        {
            int flagCount = ReadCount(reader, "TypeDeclaration.LanguageFlags");
            Dictionary<string, bool> flags = new Dictionary<string, bool>(flagCount, StringComparer.Ordinal);
            for (int i = 0; i < flagCount; i++)
            {
                string key = reader.ReadString();
                flags[key] = reader.ReadBoolean();
            }
            type = LanguageTypeRegistry.Reconstruct(languageTag, type, flags, graph.Context) with
            {
                AliasTarget = type.AliasTarget, TypeParameters = type.TypeParameters,
                Column = type.Column, EndLine = type.EndLine, EndColumn = type.EndColumn,
                Namespace = type.Namespace, Methods = type.DeclaredMethods,
                Constructors = type.Constructors, NestedTypes = type.NestedTypes,
                Fields = type.Fields, Properties = type.Properties, Events = type.Events,
                HasDocComment = type.HasDocComment, DocComment = type.DocComment,
                Interfaces = type.CachedInterfaces
            };
        }

        if (!graph.Types.TryAdd(id, type))
        {
            throw new InvalidDataException("Duplicate type identity.");
        }

        return type;
    }

    private static void WriteMethodDeclaration(BinaryWriter writer, MethodDeclaration method)
    {
        CacheWriter graph = (CacheWriter)writer;
        if (graph.Methods.TryGetValue(method, out int existing))
        {
            writer.Write(existing);
            writer.Write(false);
            return;
        }
        writer.Write(graph.Methods.Count);
        writer.Write(true);
        graph.Methods.Add(method, graph.Methods.Count);
        writer.Write(method.Name);
        writer.Write((int)method.Modifiers);
        WriteList(writer, method.Decorators, static (w, value) => w.Write(value));
        WriteNullableTypeReference(writer, method.ReturnType);
        WriteList(writer, method.Parameters, WriteParameterDeclaration);
        writer.Write(method.Line);
        WriteList(writer, method.Statements, WriteStatementInfo);
        writer.Write(method.HasDocComment);
        WriteNullableString(writer, method.DocComment);
        WriteNullableString(writer, method.ReceiverType);
        WriteList(writer, method.TypeParameters, static (w, value) => w.Write(value));
        WriteSpan(writer, method.Column, method.EndLine, method.EndColumn);
        WriteLanguageSubtype(writer, method.LanguageTag, method.LanguageFlags);
        writer.Write(method.Kind);
    }

    private static MethodDeclaration ReadMethodDeclaration(BinaryReader reader)
    {
        CacheReader graph = (CacheReader)reader;
        int id = reader.ReadInt32();
        if (!reader.ReadBoolean())
        {
            return graph.Methods.TryGetValue(id, out MethodDeclaration? existing) ? existing
                : throw new InvalidDataException("Invalid method reference.");
        }

        MethodDeclaration method = new MethodDeclaration(
            reader.ReadString(),
            (Modifier)reader.ReadInt32(),
            ReadList(reader, static r => r.ReadString(), "MethodDeclaration.Decorators"),
            ReadNullableTypeReference(reader),
            ReadList(reader, ReadParameterDeclaration, "MethodDeclaration.Parameters"),
            reader.ReadInt32())
        {
            Statements = ReadList(reader, ReadStatementInfo, "MethodDeclaration.Statements"),
            HasDocComment = reader.ReadBoolean(),
            DocComment = ReadNullableString(reader),
            ReceiverType = ReadNullableString(reader),
            TypeParameters = ReadList(reader, static r => r.ReadString(), "MethodDeclaration.TypeParameters"),
            Column = ReadNullableInt(reader),
            EndLine = ReadNullableInt(reader),
            EndColumn = ReadNullableInt(reader)
        };
        (string? tag, Dictionary<string, bool>? flags) = ReadLanguageSubtype(reader, "MethodDeclaration");
        method = method with { Kind = reader.ReadString() };
        if (tag is not null)
        {
            method = MethodTypeRegistry.Reconstruct(tag, method, flags, graph.Context) with
            {
                Kind = method.Kind,
                Statements = method.Statements, ReceiverType = method.ReceiverType,
                TypeParameters = method.TypeParameters,
                Column = method.Column, EndLine = method.EndLine, EndColumn = method.EndColumn,
                HasDocComment = method.HasDocComment, DocComment = method.DocComment
            };
        }

        if (!graph.Methods.TryAdd(id, method))
        {
            throw new InvalidDataException("Duplicate method identity.");
        }

        return method;
    }

    private static void WriteStatementInfo(BinaryWriter writer, StatementInfo statement)
    {
        CacheWriter graph = (CacheWriter)writer;
        if (graph.Statements.TryGetValue(statement, out int existing))
        {
            writer.Write(existing);
            writer.Write(false);
            return;
        }
        writer.Write(graph.Statements.Count);
        writer.Write(true);
        graph.Statements.Add(statement, graph.Statements.Count);
        writer.Write(statement.Kind);
        WriteList(writer, statement.Keywords, static (w, value) => w.Write(value));
        WriteNullableString(writer, statement.SyntaxTypeNameValue);
        WriteNullableString(writer, statement.MemberName);
        WriteList(writer, statement.Arguments, static (w, value) => w.Write(value));
        writer.Write(statement.Line);
        writer.Write(statement.IsInMethod);
        writer.Write(statement.HasRethrow);
        writer.Write(statement.IsErrorHandler);
        writer.Write(statement.IsGenericErrorHandler);
        writer.Write(statement.IsBraced);
        WriteNullableString(writer, statement.Condition);
        WriteNullableString(writer, statement.Expression);
        writer.Write(statement.CopIgnore);
        WriteExpression(writer, statement.CallTarget);
        WriteExpression(writer, statement.ValueExpression);
        WriteExpression(writer, statement.ConditionExpression);
        WriteList(writer, statement.ArgumentExpressions, WriteExpression);
        WriteSpan(writer, statement.Column, statement.EndLine, statement.EndColumn);
        WriteList(writer, statement.CachedConstructedTypeInterfaces, static (w, value) => w.Write(value));
        WriteList(writer, statement._children, WriteStatementInfo);
        WriteLanguageSubtype(writer, statement.LanguageTag, statement.LanguageFlags);
    }

    private static StatementInfo ReadStatementInfo(BinaryReader reader)
    {
        CacheReader graph = (CacheReader)reader;
        int id = reader.ReadInt32();
        if (!reader.ReadBoolean())
        {
            return graph.Statements.TryGetValue(id, out StatementInfo? existing) ? existing
                : throw new InvalidDataException("Invalid statement reference.");
        }

        StatementInfo statement = new StatementInfo(
            reader.ReadString(),
            ReadList(reader, static r => r.ReadString(), "StatementInfo.Keywords"),
            ReadNullableString(reader),
            ReadNullableString(reader),
            ReadList(reader, static r => r.ReadString(), "StatementInfo.Arguments"),
            reader.ReadInt32(),
            reader.ReadBoolean())
        {
            HasRethrow = reader.ReadBoolean(),
            IsErrorHandler = reader.ReadBoolean(),
            IsGenericErrorHandler = reader.ReadBoolean(),
            IsBraced = reader.ReadBoolean(),
            Condition = ReadNullableString(reader),
            Expression = ReadNullableString(reader),
            CopIgnore = reader.ReadString(),
            CallTarget = ReadExpression(reader),
            ValueExpression = ReadExpression(reader),
            ConditionExpression = ReadExpression(reader),
            ArgumentExpressions = ReadList(reader, r => ReadExpression(r)
                ?? throw new InvalidDataException("Null argument expression."), "StatementInfo.ArgumentExpressions"),
            Column = ReadNullableInt(reader),
            EndLine = ReadNullableInt(reader),
            EndColumn = ReadNullableInt(reader),
            ConstructedTypeInterfaces = ReadList(reader, static r => r.ReadString(), "StatementInfo.ConstructedTypeInterfaces"),
            _children = ReadList(reader, ReadStatementInfo, "StatementInfo.Children")
        };
        (string? tag, Dictionary<string, bool>? flags) = ReadLanguageSubtype(reader, "StatementInfo");
        StatementInfo result = tag is not null ? StatementTypeRegistry.Reconstruct(tag, statement, flags, graph.Context) : statement;
        result.CallTarget = statement.CallTarget;
        result.ValueExpression = statement.ValueExpression;
        result.ConditionExpression = statement.ConditionExpression;
        result.ArgumentExpressions = statement.ArgumentExpressions;
        result.Column = statement.Column;
        result.EndLine = statement.EndLine;
        result.EndColumn = statement.EndColumn;
        result.Condition = statement.Condition;
        result.Expression = statement.Expression;
        result.CopIgnore = statement.CopIgnore;
        result.ConstructedTypeInterfaces = statement.CachedConstructedTypeInterfaces;
        result._children = statement._children;
        if (!graph.Statements.TryAdd(id, result))
        {
            throw new InvalidDataException("Duplicate statement identity.");
        }

        return result;
    }

    private static void WriteExpression(BinaryWriter writer, ExpressionInfo? expression)
    {
        writer.Write(expression is not null);
        if (expression is null)
        {
            return;
        }

        CacheWriter graph = (CacheWriter)writer;
        if (graph.Expressions.TryGetValue(expression, out int existing))
        {
            writer.Write(existing);
            writer.Write(false);
            return;
        }
        writer.Write(graph.Expressions.Count);
        writer.Write(true);
        graph.Expressions.Add(expression, graph.Expressions.Count);
        writer.Write(expression.Kind);
        WriteNullableString(writer, expression.Name);
        WriteNullableString(writer, expression.StringValue);
        writer.Write(expression.BooleanValue.HasValue);
        if (expression.BooleanValue.HasValue)
        {
            writer.Write(expression.BooleanValue.Value);
        }

        WriteExpression(writer, expression.Target);
        WriteList(writer, expression.Children, WriteExpression);
    }

    private static ExpressionInfo? ReadExpression(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }

        CacheReader graph = (CacheReader)reader;
        int id = reader.ReadInt32();
        if (!reader.ReadBoolean())
        {
            return graph.Expressions.TryGetValue(id, out ExpressionInfo? existing) ? existing
                : throw new InvalidDataException("Invalid expression reference.");
        }

        ExpressionInfo expression = new ExpressionInfo(reader.ReadString())
        {
            Name = ReadNullableString(reader),
            StringValue = ReadNullableString(reader),
            BooleanValue = reader.ReadBoolean() ? reader.ReadBoolean() : null,
            Target = ReadExpression(reader),
            Children = ReadList(reader, r => ReadExpression(r)
                ?? throw new InvalidDataException("Null child expression."), "CodeExpression.Children")
        };
        if (!graph.Expressions.TryAdd(id, expression))
        {
            throw new InvalidDataException("Duplicate expression identity.");
        }

        return expression;
    }

    /// <summary>Writes a language-specific subtype tag + flags (shared by Type/Method/Statement).</summary>
    private static void WriteLanguageSubtype(BinaryWriter writer, string? tag, IReadOnlyList<KeyValuePair<string, bool>>? flags)
    {
        WriteNullableString(writer, tag);
        if (tag is not null)
        {
            IReadOnlyList<KeyValuePair<string, bool>> list = flags ?? [];
            writer.Write(list.Count);
            foreach (KeyValuePair<string, bool> flag in list)
            {
                writer.Write(flag.Key);
                writer.Write(flag.Value);
            }
        }
    }

    /// <summary>Reads a language-specific subtype tag + flags written by <see cref="WriteLanguageSubtype"/>.</summary>
    private static (string? Tag, Dictionary<string, bool> Flags) ReadLanguageSubtype(BinaryReader reader, string context)
    {
        string? tag = ReadNullableString(reader);
        Dictionary<string, bool> flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (tag is not null)
        {
            int flagCount = ReadCount(reader, $"{context}.LanguageFlags");
            for (int i = 0; i < flagCount; i++)
            {
                string key = reader.ReadString();
                flags[key] = reader.ReadBoolean();
            }
        }
        return (tag, flags);
    }

    private static void WriteFieldDeclaration(BinaryWriter writer, FieldDeclaration field)
    {
        writer.Write(field.Name);
        WriteNullableTypeReference(writer, field.Type);
        writer.Write((int)field.Modifiers);
        writer.Write(field.Line);
    }

    private static FieldDeclaration ReadFieldDeclaration(BinaryReader reader) =>
        new(reader.ReadString(), ReadNullableTypeReference(reader), (Modifier)reader.ReadInt32(), reader.ReadInt32());

    private static void WritePropertyDeclaration(BinaryWriter writer, PropertyDeclaration property)
    {
        writer.Write(property.Name);
        WriteNullableTypeReference(writer, property.Type);
        writer.Write((int)property.Modifiers);
        writer.Write(property.Line);
        writer.Write(property.HasGetter);
        writer.Write(property.HasSetter);
        writer.Write(property.HasDocComment);
        WriteNullableString(writer, property.DocComment);
    }

    private static PropertyDeclaration ReadPropertyDeclaration(BinaryReader reader)
    {
        return new PropertyDeclaration(
            reader.ReadString(),
            ReadNullableTypeReference(reader),
            (Modifier)reader.ReadInt32(),
            reader.ReadInt32())
        {
            HasGetter = reader.ReadBoolean(),
            HasSetter = reader.ReadBoolean(),
            HasDocComment = reader.ReadBoolean(),
            DocComment = ReadNullableString(reader)
        };
    }

    private static void WriteEventDeclaration(BinaryWriter writer, EventDeclaration @event)
    {
        writer.Write(@event.Name);
        WriteNullableTypeReference(writer, @event.Type);
        writer.Write((int)@event.Modifiers);
        writer.Write(@event.Line);
    }

    private static EventDeclaration ReadEventDeclaration(BinaryReader reader) =>
        new(reader.ReadString(), ReadNullableTypeReference(reader), (Modifier)reader.ReadInt32(), reader.ReadInt32());

    private static void WriteParameterDeclaration(BinaryWriter writer, ParameterDeclaration parameter)
    {
        writer.Write(parameter.Name);
        WriteNullableTypeReference(writer, parameter.Type);
        writer.Write(parameter.IsVariadic);
        writer.Write(parameter.IsKwargs);
        writer.Write(parameter.HasDefaultValue);
        writer.Write(parameter.Line);
        WriteNullableString(writer, parameter.DefaultValueText);
        writer.Write(parameter.IsOptional);
        WriteSpan(writer, parameter.Column, parameter.EndLine, parameter.EndColumn);
    }

    private static ParameterDeclaration ReadParameterDeclaration(BinaryReader reader)
    {
        return new ParameterDeclaration(
            reader.ReadString(),
            ReadNullableTypeReference(reader),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadInt32())
        {
            DefaultValueText = ReadNullableString(reader),
            IsOptional = reader.ReadBoolean(),
            Column = ReadNullableInt(reader),
            EndLine = ReadNullableInt(reader),
            EndColumn = ReadNullableInt(reader)
        };
    }

    private static void WriteImport(BinaryWriter writer, ImportInfo import)
    {
        writer.Write(import.Module);
        writer.Write(import.Line);
        WriteNullableString(writer, import.Name);
        WriteNullableString(writer, import.Alias);
        writer.Write(import.IsWildcard);
        writer.Write(import.IsStatic);
        writer.Write(import.RelativeLevel);
        WriteSpan(writer, import.Column, import.EndLine, import.EndColumn);
    }

    private static ImportInfo ReadImport(BinaryReader reader) => new(reader.ReadString(), reader.ReadInt32())
    {
        Name = ReadNullableString(reader),
        Alias = ReadNullableString(reader),
        IsWildcard = reader.ReadBoolean(),
        IsStatic = reader.ReadBoolean(),
        RelativeLevel = reader.ReadInt32(),
        Column = ReadNullableInt(reader),
        EndLine = ReadNullableInt(reader),
        EndColumn = ReadNullableInt(reader)
    };

    private static void WriteSpan(BinaryWriter writer, int? column, int? endLine, int? endColumn)
    {
        WriteNullableInt(writer, column);
        WriteNullableInt(writer, endLine);
        WriteNullableInt(writer, endColumn);
    }

    private static void WriteNullableInt(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue)
        {
            writer.Write(value.Value);
        }
    }

    private static int? ReadNullableInt(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt32() : null;

    private static void WriteTypeReference(BinaryWriter writer, TypeReference typeReference)
    {
        writer.Write(typeReference.Name);
        WriteNullableString(writer, typeReference.Namespace);
        WriteList(writer, typeReference.GenericArguments, WriteTypeReference);
        writer.Write(typeReference.OriginalText);
        writer.Write(typeReference.IsAnonymous);
    }

    private static TypeReference ReadTypeReference(BinaryReader reader) =>
        new(
            reader.ReadString(),
            ReadNullableString(reader),
            ReadList(reader, ReadTypeReference, "TypeReference.GenericArguments"),
            reader.ReadString()) { IsAnonymous = reader.ReadBoolean() };

    private static void WriteRegionInfo(BinaryWriter writer, RegionInfo region)
    {
        writer.Write(region.Name);
        writer.Write(region.StartLine);
        writer.Write(region.EndLine);
        writer.Write(region.Content);
    }

    private static RegionInfo ReadRegionInfo(BinaryReader reader) =>
        new(reader.ReadString(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadString());

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static string? ReadNullableString(BinaryReader reader)
    {
        return reader.ReadBoolean() ? reader.ReadString() : null;
    }

    private static void WriteNullableTypeReference(BinaryWriter writer, TypeReference? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            WriteTypeReference(writer, value);
        }
    }

    private static TypeReference? ReadNullableTypeReference(BinaryReader reader)
    {
        return reader.ReadBoolean() ? ReadTypeReference(reader) : null;
    }

    private static void WriteHashSet(BinaryWriter writer, HashSet<int> values)
    {
        writer.Write(values.Count);
        foreach (int value in values.Order())
        {
            writer.Write(value);
        }
    }

    private static HashSet<int> ReadHashSet(BinaryReader reader, string name)
    {
        int count = ReadCount(reader, name);
        HashSet<int> result = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            result.Add(reader.ReadInt32());
        }

        return result;
    }

    private static void WriteList<T>(BinaryWriter writer, IReadOnlyList<T> items, Action<BinaryWriter, T> writeItem)
    {
        writer.Write(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            writeItem(writer, items[i]);
        }
    }

    private static List<T> ReadList<T>(BinaryReader reader, Func<BinaryReader, T> readItem, string name)
    {
        int count = ReadCount(reader, name);
        List<T> result = new List<T>(count);
        for (int i = 0; i < count; i++)
        {
            result.Add(readItem(reader));
        }

        return result;
    }

    private static byte[] ReadBytes(BinaryReader reader, string name)
    {
        int count = ReadCount(reader, name);
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new FormatException($"Unexpected end of stream while reading {name}.");
        }

        return bytes;
    }

    private static int ReadCount(BinaryReader reader, string name)
    {
        int count = reader.ReadInt32();
        if (count < 0)
        {
            throw new FormatException($"Invalid count for {name}: {count}.");
        }

        return count;
    }
}
