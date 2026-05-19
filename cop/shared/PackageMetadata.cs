using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Cop.Core;

/// <summary>
/// Represents package metadata loaded from cop.json (preferred) or legacy YAML front-matter.
/// </summary>
public class PackageMetadata
{
    /// <summary>
    /// Canonical metadata file name for all Cop packages.
    /// </summary>
    public const string MetadataFileName = "cop.json";

    /// <summary>
    /// Package name. Must match ^[a-z][a-z0-9-]*$ and be 1-64 characters.
    /// </summary>
    [YamlMember(Alias = "name")]
    [JsonPropertyName("name")]
    [Required]
    [RegularExpression(@"^[a-z][a-z0-9-]*$")]
    [StringLength(64, MinimumLength = 1)]
    public required string Name { get; set; }

    /// <summary>
    /// Package version as semver string.
    /// </summary>
    [YamlMember(Alias = "version")]
    [JsonPropertyName("version")]
    [Required]
    public required string Version { get; set; }

    /// <summary>
    /// Package title. Maximum 256 characters.
    /// </summary>
    [YamlMember(Alias = "title")]
    [JsonPropertyName("title")]
    [Required]
    [StringLength(256)]
    public required string Title { get; set; }

    /// <summary>
    /// Package description. Maximum 1000 characters.
    /// </summary>
    [YamlMember(Alias = "description")]
    [JsonPropertyName("description")]
    [Required]
    [StringLength(1000)]
    public required string Description { get; set; }

    /// <summary>
    /// Package authors.
    /// </summary>
    [YamlMember(Alias = "authors")]
    [JsonPropertyName("authors")]
    [Required]
    public required string Authors { get; set; }

    /// <summary>
    /// Tags for discoverability.
    /// In YAML: comma-separated string. In JSON: array of strings.
    /// </summary>
    [YamlMember(Alias = "tags")]
    [YamlDotNet.Serialization.YamlConverter(typeof(YamlTagsConverter))]
    [JsonPropertyName("tags")]
    [JsonConverter(typeof(TagsConverter))]
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Primary programming language. Defaults to empty string (general/cross-language).
    /// </summary>
    [YamlMember(Alias = "language")]
    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Provider type. When set to "clr", this package contains a CLR assembly
    /// that implements <see cref="ObjectProvider"/>. Defaults to empty (no provider).
    /// </summary>
    [YamlMember(Alias = "provider")]
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Fully-qualified class name of the <see cref="ObjectProvider"/> subclass.
    /// Required when <see cref="Provider"/> is "clr". The engine instantiates
    /// exactly this class from the provider assembly.
    /// </summary>
    [YamlMember(Alias = "providerEntry")]
    [JsonPropertyName("providerEntry")]
    public string ProviderEntry { get; set; } = string.Empty;

    /// <summary>
    /// Filename of the provider DLL (e.g., "csharp-provider.dll").
    /// Required when the package's lib/ directory contains multiple DLLs
    /// (provider + its dependencies). When omitted and only one DLL exists,
    /// that DLL is used automatically.
    /// </summary>
    [YamlMember(Alias = "providerAssembly")]
    [JsonPropertyName("providerAssembly")]
    public string ProviderAssembly { get; set; } = string.Empty;

