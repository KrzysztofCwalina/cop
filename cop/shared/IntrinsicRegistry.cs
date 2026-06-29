namespace Cop.Lang;

/// <summary>
/// The category a built-in operation surfaces as in tooling. A single operation name may appear
/// under more than one kind (e.g. <c>equals</c> is both a string and a numeric predicate, with a
/// different help string for each), so the registry stores one entry per (name, kind) surface.
/// </summary>
public enum IntrinsicKind
{
    StringPredicate,
    NumericPredicate,
    CollectionPredicate,
    UniversalPredicate,
    ObjectPredicate,
    StringTransform,
    CollectionTransform,
    StringProperty,
    CollectionProperty,

    /// <summary>
    /// A built-in utility function (print, read, text, concat, take, …) — not a primitive
    /// predicate/transform/property. These are surfaced to the REPL and excluded from being treated
    /// as property names by provider pushdown. They are NOT projected into editor metadata (the
    /// editor learns them by parsing the core .cop intrinsic declarations).
    /// </summary>
    Utility,
}

/// <summary>
/// One built-in operation surface: its canonical name, short-form aliases, category, editor/REPL
/// detail, and the cross-cutting flags consumers care about.
/// </summary>
public sealed record IntrinsicOp(
    string Name,
    IntrinsicKind Kind,
    string Detail,
    string[] Aliases,
    bool Pushable = false,
    bool IsBuiltinFilter = false,
    bool IsCollectionCombinator = false)
{
    /// <summary>The canonical name plus every alias.</summary>
    public IEnumerable<string> AllNames
    {
        get
        {
            yield return Name;
            foreach (var a in Aliases) yield return a;
        }
    }
}

/// <summary>
/// The single authoritative catalog of cop's built-in primitive operations (predicates on strings,
/// numbers, collections; collection/string transforms; computed properties). It is the one place a
/// new primitive is declared; every other concern — editor metadata (<see cref="LanguageMetadata"/>),
/// REPL completion, the type-checker's builtin-filter set, provider pushdown, and CodeQL transpile —
/// reads from here instead of keeping its own hand-maintained list, so they cannot drift apart.
///
/// Domain operations (toError, CHECK, codebase(), …) are NOT here; those are declared in .cop
/// packages. This catalog is only the language's universal primitives.
/// </summary>
public static class IntrinsicRegistry
{
    private static IntrinsicOp Pred(string name, IntrinsicKind kind, string detail, string[]? aliases = null, bool pushable = false)
        => new(name, kind, detail, aliases ?? [], Pushable: pushable, IsBuiltinFilter: true);

    private static IntrinsicOp Op(string name, IntrinsicKind kind, string detail, string[]? aliases = null)
        => new(name, kind, detail, aliases ?? []);

    private static IntrinsicOp Util(string name) => new(name, IntrinsicKind.Utility, "", []);

    /// <summary>A collection-level combinator verb (e.g. <c>coll:concat(other)</c>) — applied to the
    /// whole collection rather than per item.</summary>
    private static IntrinsicOp Combinator(string name) => new(name, IntrinsicKind.Utility, "", [], IsCollectionCombinator: true);

