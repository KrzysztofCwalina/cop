using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Cop.Cli.Commands;

static class UpdateCommand
{
    const string RepoOwner = "KrzysztofCwalina";
    const string RepoName = "cop";
    const string GitHubApiBase = "https://api.github.com";

    public static int Execute(bool force = false, bool local = false)
    {
        if (local)
            return InstallLocal();
        return ExecuteAsync(force).GetAwaiter().GetResult();
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty Assembly.Location identifies the required single-file executable.")]
    private static int InstallLocal()
    {
        try
        {
            var source = Environment.ProcessPath ?? throw new IOException("Cannot locate the running Cop executable.");
            if (!Path.GetFileNameWithoutExtension(source).Equals("cop", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location))
                throw new IOException("Local installation requires the published Cop executable, not a dotnet host.");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var target = CopInstallation.InstallExecutable(source, home);
            CopInstallation.ConfigurePath(home);
            Console.WriteLine($"Installed at: {target}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"Error installing Cop: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> ExecuteAsync(bool force)
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

        // Already on the latest (or a newer local build)? Don't re-download — just say so.
        // `--force` reinstalls anyway (e.g. to repair a corrupted install, or to verify a release).
        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
        if (!force && IsUpToDate(currentVersion, tagName))
        {
            Console.WriteLine($"cop is already up to date ({currentVersion}).");
            var installed = InstallLocal();
            if (installed != 0)
                return installed;
            Console.WriteLine("Use 'cop update --force' to reinstall.");
            return 0;
        }

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

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var installDir = CopInstallation.DirectoryPath(home);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var exeName = isWindows ? "cop.exe" : "cop";

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

            CopInstallation.InstallExecutable(newExePath, home);

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

            CopInstallation.ConfigurePath(home);
            Console.WriteLine($"Updated to {tagName}");
            Console.WriteLine($"Installed at: {installDir}");

            Console.WriteLine("Packages are restored from the matching release into a version-specific cache.");

            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: Cannot write to '{installDir}' (permission denied).");
            Console.Error.WriteLine("Check the permissions on your Cop user directory.");
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
    /// True when the running build is already at or above the latest released version, so there is
    /// nothing to install. Conservative: if either version can't be determined it returns false, so
    /// the normal download/install path runs (better to reinstall than to wrongly skip an update).
    /// </summary>
    internal static bool IsUpToDate(Version? current, string? latestTag)
        => current is not null
           && VersionNotifier.ParseVersion(latestTag) is { } latest
           && current >= latest;

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
