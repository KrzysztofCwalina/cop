using System.IO.Compression;
using System.Text;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

internal static class SourceCacheSerializer
{
    private static readonly byte[] Magic = [(byte)'C', (byte)'O', (byte)'P', (byte)'C'];
    private const int FormatVersion = 3;

    public static void Save(string cachePath, byte[] fingerprint, List<SourceFile> sourceFiles)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var fileStream = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(fileStream, CompressionLevel.SmallestSize);
        using var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: false);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(fingerprint.Length);
        writer.Write(fingerprint);
        WriteList(writer, sourceFiles, WriteSourceFile);
    }

    public static List<SourceFile>? TryLoad(string cachePath, byte[] fingerprint)
    {
        if (!File.Exists(cachePath))
            return null;

        try
        {
            using var fileStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: false);

            var magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !magic.SequenceEqual(Magic))
                return null;

            var version = reader.ReadInt32();
            if (version != FormatVersion)
                return null;

            var cachedFingerprint = ReadBytes(reader, nameof(fingerprint));
            if (!cachedFingerprint.SequenceEqual(fingerprint))
                return null;

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

    private static void WriteSourceFile(BinaryWriter writer, SourceFile sourceFile)
    {
        writer.Write(sourceFile.Path);
        writer.Write(sourceFile.Language);
        WriteList(writer, sourceFile.Types, WriteTypeDeclaration);
        WriteList(writer, sourceFile.Statements, WriteStatementInfo);
        writer.Write(sourceFile.RawText);
        WriteList(writer, sourceFile.Usings, static (w, value) => w.Write(value));
        WriteNullableString(writer, sourceFile.Namespace);
        WriteList(writer, sourceFile.Regions, WriteRegionInfo);
        WriteHashSet(writer, sourceFile.CommentLines);
    }

    private static SourceFile ReadSourceFile(BinaryReader reader)
    {
        var path = reader.ReadString();
        var language = reader.ReadString();
        var types = ReadList(reader, ReadTypeDeclaration, "SourceFile.Types");
        var statements = ReadList(reader, ReadStatementInfo, "SourceFile.Statements");
        var rawText = reader.ReadString();
        var usings = ReadList(reader, static r => r.ReadString(), "SourceFile.Usings");
        var @namespace = ReadNullableString(reader);
        var regions = ReadList(reader, ReadRegionInfo, "SourceFile.Regions");
        var commentLines = ReadHashSet(reader, "SourceFile.CommentLines");

        return new SourceFile(path, language, types, statements, rawText)
        {
            Usings = usings,
            Namespace = @namespace,
            Regions = regions,
            CommentLines = commentLines
        };
    }

    private static void WriteTypeDeclaration(BinaryWriter writer, TypeDeclaration type)
    {
        writer.Write(type.Name);
        writer.Write((int)type.Kind);
        writer.Write((int)type.Modifiers);
        WriteList(writer, type.BaseTypes, static (w, value) => w.Write(value));
        WriteList(writer, type.Decorators, static (w, value) => w.Write(value));
        WriteList(writer, type.Constructors, WriteMethodDeclaration);
        WriteList(writer, type.Methods, WriteMethodDeclaration);
        WriteList(writer, type.NestedTypes, WriteTypeDeclaration);
        WriteList(writer, type.EnumValues, static (w, value) => w.Write(value));
        writer.Write(type.Line);
        writer.Write(type.HasDocComment);
        WriteNullableString(writer, type.DocComment);
        WriteList(writer, type.Fields, WriteFieldDeclaration);
        WriteList(writer, type.Properties, WritePropertyDeclaration);
        WriteList(writer, type.Events, WriteEventDeclaration);

        // Language-specific subtype tag + flags (e.g. RustType). Null for base types.
        var languageTag = type.LanguageTag;
        WriteNullableString(writer, languageTag);
        if (languageTag is not null)
        {
            var flags = type.LanguageFlags ?? [];
            writer.Write(flags.Count);
            foreach (var flag in flags)
            {
                writer.Write(flag.Key);
                writer.Write(flag.Value);
            }
        }
    }

    private static TypeDeclaration ReadTypeDeclaration(BinaryReader reader)
    {
        var type = new TypeDeclaration(
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
            Events = ReadList(reader, ReadEventDeclaration, "TypeDeclaration.Events")
        };

        // Language-specific subtype tag + flags — reconstruct the subtype if registered.
        var languageTag = ReadNullableString(reader);
        if (languageTag is not null)
        {
            var flagCount = ReadCount(reader, "TypeDeclaration.LanguageFlags");
            var flags = new Dictionary<string, bool>(flagCount, StringComparer.Ordinal);
            for (int i = 0; i < flagCount; i++)
            {
                var key = reader.ReadString();
                flags[key] = reader.ReadBoolean();
            }
            return LanguageTypeRegistry.Reconstruct(languageTag, type, flags);
        }

        return type;
    }

    private static void WriteMethodDeclaration(BinaryWriter writer, MethodDeclaration method)
    {
        writer.Write(method.Name);
        writer.Write((int)method.Modifiers);
        WriteList(writer, method.Decorators, static (w, value) => w.Write(value));
        WriteNullableTypeReference(writer, method.ReturnType);
        WriteList(writer, method.Parameters, WriteParameterDeclaration);
        writer.Write(method.Line);
        WriteList(writer, method.Statements, WriteStatementInfo);
        writer.Write(method.HasDocComment);
        WriteNullableString(writer, method.DocComment);
        WriteLanguageSubtype(writer, method.LanguageTag, method.LanguageFlags);
    }

    private static MethodDeclaration ReadMethodDeclaration(BinaryReader reader)
    {
        var method = new MethodDeclaration(
            reader.ReadString(),
            (Modifier)reader.ReadInt32(),
            ReadList(reader, static r => r.ReadString(), "MethodDeclaration.Decorators"),
            ReadNullableTypeReference(reader),
            ReadList(reader, ReadParameterDeclaration, "MethodDeclaration.Parameters"),
            reader.ReadInt32())
        {
            Statements = ReadList(reader, ReadStatementInfo, "MethodDeclaration.Statements"),
            HasDocComment = reader.ReadBoolean(),
            DocComment = ReadNullableString(reader)
        };
        var (tag, flags) = ReadLanguageSubtype(reader, "MethodDeclaration");
        return tag is not null ? MethodTypeRegistry.Reconstruct(tag, method, flags) : method;
    }

    private static void WriteStatementInfo(BinaryWriter writer, StatementInfo statement)
    {
        writer.Write(statement.Kind);
        WriteList(writer, statement.Keywords, static (w, value) => w.Write(value));
        WriteNullableString(writer, statement.TypeName);
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
        WriteList(writer, statement._children, WriteStatementInfo);
        WriteLanguageSubtype(writer, statement.LanguageTag, statement.LanguageFlags);
    }

    private static StatementInfo ReadStatementInfo(BinaryReader reader)
    {
        var statement = new StatementInfo(
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
            _children = ReadList(reader, ReadStatementInfo, "StatementInfo.Children")
        };
        var (tag, flags) = ReadLanguageSubtype(reader, "StatementInfo");
        return tag is not null ? StatementTypeRegistry.Reconstruct(tag, statement, flags) : statement;
    }

    /// <summary>Writes a language-specific subtype tag + flags (shared by Type/Method/Statement).</summary>
    private static void WriteLanguageSubtype(BinaryWriter writer, string? tag, IReadOnlyList<KeyValuePair<string, bool>>? flags)
    {
        WriteNullableString(writer, tag);
        if (tag is not null)
        {
            var list = flags ?? [];
            writer.Write(list.Count);
            foreach (var flag in list)
            {
                writer.Write(flag.Key);
                writer.Write(flag.Value);
            }
        }
    }

    /// <summary>Reads a language-specific subtype tag + flags written by <see cref="WriteLanguageSubtype"/>.</summary>
    private static (string? Tag, Dictionary<string, bool> Flags) ReadLanguageSubtype(BinaryReader reader, string context)
    {
        var tag = ReadNullableString(reader);
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (tag is not null)
        {
            var flagCount = ReadCount(reader, $"{context}.LanguageFlags");
            for (int i = 0; i < flagCount; i++)
            {
                var key = reader.ReadString();
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
            DefaultValueText = ReadNullableString(reader)
        };
    }

    private static void WriteTypeReference(BinaryWriter writer, TypeReference typeReference)
    {
        writer.Write(typeReference.Name);
        WriteNullableString(writer, typeReference.Namespace);
        WriteList(writer, typeReference.GenericArguments, WriteTypeReference);
        writer.Write(typeReference.OriginalText);
    }

    private static TypeReference ReadTypeReference(BinaryReader reader) =>
        new(
            reader.ReadString(),
            ReadNullableString(reader),
            ReadList(reader, ReadTypeReference, "TypeReference.GenericArguments"),
            reader.ReadString());

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
            writer.Write(value);
    }

    private static string? ReadNullableString(BinaryReader reader)
    {
        return reader.ReadBoolean() ? reader.ReadString() : null;
    }

    private static void WriteNullableTypeReference(BinaryWriter writer, TypeReference? value)
    {
        writer.Write(value is not null);
        if (value is not null)
            WriteTypeReference(writer, value);
    }

    private static TypeReference? ReadNullableTypeReference(BinaryReader reader)
    {
        return reader.ReadBoolean() ? ReadTypeReference(reader) : null;
    }

    private static void WriteHashSet(BinaryWriter writer, HashSet<int> values)
    {
        writer.Write(values.Count);
        foreach (var value in values.Order())
            writer.Write(value);
    }

    private static HashSet<int> ReadHashSet(BinaryReader reader, string name)
    {
        var count = ReadCount(reader, name);
        var result = new HashSet<int>();
        for (int i = 0; i < count; i++)
            result.Add(reader.ReadInt32());
        return result;
    }

    private static void WriteList<T>(BinaryWriter writer, IReadOnlyList<T> items, Action<BinaryWriter, T> writeItem)
    {
        writer.Write(items.Count);
        for (int i = 0; i < items.Count; i++)
            writeItem(writer, items[i]);
    }

    private static List<T> ReadList<T>(BinaryReader reader, Func<BinaryReader, T> readItem, string name)
    {
        var count = ReadCount(reader, name);
        var result = new List<T>(count);
        for (int i = 0; i < count; i++)
            result.Add(readItem(reader));
        return result;
    }

    private static byte[] ReadBytes(BinaryReader reader, string name)
    {
        var count = ReadCount(reader, name);
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new FormatException($"Unexpected end of stream while reading {name}.");
        return bytes;
    }

    private static int ReadCount(BinaryReader reader, string name)
    {
        var count = reader.ReadInt32();
        if (count < 0)
            throw new FormatException($"Invalid count for {name}: {count}.");
        return count;
    }
}