    /// <summary>Every built-in operation surface. Order within a kind matches editor presentation.</summary>
    public static readonly IReadOnlyList<IntrinsicOp> All =
    [
        // ── string predicates ────────────────────────────────────────────────
        Pred("equals", IntrinsicKind.StringPredicate, "(value) - case-insensitive equality", ["eq"], pushable: true),
        Pred("notEquals", IntrinsicKind.StringPredicate, "(value) - case-insensitive inequality", ["ne"]),
        Pred("startsWith", IntrinsicKind.StringPredicate, "(value) - prefix match", ["sw"], pushable: true),
        Pred("endsWith", IntrinsicKind.StringPredicate, "(value) - suffix match", ["ew"], pushable: true),
        Pred("contains", IntrinsicKind.StringPredicate, "(value) - substring match", ["ct"], pushable: true),
        Pred("containsAny", IntrinsicKind.StringPredicate, "(list) - any list item is a substring", ["ca"]),
        Pred("matches", IntrinsicKind.StringPredicate, "(pattern) - regex match", ["rx"], pushable: true),
        Pred("sameAs", IntrinsicKind.StringPredicate, "(value) - convention-insensitive comparison", ["sm"], pushable: true),
        Pred("empty", IntrinsicKind.StringPredicate, "- string is empty"),

        // ── numeric / flags predicates ───────────────────────────────────────
        Pred("equals", IntrinsicKind.NumericPredicate, "(value) - equal to", ["eq"]),
        Pred("notEquals", IntrinsicKind.NumericPredicate, "(value) - not equal to", ["ne"]),
        Pred("greaterThan", IntrinsicKind.NumericPredicate, "(value) - greater than", ["gt"], pushable: true),
        Pred("lessThan", IntrinsicKind.NumericPredicate, "(value) - less than", ["lt"], pushable: true),
        Pred("greaterOrEqual", IntrinsicKind.NumericPredicate, "(value) - greater or equal", ["ge"], pushable: true),
        Pred("lessOrEqual", IntrinsicKind.NumericPredicate, "(value) - less or equal", ["le"], pushable: true),
        Pred("isSet", IntrinsicKind.NumericPredicate, "(flag) - flags bit is set"),
        Pred("isClear", IntrinsicKind.NumericPredicate, "(flag) - flags bit is clear"),

        // ── collection predicates ────────────────────────────────────────────
        Pred("any", IntrinsicKind.CollectionPredicate, "((object) => bool) - true if any item matches"),
        Pred("none", IntrinsicKind.CollectionPredicate, "((object) => bool) - true if no items match"),
        Pred("all", IntrinsicKind.CollectionPredicate, "((object) => bool) - true if all items match"),
        Pred("count", IntrinsicKind.CollectionPredicate, "((object) => bool) - count items matching predicate"),
        Pred("contains", IntrinsicKind.CollectionPredicate, "(value) - list contains value", ["ct"]),
        Pred("containsAny", IntrinsicKind.CollectionPredicate, "(values) - list contains any value from list", ["ca"]),
        Pred("empty", IntrinsicKind.CollectionPredicate, "- collection is empty"),

        // ── universal predicates ─────────────────────────────────────────────
        new("in", IntrinsicKind.UniversalPredicate, "(list) - value is member of list", [], IsBuiltinFilter: true),
        Op("isError", IntrinsicKind.UniversalPredicate, "- value is an error"),

        // ── object predicates ────────────────────────────────────────────────
        Op("containsKey", IntrinsicKind.ObjectPredicate, "(name) - object has field with given name"),

        // ── string properties ────────────────────────────────────────────────
        Op("Length", IntrinsicKind.StringProperty, ": int - string length"),
        Op("Lower", IntrinsicKind.StringProperty, ": string - lowercase"),
        Op("Upper", IntrinsicKind.StringProperty, ": string - uppercase"),
        Op("Normalized", IntrinsicKind.StringProperty, ": string - convention-insensitive form"),
        Op("Words", IntrinsicKind.StringProperty, ": [string] - split into words"),

        // ── string transforms ────────────────────────────────────────────────
        Op("Trim", IntrinsicKind.StringTransform, "(suffix) - remove suffix"),
        Op("Replace", IntrinsicKind.StringTransform, "(old, new) - replace substring"),

        // ── collection properties ────────────────────────────────────────────
        Op("Count", IntrinsicKind.CollectionProperty, ": int - number of items"),
        Op("First", IntrinsicKind.CollectionProperty, "- first item"),
        Op("Last", IntrinsicKind.CollectionProperty, "- last item"),
        Op("Single", IntrinsicKind.CollectionProperty, "- single item (nic if not exactly one)"),
        Op("Tail", IntrinsicKind.CollectionProperty, "- all elements except the first"),

        // ── collection transforms ────────────────────────────────────────────
        Op("Where", IntrinsicKind.CollectionTransform, "((object) => bool) - filter items"),
        Op("First", IntrinsicKind.CollectionTransform, "((object) => bool?) - first matching item"),
        Op("Last", IntrinsicKind.CollectionTransform, "((object) => bool?) - last matching item"),
        Op("Single", IntrinsicKind.CollectionTransform, "((object) => bool?) - single matching item"),
        Op("ElementAt", IntrinsicKind.CollectionTransform, "(index: int) - item at position"),
        Op("Select", IntrinsicKind.CollectionTransform, "((object) => object) - project each item"),
        Op("OrderBy", IntrinsicKind.CollectionTransform, "((object) => object) - sort ascending"),
        Op("OrderByDescending", IntrinsicKind.CollectionTransform, "((object) => object) - sort descending"),
        Op("Distinct", IntrinsicKind.CollectionTransform, "- remove duplicates"),
        Op("GroupBy", IntrinsicKind.CollectionTransform, "((object) => object) - group by key -> Key, Items"),
        Op("Sum", IntrinsicKind.CollectionTransform, "((object) => float) - sum numeric field"),
        Op("Min", IntrinsicKind.CollectionTransform, "((object) => float) - minimum value"),
        Op("Max", IntrinsicKind.CollectionTransform, "((object) => float) - maximum value"),
        Op("Average", IntrinsicKind.CollectionTransform, "((object) => float) - average value"),
        Op("Reduce", IntrinsicKind.CollectionTransform, "((object, object) => object, initial) - reduce collection"),

        // ── collection-level utility verbs (NOT projected into editor metadata; surfaced via .cop) ──
        // Only verbs that are never property names belong here — accessor-style builtins like
        // File/Path/Get/Matches/Text collide with real property names (Type.File, Line.Text, …) and
        // must NOT be treated as collection functions by pushdown.
        Util("take"), Util("skip"), Combinator("concat"), Combinator("push"), Util("pop"), Combinator("enqueue"),
        Util("text"), Util("print"), Util("debug"), Util("save"), Util("read"),
        Util("provider"), Util("source"), Util("sink"), Util("assert"), Util("fail"), Util("error"),
    ];

