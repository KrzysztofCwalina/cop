using System.ComponentModel.DataAnnotations;
using YamlDotNet.Serialization;

namespace Cop.Core;

/// <summary>
/// Represents metadata from a package's markdown file with YAML front-matter.
/// </summary>
public class PackageMetadata
{
    /// <summary>
    /// Package name. Must match ^[a-z][a-z0-9-]*$ and be 1-64 characters.
    /// </summary>
    [YamlMember(Alias = "name")]
    [Required]
    [RegularExpression(@"^[a-z][a-z0-9-]*$")]
    [StringLength(64, MinimumLength = 1)]
    public required string Name { get; set; }

    /// <summary>
    /// Package version as semver string.
    /// </summary>
    [YamlMember(Alias = "version")]
    [Required]
    public required string Version { get; set; }

    /// <summary>
    /// Package title. Maximum 256 characters.
    /// </summary>
    [YamlMember(Alias = "title")]
    [Required]
    [StringLength(256)]
    public required string Title { get; set; }

    /// <summary>
    /// Package description. Maximum 1000 characters.
    /// </summary>
    [YamlMember(Alias = "description")]
    [Required]
    [StringLength(1000)]
    public required string Description { get; set; }

    /// <summary>
    /// Package authors.
    /// </summary>
    [YamlMember(Alias = "authors")]
    [Required]
    public required string Authors { get; set; }

    /// <summary>
    /// Comma-separated tags. Defaults to empty string.
    /// </summary>
    [YamlMember(Alias = "tags")]
    public string Tags { get; set; } = string.Empty;

    /// <summary>
    /// Primary programming language. Defaults to empty string (general/cross-language).
    /// </summary>
    [YamlMember(Alias = "language")]
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// List of dependencies. Each entry is a fully-qualified package path with optional version,
    /// e.g., "github.com/org/repo/test: 1.0.0" or "github.com/org/repo/test".
    /// Defaults to empty list.
    /// </summary>
    [YamlMember(Alias = "dependencies")]
    public List<string> Dependencies { get; set; } = [];

    /// <summary>
    /// Parses PackageMetadata from markdown content with YAML front-matter.
    /// Front-matter is delimited by --- at the start of the file.
    /// </summary>
    /// <param name="content">The markdown file content.</param>
    /// <returns>Parsed PackageMetadata instance.</returns>
    /// <exception cref="ArgumentException">Thrown if front-matter is malformed.</exception>
    public static PackageMetadata ParseFromMarkdown(string content)
    {
        if (string.IsNullOrEmpty(content))
            throw new ArgumentException("Content cannot be null or empty.", nameof(content));

        const string delimiter = "---";
        
        // Find the first --- delimiter
        int firstDelimiterIndex = content.IndexOf(delimiter);
        if (firstDelimiterIndex != 0)
            throw new ArgumentException("Content must start with --- delimiter.", nameof(content));

        // Find the second --- delimiter (end of front-matter)
        int secondDelimiterIndex = content.IndexOf(delimiter, firstDelimiterIndex + delimiter.Length);
        if (secondDelimiterIndex == -1)
            throw new ArgumentException("Closing --- delimiter not found.", nameof(content));

        // Extract YAML front-matter
        string yamlContent = content.Substring(
            firstDelimiterIndex + delimiter.Length,
            secondDelimiterIndex - (firstDelimiterIndex + delimiter.Length)
        ).Trim();

        // Deserialize YAML using YamlDotNet
        var deserializer = new DeserializerBuilder().Build();
        var metadata = deserializer.Deserialize<PackageMetadata>(yamlContent)
            ?? throw new ArgumentException("Failed to deserialize YAML front-matter.", nameof(content));

        return metadata;
    }

    /// <summary>
    /// Parses PackageMetadata from a markdown file.
    /// </summary>
    /// <param name="filePath">Path to the markdown file.</param>
    /// <returns>Parsed PackageMetadata instance.</returns>
    /// <exception cref="FileNotFoundException">Thrown if file does not exist.</exception>
    public static PackageMetadata ParseFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}", filePath);

        string content = File.ReadAllText(filePath);
        return ParseFromMarkdown(content);
    }
}
