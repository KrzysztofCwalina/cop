using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Cop.Cli.Commands;

static class UpdateCommand
{
    const string RepoOwner = "KrzysztofCwalina";
    const string RepoName = "cop";
    const string GitHubApiBase = "https://api.github.com";

    public static int Execute()
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    static async Task<int> ExecuteAsync()
    {
        var rid = GetCurrentRid();
        if (rid == null)
        {
            Console.Error.WriteLine("Error: Could not determine platform. Supported: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64");
            return 1;
        }

        var expectedAsset = $"cop-{rid}.zip";
        Console.WriteLine($"Platform: {rid}");
        Console.WriteLine("Checking for updates...");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cop-updater/1.0");

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
        var tagName = releaseJson.GetProperty("tag_name").GetString();
        var assets = releaseJson.GetProperty("assets");

        // Find matching asset
        string? downloadUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, expectedAsset, StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (downloadUrl == null)
        {
            Console.Error.WriteLine($"Error: Release {tagName} does not have an asset for {rid}.");
            Console.Error.WriteLine($"Available assets:");
            foreach (var asset in assets.EnumerateArray())
            {
                Console.Error.WriteLine($"  {asset.GetProperty("name").GetString()}");
            }
            return 1;
        }

        Console.WriteLine($"Downloading {tagName} ({expectedAsset})...");

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

        Console.WriteLine($"Downloaded {zipBytes.Length / (1024 * 1024)}MB. Installing...");

        // Determine current exe path
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe))
        {
            Console.Error.WriteLine("Error: Could not determine current executable path.");
            return 1;
        }

        var installDir = Path.GetDirectoryName(currentExe)!;
        var exeName = Path.GetFileName(currentExe);

        // Extract zip to temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), $"cop-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using (var zipStream = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(tempDir);
            }

            // Find the main executable in the extracted files
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var newExePath = isWindows
                ? Path.Combine(tempDir, "cop.exe")
                : Path.Combine(tempDir, "cop");

            if (!File.Exists(newExePath))
            {
                Console.Error.WriteLine($"Error: Expected executable not found in archive.");
                return 1;
            }

            if (isWindows)
            {
                // Windows: can't overwrite running exe. Rename current, copy new, schedule old deletion.
                var backupPath = currentExe + ".old";
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Move(currentExe, backupPath);
                File.Copy(newExePath, currentExe);
                // Try to delete backup; if locked, it'll be cleaned up next time
                try { File.Delete(backupPath); } catch { }
            }
            else
            {
                // Unix: can overwrite via delete + copy (inode-based, running process keeps old)
                File.Delete(currentExe);
                File.Copy(newExePath, currentExe);
                // Set executable permissions
                File.SetUnixFileMode(currentExe,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            // Copy any additional files (e.g., native libs) but skip the main exe
            foreach (var file in Directory.GetFiles(tempDir))
            {
                var fileName = Path.GetFileName(file);
                if (string.Equals(fileName, exeName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var destPath = Path.Combine(installDir, fileName);
                if (File.Exists(destPath))
                    File.Delete(destPath);
                File.Copy(file, destPath);
            }

            Console.WriteLine($"Updated to {tagName}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during installation: {ex.Message}");
            return 1;
        }
        finally
        {
            // Clean up temp directory
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    static string? GetCurrentRid()
    {
        string os;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            os = "win";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            os = "linux";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            os = "osx";
        else
            return null;

        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null!
        };

        if (arch == null) return null;
        return $"{os}-{arch}";
    }
}
