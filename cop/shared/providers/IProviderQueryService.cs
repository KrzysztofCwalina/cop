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
    /// <param name="providerName">Provider namespace (e.g., "csharp", "files")</param>
    /// <param name="collectionName">Collection name (e.g., "Types", "Files")</param>
    /// <param name="pathOverride">Path to scan (relative to invocation directory or absolute)</param>
    /// <returns>Collection items, or empty list if the path is invalid or provider fails.</returns>
    List<object> Query(string providerName, string collectionName, string pathOverride);

    /// <summary>
    /// Queries a provider with a ProviderQuery and returns the result as a CopValue.
    /// If the provider returns a single collection, returns it as a CopList.
    /// If the provider returns multiple collections, returns a CopObject with named fields.
    /// </summary>
    Cop.Lang.Interpreter.CopValue QueryProvider(string providerName, Cop.Core.ProviderQuery query);
}
