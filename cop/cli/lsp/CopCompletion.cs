using System.Text.RegularExpressions;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;

namespace Cop.Cli.Lsp;

/// <summary>A completion item: label, a short detail string, and an LSP CompletionItemKind.</summary>
internal readonly record struct CompletionEntry(string Label, string Detail, int Kind);

/// <summary>
/// Produces completions for a position in a cop document, entirely from the real compiler's
/// <see cref="SemanticModel"/> and <see cref="LanguageMetadata"/> (the single source of built-ins).
/// Replaces the JavaScript completion reimplementation: the same parser/binder/type model that
/// powers <c>cop verify</c> now drives IntelliSense, so they can never disagree.
/// </summary>
internal static class CopCompletion
{
    // LSP CompletionItemKind values.
    private const int Keyword = 14, Property = 10, Function = 3, Method = 2, Variable = 6,
                      Class = 7, Module = 9, Struct = 22;

    private static readonly Regex DotChain = new(@"([A-Za-z_][A-Za-z0-9_.:()'\-]*)\.\s*$", RegexOptions.Compiled);
    private static readonly Regex ColonChain = new(@"([A-Za-z_][A-Za-z0-9_.:()'\-]*)\s*:\s*$", RegexOptions.Compiled);

    private static readonly string[] DeclarationKeywordNames =
        ["predicate", "function", "type", "let", "command", "flags", "enum", "collection"];

    public static IReadOnlyList<CompletionEntry> Complete(
        SemanticModel model, ModuleNode? fileModule, string text, int line, int character,
        IReadOnlyCollection<string> importablePackages)
    {
        var lines = CopEditorContext.SplitLines(text);
        if (line < 0 || line >= lines.Length) return [];
        var lineText = lines[line];
        var before = lineText[..Math.Min(Math.Max(character, 0), lineText.Length)];
        var locals = CopEditorContext.BuildLocals(model, fileModule, lines, line);

        // Context detection (mirrors the editor's previous behavior), most specific first.
        if (Regex.IsMatch(before, @"runtime::\s*$")) return RuntimeTypes(model);
        if (Regex.IsMatch(before, @"^\s*(?:export\s+)?import\s+$")) return Packages(importablePackages);
        if (Regex.IsMatch(before, @"(?<!:):\s*$")) return Colon(model, before, locals);
        if (Regex.IsMatch(before, @"\.\s*$")) return Dot(model, before, locals);
        if (Regex.IsMatch(before, @"=>\s*$")) return Actions(model);
        if (Regex.IsMatch(before, @"^\s*export\s+$")) return DeclarationKeywords();
        return General(model);
    }

    // ── Contexts ───────────────────────────────────────────────────────────

    private static IReadOnlyList<CompletionEntry> Dot(
        SemanticModel model, string before, Dictionary<string, TypeInfo> locals)
    {
        var m = DotChain.Match(before);
        if (!m.Success) return [];
        var chain = m.Groups[1].Value;

        // Namespace-qualified: `csharp.` → that package's exported functions.
        if (!chain.Contains('.') && !chain.Contains(':') && model.IsNamespace(chain))
            return Dedupe(model.NamespaceFunctions(chain).Select(c => new CompletionEntry(c.Name, FnDetail(c), Function)));

        var t = model.InferExpressionType(chain, locals);
        if (t is null) return [];

        var entries = new List<CompletionEntry>();
        if (t.Value.IsCollection)
        {
            entries.AddRange(Builtins(LanguageMetadata.CollectionProperties, Property));
            entries.AddRange(Builtins(LanguageMetadata.CollectionTransforms, Method));
        }
        else if (SemanticModel.IsStringType(t.Value.Name))
        {
            entries.AddRange(Builtins(LanguageMetadata.StringProperties, Property));
            entries.AddRange(Builtins(LanguageMetadata.StringTransforms, Method));
        }
        else if (model.IsKnownType(t.Value.Name))
        {
            entries.AddRange(model.PropertiesOf(t.Value.Name).Select(p => new CompletionEntry(p.Name, p.Type, Property)));
        }
        return Dedupe(entries);
    }

