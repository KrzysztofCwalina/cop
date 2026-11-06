// Lists and updates the package feeds configured for CLI package resolution.

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
        Command feedCommand = new Command("feed", "Manage package feeds");

        // feed add <url>
        Argument<string> addUrlArg = new Argument<string>("url");
        Command addCommand = new Command("add", "Add a package feed")
        {
            addUrlArg
        };
        addCommand.SetAction(parseResult => ExecuteAdd(parseResult.GetValue(addUrlArg)
            ?? throw new InvalidOperationException("The required feed URL is missing.")));
        feedCommand.Add(addCommand);

        // feed remove <url>
        Argument<string> removeUrlArg = new Argument<string>("url");
        Command removeCommand = new Command("remove", "Remove a package feed")
        {
            removeUrlArg
        };
        removeCommand.SetAction(parseResult => ExecuteRemove(parseResult.GetValue(removeUrlArg)
            ?? throw new InvalidOperationException("The required feed URL is missing.")));
        feedCommand.Add(removeCommand);

        // feed list
        Command listCommand = new Command("list", "List configured feeds");
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
            FeedManager feedManager = new FeedManager();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or System.Security.SecurityException)
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
            FeedManager feedManager = new FeedManager();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or System.Security.SecurityException)
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
            FeedManager feedManager = new FeedManager();
            List<string> feeds = feedManager.GetFeeds();

            if (feeds.Count == 0)
            {
                Console.WriteLine("No feeds configured.");
                return 0;
            }

            Console.WriteLine("Configured feeds:");
            foreach (string feed in feeds)
            {
                // First feed is always the default
                string marker = feeds.IndexOf(feed) == 0 ? "*" : " ";
                Console.WriteLine($"{marker} {feed}");
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Error listing feeds: {ex.Message}");
            return 1;
        }
    }
}
