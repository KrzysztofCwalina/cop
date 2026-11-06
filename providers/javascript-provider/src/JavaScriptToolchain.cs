// Maps TypeScript compiler responses into the shared Cop source model.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>Discovers an already-installed compiler without invoking npm or assuming global require resolution.</summary>
public sealed record JavaScriptToolchain(string NodePath, string CompilerPath)
{
    /// <summary>Finds installed Node.js and TypeScript for a source path.</summary>
    public static JavaScriptToolchain? Discover(string sourcePath, string? searchPath = null)
    {
        string? node = ToolchainProcess.FindExecutable("node", searchPath);
        if (node is null)
        {
            return null;
        }

        for (DirectoryInfo? directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!);
            directory is not null; directory = directory.Parent)
        {
            string local = Path.Combine(directory.FullName, "node_modules", "typescript", "lib", "typescript.js");
            if (File.Exists(local))
            {
                return new(node, local);
            }
        }

        // npm's Windows launchers sit beside node_modules; Unix tsc is normally a symlink
        // into ../lib/node_modules/typescript/bin/tsc (including version-manager installations).
        foreach (string entry in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            foreach (string? launcherName in OperatingSystem.IsWindows()
                ? new[] { "tsc.cmd", "tsc.ps1", "tsc.exe", "tsc" } : new[] { "tsc" })
            {
                FileInfo launcher = new FileInfo(Path.Combine(directory, launcherName));
                if (!launcher.Exists)
                {
                    continue;
                }

                FileSystemInfo? target = launcher.ResolveLinkTarget(returnFinalTarget: true);
                string[] candidates = new[]
                {
                    Path.Combine(directory, "node_modules", "typescript", "lib", "typescript.js"),
                    Path.GetFullPath(Path.Combine(directory, "..", "lib", "node_modules", "typescript", "lib", "typescript.js")),
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target?.FullName ?? launcher.FullName)!,
                        "..", "lib", "typescript.js"))
                };
                foreach (string? candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        return new(node, candidate);
                    }
                }
            }
        }
        return null;
    }
}