    private static IReadOnlyList<CompletionEntry> Colon(
        SemanticModel model, string before, Dictionary<string, TypeInfo> locals)
    {
        var entries = new List<CompletionEntry>();
        entries.AddRange(Builtins(LanguageMetadata.UniversalPredicates, Method));
        entries.AddRange(Builtins(LanguageMetadata.ObjectPredicates, Method));

        var m = ColonChain.Match(before);
        var t = m.Success ? model.InferExpressionType(m.Groups[1].Value, locals) : null;
        string? element = t?.Name;

        if (t is { IsCollection: true })
            entries.AddRange(Builtins(LanguageMetadata.CollectionPredicates, Method));

        if (element is null || SemanticModel.IsStringType(element))
            entries.AddRange(Builtins(LanguageMetadata.StringPredicates, Method));
        if (element is null || SemanticModel.IsNumericType(element))
            entries.AddRange(Builtins(LanguageMetadata.NumericPredicates, Method));

        // Declared predicates/functions: predicates filtered by applicability to the element type.
        foreach (var c in model.Callables())
        {
            if (c.IsPredicate)
            {
                if (model.PredicateApplies(c.ParamTypes.Count > 0 ? c.ParamTypes[0] : null, element))
                    entries.Add(new CompletionEntry(c.Name, PredDetail(c), Method));
            }
            else
            {
                entries.Add(new CompletionEntry(c.Name, FnDetail(c), Function));
            }
        }
        return Dedupe(entries);
    }

    private static IReadOnlyList<CompletionEntry> General(SemanticModel model)
    {
        var entries = new List<CompletionEntry>();
        foreach (var kw in LanguageMetadata.Keywords)
            entries.Add(new CompletionEntry(kw.Name, kw.Detail, Keyword));
        entries.AddRange(Actions(model));
        foreach (var name in model.LetNames())
            entries.Add(new CompletionEntry(name, "let", Variable));
        foreach (var name in model.TypeNames())
            entries.Add(new CompletionEntry(name, "type", Struct));
        foreach (var c in model.Callables())
            entries.Add(c.IsPredicate
                ? new CompletionEntry(c.Name, PredDetail(c), Method)
                : new CompletionEntry(c.Name, FnDetail(c), Function));
        entries.Add(new CompletionEntry(Evaluator.ImplicitItemVariable, "the current element", Variable));
        entries.Add(new CompletionEntry("runtime", "runtime providers", Module));
        return Dedupe(entries);
    }

    private static IReadOnlyList<CompletionEntry> Actions(SemanticModel model) =>
        Dedupe(model.Callables()
            .Where(c => c.Name.Length > 0 && c.Name.Any(char.IsLetter)
                        && c.Name == c.Name.ToUpperInvariant())
            .Select(c => new CompletionEntry(c.Name, FnDetail(c), Function)));

    private static IReadOnlyList<CompletionEntry> RuntimeTypes(SemanticModel model) =>
        Dedupe(model.TypeNames().Select(t => new CompletionEntry(t, "runtime type", Class)));

    private static IReadOnlyList<CompletionEntry> Packages(IReadOnlyCollection<string> packages) =>
        Dedupe(packages.Select(p => new CompletionEntry(p, "package", Module)));

    private static IReadOnlyList<CompletionEntry> DeclarationKeywords() =>
        DeclarationKeywordNames.Select(k => new CompletionEntry(k, "declaration", Keyword)).ToList();

    // ── Helpers ────────────────────────────────────────────────────────────

    private static IEnumerable<CompletionEntry> Builtins(MetadataEntry[] entries, int kind) =>
        entries.Select(e => new CompletionEntry(e.Name, e.Detail, kind));

    private static string FnDetail(CallableInfo c)
    {
        var pars = string.Join(", ", c.ParamTypes.Select(p => p ?? "?"));
        return c.ReturnType is not null ? $"({pars}) => {c.ReturnType}" : $"({pars})";
    }

    private static string PredDetail(CallableInfo c)
    {
        var pars = string.Join(", ", c.ParamTypes.Select(p => p ?? "?"));
        var ret = c.IsNarrowing && c.ReturnType is not null ? c.ReturnType : "bool";
        return $"({pars}) => {ret}";
    }

    private static IReadOnlyList<CompletionEntry> Dedupe(IEnumerable<CompletionEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CompletionEntry>();
        foreach (var e in entries)
            if (seen.Add(e.Label))
                result.Add(e);
        return result;
    }
}
