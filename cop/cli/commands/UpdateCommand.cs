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

        // Determine install directory — prefer ~/.cop, fall back to current exe location
        var installDir = GetInstallDirectory();
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var exeName = isWindows ? "cop.exe" : "cop";
        var targetExe = Path.Combine(installDir, exeName);

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
            var newExePath = Path.Combine(tempDir, exeName);

            if (!File.Exists(newExePath))
            {
                Console.Error.WriteLine($"Error: Expected executable not found in archive.");
                return 1;
            }

            // Ensure install directory exists
            Directory.CreateDirectory(installDir);

            if (isWindows)
            {
                // Windows: can't overwrite running exe. Rename current, copy new, schedule old deletion.
                var backupPath = targetExe + ".old";
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                if (File.Exists(targetExe))
                    File.Move(targetExe, backupPath);
                File.Copy(newExePath, targetExe);
                // Try to delete backup; if locked, it'll be cleaned up next time
                try { File.Delete(backupPath); } catch { }
            }
            else
            {
                // Unix: can overwrite via delete + copy (inode-based, running process keeps old)
                if (File.Exists(targetExe))
                    File.Delete(targetExe);
                File.Copy(newExePath, targetExe);
                // Set executable permissions
                File.SetUnixFileMode(targetExe,
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
            Console.WriteLine($"Installed at: {installDir}");

            // Refresh package cache — seed packages may have new types/predicates
            RefreshPackageCache();

            // Warn if a shadowing copy exists on PATH
            WarnAboutShadowingInstalls(installDir, exeName);

            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: Cannot write to '{installDir}' (permission denied).");
            Console.Error.WriteLine($"The install location requires elevated privileges.");
            var userDir = GetUserInstallDirectory();
            Console.Error.WriteLine($"Suggestion: install cop to '{userDir}' (user-writable) and add it to PATH.");
            return 1;
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

    /// <summary>
    /// Returns the preferred install directory:
    /// - If running from ~/.cop, use that.
    /// - If running from a non-writable location (Program Files), use ~/.cop instead.
    /// - Otherwise, use the current executable's directory.
    /// </summary>
    private static string GetInstallDirectory()
    {
        var userDir = GetUserInstallDirectory();

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe))
            return userDir;

        var currentDir = Path.GetDirectoryName(currentExe)!;

        // If already running from ~/.cop, stay there
        if (currentDir.Equals(userDir, StringComparison.OrdinalIgnoreCase))
            return userDir;

        // Check if current location is writable
        if (IsAdminOnlyPath(currentDir))
        {
            Console.Error.WriteLine($"Warning: Current install at '{currentDir}' requires admin to update.");
            Console.Error.WriteLine($"Updating to user-writable location: {userDir}");
            return userDir;
        }

        // Use current location if writable
        return currentDir;
    }

    private static string GetUserInstallDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cop");

    /// <summary>
    /// Checks if a path is in a known admin-only location (Program Files, etc.)
    /// </summary>
    private static bool IsAdminOnlyPath(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return (!string.IsNullOrEmpty(programFiles) && path.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(programFilesX86) && path.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Warns if there are other copies of cop on PATH that might shadow the updated one.
    /// </summary>
    private static void WarnAboutShadowingInstalls(string installDir, string exeName)
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var shadows = new List<string>();
        bool foundInstallDir = false;

        foreach (var dir in pathDirs)
        {
            var normalized = Path.GetFullPath(dir.Trim());
            if (normalized.Equals(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase))
            {
                foundInstallDir = true;
                continue;
            }

            var candidate = Path.Combine(normalized, exeName);
            if (File.Exists(candidate))
            {
                if (!foundInstallDir)
                    shadows.Add(normalized); // ahead on PATH = shadows our install
            }
        }

        if (shadows.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Warning: Other cop installations found ahead on PATH (may shadow this update):");
            foreach (var s in shadows)
                Console.Error.WriteLine($"  {Path.Combine(s, exeName)}");
            Console.Error.WriteLine($"Consider removing them or reordering PATH so '{installDir}' comes first.");
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

    /// <summary>
    /// Deletes the package cache so seed packages are re-downloaded on next run.
    /// New exe versions may include updated type definitions or predicates in packages.
    /// </summary>
    private static void RefreshPackageCache()
    {
        var cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cop", "packages");

        if (!Directory.Exists(cachePath))
            return;

        try
        {
            Directory.Delete(cachePath, recursive: true);
            Console.WriteLine("Refreshed package cache (will re-download on next run)");
        }
        catch
        {
            // Non-fatal: packages may be locked. They'll be stale but won't crash.
            Console.Error.WriteLine("Warning: Could not refresh package cache. Run 'cop package restore' to update packages.");
        }
    }
}
