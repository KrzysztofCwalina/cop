// Coordinates document analysis and semantic information for Cop editor features.

using Cop.Cli.Commands;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

namespace Cop.Cli.Lsp;

/// <summary>
/// Bridges the editor (LSP) surface to the REAL compiler pipeline. Given an open document and
/// its current (possibly unsaved) buffer text, it runs the exact same
/// parse -> import-resolution -> bind -> type-check pipeline as <c>cop verify</c>
/// (<see cref="VerifyCommand.CollectDiagnostics"/>) over the document's whole directory,
/// substituting the in-memory buffer for the file on disk, and returns the diagnostics that
/// belong to the open document.
///
/// The point: the editor never reimplements the compiler. There is one analysis pipeline, used
/// by <c>cop verify</c>, the language server, and any other tool. This is what kept breaking when
/// the VS Code extension shipped its own JavaScript parser/binder/type-checker.
/// </summary>
internal static class CopLanguageService
{
    /// <summary>
    /// Analyzes <paramref name="filePath"/> using <paramref name="bufferText"/> as its current
    /// content, in the context of every other <c>.cop</c> file in the same directory (a cop
    /// "program" is the whole directory). Returns only the diagnostics that belong to the open
    /// document so the editor underlines the right file.
    /// </summary>
    public static List<CopDiagnostic> Analyze(string filePath, string bufferText)
    {
        string full = Path.GetFullPath(filePath);
        string dir = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();

        string[] files;
        if (Directory.Exists(dir))
        {
            files = Directory.GetFiles(dir, "*.cop");
            if (!files.Any(f => PathsEqual(f, full)))
            {
                files = [.. files, full];
            }
        }
        else
        {
            files = [full];
        }
        Array.Sort(files, StringComparer.Ordinal);

        List<CopDiagnostic> all = VerifyCommand.CollectDiagnostics(files, dir, f =>
        {
            if (PathsEqual(f, full))
            {
                return bufferText;
            }
            return File.ReadAllText(f);
        });

        // Only surface diagnostics for the open document. A diagnostic with no file (rare) is
        // attributed to the open document so it is not silently dropped.
        return [.. all.Where(d => d.FilePath is null || PathsEqual(d.FilePath, full))];
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the go-to-definition location for the identifier at the cursor: the top-level
    /// declaration (let / predicate / function / type / enum / flags / command) with that name,
    /// anywhere in the program (a cop directory is one program). Returns null when there is no such
    /// declaration (e.g. a built-in or a provider-supplied name). 0-based line/character for LSP.
    /// </summary>
    public static DefinitionLocation? Definition(string filePath, string bufferText, int line, int character)
    {
        string[] bufferLines = CopEditorContext.SplitLines(bufferText);
        if (line < 0 || line >= bufferLines.Length)
        {
            return null;
        }

        (string? word, int _) = CopEditorContext.WordAt(bufferLines[line], character);
        if (word is null)
        {
            return null;
        }

        string full = Path.GetFullPath(filePath);
        string dir = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        foreach (string f in ProgramFiles(full, dir))
        {
            string text = PathsEqual(f, full) ? ModelText(bufferText, line) : SafeRead(f);
            ModuleNode module = CopParser.ParseWithRecovery(text, f, out _);

            foreach (Declaration decl in module.Declarations)
            {
                if (DeclarationName(decl) != word)
                {
                    continue;
                }

                int declLine = decl.Line > 0 ? decl.Line - 1 : 0;
                int column = 0;
                string[] fileLines = CopEditorContext.SplitLines(text);
                if (declLine >= 0 && declLine < fileLines.Length)
                {
                    int idx = fileLines[declLine].IndexOf(word, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        column = idx;
                    }
                }
                return new DefinitionLocation(f, declLine, column, word.Length);
            }
        }
        return null;
    }

    private static string? DeclarationName(Declaration decl) => decl switch
    {
        TypeDecl t => t.Name,
        EnumDecl e => e.Name,
        FlagsDecl fl => fl.Name,
        FunctionDecl fn => fn.Name,
        LetDecl l => l.Name,
        CommandDecl c => c.Name,
        _ => null
    };

    /// <summary>
    /// Hover markdown for a position in <paramref name="filePath"/> using <paramref name="bufferText"/>
    /// as its content, resolved against the whole program (so package/provider types resolve). Runs the
    /// real compiler via <see cref="SemanticModel"/> — no editor-side type inference. Null when there is
    /// nothing confident to show.
    /// </summary>
    public static string? Hover(string filePath, string bufferText, int line, int character)
    {
        string full = Path.GetFullPath(filePath);
        string dir = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        string[] files = ProgramFiles(full, dir);
        string modelText = ModelText(bufferText, line);

        List<ModuleNode> modules = VerifyCommand.LoadProgramModules(files, dir, f =>
            PathsEqual(f, full) ? modelText : SafeRead(f));
        SemanticModel model = SemanticModel.Build(modules);

        ModuleNode? fileModule = TryParse(modelText, full);
        return CopHover.Hover(model, fileModule, bufferText, line, character);
    }

    /// <summary>
    /// Completions for a position in <paramref name="filePath"/> using <paramref name="bufferText"/>
    /// as its content. Driven entirely by the real compiler (<see cref="SemanticModel"/>) and
    /// <see cref="LanguageMetadata"/>; the editor no longer infers types or scans declarations itself.
    /// </summary>
    public static IReadOnlyList<CompletionEntry> Complete(string filePath, string bufferText, int line, int character)
    {
        string full = Path.GetFullPath(filePath);
        string dir = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        string[] files = ProgramFiles(full, dir);
        string modelText = ModelText(bufferText, line);

        List<ModuleNode> modules = VerifyCommand.LoadProgramModules(files, dir,
            f => PathsEqual(f, full) ? modelText : SafeRead(f),
            out IReadOnlyDictionary<string, List<ModuleNode>>? namespaces);
        SemanticModel model = SemanticModel.Build(modules, namespaces);

        ModuleNode? fileModule = TryParse(modelText, full);
        IReadOnlyCollection<string> packages = VerifyCommand.ListAvailablePackages(dir);
        return CopCompletion.Complete(
            model, fileModule, bufferText, (line, character), packages);
    }

    private static string[] ProgramFiles(string fullPath, string dir)
    {
        if (!Directory.Exists(dir))
        {
            return [fullPath];
        }

        string[] files = Directory.GetFiles(dir, "*.cop");
        return files.Any(f => PathsEqual(f, fullPath)) ? files : [.. files, fullPath];
    }

    /// <summary>
    /// Returns a version of <paramref name="buffer"/> that parses, for building the type model while the
    /// user is mid-edit. If the buffer parses, it is used as-is; otherwise the in-progress line is
    /// blanked (and, failing that, the tail from the cursor line is dropped) so the rest of the file's
    /// declarations still populate the model. Falls back to the original buffer.
    /// </summary>
    private static string ModelText(string buffer, int line)
    {
        if (Parses(buffer))
        {
            return buffer;
        }

        string[] lines = buffer.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (line >= 0 && line < lines.Length)
        {
            string saved = lines[line];

            // Repair 1: keep a declaration's signature (so its parameters stay in scope for locals)
            // and give it a trivial body. Turns `predicate p(Dog) => Dog.` into `predicate p(Dog) => true`.
            int arrow = saved.IndexOf("=>", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                lines[line] = saved[..(arrow + 2)] + " true";
                string repaired = string.Join("\n", lines);
                if (Parses(repaired))
                {
                    return repaired;
                }
            }

            // Repair 2: blank the in-progress line entirely.
            lines[line] = "";
            string blanked = string.Join("\n", lines);
            if (Parses(blanked))
            {
                return blanked;
            }

            lines[line] = saved;
        }

        // Repair 3: drop everything from the cursor line onward.
        if (line > 0 && line <= lines.Length)
        {
            string head = string.Join("\n", lines.Take(line));
            if (Parses(head))
            {
                return head;
            }
        }
        return buffer;
    }

    private static bool Parses(string text)
    {
        try { CopParser.Parse(text, "probe.cop"); return true; }
        catch (ParseException) { return false; }
    }

    private static ModuleNode? TryParse(string text, string path)
    {
        try { return CopParser.Parse(text, path); }
        catch (ParseException) { return null; }
    }

    private static string SafeRead(string file)
    {
        try { return File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[cop-editor] Could not read '{file}': {ex.Message}");
            return string.Empty;
        }
    }
}
