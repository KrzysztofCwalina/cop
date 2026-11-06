// Holds parsed REPL state and lazily initialized provider data.

using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Parsed state and provider services for an interactive REPL session.
/// </summary>
public class ReplContext : IAsyncDisposable
{
    /// <summary>Parsed source modules available to the session.</summary>
    public List<Engine.ParsedModule> Modules { get; }

    /// <summary>Declared types and provider-backed collections.</summary>
    public TypeRegistry TypeRegistry { get; }

    /// <summary>Root directory analyzed by providers.</summary>
    public string RootPath { get; }

    /// <summary>Directory containing the session's Cop scripts.</summary>
    public string ScriptsDir { get; }

    /// <summary>External providers discovered through imports.</summary>
    public List<(string Dir, PackageMetadata Meta)> ProviderPackages { get; }

    /// <summary>Whether provider data has been initialized for this session.</summary>
    public bool HasLoadedProviders { get; set; }

    /// <summary>Nonfatal diagnostics accumulated while preparing the session.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Number of script files considered, including value-only files.</summary>
    public int TotalFileCount { get; init; }

    /// <summary>Service for executing path-scoped provider queries.</summary>
    public ProviderQueryService QueryService { get; set; }

    /// <summary>Creates a session context from parsed scripts and provider metadata.</summary>
    public ReplContext(List<Engine.ParsedModule> modules, TypeRegistry typeRegistry,
        string rootPath, string scriptsDir,
        List<(string Dir, PackageMetadata Meta)> providerPackages)
    {
        Modules = modules;
        TypeRegistry = typeRegistry;
        RootPath = rootPath;
        ScriptsDir = scriptsDir;
        ProviderPackages = providerPackages;
        TotalFileCount = modules.Count;
        QueryService = new ProviderQueryService(
            Directory.GetCurrentDirectory(), Engine.ExcludedDirectoryNames);
    }

    /// <summary>Releases external providers retained for interactive queries.</summary>
    public ValueTask DisposeAsync() => QueryService.DisposeAsync();
}
