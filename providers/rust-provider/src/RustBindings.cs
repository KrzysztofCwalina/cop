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
            ["IsUnion"] = o => (object)((RustTypeDeclaration)o).IsUnion,
            ["IsTupleStruct"] = o => (object)((RustTypeDeclaration)o).IsTupleStruct,
            ["IsUnitStruct"] = o => (object)((RustTypeDeclaration)o).IsUnitStruct,
            ["IsNegativeImpl"] = o => (object)((RustTypeDeclaration)o).IsNegativeImpl,
        };
        bindings.Accessors["RustType"] = rustAccessors;

        bindings.ClrTypeMappings[typeof(RustMethodDeclaration)] = "RustMethod";
        var rustMethodAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Method"])
        {
            ["IsUnsafe"] = o => (object)((RustMethodDeclaration)o).IsUnsafe,
            ["IsConst"] = o => (object)((RustMethodDeclaration)o).IsConst,
            ["IsExtern"] = o => (object)((RustMethodDeclaration)o).IsExtern,
        };
        bindings.Accessors["RustMethod"] = rustMethodAccessors;

        bindings.ClrTypeMappings[typeof(RustStatementInfo)] = "RustStatement";
        var rustStatementAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Statement"])
        {
            ["IsMacroCall"] = o => (object)((RustStatementInfo)o).IsMacroCall,
            ["IsPanic"] = o => (object)((RustStatementInfo)o).IsPanic,
        };
        bindings.Accessors["RustStatement"] = rustStatementAccessors;

        return bindings;
    }
}
