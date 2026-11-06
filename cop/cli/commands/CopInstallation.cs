// Locates installed Cop executables and installation assets.

namespace Cop.Cli.Commands;

internal static class CopInstallation
{
    internal static string DirectoryPath(string userHome) => Path.Combine(userHome, ".cop");

    internal static string InstallExecutable(string source, string userHome)
    {
        string directory = DirectoryPath(userHome);
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, OperatingSystem.IsWindows() ? "cop.exe" : "cop");
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target), comparison))
        {
            string staged = target + ".new";
            string backup = target + ".old";
            File.Copy(source, staged, overwrite: true);
            try
            {
                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }

                if (File.Exists(target))
                {
                    File.Move(target, backup);
                }

                try
                {
                    File.Move(staged, target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (File.Exists(backup) && !File.Exists(target))
                    {
                        File.Move(backup, target);
                    }

                    throw;
                }
                DeleteBackup(backup);
            }
            finally
            {
                if (File.Exists(staged))
                {
                    File.Delete(staged);
                }
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        string legacy = Path.Combine(userHome, ".dotnet", "tools", "cop.exe");
        string sourceBundle = Path.Combine(Path.GetDirectoryName(source)!, "Cop.RustFrontend.Bundle.zip");
        string targetBundle = Path.Combine(directory, "Cop.RustFrontend.Bundle.zip");
        if (OperatingSystem.IsWindows() && !File.Exists(sourceBundle) && File.Exists(legacy))
        {
            sourceBundle = Path.Combine(Path.GetDirectoryName(legacy)!, "Cop.RustFrontend.Bundle.zip");
        }

        if (File.Exists(sourceBundle) && !File.Exists(targetBundle))
        {
            File.Copy(sourceBundle, targetBundle);
        }

        if (OperatingSystem.IsWindows() && File.Exists(legacy))
        {
            string backup = legacy + ".old";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(legacy, backup);
            DeleteBackup(backup);
            Console.WriteLine($"Removed legacy Cop executable: {legacy}");
        }
        return target;
    }

    internal static string WindowsPath(string? path, string userHome)
    {
        string canonical = DirectoryPath(userHome);
        IEnumerable<string> entries = (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry =>
            {
                string expanded = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (expanded.Equals(canonical, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string? parent = Path.GetDirectoryName(expanded);
                string name = Path.GetFileName(expanded);
                return !((name.Equals("win-x64", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("win-arm64", StringComparison.OrdinalIgnoreCase))
                    && string.Equals(Path.GetFileName(parent), "install", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(expanded, "cop.exe")));
            });
        return string.Join(';', new[] { canonical }.Concat(entries));
    }

    internal static void ConfigurePath(string userHome)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine($"Add '{DirectoryPath(userHome)}' to PATH and remove other Cop installation directories.");
            return;
        }

        string? userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        string updated = WindowsPath(userPath, userHome);
        if (updated != userPath)
        {
            Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);
            Console.WriteLine("Updated user PATH. Open a new terminal to use the installed Cop.");
        }
        Environment.SetEnvironmentVariable("PATH", WindowsPath(Environment.GetEnvironmentVariable("PATH"), userHome));
    }

    private static void DeleteBackup(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: Old executable '{path}' is still in use; it is no longer on PATH. {ex.Message}");
        }
    }
}
