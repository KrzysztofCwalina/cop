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

        var modules = VerifyCommand.LoadProgramModules(files, dir, f =>
            PathsEqual(f, full) ? bufferText : SafeRead(f));
        var model = SemanticModel.Build(modules);

        ModuleNode? fileModule = null;
        try { fileModule = CopParser.Parse(bufferText, full); }
        catch { /* an unparseable buffer still allows type/keyword hovers */ }

        return CopHover.Hover(model, fileModule, bufferText, line, character);
    }

    private static string SafeRead(string file)
    {
        try { return File.ReadAllText(file); }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }
}
