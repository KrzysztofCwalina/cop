using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Cop.Core;

namespace Cop.Cli.Commands;

public static class SearchCommand
{
    public static Command Create()
    {
        var queryArg = new Argument<string>("query");
        var command = new Command("search", "Search for packages across configured feeds")
        {
            queryArg
        };

        command.SetAction(parseResult => Execute(parseResult.GetValue(queryArg)));

        return command;
    }

    private static int Execute(string query)
    {
        try
        {
            return ExecuteAsync(query).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("Error: Query cannot be empty.");
            return 1;
        }

        // Load feeds
        var feedManager = new FeedManager();
        var feeds = feedManager.GetFeeds();

        if (feeds.Count == 0)
        {
            Console.WriteLine("No packages found matching '{0}'", query);
            return 0;
        }

        // Create HTTP client with GitHub token if available
        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        using var httpClient = new HttpClient();
        var packageSource = new GitHubPackageSource(httpClient, githubToken);

        // Search across feeds
        var results = new List<FeedSearchResult>();
        var queryLower = query.ToLowerInvariant();

        foreach (var feed in feeds)
        {
            // Parse feed URL: github.com/owner/repo
            var feedParts = feed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (feedParts.Length != 3 || feedParts[0] != "github.com")
            {
                Console.Error.WriteLine($"Warning: Invalid feed format: '{feed}'. Skipping.");
                continue;
            }

            var owner = feedParts[1];
            var repo = feedParts[2];

            // List packages in this feed
            var packages = await packageSource.ListPackagesAsync(owner, repo);
            
            // Filter packages by query
            var matchingPackages = new List<PackageInfo>();
            foreach (var packageName in packages)
            {
                if (packageName.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
                {
                    // Try to fetch metadata for display
                    var packageInfo = new PackageInfo { Name = packageName };
                    
                    try
                    {
                        var packageRef = new PackageReference
                        {
                            FullPath = $"{feed}/{packageName}",
                            Host = "github.com",
                            Owner = owner,
                            Repo = repo,
                            PackageName = packageName,
                            Version = null
                        };

                        var metadata = await packageSource.GetPackageMetadataAsync(packageRef);
                        packageInfo.Title = metadata.Title;
                        packageInfo.Version = metadata.Version;
                    }
                    catch
                    {
                        // If metadata fetch fails, just use the package name
                        // Keep packageInfo as is with defaults
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

        foreach (var feedResult in results)
        {
            Console.WriteLine("Feed: {0}", feedResult.Feed);
            foreach (var package in feedResult.Packages)
            {
                // Display in table format: name, title, version
                var name = package.Name.PadRight(20);
                var title = (package.Title ?? "").PadRight(30);
                var version = package.Version ?? "unknown";
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
