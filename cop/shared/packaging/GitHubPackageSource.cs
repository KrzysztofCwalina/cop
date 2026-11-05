// Retrieves package metadata and content from GitHub repositories.

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
/// GitHub REST API client for fetching package content from GitHub repositories.
/// Packages are stored under the `packages/` folder and referenced by Go-style paths.
/// </summary>
public class GitHubPackageSource
{
    private const string GitHubApiBaseUrl = "https://api.github.com";
    private const string UserAgent = "cop-cli";

    private readonly HttpClient _httpClient;
    private readonly string? _githubToken;

    /// <summary>
    /// Creates a GitHub package client using the supplied transport and optional token.
    /// </summary>
    public GitHubPackageSource(HttpClient httpClient, string? githubToken = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _githubToken = githubToken;

        // Configure default headers (User-Agent only; Authorization is added per-request for security)
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    /// <summary>
    /// Fetches package metadata from the package's cop.json file.
    /// Searches recursively through group folders if not found at the top level.
    /// </summary>
    public async Task<PackageMetadata> GetPackageMetadataAsync(
        PackageReference packageRef,
        CancellationToken ct = default)
    {
        string? packagePath = await FindPackagePathAsync(
            packageRef.Owner, packageRef.Repo, packageRef.PackageName, GetPackageRef(packageRef), ct);

        if (packagePath is null)
        {
            throw new PackageNotFoundException(
                $"Package '{packageRef.PackageName}' not found in {packageRef.Owner}/{packageRef.Repo}");
        }

        string url = BuildContentsUrl(
            packageRef.Owner,
            packageRef.Repo,
            $"{packagePath}/{PackageMetadata.MetadataFileName}",
            GetPackageRef(packageRef));

        try
        {
            using HttpRequestMessage request = CreateRequestWithAuth(HttpMethod.Get, url);
            HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(ct);
                GitHubContentResponse? contentDto = JsonSerializer.Deserialize<GitHubContentResponse>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (contentDto?.Content != null)
                {
                    string decodedContent = DecodeBase64(contentDto.Content);
                    return PackageMetadata.ParseFromJson(decodedContent);
                }
            }

            // Fallback to legacy {name}.md
            string legacyUrl = BuildContentsUrl(
                packageRef.Owner,
                packageRef.Repo,
                $"{packagePath}/{packageRef.PackageName}.md",
                GetPackageRef(packageRef));

            using HttpRequestMessage legacyRequest = CreateRequestWithAuth(HttpMethod.Get, legacyUrl);
            HttpResponseMessage legacyResponse = await _httpClient.SendAsync(legacyRequest, ct);
            legacyResponse.EnsureSuccessStatusCode();

            string legacyJson = await legacyResponse.Content.ReadAsStringAsync(ct);
            GitHubContentResponse? legacyDto = JsonSerializer.Deserialize<GitHubContentResponse>(
                legacyJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (legacyDto?.Content == null)
            {
                throw new PackageNotFoundException(
                    $"Failed to decode package metadata for '{packageRef.PackageName}'");
            }

            string decodedLegacy = DecodeBase64(legacyDto.Content);
            return PackageMetadata.ParseFromMarkdown(decodedLegacy);
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
        string? packagePath = await FindPackagePathAsync(
            packageRef.Owner, packageRef.Repo, packageRef.PackageName, GetPackageRef(packageRef), ct);

        if (packagePath is null)
        {
            throw new PackageNotFoundException(
                $"Package '{packageRef.PackageName}' not found in {packageRef.Owner}/{packageRef.Repo}");
        }

        Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
        await DownloadDirectoryRecursiveAsync(
            packageRef.Owner,
            packageRef.Repo,
            packagePath,
            GetPackageRef(packageRef),
            "",
            files,
            ct);

        return files;
    }

    private static string? GetPackageRef(PackageReference packageRef) =>
        packageRef.Version ?? (packageRef.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && packageRef.Owner.Equals("KrzysztofCwalina", StringComparison.OrdinalIgnoreCase)
            && packageRef.Repo.Equals("cop", StringComparison.OrdinalIgnoreCase)
                ? $"v{ProviderCompatibility.HostVersion}" : null);

    /// <summary>
    /// Lists all packages in the packages folder, recursing into group folders.
    /// A directory is a package if it contains cop.json. Otherwise it's a group folder.
    /// </summary>
    public async Task<List<string>> ListPackagesAsync(
        string owner,
        string repo,
        CancellationToken ct = default)
    {
        List<string> packages = new List<string>();
        await CollectPackagesAsync(owner, repo, "packages", packages, ct);
        return packages.OrderBy(name => name).ToList();
    }

    private async Task CollectPackagesAsync(
        string owner, string repo, string dirPath,
        List<string> packages, CancellationToken ct)
    {
        try
        {
            string url = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/contents/{dirPath}";
            using HttpRequestMessage request = CreateRequestWithAuth(HttpMethod.Get, url);
            HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            List<GitHubContentItem>? items = JsonSerializer.Deserialize<List<GitHubContentItem>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (items is null)
            {
                return;
            }

            List<GitHubContentItem> dirs = items.Where(i => i.Type == "dir").ToList();
            HashSet<string> files = items.Where(i => i.Type == "file").Select(i => i.Name).ToHashSet();

            foreach (GitHubContentItem? dir in dirs)
            {
                if (dir.Name.StartsWith('.'))
                {
                    continue;
                }

                // A directory is a package if it contains cop.json among its children.
                bool isPackage = await IsGitHubPackageAsync(owner, repo, dir.Path, dir.Name, ct);
                if (isPackage)
                {
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
            // Check for cop.json first
            string metadataUrl = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/contents/{dirPath}/{PackageMetadata.MetadataFileName}";
            using HttpRequestMessage metadataRequest = CreateRequestWithAuth(HttpMethod.Get, metadataUrl);
            HttpResponseMessage response = await _httpClient.SendAsync(metadataRequest, ct);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // Fallback: check for legacy {name}.md
            string legacyUrl = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/contents/{dirPath}/{dirName}.md";
            using HttpRequestMessage legacyRequest = CreateRequestWithAuth(HttpMethod.Get, legacyUrl);
            HttpResponseMessage legacyResponse = await _httpClient.SendAsync(legacyRequest, ct);
            return legacyResponse.IsSuccessStatusCode;
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
        // Try direct path with cop.json first
        string directPath = $"packages/{packageName}";
        string directUrl = BuildContentsUrl(owner, repo, $"{directPath}/{PackageMetadata.MetadataFileName}", version);
        try
        {
            using HttpRequestMessage directRequest = CreateRequestWithAuth(HttpMethod.Get, directUrl);
            HttpResponseMessage response = await _httpClient.SendAsync(directRequest, ct);
            if (response.IsSuccessStatusCode)
            {
                return directPath;
            }
        }
        catch { /* fall through */ }

        // Try direct path with legacy {name}.md
        string legacyDirectUrl = BuildContentsUrl(owner, repo, $"{directPath}/{packageName}.md", version);
        try
        {
            using HttpRequestMessage legacyDirectRequest = CreateRequestWithAuth(HttpMethod.Get, legacyDirectUrl);
            HttpResponseMessage response = await _httpClient.SendAsync(legacyDirectRequest, ct);
            if (response.IsSuccessStatusCode)
            {
                return directPath;
            }
        }
        catch { /* fall through to group search */ }

        // List packages/ root and search group folders
        string rootUrl = BuildContentsUrl(owner, repo, "packages", version);
        try
        {
            using HttpRequestMessage rootRequest = CreateRequestWithAuth(HttpMethod.Get, rootUrl);
            HttpResponseMessage response = await _httpClient.SendAsync(rootRequest, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            List<GitHubContentItem>? items = JsonSerializer.Deserialize<List<GitHubContentItem>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (items is null)
            {
                return null;
            }

            foreach (GitHubContentItem? item in items.Where(i => i.Type == "dir" && !i.Name.StartsWith('.')))
            {
                // Try nested path: packages/{group}/{packageName}/cop.json
                string nestedPath = $"packages/{item.Name}/{packageName}";
                string nestedUrl = BuildContentsUrl(owner, repo, $"{nestedPath}/{PackageMetadata.MetadataFileName}", version);
                try
                {
                    using HttpRequestMessage nestedRequest = CreateRequestWithAuth(HttpMethod.Get, nestedUrl);
                    HttpResponseMessage nestedResponse = await _httpClient.SendAsync(nestedRequest, ct);
                    if (nestedResponse.IsSuccessStatusCode)
                    {
                        return nestedPath;
                    }
                }
                catch { /* continue */ }

                // Fallback: try legacy {name}.md
                string nestedLegacyUrl = BuildContentsUrl(owner, repo, $"{nestedPath}/{packageName}.md", version);
                try
                {
                    using HttpRequestMessage nestedLegacyRequest = CreateRequestWithAuth(HttpMethod.Get, nestedLegacyUrl);
                    HttpResponseMessage nestedLegacyResponse = await _httpClient.SendAsync(nestedLegacyRequest, ct);
                    if (nestedLegacyResponse.IsSuccessStatusCode)
                    {
                        return nestedPath;
                    }
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
        if (packageRef.Version is null && GetPackageRef(packageRef) is { } releaseRef)
        {
            return releaseRef;
        }

        string url = $"{GitHubApiBaseUrl}/repos/{packageRef.Owner}/{packageRef.Repo}/git/refs/tags/{packageRef.PackageName}/";

        try
        {
            using HttpRequestMessage request = CreateRequestWithAuth(HttpMethod.Get, url);
            HttpResponseMessage response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            EnsureSuccess(response);

            string json = await response.Content.ReadAsStringAsync(ct);
            List<GitHubRefResponse>? refs = JsonSerializer.Deserialize<List<GitHubRefResponse>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (refs == null || refs.Count == 0)
            {
                return null;
            }

            // Extract version from tag refs (format: refs/tags/{packageName}/{version})
            List<string> versions = refs
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
        string url = BuildContentsUrl(owner, repo, dirPath, version);

        try
        {
            using HttpRequestMessage request = CreateRequestWithAuth(HttpMethod.Get, url);
            HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
            EnsureSuccess(response);

            string json = await response.Content.ReadAsStringAsync(ct);
            List<GitHubContentItem>? items = JsonSerializer.Deserialize<List<GitHubContentItem>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (items == null)
            {
                return;
            }

            foreach (GitHubContentItem item in items)
            {
                string itemRelativePath = string.IsNullOrEmpty(relativePath)
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
                    else if (item.DownloadUrl != null)
                    {
                        // Directory listings don't include content; download each file individually
                        using HttpRequestMessage fileRequest = CreateRequestWithAuth(HttpMethod.Get, item.DownloadUrl);
                        HttpResponseMessage fileResponse = await _httpClient.SendAsync(fileRequest, ct);
                        EnsureSuccess(fileResponse);
                        files[itemRelativePath] = await fileResponse.Content.ReadAsByteArrayAsync(ct);
                    }
                    else
                    {
                        // Neither inline content nor a download URL — don't silently drop the file
                        // (it would produce a corrupt, incomplete package). Surface it loudly.
                        throw new Exception(
                            $"Cannot download '{itemRelativePath}': the GitHub API returned neither inline " +
                            "content nor a download URL (unsupported entry, e.g. a submodule or Git LFS pointer).");
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
        string url = $"{GitHubApiBaseUrl}/repos/{owner}/{repo}/contents/{path}";
        if (!string.IsNullOrEmpty(version))
        {
            url += $"?ref={version}";
        }

        return url;
    }

    /// <summary>
    /// Creates an HttpRequestMessage with Authorization header added per-request (not stored in DefaultRequestHeaders).
    /// This prevents token exposure in logs, debugging tools, and memory dumps.
    /// </summary>
    private HttpRequestMessage CreateRequestWithAuth(HttpMethod method, string url)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(_githubToken))
        {
            request.Headers.Add("Authorization", $"Bearer {_githubToken}");
        }
        return request;
    }

    /// <summary>
    /// Decodes a base64-encoded string.
    /// </summary>
    private static string DecodeBase64(string encodedContent)
    {
        try
        {
            byte[] data = Convert.FromBase64String(encodedContent);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to decode base64 content: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Like <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>, but surfaces a friendly,
    /// actionable message when the failure is a GitHub API rate limit (403/429 with
    /// <c>X-RateLimit-Remaining: 0</c> on the RESPONSE). The previous <c>CheckRateLimit</c> inspected
    /// <c>DefaultRequestHeaders</c> (request headers) for a response-only header, so it never fired.
    /// </summary>
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if ((response.StatusCode == System.Net.HttpStatusCode.Forbidden
                || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            && response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? values)
            && int.TryParse(values.FirstOrDefault(), out int remaining) && remaining == 0)
        {
            throw new Exception(
                "GitHub API rate limit exceeded. Please try again later or set a GitHub token " +
                "(GITHUB_TOKEN) for higher rate limits.");
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Parses a semantic version string for comparison.
    /// Returns a tuple of (major, minor, patch) for sorting.
    /// </summary>
    private static (int, int, int) ParseSemanticVersion(string version)
    {
        string[] parts = version.TrimStart('v').Split('.');
        int major = parts.Length > 0 && int.TryParse(parts[0], out int m) ? m : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int p) ? p : 0;
        return (major, minor, patch);
    }

    /// <summary>
    /// DTO for GitHub Contents API response.
    /// </summary>
    private class GitHubContentResponse
    {
        /// <summary>The file or directory name returned by the Contents API.</summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>The repository-relative path of the requested content.</summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        /// <summary>The Git object identifier associated with this content.</summary>
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        /// <summary>The size of the content in bytes.</summary>
        [JsonPropertyName("size")]
        public int Size { get; set; }

        /// <summary>The content kind reported by GitHub, such as file or dir.</summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>The inline file payload in the encoding reported by GitHub.</summary>
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        /// <summary>The encoding applied to the inline content payload.</summary>
        [JsonPropertyName("encoding")]
        public string? Encoding { get; set; }

        /// <summary>The REST API URL of this content resource.</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        /// <summary>The browser URL of the file or directory on GitHub.</summary>
        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        /// <summary>The Git data API URL of the underlying object.</summary>
        [JsonPropertyName("git_url")]
        public string? GitUrl { get; set; }

        /// <summary>The direct download URL of the file, when available.</summary>
        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        /// <summary>Related REST, Git data, and browser resource links.</summary>
        [JsonPropertyName("_links")]
        public GitHubLinks? Links { get; set; }
    }

    /// <summary>
    /// DTO for GitHub Contents API list response item.
    /// </summary>
    private class GitHubContentItem
    {
        /// <summary>The entry name in a Contents API directory listing.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>The repository-relative path of the directory entry.</summary>
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        /// <summary>The Git object identifier of the listed entry.</summary>
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        /// <summary>The entry size in bytes.</summary>
        [JsonPropertyName("size")]
        public int Size { get; set; }

        /// <summary>The entry kind reported by GitHub, such as file or dir.</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>The inline file payload when included in the response.</summary>
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        /// <summary>The encoding of the optional inline file payload.</summary>
        [JsonPropertyName("encoding")]
        public string? Encoding { get; set; }

        /// <summary>The REST API URL of the listed content.</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        /// <summary>The browser URL of the listed content on GitHub.</summary>
        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        /// <summary>The Git data API URL of the entry's underlying object.</summary>
        [JsonPropertyName("git_url")]
        public string? GitUrl { get; set; }

        /// <summary>The direct file download URL, when available.</summary>
        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        /// <summary>Related REST, Git data, and browser links for the entry.</summary>
        [JsonPropertyName("_links")]
        public GitHubLinks? Links { get; set; }
    }

    /// <summary>
    /// DTO for GitHub API reference response.
    /// </summary>
    private class GitHubRefResponse
    {
        /// <summary>The fully qualified Git reference name.</summary>
        [JsonPropertyName("ref")]
        public string Ref { get; set; } = string.Empty;

        /// <summary>The reference's opaque GitHub node identifier.</summary>
        [JsonPropertyName("node_id")]
        public string? NodeId { get; set; }

        /// <summary>The REST API URL of the reference.</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        /// <summary>The Git object targeted by this reference.</summary>
        [JsonPropertyName("object")]
        public GitHubRefObject? Object { get; set; }
    }

    /// <summary>
    /// DTO for GitHub reference object.
    /// </summary>
    private class GitHubRefObject
    {
        /// <summary>The target Git object's identifier.</summary>
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        /// <summary>The target object kind, such as commit or tag.</summary>
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        /// <summary>The Git data API URL of the target object.</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }

    /// <summary>
    /// DTO for GitHub links.
    /// </summary>
    private class GitHubLinks
    {
        /// <summary>The REST API URL of the linked resource.</summary>
        [JsonPropertyName("self")]
        public string? Self { get; set; }

        /// <summary>The Git data API URL of the linked object.</summary>
        [JsonPropertyName("git")]
        public string? Git { get; set; }

        /// <summary>The browser URL of the linked resource on GitHub.</summary>
        [JsonPropertyName("html")]
        public string? Html { get; set; }
    }
}
