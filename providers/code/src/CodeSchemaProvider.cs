// Registers the shared code-model schema and interpreter bindings.

using Cop.Core;

namespace Cop.Providers;

/// <summary>
/// Minimal provider that registers code type schema and runtime bindings
/// without parsing any source files. Used as a built-in to ensure code types
/// (Type, Statement, Method, etc.) are always available in the type system.
/// Actual data comes from language-specific providers (CSharp, Python, JavaScript).
/// </summary>
public class CodeSchemaProvider : DataProvider
{

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => CodeSchema.GetTypesOnlyJson();

    /// <inheritdoc />
    public override RuntimeBindings? GetRuntimeBindings() => CodeBindings.Build();

    /// <inheritdoc />
    public override ValueTask<object?> QueryAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(null);
    }

    /// <inheritdoc />
    public override string ToString() => "CodeSchemaProvider";
}
