namespace Cop.Lang;

/// <summary>
/// Implemented by data providers that register additional capabilities
/// (e.g., document loaders, file parsers) into the TypeRegistry.
/// Called by the runtime after schema registration.
/// </summary>
public interface ICapabilityProvider
{
    void RegisterCapabilities(TypeRegistry registry, string rootPath);
}
