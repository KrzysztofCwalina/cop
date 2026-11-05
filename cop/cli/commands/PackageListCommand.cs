// Displays packages available from configured feeds.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
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
    /// <summary>Creates the feed package-list command and its display options.</summary>
    public static Command Create()
    {
        Option<string?> feedOption = new Option<string?>("--feed") { Description = "List packages from a specific feed only" };
        Command command = new Command("list", "List all available packages across feeds")
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
        FeedManager feedManager = new FeedManager();
        List<string> feeds = feedManager.GetFeeds();

        if (!string.IsNullOrEmpty(feedFilter))
        {
            List<string> matched = feeds.Where(f => f.Equals(feedFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matched.Count == 0)
            {
                // Allow `--feed <local dir>` to list an ad-hoc local feed that isn't configured.
                // This makes listing a local package directory deterministic and offline.
                if (FeedManager.IsLocalFeed(feedFilter) && Directory.Exists(feedFilter))
                {
                    feeds = new List<string> { feedFilter };
                }
                else
                {
                    Console.Error.WriteLine($"Feed '{feedFilter}' is not configured. Use 'cop package feed list' to see feeds.");
                    return 1;
                }
            }
            else
            {
                feeds = matched;
            }
        }

        using HttpClient httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "cop-cli");
        LocalPackageSource localSource = new LocalPackageSource();

        bool first = true;
        foreach (string feed in feeds)
        {
            if (!first)
            {
                Console.WriteLine();
            }

            first = false;

            Console.WriteLine(feed);

            if (FeedManager.IsLocalFeed(feed))
            {
                List<string> packages = localSource.ListPackages(feed);
                if (packages.Count == 0)
                {
                    Console.WriteLine("  (no packages found)");
                    continue;
                }
                foreach (string p in packages)
                {
                    Console.WriteLine($"  {p}");
                }
            }
            else
            {
                string[] feedParts = feed.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (feedParts.Length != 3 || !feedParts[0].Equals("github.com", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"  (invalid feed format — skipping)");
                    continue;
                }

                List<string> packages = await ListPackagesViaTreeAsync(httpClient, feedParts[1], feedParts[2]);
                if (packages.Count == 0)
                {
                    Console.WriteLine("  (no packages found)");
                    continue;
                }
                foreach (string p in packages)
                {
                    Console.WriteLine($"  {p}");
                }
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
        string? token = GetGhToken();
        List<string>? packages = await TryListFromBranch(httpClient, owner, repo, "master", token);
        if (packages == null)
        {
            packages = await TryListFromBranch(httpClient, owner, repo, "main", token);
        }

        return packages ?? new List<string>();
    }

    private static async Task<List<string>?> TryListFromBranch(
        HttpClient httpClient, string owner, string repo, string branch, string? token)
    {
        string url = $"https://api.github.com/repos/{owner}/{repo}/git/trees/{branch}?recursive=1";

        HttpResponseMessage response;
        if (token != null)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
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
                    HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("Authorization", $"Bearer {token}");
                    response = await httpClient.SendAsync(request);
                }
            }
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // Branch doesn't exist — caller will try another
        }

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"  (GitHub API returned {(int)response.StatusCode} — try again later or set GITHUB_TOKEN)");
            return new List<string>();
        }

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement tree = doc.RootElement.GetProperty("tree");

        List<string> packages = new List<string>();
        foreach (JsonElement item in tree.EnumerateArray())
        {
            string? path = item.GetProperty("path").GetString();
            if (path == null)
            {
                continue;
            }

            if (path.StartsWith("packages/") && path.EndsWith("/" + PackageMetadata.MetadataFileName))
            {
                string[] segments = path.Split('/');
                string packageName = segments[segments.Length - 2];
                packages.Add(packageName);
            }
        }

        return packages.OrderBy(n => n).ToList();
    }

    private static string? GetGhToken()
    {
        string? envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            return envToken;
        }

        try
        {
            ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using Process? process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    return output;
                }
            }
        }
        catch { }

        return null;
    }
}
