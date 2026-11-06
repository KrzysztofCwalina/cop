// Wraps a provider-owned document with its path and language identity.

namespace Cop.Lang;

/// <summary>
/// A general-purpose document that the interpreter processes.
/// Documents are opaque to the interpreter — data providers populate them
/// and register collection extractors that know how to extract typed items.
/// </summary>
public record Document(string Path, string Language, object Inner)
{
    /// <summary>Returns the provider-owned document as the requested CLR type.</summary>
    /// <typeparam name="T">Expected CLR type of the wrapped document.</typeparam>
    /// <returns>The wrapped document, cast to the requested type.</returns>
    public T As<T>() => (T)Inner;
}
