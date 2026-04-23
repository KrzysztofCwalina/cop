using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cop.Core
{
    /// <summary>
    /// Manages the list of package feeds (GitHub repos) for package discovery.
    /// Default feed is the cop repo itself. Users can add additional feeds.
    /// Feeds are for discovery only (cop search) — cop restore uses fully-qualified paths directly.
    /// </summary>
    public class FeedManager
    {
        private const string DefaultFeed = "github.com/KrzysztofCwalina/cop";
        private const string FeedsFileName = "feeds.json";
        
        private List<string> _feeds;
        private string _feedsFilePath;

        /// <summary>
        /// Initializes a new instance of the FeedManager class.
        /// Loads feeds from ~/.cop/feeds.json if it exists, otherwise uses default feed only.
        /// </summary>
        public FeedManager()
        {
            var copDir = GetCopDirectory();
            _feedsFilePath = Path.Combine(copDir, FeedsFileName);
            _feeds = new List<string> { DefaultFeed };

            if (File.Exists(_feedsFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_feedsFilePath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var data = JsonSerializer.Deserialize<FeedsData>(json, options);

                    if (data?.Feeds != null && data.Feeds.Count > 0)
                    {
                        // Load feeds from file, ensuring default feed is always first and not duplicated
                        _feeds.Clear();
                        _feeds.Add(DefaultFeed);
                        
                        foreach (var feed in data.Feeds)
                        {
                            if (feed != DefaultFeed && !_feeds.Contains(feed))
                            {
                                _feeds.Add(feed);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    // If JSON is corrupt or file can't be read, just use defaults
                    _feeds = new List<string> { DefaultFeed };
                }
            }
        }

        /// <summary>
        /// Returns all feeds (default + user feeds). Default feed is always first.
        /// </summary>
        public List<string> GetFeeds()
        {
            return new List<string>(_feeds);
        }

        /// <summary>
        /// Adds a feed URL or local directory path. 
        /// GitHub feeds must be github.com/{owner}/{repo}. Local feeds must be existing directory paths.
        /// Throws if feed already exists or format is invalid.
        /// </summary>
        public void AddFeed(string feed)
        {
            if (string.IsNullOrWhiteSpace(feed))
            {
                throw new ArgumentException("Feed cannot be null or empty.", nameof(feed));
            }

            feed = feed.Trim();

            // Validate format: either a local directory or github.com/{owner}/{repo}
            if (!IsLocalFeed(feed) && !IsValidGitHubFeedFormat(feed))
            {
                throw new ArgumentException(
                    $"Invalid feed: '{feed}'. Must be a local directory path or 'github.com/{{owner}}/{{repo}}'.",
                    nameof(feed));
            }

            // For local feeds, resolve to full path and validate existence
            if (IsLocalFeed(feed))
            {
                feed = Path.GetFullPath(feed);
                if (!Directory.Exists(feed))
                {
                    throw new ArgumentException(
                        $"Local feed directory does not exist: '{feed}'.",
                        nameof(feed));
                }
            }

            if (_feeds.Contains(feed))
            {
                throw new InvalidOperationException($"Feed '{feed}' already exists.");
            }

            _feeds.Add(feed);
            SaveFeeds();
        }

        /// <summary>
        /// Removes a feed. Cannot remove the default feed.
        /// Throws if feed is not found or is the default feed.
        /// </summary>
        public void RemoveFeed(string feed)
        {
            if (string.IsNullOrWhiteSpace(feed))
            {
                throw new ArgumentException("Feed cannot be null or empty.", nameof(feed));
            }

            feed = feed.Trim();

            if (feed == DefaultFeed)
            {
                throw new InvalidOperationException($"Cannot remove the default feed '{DefaultFeed}'.");
            }

            if (!_feeds.Contains(feed))
            {
                throw new InvalidOperationException($"Feed '{feed}' not found.");
            }

            _feeds.Remove(feed);
            SaveFeeds();
        }

        /// <summary>
        /// Returns the ~/.cop/ directory path. Creates the directory if it doesn't exist.
        /// </summary>
        public string GetCopDirectory()
        {
            var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var copDirPath = Path.Combine(userProfilePath, ".cop");

            if (!Directory.Exists(copDirPath))
            {
                Directory.CreateDirectory(copDirPath);
            }

            return copDirPath;
        }

        /// <summary>
        /// Returns true if the feed string refers to a local directory path (not a github.com URL).
        /// </summary>
        public static bool IsLocalFeed(string feed)
        {
            if (string.IsNullOrWhiteSpace(feed)) return false;
            // A local feed is any path that is rooted (absolute) or starts with . (relative)
            // but is NOT a github.com/... URL
            if (feed.StartsWith("github.com", StringComparison.OrdinalIgnoreCase)) return false;
            return Path.IsPathRooted(feed) || feed.StartsWith(".", StringComparison.Ordinal);
        }

        /// <summary>
        /// Validates that a feed URL follows the GitHub format.
        /// </summary>
        private static bool IsValidGitHubFeedFormat(string feed)
        {
            var parts = feed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 3 && parts[0] == "github.com" && !string.IsNullOrEmpty(parts[1]) && !string.IsNullOrEmpty(parts[2]);
        }

        /// <summary>
        /// Saves the current feeds to ~/.cop/feeds.json.
        /// </summary>
        private void SaveFeeds()
        {
            try
            {
                var copDir = GetCopDirectory();
                _feedsFilePath = Path.Combine(copDir, FeedsFileName);

                var data = new FeedsData { Feeds = _feeds };
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(data, options);

                File.WriteAllText(_feedsFilePath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Failed to save feeds to {_feedsFilePath}.", ex);
            }
        }

        /// <summary>
        /// Data model for feeds.json deserialization.
        /// </summary>
        private class FeedsData
        {
            [JsonPropertyName("feeds")]
            public List<string> Feeds { get; set; } = new List<string>();
        }
    }
}
