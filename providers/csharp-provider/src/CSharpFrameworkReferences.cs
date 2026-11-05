// Resolves additional framework reference packs without evaluating project build targets.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Cop.Core;
using Microsoft.CodeAnalysis;

namespace Cop.Providers.SourceParsers;

internal static class CSharpFrameworkReferences
{
    private static readonly Lazy<string[]> s_packRoots = new(FindPackRoots);

    internal static IEnumerable<MetadataReference> Resolve(
        string framework, string target, string project)
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
            string? directory = s_packRoots.Value
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

    private static string[] FindPackRoots() =>
        FindPackRoots(Path.GetFullPath(Path.Combine(
            RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "packs")),
            FindSdkPackRoots);

    private static string[] FindPackRoots(string runtimePacks, Func<string[]> discoverSdkPacks)
    {
        return new[] { runtimePacks }.Concat(discoverSdkPacks())
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] FindSdkPackRoots()
    {
        List<string> roots = [];
        try
        {
            ProcessStartInfo start = new("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--list-sdks");
            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start SDK discovery.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                ProviderWarnings.ReportOnce("csharp", "sdk-discovery",
                    "SDK discovery timed out while locating framework references.");
                return roots.ToArray();
            }
            if (process.ExitCode != 0)
            {
                ProviderWarnings.ReportOnce("csharp", "sdk-discovery",
                    $"SDK discovery failed while locating framework references: {error.Result}");
                return roots.ToArray();
            }
            foreach (string line in output.Result.Split('\n'))
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
