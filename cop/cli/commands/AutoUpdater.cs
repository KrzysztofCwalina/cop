using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Cop.Cli.Commands;

/// <summary>
/// Automatic update checker that runs on each cop invocation.
/// Checks GitHub for a newer release at most once every 24 hours,
/// and silently applies the update if one is found.
/// </summary>
static class AutoUpdater
{
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    const string RepoOwner = "KrzysztofCwalina";
    const string RepoName = "cop";
    const string GitHubApiBase = "https://api.github.com";
    const string StateFileName = "update-check.json";
    const string LockFileName = "update-check.lock";

    /// <summary>
    /// Attempts a silent auto-update. Returns quickly if no update is needed.
    /// Never throws — all errors are swallowed to avoid disrupting normal operation.
    /// </summary>
    public static void TryAutoUpdate()
    {
        try
        {
            // Opt-out via environment variable
            var noUpdate = Environment.GetEnvironmentVariable("COP_NO_AUTO_UPDATE");
            if (!string.IsNullOrEmpty(noUpdate) && noUpdate != "0")
                return;

            // Skip in CI environments
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
                return;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")))
                return;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
                return;

            TryAutoUpdateCore();
        }
        catch
        {
            // Never fail the main process
        }
    }

    static void TryAutoUpdateCore()
    {
        var stateDir = GetStateDirectory();
        if (stateDir == null) return;

        var stateFile = Path.Combine(stateDir, StateFileName);
        var lockFile = Path.Combine(stateDir, LockFileName);

        // Check if enough time has passed since last check
        var state = ReadState(stateFile);
        if (state.LastCheck.HasValue && DateTime.UtcNow - state.LastCheck.Value < CheckInterval)
            return;

        // Acquire lock file to prevent concurrent updates
        FileStream? lockStream = null;
        try
        {
            lockStream = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch
        {
            // Another process is updating — skip
            return;
        }

        try
        {
            // Re-check state after acquiring lock (another process may have just updated)
            state = ReadState(stateFile);
            if (state.LastCheck.HasValue && DateTime.UtcNow - state.LastCheck.Value < CheckInterval)
                return;

            // Record that we're attempting a check now (prevents retry storms on failure)
            WriteState(stateFile, new UpdateState { LastCheck = DateTime.UtcNow, LatestTag = state.LatestTag });

            // Get current version
            var currentVersion = GetCurrentVersion();
            if (currentVersion == null) return;

            // Check GitHub for latest release
            var (latestTag, downloadUrl) = CheckLatestRelease();
            if (latestTag == null || downloadUrl == null) return;

            // Parse and compare versions
            var latestVersion = ParseTagVersion(latestTag);
            if (latestVersion == null || latestVersion <= currentVersion)
            {
                WriteState(stateFile, new UpdateState { LastCheck = DateTime.UtcNow, LatestTag = latestTag });
                return;
            }

            // Determine install directory — only update if current location is writable
            var installDir = GetAutoUpdateInstallDirectory();
            if (installDir == null) return;

            // Perform the update
            Console.Error.WriteLine($"Updating cop to {latestTag}...");
            var success = PerformUpdate(downloadUrl, installDir);

            if (success)
            {
                InvalidateCachedProviders();
                Console.Error.WriteLine($"Updated. New version takes effect on next run.");
                WriteState(stateFile, new UpdateState { LastCheck = DateTime.UtcNow, LatestTag = latestTag });
            }
        }
        finally
        {
            lockStream?.Dispose();
            try { File.Delete(lockFile); } catch { }
        }
    }

    static (string? tag, string? downloadUrl) CheckLatestRelease()
    {
        var rid = GetCurrentRid();
        if (rid == null) return (null, null);

        var expectedAsset = $"cop-{rid}.zip";

        using var http = new HttpClient();
        http.Timeout = MetadataTimeout;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cop-autoupdater/1.0");

        var releaseUrl = $"{GitHubApiBase}/repos/{RepoOwner}/{RepoName}/releases/latest";

        try
        {
            var response = http.GetAsync(releaseUrl).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return (null, null);

            var json = response.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
            var tagName = json.GetProperty("tag_name").GetString();
            var assets = json.GetProperty("assets");

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

            return (tagName, downloadUrl);
        }
        catch
        {
            return (null, null);
        }
    }

    static bool PerformUpdate(string downloadUrl, string installDir)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var exeName = isWindows ? "cop.exe" : "cop";
        var targetExe = Path.Combine(installDir, exeName);

        using var http = new HttpClient();
        http.Timeout = DownloadTimeout;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("cop-autoupdater/1.0");

        byte[] zipBytes;
        try
        {
            zipBytes = http.GetByteArrayAsync(downloadUrl).GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"cop-autoupdate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using (var zipStream = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(tempDir);
            }

            var newExePath = Path.Combine(tempDir, exeName);
            if (!File.Exists(newExePath)) return false;

            if (isWindows)
            {
                var backupPath = targetExe + ".old";
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                if (File.Exists(targetExe))
                    File.Move(targetExe, backupPath);
                File.Copy(newExePath, targetExe);
                try { File.Delete(backupPath); } catch { }
            }
            else
            {
                if (File.Exists(targetExe))
                    File.Delete(targetExe);
                File.Copy(newExePath, targetExe);
                File.SetUnixFileMode(targetExe,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            // Copy additional files
            foreach (var file in Directory.GetFiles(tempDir))
            {
                var fileName = Path.GetFileName(file);
                if (string.Equals(fileName, exeName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var destPath = Path.Combine(installDir, fileName);
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Copy(file, destPath);
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// For auto-update, only update the current exe's directory if it's writable.
    /// Does not redirect to ~/.cop — that's a manual update decision.
    /// </summary>
    static string? GetAutoUpdateInstallDirectory()
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe)) return null;

        var currentDir = Path.GetDirectoryName(currentExe);
        if (string.IsNullOrEmpty(currentDir)) return null;

        // Check if writable by attempting to open a temp file
        try
        {
            var testFile = Path.Combine(currentDir, $".cop-write-test-{Guid.NewGuid():N}");
            using (File.Create(testFile)) { }
            File.Delete(testFile);
            return currentDir;
        }
        catch
        {
            return null;
        }
    }

    static Version? GetCurrentVersion()
    {
        return typeof(AutoUpdater).Assembly.GetName().Version;
    }

    static Version? ParseTagVersion(string tag)
    {
        // Strip leading 'v' if present
        var versionStr = tag.StartsWith('v') ? tag[1..] : tag;

        // Try standard Version parse first (e.g., "2026.6.5.7")
        if (Version.TryParse(versionStr, out var v))
            return v;

        // Handle tag format "2026.06.05h" (YYYY.MM.DDletter where letter = build number a=1,b=2,...)
        var lastDot = versionStr.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < versionStr.Length - 1)
        {
            var dayPart = versionStr[(lastDot + 1)..];
            // Find where digits end and letter suffix begins
            int digitEnd = 0;
            while (digitEnd < dayPart.Length && char.IsDigit(dayPart[digitEnd]))
                digitEnd++;

            if (digitEnd > 0 && digitEnd < dayPart.Length && char.IsLetter(dayPart[digitEnd]))
            {
                var numericPart = versionStr[..lastDot] + "." + dayPart[..digitEnd];
                var letter = char.ToLower(dayPart[digitEnd]);
                int buildNum = letter - 'a' + 1;
                if (Version.TryParse(numericPart, out var baseVersion))
                    return new Version(baseVersion.Major, baseVersion.Minor, baseVersion.Build, buildNum);
            }
        }

        return null;
    }

    static string? GetStateDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cop");
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return null;
        }
    }

