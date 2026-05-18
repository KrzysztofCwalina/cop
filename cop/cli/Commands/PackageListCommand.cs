using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Cop.Core;

namespace Cop.Cli.Commands;

/// <summary>
/// Lists all packages available in configured feeds.
/// </summary>
public static class PackageListCommand
{
    public static Command Create()
    {
        var feedOption = new Option<string?>("--feed") { Description = "List packages from a specific feed only" };
        var command = new Command("list", "List all available packages across feeds")
        {
            feedOption
        };

        command.SetAction(parseResult => Execute(parseResult.GetValue(feedOption)));

        return command;
    }

    private static int Execute(string? feedFilter)
    {
        try
        {
            return ExecuteAsync(feedFilter).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(string? feedFilter)
    {
        var feedManager = new FeedManager();
        var feeds = feedManager.GetFeeds();

        if (!string.IsNullOrEmpty(feedFilter))
        {
            feeds = feeds.Where(f => f.Equals(feedFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (feeds.Count == 0)
            {
                Console.Error.WriteLine($"Feed '{feedFilter}' is not configured. Use 'cop package feed list' to see feeds.");
                return 1;
            }
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "cop-cli");
        var localSource = new LocalPackageSource();

        bool first = true;
        foreach (var feed in feeds)
        {
            if (!first) Console.WriteLine();
            first = false;

            Console.WriteLine(feed);

            if (FeedManager.IsLocalFeed(feed))
            {
                var packages = localSource.ListPackages(feed);
                if (packages.Count == 0)
                {
                    Console.WriteLine("  (no packages found)");
                    continue;
                }
                foreach (var p in packages)
                    Console.WriteLine($"  {p}");
            }
            else
            {
                var feedParts = feed.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (feedParts.Length != 3 || !feedParts[0].Equals("github.com", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"  (invalid feed format — skipping)");
                    continue;
                }

                var packages = await ListPackagesViaTreeAsync(httpClient, feedParts[1], feedParts[2]);
                if (packages.Count == 0)
                {
                    Console.WriteLine("  (no packages found)");
                    continue;
                }
                foreach (var p in packages)
                    Console.WriteLine($"  {p}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Lists packages using the Git Trees API (single call for public repos).
    /// Falls back to gh CLI token if rate-limited.
    /// </summary>
    private static async Task<List<string>> ListPackagesViaTreeAsync(
        HttpClient httpClient, string owner, string repo)
    {
        // Try master first (most common for existing repos), fall back to main
        var token = GetGhToken();
        var packages = await TryListFromBranch(httpClient, owner, repo, "master", token);
        if (packages == null)
            packages = await TryListFromBranch(httpClient, owner, repo, "main", token);

        return packages ?? new List<string>();
    }

    private static async Task<List<string>?> TryListFromBranch(
        HttpClient httpClient, string owner, string repo, string branch, string? token)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/git/trees/{branch}?recursive=1";

        HttpResponseMessage response;
        if (token != null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {token}");
            response = await httpClient.SendAsync(request);
        }
        else
        {
            response = await httpClient.GetAsync(url);
            // If rate-limited, retry with gh token
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                token = GetGhToken();
                if (token != null)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("Authorization", $"Bearer {token}");
                    response = await httpClient.SendAsync(request);
                }
            }
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // Branch doesn't exist — caller will try another

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"  (GitHub API returned {(int)response.StatusCode} — try again later or set GITHUB_TOKEN)");
            return new List<string>();
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var tree = doc.RootElement.GetProperty("tree");

        var packages = new List<string>();
        foreach (var item in tree.EnumerateArray())
        {
            var path = item.GetProperty("path").GetString();
            if (path == null) continue;

            if (path.StartsWith("packages/") && path.EndsWith("/" + PackageMetadata.MetadataFileName))
            {
                var segments = path.Split('/');
                var packageName = segments[segments.Length - 2];
                packages.Add(packageName);
            }
        }

        return packages.OrderBy(n => n).ToList();
    }

    private static string? GetGhToken()
    {
        var envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken)) return envToken;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    return output;
            }
        }
        catch { }

        return null;
    }
}

