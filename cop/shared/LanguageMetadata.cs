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
/// The authoritative catalog of cop language elements that are NOT expressible as ordinary
/// <c>.cop</c> declarations: the primitive predicates/properties/transforms that operate on
/// built-in primitive kinds (string, int, bool, collections), the universal predicates, and
/// keyword presentation metadata.
///
/// This is the single source of truth consumed by the metadata generator (tools/copmeta),
/// which emits <c>install/vscode-cop/metadata.json</c>. The VS Code extension and TextMate
/// grammar are driven entirely from that file, so editor IntelliSense and colorization can
/// never drift from the language. Add a new built-in primitive operation here and it flows to
/// the editor automatically on the next publish.
///
/// Domain concepts (Violation, toError, CHECK, the code model, language providers, …) are NOT
/// here — those live in <c>.cop</c> packages and are discovered by loading those packages.
/// </summary>
public static class LanguageMetadata
{
    /// <summary>Predicates available on <c>string</c> values (e.g. <c>Name:startsWith('I')</c>).</summary>
    public static readonly MetadataEntry[] StringPredicates =
    [
        new("equals", "(value) - case-insensitive equality"),
        new("notEquals", "(value) - case-insensitive inequality"),
        new("startsWith", "(value) - prefix match"),
        new("endsWith", "(value) - suffix match"),
        new("contains", "(value) - substring match"),
        new("containsAny", "(list) - any list item is a substring"),
        new("matches", "(pattern) - regex match"),
        new("sameAs", "(value) - convention-insensitive comparison"),
        new("empty", "- string is empty"),
    ];

    /// <summary>Predicates available on numeric (<c>int</c>/<c>float</c>) and flags values.</summary>
    public static readonly MetadataEntry[] NumericPredicates =
    [
        new("equals", "(value) - equal to"),
        new("notEquals", "(value) - not equal to"),
        new("greaterThan", "(value) - greater than"),
        new("lessThan", "(value) - less than"),
        new("greaterOrEqual", "(value) - greater or equal"),
        new("lessOrEqual", "(value) - less or equal"),
        new("isSet", "(flag) - flags bit is set"),
        new("isClear", "(flag) - flags bit is clear"),
    ];

    /// <summary>Predicates available on collections (e.g. <c>BaseTypes:contains('Exception')</c>).</summary>
    public static readonly MetadataEntry[] CollectionPredicates =
    [
        new("any", "((object) => bool) - true if any item matches"),
        new("none", "((object) => bool) - true if no items match"),
        new("all", "((object) => bool) - true if all items match"),
        new("count", "((object) => bool) - count items matching predicate"),
        new("contains", "(value) - list contains value"),
        new("containsAny", "(values) - list contains any value from list"),
        new("empty", "- collection is empty"),
    ];

    /// <summary>Predicates available on any value, regardless of type.</summary>
    public static readonly MetadataEntry[] UniversalPredicates =
    [
        new("in", "(list) - value is member of list"),
        new("isError", "- value is an error"),
    ];

    /// <summary>Predicates available on dynamic object values.</summary>
    public static readonly MetadataEntry[] ObjectPredicates =
    [
        new("containsKey", "(name) - object has field with given name"),
    ];

    /// <summary>Computed properties available on <c>string</c> values.</summary>
    public static readonly MetadataEntry[] StringProperties =
    [
        new("Length", ": int - string length"),
        new("Lower", ": string - lowercase"),
        new("Upper", ": string - uppercase"),
        new("Normalized", ": string - convention-insensitive form"),
        new("Words", ": [string] - split into words"),
    ];

    /// <summary>Transform functions available on <c>string</c> values.</summary>
    public static readonly MetadataEntry[] StringTransforms =
    [
        new("Trim", "(suffix) - remove suffix"),
        new("Replace", "(old, new) - replace substring"),
    ];

    /// <summary>Computed properties available on collections.</summary>
    public static readonly MetadataEntry[] CollectionProperties =
    [
        new("Count", ": int - number of items"),
        new("First", "- first item"),
        new("Last", "- last item"),
        new("Single", "- single item (nic if not exactly one)"),
        new("Tail", "- all elements except the first"),
    ];

    /// <summary>Transform functions available on collections.</summary>
    public static readonly MetadataEntry[] CollectionTransforms =
    [
        new("Where", "((object) => bool) - filter items"),
        new("First", "((object) => bool?) - first matching item"),
        new("Last", "((object) => bool?) - last matching item"),
        new("Single", "((object) => bool?) - single matching item"),
        new("ElementAt", "(index: int) - item at position"),
        new("Select", "((object) => object) - project each item"),
        new("OrderBy", "((object) => object) - sort ascending"),
        new("OrderByDescending", "((object) => object) - sort descending"),
        new("Distinct", "- remove duplicates"),
        new("GroupBy", "((object) => object) - group by key -> Key, Items"),
        new("Sum", "((object) => float) - sum numeric field"),
        new("Min", "((object) => float) - minimum value"),
        new("Max", "((object) => float) - maximum value"),
        new("Average", "((object) => float) - average value"),
        new("Reduce", "((object, object) => object, initial) - reduce collection"),
    ];

    /// <summary>
    /// Presentation metadata (detail text + highlighting category) for keywords. The set of
    /// keyword <em>names</em> is owned by <see cref="Tokenizer.Keywords"/>; this only supplies
    /// how each is described and colored. Categories: <c>declaration</c>, <c>control</c>,
    /// <c>constant</c>. A test asserts these keys exactly match the tokenizer's keyword set.
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
