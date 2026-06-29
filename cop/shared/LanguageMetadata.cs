namespace Cop.Lang;

/// <summary>
/// A named language element surfaced to editor tooling, with a short human-readable detail.
/// </summary>
public readonly record struct MetadataEntry(string Name, string Detail);

/// <summary>
/// A reserved word with its presentation category (for syntax highlighting and completion).
/// </summary>
public readonly record struct KeywordEntry(string Name, string Detail, string Category);

/// <summary>
/// Editor/REPL presentation of cop's built-in language elements, consumed by the metadata generator
/// (tools/copmeta) which emits <c>install/vscode-cop/metadata.json</c>.
///
/// The primitive predicate/property/transform catalogs are projected directly from
/// <see cref="IntrinsicRegistry"/> — the single source of truth — so editor metadata can never drift
/// from the runtime's set of built-ins. This type only adds the editor shape and keyword
/// presentation metadata (the keyword <em>names</em> are owned by <see cref="Tokenizer.Keywords"/>).
///
/// Domain concepts (Violation, toError, CHECK, the code model, providers, …) are NOT here — those
/// live in <c>.cop</c> packages and are discovered by loading those packages.
/// </summary>
public static class LanguageMetadata
{
    private static MetadataEntry[] Project(IntrinsicKind kind) =>
        IntrinsicRegistry.OfKind(kind).Select(o => new MetadataEntry(o.Name, o.Detail)).ToArray();

    /// <summary>Predicates available on <c>string</c> values (e.g. <c>Name:startsWith('I')</c>).</summary>
    public static readonly MetadataEntry[] StringPredicates = Project(IntrinsicKind.StringPredicate);

    /// <summary>Predicates available on numeric (<c>int</c>/<c>float</c>) and flags values.</summary>
    public static readonly MetadataEntry[] NumericPredicates = Project(IntrinsicKind.NumericPredicate);

    /// <summary>Predicates available on collections (e.g. <c>BaseTypes:contains('Exception')</c>).</summary>
    public static readonly MetadataEntry[] CollectionPredicates = Project(IntrinsicKind.CollectionPredicate);

    /// <summary>Predicates available on any value, regardless of type.</summary>
    public static readonly MetadataEntry[] UniversalPredicates = Project(IntrinsicKind.UniversalPredicate);

    /// <summary>Predicates available on dynamic object values.</summary>
    public static readonly MetadataEntry[] ObjectPredicates = Project(IntrinsicKind.ObjectPredicate);

    /// <summary>Computed properties available on <c>string</c> values.</summary>
    public static readonly MetadataEntry[] StringProperties = Project(IntrinsicKind.StringProperty);

    /// <summary>Transform functions available on <c>string</c> values.</summary>
    public static readonly MetadataEntry[] StringTransforms = Project(IntrinsicKind.StringTransform);

    /// <summary>Computed properties available on collections.</summary>
    public static readonly MetadataEntry[] CollectionProperties = Project(IntrinsicKind.CollectionProperty);

    /// <summary>Transform functions available on collections.</summary>
    public static readonly MetadataEntry[] CollectionTransforms = Project(IntrinsicKind.CollectionTransform);

    /// <summary>
    /// Presentation metadata (detail text + highlighting category) for keywords. The set of
    /// keyword <em>names</em> is owned by <see cref="Tokenizer.Keywords"/>; this only supplies how
    /// each is described and colored. Categories: <c>declaration</c>, <c>control</c>, <c>constant</c>.
    /// A test asserts these keys exactly match the tokenizer's keyword set.
    /// </summary>
    public static readonly KeywordEntry[] Keywords =
    [
        new("type", "Define a type", "declaration"),
        new("collection", "Declare a collection", "declaration"),
        new("command", "Define a named command", "declaration"),
        new("predicate", "Define a boolean test", "declaration"),
        new("function", "Define a transform function", "declaration"),
        new("enum", "Define an enumeration", "declaration"),
        new("flags", "Define flag constants", "declaration"),
        new("import", "Import a package", "control"),
        new("export", "Export declarations", "control"),
        new("let", "Bind a named value", "control"),
        new("foreach", "Iterate over a collection", "control"),
        new("feed", "Specify a package feed directory", "control"),
        new("async", "Run foreach asynchronously", "control"),
        new("test", "Define a test assertion", "control"),
        new("intrinsic", "Runtime-implemented function body", "control"),
        new("RUN", "Invoke another command", "control"),
        new("true", "Boolean true", "constant"),
        new("false", "Boolean false", "constant"),
        new("nic", "Null value", "constant"),
    ];
}
