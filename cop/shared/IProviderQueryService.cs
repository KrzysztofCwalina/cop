namespace Cop.Lang;

/// <summary>
/// Interface for querying providers with path overrides at evaluation time.
/// Implemented by the runtime's ProviderQueryService.
/// </summary>
public interface IProviderQueryService
{
    /// <summary>
    /// Queries a collection from a provider with a path override.
    /// </summary>
    /// <param name="providerName">Provider namespace (e.g., "csharp", "filesystem")</param>
    /// <param name="collectionName">Collection name (e.g., "Types", "Files")</param>
    /// <param name="pathOverride">Path to scan (relative to invocation directory or absolute)</param>
    /// <returns>Collection items, or empty list if the path is invalid or provider fails.</returns>
    List<object> Query(string providerName, string collectionName, string pathOverride);
}
