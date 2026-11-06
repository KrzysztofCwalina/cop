// Starts the Cop language server for editor integration.

using System.CommandLine;
using Cop.Cli.Lsp;

namespace Cop.Cli.Commands;

/// <summary>
/// Starts the cop language server: a Language Server Protocol (LSP) server over stdio that an
/// editor (e.g. the VS Code extension) launches to get live diagnostics from the real compiler.
/// Diagnostics come from the same parse + bind + type-check pipeline as <c>cop verify</c>, so the
/// editor experience can never drift from the compiler.
/// </summary>
public static class LangServerCommand
{
    /// <summary>
    /// Creates the command that serves language-protocol requests over stdio.
    /// </summary>
    public static Command Create()
    {
        Command command = new Command(
            "langserver",
            "Start the cop language server (LSP over stdio) for editor integration");
        command.SetAction(_ => Execute());
        return command;
    }

    /// <summary>Runs the language server until its input stream closes.</summary>
    public static int Execute()
    {
        // Capture the raw stdio streams BEFORE redirecting Console.Out: the LSP protocol owns
        // stdout, so any stray Console.Out write from the pipeline must not corrupt the framing.
        Stream stdin = Console.OpenStandardInput();
        Stream stdout = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        CopLanguageServer server = new CopLanguageServer(stdin, stdout);
        return server.Run();
    }
}
