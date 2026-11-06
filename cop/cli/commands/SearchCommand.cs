// Searches package feeds and displays matching packages.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Cop.Core;

namespace Cop.Cli.Commands;

/// <summary>Finds packages across the configured package feeds.</summary>
public static class SearchCommand
{
    /// <summary>
    /// Creates the package-search command with its required search query.
    /// </summary>
    public static Command Create()
    {
        Argument<string> queryArg = new Argument<string>("query");
        Command command = new Command("search", "Search for packages across configured feeds")
        {
            queryArg
        };

        command.SetAction(parseResult => ExecuteAsync(parseResult.GetValue(queryArg)
            ?? throw new InvalidOperationException("The required search query is missing.")));

        return command;
    }

    private static async Task<int> ExecuteAsync(string query)
    {
        try
        {
            return await SearchFeedsAsync(query);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            HttpRequestException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> SearchFeedsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("Error: Query cannot be empty.");
            return 1;
        }

        // Load feeds
        FeedManager feedManager = new FeedManager();
        List<string> feeds = feedManager.GetFeeds();

        if (feeds.Count == 0)
        {
            Console.WriteLine("No packages found matching '{0}'", query);
            return 0;
        }

        // Create HTTP client with GitHub token if available
        string? githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        using HttpClient httpClient = new HttpClient();
        GitHubPackageSource packageSource = new GitHubPackageSource(httpClient, githubToken);

        // Search across feeds
        List<FeedSearchResult> results = new List<FeedSearchResult>();
        string queryLower = query.ToLowerInvariant();

        foreach (string feed in feeds)
        {
            // Parse feed URL: github.com/owner/repo
            string[] feedParts = feed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (feedParts.Length != 3 || feedParts[0] != "github.com")
            {
                Console.Error.WriteLine($"Warning: Invalid feed format: '{feed}'. Skipping.");
                continue;
            }

            string owner = feedParts[1];
            string repo = feedParts[2];

            // List packages in this feed
            List<string> packages = await packageSource.ListPackagesAsync(owner, repo);

            // Filter packages by query
            List<PackageInfo> matchingPackages = new List<PackageInfo>();
            foreach (string packageName in packages)
            {
                if (packageName.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
                {
                    // Try to fetch metadata for display
                    PackageInfo packageInfo = new PackageInfo { Name = packageName };

                    try
                    {
                        PackageReference packageRef = new PackageReference
                        {
                            FullPath = $"{feed}/{packageName}",
                            Host = "github.com",
                            Owner = owner,
                            Repo = repo,
                            PackageName = packageName,
                            Version = null
                        };

                        PackageMetadata metadata = await packageSource.GetPackageMetadataAsync(packageRef);
                        packageInfo.Title = metadata.Title;
                        packageInfo.Version = metadata.Version;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or
                        IOException or System.Text.Json.JsonException)
                    {
                        Console.Error.WriteLine(
                            $"Warning: Could not read metadata for {packageName}: {ex.Message}");
                    }

                    matchingPackages.Add(packageInfo);
                }
            }

            if (matchingPackages.Count > 0)
            {
                results.Add(new FeedSearchResult
                {
                    Feed = feed,
                    Packages = matchingPackages
                });
            }
        }

        // Display results
        if (results.Count == 0)
        {
            Console.WriteLine("No packages found matching '{0}'", query);
            return 0;
        }

        foreach (FeedSearchResult feedResult in results)
        {
            Console.WriteLine("Feed: {0}", feedResult.Feed);
            foreach (PackageInfo package in feedResult.Packages)
            {
                // Display in table format: name, title, version
                string name = package.Name.PadRight(20);
                string title = (package.Title ?? "").PadRight(30);
                string version = package.Version ?? "unknown";
                Console.WriteLine("  {0} {1} {2}", name, title, version);
            }
            Console.WriteLine();
        }

        return 0;
    }

    private class FeedSearchResult
    {
        public string Feed { get; set; } = string.Empty;
        public List<PackageInfo> Packages { get; set; } = new();
    }

    private class PackageInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Version { get; set; }
    }
}
