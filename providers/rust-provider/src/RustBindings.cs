using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Rust-specific runtime bindings. Extends the shared <see cref="CodeBindings"/> with the
/// <c>RustType</c> cop type so Rust-only fields (traits, impl blocks, unsafe) resolve to real
/// data when a check narrows with <c>:asRust</c>. The shared CodeBindings/CodeSchema stay
/// untouched — Rust specifics live only here, keyed to the <see cref="RustTypeDeclaration"/>
/// CLR type, which the runtime selects per item by CLR-type mapping.
/// </summary>
public static class RustBindings
{
    public static RuntimeBindings Build()
    {
        var bindings = CodeBindings.Build();

        // Only Rust emits RustTypeDeclaration, so this mapping never affects other languages.
        bindings.ClrTypeMappings[typeof(RustTypeDeclaration)] = "RustType";

        // RustType accessors = all base Type accessors (which cast to the TypeDeclaration base,
        // so they work on the subclass too) + the Rust-only fields.
        var rustAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Type"])
        {
            ["IsTrait"] = o => (object)((RustTypeDeclaration)o).IsTrait,
            ["IsImpl"] = o => (object)((RustTypeDeclaration)o).IsImpl,
            ["IsUnsafe"] = o => (object)((RustTypeDeclaration)o).IsUnsafe,
        };
        bindings.Accessors["RustType"] = rustAccessors;

        return bindings;
    }
}
