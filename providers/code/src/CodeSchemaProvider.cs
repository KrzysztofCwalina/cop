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
    public override DataFormat SupportedFormats => DataFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema() => CodeSchema.GetJson();

    public override RuntimeBindings? GetRuntimeBindings() => CodeBindings.Build();

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query) => new();

    public override string ToString() => "CodeSchemaProvider";
}
