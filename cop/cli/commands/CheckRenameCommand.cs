// Implements `cop checks rename`.

using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

namespace Cop.Cli.Commands;

/// <summary>
/// Implements <c>cop checks rename &lt;old&gt; &lt;new&gt; [&lt;file.cop|directory&gt;]</c>: renames a check
/// declaration and every reference to it across the program, verifying the result before writing.
/// </summary>
internal static class CheckRenameCommand
{
    private const string Usage = "usage: cop checks rename <old-name> <new-name> [<file.cop|directory>]";

    /// <summary>Runs <c>cop checks rename</c> with the arguments that follow <c>rename</c>.</summary>
    public static int Execute(string[] args)
    {
        if (args.Length is < 2 or > 3 || args.Any(argument => argument.StartsWith('-')))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string oldName = args[0];
        string newName = args[1];
        if (oldName == newName)
        {
            Console.Error.WriteLine("cop checks rename: the new name is the same as the old name.");
            return 2;
        }

        string? nameError = CheckRenamer.ValidateName(newName);
        if (nameError is not null)
        {
            Console.Error.WriteLine($"cop checks rename: {nameError}");
            return 2;
        }

        string fullPath = Path.GetFullPath(args.Length == 3
            ? args[2]
            : Path.Combine(Directory.GetCurrentDirectory(), "cop-checks"));
        string rootPath;
        if (Directory.Exists(fullPath))
        {
            rootPath = fullPath;
        }
        else if (File.Exists(fullPath) &&
            string.Equals(Path.GetExtension(fullPath), ".cop", StringComparison.OrdinalIgnoreCase))
        {
            // A file belongs to the program formed by every .cop file in its directory.
            rootPath = Path.GetDirectoryName(fullPath)!;
        }
        else
        {
            Console.Error.WriteLine($"cop checks rename: path not found: {fullPath}");
            return 2;
        }

        string[] files = Directory.GetFiles(rootPath, "*.cop", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        Dictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);
        List<CheckCatalogueSource> parsed = [];
        try
        {
            foreach (string file in files)
            {
                string source = File.ReadAllText(file);
                sources[file] = source;
                parsed.Add(new CheckCatalogueSource(CopParser.Parse(source, file), file, source, null));
            }
        }
        catch (Exception ex) when (ex is ParseException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"cop checks rename: {ex.Message}");
            return 1;
        }

        List<(string File, Declaration Declaration)> declarations = parsed
            .SelectMany(source => source.Module.Declarations.Select(declaration => (source.FilePath, declaration)))
            .ToList();
        List<(string File, Declaration Declaration)> targets = declarations
            .Where(entry => entry.Declaration is LetDecl { Name: var let } && let == oldName ||
                entry.Declaration is FunctionDecl { Name: var function } && function == oldName)
            .ToList();
        if (targets.Count != 1)
        {
            Console.Error.WriteLine(targets.Count == 0
                ? $"cop checks rename: no check named '{oldName}' is declared under {rootPath}."
                : $"cop checks rename: '{oldName}' is declared {targets.Count} times under {rootPath}; rename it manually.");
            return 1;
        }

        ModuleLoader loader = new(CheckCatalogueCommand.GetFeedPaths(rootPath, parsed));
        loader.ResolveImports(parsed.Select(source => source.Module), new Evaluator());
        IEnumerable<Declaration> allDeclarations = declarations.Select(entry => entry.Declaration)
            .Concat(loader.LoadedFiles.SelectMany(file => file.Module.Declarations));
        if (allDeclarations.Any(declaration => NameOf(declaration) == newName))
        {
            Console.Error.WriteLine($"cop checks rename: '{newName}' is already declared in this program or its imports.");
            return 1;
        }

        Dictionary<string, string> edited = new(StringComparer.OrdinalIgnoreCase);
        int references = 0;
        foreach ((string file, string source) in sources)
        {
            string updated = CheckRenamer.Rename(source, oldName, newName, out int count);
            references += count;
            if (count > 0)
            {
                edited[file] = updated;
            }
        }

        int errorsBefore = CountErrors(files, rootPath, file => sources[file]);
        int errorsAfter = CountErrors(files, rootPath,
            file => edited.TryGetValue(file, out string? text) ? text : sources[file]);
        if (errorsAfter > errorsBefore)
        {
            Console.Error.WriteLine($"cop checks rename: renaming '{oldName}' to '{newName}' would introduce " +
                $"{errorsAfter - errorsBefore} verification error(s); no files were changed.");
            return 1;
        }

        string declaringFile = targets[0].File;
        string? renamedFile = null;
        if (string.Equals(Path.GetFileNameWithoutExtension(declaringFile), oldName, StringComparison.Ordinal))
        {
            string candidate = Path.Combine(Path.GetDirectoryName(declaringFile)!, newName + ".cop");
            if (!File.Exists(candidate))
            {
                renamedFile = candidate;
            }
        }

        try
        {
            foreach ((string file, string text) in edited)
            {
                File.WriteAllText(file, text);
            }
            if (renamedFile is not null)
            {
                File.Move(declaringFile, renamedFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"cop checks rename: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Renamed '{oldName}' to '{newName}': {references} reference(s) in {edited.Count} file(s).");
        if (renamedFile is not null)
        {
            Console.WriteLine($"Renamed file {Path.GetRelativePath(rootPath, declaringFile)} to {Path.GetRelativePath(rootPath, renamedFile)}.");
        }
        return 0;
    }

    private static int CountErrors(string[] files, string rootPath, Func<string, string> readSource) =>
        VerifyCommand.CollectDiagnostics(files, rootPath, readSource)
            .Count(diagnostic => diagnostic.Severity == CopDiagnosticSeverity.Error);

    private static string? NameOf(Declaration declaration) => declaration switch
    {
        LetDecl let => let.Name,
        FunctionDecl function => function.Name,
        CommandDecl command => command.Name,
        TypeDecl type => type.Name,
        EnumDecl enumeration => enumeration.Name,
        FlagsDecl flags => flags.Name,
        _ => null
    };
}
