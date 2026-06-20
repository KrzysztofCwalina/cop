using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// C#-specific runtime bindings. Extends the shared <see cref="CodeBindings"/> with the
/// <c>CSharpType</c> cop type so language-specific fields (records, partial, …) resolve to
/// real data when a check narrows with <c>:asCSharp</c>.
///
/// The shared <see cref="CodeBindings"/> / <c>CodeSchema</c> stay untouched — C# specifics
/// live only here, keyed to the <see cref="CSharpTypeDeclaration"/> CLR type. The runtime
/// selects the <c>CSharpType</c> accessor set per item by CLR-type mapping, so a Types
/// collection declared as <c>[Type]</c> still surfaces these fields for C# items.
/// </summary>
public static class CSharpBindings
{
    public static RuntimeBindings Build()
    {
        var bindings = CodeBindings.Build();

        // Only C# emits CSharpTypeDeclaration, so this mapping never affects other languages.
        bindings.ClrTypeMappings[typeof(CSharpTypeDeclaration)] = "CSharpType";

        // CSharpType accessors = all base Type accessors (which cast to the TypeDeclaration
        // base, so they work on the subclass too) + the C#-only fields.
        var csharpAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Type"])
        {
            ["IsRecord"] = o => (object)((CSharpTypeDeclaration)o).IsRecord,
            ["IsPartial"] = o => (object)((CSharpTypeDeclaration)o).IsPartial,
        };
        bindings.Accessors["CSharpType"] = csharpAccessors;

        return bindings;
    }
}
