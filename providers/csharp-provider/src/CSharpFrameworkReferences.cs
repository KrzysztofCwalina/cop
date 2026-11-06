// Resolves additional framework reference packs without evaluating project build targets.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Cop.Core;
using Microsoft.CodeAnalysis;

namespace Cop.Providers.SourceParsers;

internal static class CSharpFrameworkReferences
{
    private static readonly AsyncProviderValue<string[]> s_packRoots = new(FindPackRootsAsync);

    internal static async ValueTask<IEnumerable<MetadataReference>> ResolveAsync(
        string framework, string target, string project, CancellationToken cancellationToken)
    {
        if (framework == "Microsoft.NETCore.App")
        {
            return [];
        }
        string targetDirectory = target.Split('-')[0].ToLowerInvariant();
        if (framework.Length == 0 || framework.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '.' && character != '-') ||
            !targetDirectory.StartsWith("net", StringComparison.Ordinal) ||
            targetDirectory.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '.'))
        {
            ProviderWarnings.ReportOnce("csharp", project,
                $"Cannot resolve framework references for target '{target}' in '{project}'.");
            return [];
        }

        string pack = framework switch
        {
            "Microsoft.WindowsDesktop.App.WPF" or "Microsoft.WindowsDesktop.App.WindowsForms"
                => "Microsoft.WindowsDesktop.App.Ref",
            _ => framework + ".Ref"
        };
        try
        {
            string[] roots = await s_packRoots.GetValueAsync(cancellationToken).ConfigureAwait(false);
            string? directory = roots
                .Select(root => Path.Combine(root, pack))
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateDirectories)
                .Select(path => new
                {
                    Path = Path.Combine(path, "ref", targetDirectory),
                    Version = Version.TryParse(
                        Path.GetFileName(path).Split('-')[0], out Version? version)
                        ? version : null,
                    IsPreview = Path.GetFileName(path).Contains('-')
                })
                .Where(candidate => candidate.Version is not null &&
                    Directory.Exists(candidate.Path))
                .OrderByDescending(candidate => candidate.Version)
                .ThenBy(candidate => candidate.IsPreview)
                .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();
            if (directory is not null)
            {
                return Directory.EnumerateFiles(directory, "*.dll")
                    .Order(StringComparer.Ordinal)
                    .Select(path => MetadataReference.CreateFromFile(path))
                    .ToArray();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProviderWarnings.ReportOnce("csharp", project,
                $"Could not read framework references for '{project}': {ex.Message}");
            return [];
        }

        ProviderWarnings.ReportOnce("csharp", project,
            $"Framework reference pack '{pack}' for '{target}' is unavailable for '{project}'. " +
            "Install the matching .NET SDK or targeting pack to enable semantic analysis.");
        return [];
    }

    private static async ValueTask<string[]> FindPackRootsAsync(CancellationToken cancellationToken)
    {
        string[] sdkPacks = await FindSdkPackRootsAsync(cancellationToken).ConfigureAwait(false);
        return FindPackRoots(Path.GetFullPath(Path.Combine(
            RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs")), () => sdkPacks);
    }

    private static string[] FindPackRoots(string runtimePacks, Func<string[]> discoverSdkPacks)
    {
        return new[] { runtimePacks }.Concat(discoverSdkPacks())
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private static async ValueTask<string[]> FindSdkPackRootsAsync(CancellationToken cancellationToken)
    {
        List<string> roots = [];
        try
        {
            ToolchainResult result = await ToolchainProcess.RunAsync(
                new ToolchainCommand("dotnet", ["--list-sdks"], Environment.CurrentDirectory),
                new ToolchainRunOptions { Timeout = TimeSpan.FromSeconds(10) }, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status != ToolchainStatus.Success)
            {
                ProviderWarnings.ReportOnce("csharp", "sdk-discovery",
                    $"SDK discovery failed while locating framework references: " +
                    $"{result.Diagnostic} {result.StandardError}");
                return roots.ToArray();
            }
            foreach (string line in result.StandardOutput.Split('\n'))
            {
                int opening = line.LastIndexOf('[');
                int closing = line.LastIndexOf(']');
                if (opening < 0 || closing <= opening)
                {
                    continue;
                }

                string sdkDirectory = line[(opening + 1)..closing];
                string? root = Path.GetDirectoryName(sdkDirectory);
                if (root is not null)
                {
                    roots.Add(Path.Combine(root, "packs"));
                }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            ProviderWarnings.ReportOnce("csharp", "sdk-discovery",
                $"Could not locate installed SDK framework reference packs: {ex.Message}");
        }
        return roots.Distinct(StringComparer.Ordinal).ToArray();
    }
}
