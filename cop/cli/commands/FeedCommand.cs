using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Collections.Generic;
using System.Linq;
using Cop.Core;

namespace Cop.Cli.Commands;

/// <summary>
/// Manages package feeds (GitHub repos used for package discovery).
/// Provides subcommands for adding, removing, and listing feeds.
/// </summary>
public static class FeedCommand
{
    /// <summary>
    /// Creates the feed parent command with add, remove, and list subcommands.
    /// </summary>
    public static Command Create()
    {
        var feedCommand = new Command("feed", "Manage package feeds");

        // feed add <url>
        var addUrlArg = new Argument<string>("url");
        var addCommand = new Command("add", "Add a package feed")
        {
            addUrlArg
        };
        addCommand.SetAction(parseResult => ExecuteAdd(parseResult.GetValue(addUrlArg)));
        feedCommand.Add(addCommand);

        // feed remove <url>
        var removeUrlArg = new Argument<string>("url");
        var removeCommand = new Command("remove", "Remove a package feed")
        {
            removeUrlArg
        };
        removeCommand.SetAction(parseResult => ExecuteRemove(parseResult.GetValue(removeUrlArg)));
        feedCommand.Add(removeCommand);

        // feed list
        var listCommand = new Command("list", "List configured feeds");
        listCommand.SetAction(_ => ExecuteList());
        feedCommand.Add(listCommand);

        return feedCommand;
    }

    /// <summary>
    /// Executes the feed add subcommand.
    /// </summary>
    private static int ExecuteAdd(string url)
    {
        try
        {
            var feedManager = new FeedManager();
            feedManager.AddFeed(url);
            Console.WriteLine($"Successfully added feed: {url}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error adding feed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Executes the feed remove subcommand.
    /// </summary>
    private static int ExecuteRemove(string url)
    {
        try
        {
            var feedManager = new FeedManager();
            feedManager.RemoveFeed(url);
            Console.WriteLine($"Successfully removed feed: {url}");
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error removing feed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Executes the feed list subcommand.
    /// Lists all feeds with the default feed marked with an asterisk (*).
    /// </summary>
    private static int ExecuteList()
    {
        try
        {
            var feedManager = new FeedManager();
            var feeds = feedManager.GetFeeds();

            if (feeds.Count == 0)
            {
                Console.WriteLine("No feeds configured.");
                return 0;
            }

            Console.WriteLine("Configured feeds:");
            foreach (var feed in feeds)
            {
                // First feed is always the default
                string marker = feeds.IndexOf(feed) == 0 ? "*" : " ";
                Console.WriteLine($"{marker} {feed}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error listing feeds: {ex.Message}");
            return 1;
        }
    }
}