    static UpdateState ReadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return new UpdateState();
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            DateTime? lastCheck = null;
            if (root.TryGetProperty("lastCheck", out var lc) && lc.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(lc.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    lastCheck = dt;
            }

            string? latestTag = null;
            if (root.TryGetProperty("latestTag", out var lt) && lt.ValueKind == JsonValueKind.String)
                latestTag = lt.GetString();

            return new UpdateState { LastCheck = lastCheck, LatestTag = latestTag };
        }
        catch
        {
            return new UpdateState();
        }
    }

    static void WriteState(string path, UpdateState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                lastCheck = state.LastCheck?.ToString("O"),
                latestTag = state.LatestTag
            });
            // Atomic write: write to temp then replace
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch { }
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

        string? arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null
        };

        return arch != null ? $"{os}-{arch}" : null;
    }

    record UpdateState
    {
        public DateTime? LastCheck { get; init; }
        public string? LatestTag { get; init; }
    }

    /// <summary>
    /// After cop.exe is updated, cached provider DLLs compiled against the old version
    /// will cause TypeLoadException. Clear them so they're re-downloaded from feeds.
    /// </summary>
    static void InvalidateCachedProviders()
    {
        try
        {
            var packagesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cop", "packages");
            if (!Directory.Exists(packagesDir)) return;

            foreach (var pkgDir in Directory.GetDirectories(packagesDir))
            {
                var libDir = Path.Combine(pkgDir, "lib");
                if (Directory.Exists(libDir))
                {
                    try { Directory.Delete(libDir, recursive: true); }
                    catch { }
                }
            }
        }
        catch { }
    }
}
