namespace CopDocs;

using System.Text.Json.Serialization;

/// <summary>
/// The full reference data model matching the HTML app's JavaScript schema.
/// </summary>
public class ReferenceData
{
    public List<Category> Categories { get; set; } = [];
    public Dictionary<string, PackageEntry> Packages { get; set; } = [];
}

public class Category
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("packages")]
    public List<string> PackageIds { get; set; } = [];
}

public class PackageEntry
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("overview")]
    public string Overview { get; set; } = "";

    [JsonPropertyName("samples")]
    public List<SampleEntry> Samples { get; set; } = [];

    [JsonPropertyName("collections")]
    public List<string> Collections { get; set; } = [];

    [JsonPropertyName("enums")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<EnumEntry>? Enums { get; set; }

    [JsonPropertyName("types")]
    public Dictionary<string, TypeEntry> Types { get; set; } = [];

    [JsonPropertyName("predicates")]
    public List<PredicateEntry> Predicates { get; set; } = [];

    [JsonPropertyName("functions")]
    public List<FunctionEntry> Functions { get; set; } = [];

    [JsonPropertyName("checks")]
    public List<CheckEntry> Checks { get; set; } = [];

    [JsonPropertyName("commands")]
    public List<CommandEntry> Commands { get; set; } = [];
}

public class SampleEntry
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";
}

public class EnumEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("values")]
    public string Values { get; set; } = "";

    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }
}

public class TypeEntry
{
    [JsonPropertyName("desc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Desc { get; set; }

    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }

    [JsonPropertyName("isTrait")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsTrait { get; set; }

    [JsonPropertyName("baseType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseType { get; set; }

    [JsonPropertyName("conformers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Conformers { get; set; }

    [JsonPropertyName("traits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Traits { get; set; }

    [JsonPropertyName("props")]
    public List<PropEntry> Props { get; set; } = [];
}

public class PropEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";
}

public class PredicateEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("appliesTo")]
    public string AppliesTo { get; set; } = "";

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }
}

public class FunctionEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("params")]
    public string Params { get; set; } = "";

    [JsonPropertyName("returns")]
    public string Returns { get; set; } = "";

    [JsonPropertyName("appliesTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppliesTo { get; set; }

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }
}

public class CheckEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }
}

public class CommandEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("params")]
    public string Params { get; set; } = "";

    [JsonPropertyName("desc")]
    public string Desc { get; set; } = "";

    [JsonPropertyName("sourceUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceUrl { get; set; }
}
