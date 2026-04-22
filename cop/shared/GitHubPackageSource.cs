using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Cop.Core;

/// <summary>
/// Exception thrown when a package is not found in the GitHub repository.
/// </summary>
public class PackageNotFoundException : Exception
{
    public PackageNotFoundException(string message) : base(message) { }
    public PackageNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// GitHub REST API client for fetching package content from GitHub repositories.
/// Packages are stored under the `packages/` folder and referenced by Go-style paths.
/// </summary>
public class GitHubPackageSource
{
    private const string GitHubApiBaseUrl = "https://api.github.com";
    private const string UserAgent = "cop-cli";

    private readonly HttpClient _httpClient;
    private readonly string? _githubToken;

    public GitHubPackageSource(HttpClient httpClient, string? githubToken = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _githubToken = githubToken;

        // Configure default headers
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        if (!string.IsNullOrEmpty(githubToken))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {githubToken}");
        }
    }

    /// <summary>
    /// Fetches package metadata from the package's markdown file.
    /// Searches recursively through group folders if not found at the top level.
    /// </summary>
    public async Task<PackageMetadata> GetPackageMetadataAsync(
        PackageReference packageRef,
        CancellationToken ct = default)
    {
        var packagePath = await FindPackagePathAsync(
            packageRef.Owner, packageRef.Repo, packageRef.PackageName, packageRef.Version, ct);

        if (packagePath is null)
            throw new PackageNotFoundException(
                $"Package '{packageRef.PackageName}' not found in {packageRef.Owner}/{packageRef.Repo}");

        var url = BuildContentsUrl(
            packageRef.Owner,
            packageRef.Repo,
            $"{packagePath}/{packageRef.PackageName}.md",
            packageRef.Version);

        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var contentDto = JsonSerializer.Deserialize<GitHubContentResponse>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (contentDto?.Content == null)
            {
                throw new PackageNotFoundException(
                    $"Failed to decode package metadata for '{packageRef.PackageName}'");
            }

            var decodedContent = DecodeBase64(contentDto.Content);
            return PackageMetadata.ParseFromMarkdown(decodedContent);
        }
        catch (PackageNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PackageNotFoundException(
                $"Failed to fetch package metadata for '{packageRef.PackageName}': {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Downloads all files from a package directory recursively.
    /// Searches through group folders if the package is not at the top level.
    /// </summary>
    public async Task<Dictionary<string, byte[]>> DownloadPackageFilesAsync(
        PackageReference packageRef,
        CancellationToken ct = default)
    {
        var packagePath = await FindPackagePathAsync(
            packageRef.Owner, packageRef.Repo, packageRef.PackageName, packageRef.Version, ct);

        if (packagePath is null)
            throw new PackageNotFoundException(
                $"Package '{packageRef.PackageName}' not found in {packageRef.Owner}/{packageRef.Repo}");

        var files = new Dictionary<string, byte[]>();
        await DownloadDirectoryRecursiveAsync(
            packageRef.Owner,
            packageRef.Repo,
            packagePath,
            packageRef.Version,
            "",
            files,
            ct);

        return files;
    }

    /// <summary>
    /// Lists all packages in the packages folder, recursing into group folders.
    /// A directory is a package if it contains {dirName}.md. Otherwise it's a group folder.
    /// </summary>
    public async Task<List<string>> ListPackagesAsync(
        string owner,
        string repo,
        CancellationToken ct = default)
    {
        var packages = new List<string>();
        await CollectPackagesAsync(owner, repo, "packages", packages, ct);
        return packages.OrderBy(name => name).ToList();
    }

    private async Task CollectPackagesAsync(
        string owner, string repo, string dirPath,
        List<string> packages, CancellationToken ct)
    {
        try
        {
            var url = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/contents/{dirPath}";
            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync(ct);
            var items = JsonSerializer.Deserialize<List<GitHubContentItem>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (items is null) return;

            var dirs = items.Where(i => i.Type == "dir").ToList();
            var files = items.Where(i => i.Type == "file").Select(i => i.Name).ToHashSet();

            foreach (var dir in dirs)
            {
                if (dir.Name.StartsWith('.')) continue;

                // Check if this directory has a matching .md file alongside it
                // (not reliable — .md is inside the package dir). Instead, list its contents.
                // A directory is a package if it contains {name}.md among its children.
                var isPackage = await IsGitHubPackageAsync(owner, repo, dir.Path, dir.Name, ct);
                if (isPackage)
                {
                    packages.Add(dir.Name);
                }
                else
                {
                    // Group folder — recurse
                    await CollectPackagesAsync(owner, repo, dir.Path, packages, ct);
                }
            }
        }
        catch (HttpRequestException)
        {
            // Ignore errors for subdirectories
        }
    }

    private async Task<bool> IsGitHubPackageAsync(
        string owner, string repo, string dirPath, string dirName, CancellationToken ct)
    {
        try
        {
            var metadataUrl = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/contents/{dirPath}/{dirName}.md";
            var response = await _httpClient.GetAsync(metadataUrl, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the GitHub path for a package, searching through group folders if needed.
    /// Tries direct path first (packages/{name}), then searches group directories.
    /// Returns null if the package is not found.
    /// </summary>
    private async Task<string?> FindPackagePathAsync(
        string owner, string repo, string packageName, string? version, CancellationToken ct)
    {
        // Try direct path first
        var directPath = $"packages/{packageName}";
        var directUrl = BuildContentsUrl(owner, repo, $"{directPath}/{packageName}.md", version);
        try
        {
            var response = await _httpClient.GetAsync(directUrl, ct);
            if (response.IsSuccessStatusCode) return directPath;
        }
        catch { /* fall through to group search */ }

        // List packages/ root and search group folders
        var rootUrl = BuildContentsUrl(owner, repo, "packages", version);
        try
        {
            var response = await _httpClient.GetAsync(rootUrl, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var items = JsonSerializer.Deserialize<List<GitHubContentItem>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (items is null) return null;

            foreach (var item in items.Where(i => i.Type == "dir" && !i.Name.StartsWith('.')))
            {
                // Try nested path: packages/{group}/{packageName}
                var nestedPath = $"packages/{item.Name}/{packageName}";
                var nestedUrl = BuildContentsUrl(owner, repo, $"{nestedPath}/{packageName}.md", version);
                try
                {
                    var nestedResponse = await _httpClient.GetAsync(nestedUrl, ct);
                    if (nestedResponse.IsSuccessStatusCode) return nestedPath;
                }
                catch { continue; }
            }
        }
        catch { /* not found */ }

        return null;
    }

    /// <summary>
    /// Gets the latest version of a package by examining git tags.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(
        PackageReference packageRef,
        CancellationToken ct = default)
    {
        var url = $"{GitHubApiBaseUrl}/repos/{packageRef.Owner}/{packageRef.Repo}/git/refs/tags/{packageRef.PackageName}/";

        try
        {
            var response = await _httpClient.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var refs = JsonSerializer.Deserialize<List<GitHubRefResponse>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (refs == null || refs.Count == 0)
            {
                return null;
            }

            // Extract version from tag refs (format: refs/tags/{packageName}/{version})
            var versions = refs
                .Where(r => r.Ref.StartsWith($"refs/tags/{packageRef.PackageName}/", StringComparison.Ordinal))
                .Select(r => r.Ref.Substring($"refs/tags/{packageRef.PackageName}/".Length))
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();

            if (versions.Count == 0)
            {
                return null;
            }

            // Sort by semantic version and return the highest
            return versions
                .OrderByDescending(v => ParseSemanticVersion(v))
                .FirstOrDefault();
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Recursively downloads all files from a directory.
    /// </summary>
    private async Task DownloadDirectoryRecursiveAsync(
        string owner,
        string repo,
        string dirPath,
        string? version,
        string relativePath,
        Dictionary<string, byte[]> files,
        CancellationToken ct)
    {
        CheckRateLimit();

        var url = BuildContentsUrl(owner, repo, dirPath, version);

        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var items = JsonSerializer.Deserialize<List<GitHubContentItem>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                var itemRelativePath = string.IsNullOrEmpty(relativePath)
                    ? item.Name
                    : $"{relativePath}/{item.Name}";

                if (item.Type == "dir")
                {
                    await DownloadDirectoryRecursiveAsync(
                        owner,
                        repo,
                        item.Path,
                        version,
                        itemRelativePath,
                        files,
                        ct);
                }
                else if (item.Type == "file")
                {
                    if (item.Content != null && item.Encoding == "base64")
                    {
                        files[itemRelativePath] = Convert.FromBase64String(item.Content);
                    }
                }
            }
        }
        catch (HttpRequestException ex)
        {
            throw new Exception($"Failed to download directory '{dirPath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds a GitHub Contents API URL.
    /// </summary>
    private static string BuildContentsUrl(
        string owner,
        string repo,
        string path,
        string? version = null)
    {
        var url = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/contents/{path}";
        if (!string.IsNullOrEmpty(version))
        {
            url += $"?ref={version}";
        }

        return url;
    }

    /// <summary>
    /// Decodes a base64-encoded string.
    /// </summary>
    private static string DecodeBase64(string encodedContent)
    {
        try
        {
            var data = Convert.FromBase64String(encodedContent);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to decode base64 content: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Checks the X-RateLimit-Remaining header and throws if rate limit is exceeded.
    /// </summary>
    private void CheckRateLimit()
    {
        if (_httpClient.DefaultRequestHeaders.TryGetValues("X-RateLimit-Remaining", out var values))
        {
            if (int.TryParse(values.FirstOrDefault(), out var remaining) && remaining == 0)
            {
                throw new Exception(
                    "GitHub API rate limit exceeded. Please try again later or provide a GitHub token for higher rate limits.");
            }
        }
    }

    /// <summary>
    /// Parses a semantic version string for comparison.
    /// Returns a tuple of (major, minor, patch) for sorting.
    /// </summary>
    private static (int, int, int) ParseSemanticVersion(string version)
    {
        var parts = version.TrimStart('v').Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return (major, minor, patch);
    }

    /// <summary>
    /// DTO for GitHub Contents API response.
    /// </summary>
    private class GitHubContentResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        [JsonPropertyName("size")]
        public int Size { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("encoding")]
        public string? Encoding { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("git_url")]
        public string? GitUrl { get; set; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("_links")]
        public GitHubLinks? Links { get; set; }
    }

    /// <summary>
    /// DTO for GitHub Contents API list response item.
    /// </summary>
    private class GitHubContentItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        [JsonPropertyName("size")]
        public int Size { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("encoding")]
        public string? Encoding { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("git_url")]
        public string? GitUrl { get; set; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("_links")]
        public GitHubLinks? Links { get; set; }
    }

    /// <summary>
    /// DTO for GitHub API reference response.
    /// </summary>
    private class GitHubRefResponse
    {
        [JsonPropertyName("ref")]
        public string Ref { get; set; } = string.Empty;

        [JsonPropertyName("node_id")]
        public string? NodeId { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("object")]
        public GitHubRefObject? Object { get; set; }
    }

    /// <summary>
    /// DTO for GitHub reference object.
    /// </summary>
    private class GitHubRefObject
    {
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    /// <summary>
    /// DTO for GitHub links.
    /// </summary>
    private class GitHubLinks
    {
        [JsonPropertyName("self")]
        public string? Self { get; set; }

        [JsonPropertyName("git")]
        public string? Git { get; set; }

        [JsonPropertyName("html")]
        public string? Html { get; set; }
    }
}