    /// <summary>All operation surfaces of the given kind, in declaration order.</summary>
    public static IEnumerable<IntrinsicOp> OfKind(IntrinsicKind kind) => All.Where(o => o.Kind == kind);

    /// <summary>The distinct set of names (canonical + aliases) across ops matching <paramref name="filter"/>.</summary>
    public static HashSet<string> NameSet(Func<IntrinsicOp, bool> filter) =>
        All.Where(filter).SelectMany(o => o.AllNames).ToHashSet(StringComparer.Ordinal);

    /// <summary>Collection-level combinator verbs (concat/push/enqueue) — applied to the whole
    /// collection, not per item.</summary>
    public static HashSet<string> CollectionCombinatorNames() => NameSet(o => o.IsCollectionCombinator);

    /// <summary>
    /// Canonical-name -> short alias pairs for ops that have an alias. Used to register the short
    /// forms in the runtime so e.g. <c>x:eq('y')</c> works the same per-item as in provider pushdown.
    /// </summary>
    public static IEnumerable<(string Canonical, string Alias)> AliasPairs =>
        All.SelectMany(o => o.Aliases.Select(a => (o.Name, a)))
           .Distinct();

    private static readonly IntrinsicKind[] PredicateKinds =
    [
        IntrinsicKind.StringPredicate, IntrinsicKind.NumericPredicate, IntrinsicKind.CollectionPredicate,
        IntrinsicKind.UniversalPredicate, IntrinsicKind.ObjectPredicate,
    ];

    /// <summary>Distinct canonical names of every built-in predicate (no aliases). For completion.</summary>
    public static IReadOnlyList<string> PredicateNames() =>
        All.Where(o => PredicateKinds.Contains(o.Kind)).Select(o => o.Name).Distinct().ToList();

    /// <summary>Distinct canonical names of string/collection transforms plus utility functions. For completion.</summary>
    public static IReadOnlyList<string> TransformNames() =>
        All.Where(o => o.Kind is IntrinsicKind.StringTransform or IntrinsicKind.CollectionTransform or IntrinsicKind.Utility)
           .Select(o => o.Name).Distinct().ToList();

    /// <summary>Distinct canonical names of string/collection computed properties. For completion.</summary>
    public static IReadOnlyList<string> PropertyNames() =>
        All.Where(o => o.Kind is IntrinsicKind.StringProperty or IntrinsicKind.CollectionProperty)
           .Select(o => o.Name).Distinct().ToList();

    /// <summary>
    /// Collection-level function names (incl. aliases, case-insensitive) that provider pushdown must
    /// not treat as property names: collection transforms, utility functions, and the collection
    /// predicates other than the string/collection-dual <c>contains</c>/<c>containsAny</c>.
    /// </summary>
    public static HashSet<string> CollectionFunctionNames() =>
        All.Where(o =>
                o.Kind is IntrinsicKind.CollectionTransform or IntrinsicKind.Utility
                || (o.Kind is IntrinsicKind.CollectionPredicate && o.Name is not ("contains" or "containsAny")))
           .SelectMany(o => o.AllNames)
           .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
