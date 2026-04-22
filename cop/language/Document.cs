namespace Cop.Lang;

/// <summary>
/// A general-purpose document that the interpreter processes.
/// Documents are opaque to the interpreter — data providers populate them
/// and register collection extractors that know how to extract typed items.
/// </summary>
public record Document(string Path, string Language, object Inner)
{
    public T As<T>() => (T)Inner;
}
