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
        var full = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();

        string[] files;
        if (Directory.Exists(dir))
        {
            files = Directory.GetFiles(dir, "*.cop");
            if (!files.Any(f => PathsEqual(f, full)))
                files = [.. files, full];
        }
        else
        {
            files = [full];
        }
        Array.Sort(files, StringComparer.Ordinal);

        var all = VerifyCommand.CollectDiagnostics(files, dir, f =>
        {
            if (PathsEqual(f, full)) return bufferText;
            // A sibling file can be deleted/locked mid-edit; never let that crash analysis.
            try { return File.ReadAllText(f); }
            catch (IOException) { return string.Empty; }
            catch (UnauthorizedAccessException) { return string.Empty; }
        });

        // Only surface diagnostics for the open document. A diagnostic with no file (rare) is
        // attributed to the open document so it is not silently dropped.
        return [.. all.Where(d => d.FilePath is null || PathsEqual(d.FilePath, full))];
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Hover markdown for a position in <paramref name="filePath"/> using <paramref name="bufferText"/>
    /// as its content, resolved against the whole program (so package/provider types resolve). Runs the
    /// real compiler via <see cref="SemanticModel"/> — no editor-side type inference. Null when there is
    /// nothing confident to show.
    /// </summary>
    public static string? Hover(string filePath, string bufferText, int line, int character)
    {
        var full = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        var files = ProgramFiles(full, dir);
        var modelText = ModelText(bufferText, line);

        var modules = VerifyCommand.LoadProgramModules(files, dir, f =>
            PathsEqual(f, full) ? modelText : SafeRead(f));
        var model = SemanticModel.Build(modules);

        var fileModule = TryParse(modelText, full);
        return CopHover.Hover(model, fileModule, bufferText, line, character);
    }

    /// <summary>
    /// Completions for a position in <paramref name="filePath"/> using <paramref name="bufferText"/>
    /// as its content. Driven entirely by the real compiler (<see cref="SemanticModel"/>) and
    /// <see cref="LanguageMetadata"/>; the editor no longer infers types or scans declarations itself.
    /// </summary>
    public static IReadOnlyList<CompletionEntry> Complete(string filePath, string bufferText, int line, int character)
    {
        var full = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
        var files = ProgramFiles(full, dir);
        var modelText = ModelText(bufferText, line);

        var modules = VerifyCommand.LoadProgramModules(files, dir,
            f => PathsEqual(f, full) ? modelText : SafeRead(f),
            out var namespaces);
        var model = SemanticModel.Build(modules, namespaces);

        var fileModule = TryParse(modelText, full);
        var packages = VerifyCommand.ListAvailablePackages(dir);
        return CopCompletion.Complete(model, fileModule, bufferText, line, character, packages);
    }

    private static string[] ProgramFiles(string fullPath, string dir)
    {
        if (!Directory.Exists(dir)) return [fullPath];
        var files = Directory.GetFiles(dir, "*.cop");
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
        if (Parses(buffer)) return buffer;
        var lines = buffer.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (line >= 0 && line < lines.Length)
        {
            var saved = lines[line];

            // Repair 1: keep a declaration's signature (so its parameters stay in scope for locals)
            // and give it a trivial body. Turns `predicate p(Dog) => Dog.` into `predicate p(Dog) => true`.
            int arrow = saved.IndexOf("=>", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                lines[line] = saved[..(arrow + 2)] + " true";
                var repaired = string.Join("\n", lines);
                if (Parses(repaired)) return repaired;
            }

            // Repair 2: blank the in-progress line entirely.
            lines[line] = "";
            var blanked = string.Join("\n", lines);
            if (Parses(blanked)) return blanked;
            lines[line] = saved;
        }

        // Repair 3: drop everything from the cursor line onward.
        if (line > 0 && line <= lines.Length)
        {
            var head = string.Join("\n", lines.Take(line));
            if (Parses(head)) return head;
        }
        return buffer;
    }

    private static bool Parses(string text)
    {
        try { CopParser.Parse(text, "probe.cop"); return true; }
        catch { return false; }
    }

    private static ModuleNode? TryParse(string text, string path)
    {
        try { return CopParser.Parse(text, path); }
        catch { return null; }
    }

    private static string SafeRead(string file)
    {
        try { return File.ReadAllText(file); }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }
}
