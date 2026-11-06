// Installs or updates the Cop Visual Studio Code extension.

using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cop.Cli.Commands;

static class VscodeCommand
{
    const string RepoOwner = "KrzysztofCwalina";
    const string RepoName = "cop";
    const string GitHubApiBase = "https://api.github.com";
    const string AssetName = "cop-vscode.zip";

    /// <summary>Downloads and installs the latest Visual Studio Code extension.</summary>
    public static async Task<int> ExecuteAsync()
    {
        Console.WriteLine("Installing VS Code extension...");

        using HttpClient http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cop-vscode/1.0");

        // Get latest release
        string releaseUrl = $"{GitHubApiBase}/repos/{RepoOwner}/{RepoName}/releases/latest";
        HttpResponseMessage releaseResponse;
        try
        {
            releaseResponse = await http.GetAsync(releaseUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Error: Could not reach GitHub: {ex.Message}");
            return 1;
        }

        using HttpResponseMessage responseLifetime = releaseResponse;
        if (!releaseResponse.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Error: GitHub API returned {(int)releaseResponse.StatusCode}. Check your network connection.");
            return 1;
        }

        JsonElement releaseJson = await releaseResponse.Content.ReadFromJsonAsync<JsonElement>();
        string tagName = releaseJson.GetProperty("tag_name").GetString() ?? "unknown";
        JsonElement assets = releaseJson.GetProperty("assets");

        // Find the VS Code extension asset
        string? downloadUrl = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (downloadUrl == null)
        {
            Console.Error.WriteLine($"Error: Release {tagName} does not include {AssetName}.");
            Console.Error.WriteLine("The VS Code extension may not be available in this release yet.");
            return 1;
        }

        // Download the zip
        byte[] zipBytes;
        try
        {
            zipBytes = await http.GetByteArrayAsync(downloadUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Error: Download failed: {ex.Message}");
            return 1;
        }

        // Read version from package.json inside the zip
        string version = "0.0.0";
        using (MemoryStream zipStream = new MemoryStream(zipBytes))
        using (ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            ZipArchiveEntry? packageEntry = archive.GetEntry("package.json");
            if (packageEntry != null)
            {
                using StreamReader reader = new StreamReader(packageEntry.Open());
                using JsonDocument packageJson = JsonDocument.Parse(await reader.ReadToEndAsync());
                if (packageJson.RootElement.TryGetProperty("version", out JsonElement ver))
                {
                    version = ver.GetString() ?? version;
                }
            }
        }

        // Install to ~/.vscode/extensions/cop.cop-language-<version>
        string extensionsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vscode", "extensions");
        string targetDir = Path.Combine(extensionsDir, $"cop.cop-language-{version}");

        // Clean previous installation
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }

        Directory.CreateDirectory(targetDir);

        // Extract
        using (MemoryStream zipStream = new MemoryStream(zipBytes))
        using (ZipArchive archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(targetDir);
        }

        Console.WriteLine($"Installed cop VS Code extension v{version}");
        Console.WriteLine($"  -> {targetDir}");
        Console.WriteLine("Restart VS Code to activate syntax highlighting for .cop files.");
        return 0;
    }
}
