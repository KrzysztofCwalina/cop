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

    public static int Execute()
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    static async Task<int> ExecuteAsync()
    {
        Console.WriteLine("Installing VS Code extension...");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cop-vscode/1.0");

        // Get latest release
        var releaseUrl = $"{GitHubApiBase}/repos/{RepoOwner}/{RepoName}/releases/latest";
        HttpResponseMessage releaseResponse;
        try
        {
            releaseResponse = await http.GetAsync(releaseUrl);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Could not reach GitHub: {ex.Message}");
            return 1;
        }

        if (!releaseResponse.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Error: GitHub API returned {(int)releaseResponse.StatusCode}. Check your network connection.");
            return 1;
        }

        var releaseJson = await releaseResponse.Content.ReadFromJsonAsync<JsonElement>();
        var tagName = releaseJson.GetProperty("tag_name").GetString() ?? "unknown";
        var assets = releaseJson.GetProperty("assets");

        // Find the VS Code extension asset
        string? downloadUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Download failed: {ex.Message}");
            return 1;
        }

        // Read version from package.json inside the zip
        string version = "0.0.0";
        using (var zipStream = new MemoryStream(zipBytes))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            var packageEntry = archive.GetEntry("package.json");
            if (packageEntry != null)
            {
                using var reader = new StreamReader(packageEntry.Open());
                var packageJson = JsonDocument.Parse(await reader.ReadToEndAsync());
                if (packageJson.RootElement.TryGetProperty("version", out var ver))
                    version = ver.GetString() ?? version;
            }
        }

        // Install to ~/.vscode/extensions/cop.cop-language-<version>
        var extensionsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vscode", "extensions");
        var targetDir = Path.Combine(extensionsDir, $"cop.cop-language-{version}");

        // Clean previous installation
        if (Directory.Exists(targetDir))
            Directory.Delete(targetDir, recursive: true);
        Directory.CreateDirectory(targetDir);

        // Extract
        using (var zipStream = new MemoryStream(zipBytes))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(targetDir);
        }

        Console.WriteLine($"Installed cop VS Code extension v{version}");
        Console.WriteLine($"  -> {targetDir}");
        Console.WriteLine("Restart VS Code to activate syntax highlighting for .cop files.");
        return 0;
    }
}