    /// <summary>
    /// Returns true if this package contains a CLR provider assembly.
    /// </summary>
    [JsonIgnore]
    [YamlIgnore]
    public bool IsClrProvider => string.Equals(Provider, "clr", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// List of dependencies. Each entry is a fully-qualified package path with optional version,
    /// e.g., "github.com/org/repo/test: 1.0.0" or "github.com/org/repo/test".
    /// Defaults to empty list.
    /// </summary>
    [YamlMember(Alias = "dependencies")]
    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; set; } = [];

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = true
    };

    /// <summary>
    /// Returns true if the directory contains a cop.json metadata file.
    /// </summary>
    public static bool IsPackageDir(string dirPath)
    {
        return File.Exists(Path.Combine(dirPath, MetadataFileName));
    }

    /// <summary>
    /// Attempts to load PackageMetadata from a directory.
    /// Checks for cop.json first, then falls back to legacy {dirName}.md with YAML front-matter.
    /// </summary>
    public static PackageMetadata? TryLoadFromDirectory(string dirPath)
    {
        var jsonPath = Path.Combine(dirPath, MetadataFileName);
        if (File.Exists(jsonPath))
        {
            try
            {
                var json = File.ReadAllText(jsonPath);
                return ParseFromJson(json);
            }
            catch { return null; }
        }

        // Legacy fallback: {dirName}.md with YAML front-matter
        var dirName = Path.GetFileName(dirPath);
        if (string.IsNullOrEmpty(dirName)) return null;
        var mdPath = Path.Combine(dirPath, $"{dirName}.md");
        if (File.Exists(mdPath))
        {
            try
            {
                var content = File.ReadAllText(mdPath);
                if (content.StartsWith("---"))
                    return ParseFromMarkdown(content);
            }
            catch { return null; }
        }

        return null;
    }

    /// <summary>
    /// Parses PackageMetadata from a JSON string (cop.json content).
    /// </summary>
    public static PackageMetadata ParseFromJson(string json)
    {
        if (string.IsNullOrEmpty(json))
            throw new ArgumentException("JSON content cannot be null or empty.", nameof(json));

        return JsonSerializer.Deserialize<PackageMetadata>(json, s_jsonOptions)
            ?? throw new ArgumentException("Failed to deserialize cop.json.", nameof(json));
    }

    /// <summary>
    /// Parses PackageMetadata from a cop.json file.
    /// </summary>
    public static PackageMetadata ParseFromJsonFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}", filePath);

        string json = File.ReadAllText(filePath);
        return ParseFromJson(json);
    }

    /// <summary>
    /// Serializes this metadata to a JSON string suitable for writing to cop.json.
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, s_jsonOptions);
    }

    /// <summary>
    /// Parses PackageMetadata from markdown content with YAML front-matter.
    /// Front-matter is delimited by --- at the start of the file.
    /// </summary>
    public static PackageMetadata ParseFromMarkdown(string content)
    {
        if (string.IsNullOrEmpty(content))
            throw new ArgumentException("Content cannot be null or empty.", nameof(content));

        const string delimiter = "---";

        int firstDelimiterIndex = content.IndexOf(delimiter);
        if (firstDelimiterIndex != 0)
            throw new ArgumentException("Content must start with --- delimiter.", nameof(content));

        int secondDelimiterIndex = content.IndexOf(delimiter, firstDelimiterIndex + delimiter.Length);
        if (secondDelimiterIndex == -1)
            throw new ArgumentException("Closing --- delimiter not found.", nameof(content));

        string yamlContent = content.Substring(
            firstDelimiterIndex + delimiter.Length,
            secondDelimiterIndex - (firstDelimiterIndex + delimiter.Length)
        ).Trim();

        var deserializer = new DeserializerBuilder().Build();
        var metadata = deserializer.Deserialize<PackageMetadata>(yamlContent)
            ?? throw new ArgumentException("Failed to deserialize YAML front-matter.", nameof(content));

        return metadata;
    }

    /// <summary>
    /// Parses PackageMetadata from a markdown file.
    /// </summary>
    public static PackageMetadata ParseFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}", filePath);

        string content = File.ReadAllText(filePath);
        return ParseFromMarkdown(content);
    }

    /// <summary>
    /// Handles Tags as either a JSON string array or a comma-separated string.
    /// </summary>
    private class TagsConverter : JsonConverter<List<string>>
    {
        public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                        list.Add(reader.GetString()!);
                }
                return list;
            }
            if (reader.TokenType == JsonTokenType.String)
            {
                var str = reader.GetString() ?? "";
                return str.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            return [];
        }

        public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var tag in value)
                writer.WriteStringValue(tag);
            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// YamlDotNet converter that handles Tags as a comma-separated scalar or a YAML sequence.
    /// </summary>
    private class YamlTagsConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(List<string>);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                if (string.IsNullOrWhiteSpace(scalar.Value))
                    return new List<string>();
                return scalar.Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }

            if (parser.TryConsume<SequenceStart>(out _))
            {
                var list = new List<string>();
                while (!parser.TryConsume<SequenceEnd>(out _))
                {
                    if (parser.TryConsume<Scalar>(out var item))
                        list.Add(item.Value);
                }
                return list;
            }

            return new List<string>();
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var list = value as List<string> ?? [];
            emitter.Emit(new Scalar(string.Join(", ", list)));
        }
    }
}
