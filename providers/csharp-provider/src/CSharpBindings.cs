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
            ["IsRecordStruct"] = o => (object)((CSharpTypeDeclaration)o).IsRecordStruct,
            ["IsReadOnly"] = o => (object)((CSharpTypeDeclaration)o).IsReadOnly,
            ["IsRef"] = o => (object)((CSharpTypeDeclaration)o).IsRef,
            ["IsFileLocal"] = o => (object)((CSharpTypeDeclaration)o).IsFileLocal,
            ["IsPartial"] = o => (object)((CSharpTypeDeclaration)o).IsPartial,
            ["HasPrimaryConstructor"] = o => (object)((CSharpTypeDeclaration)o).HasPrimaryConstructor,
            ["IsGeneric"] = o => (object)((CSharpTypeDeclaration)o).IsGeneric,
        };
        bindings.Accessors["CSharpType"] = csharpAccessors;

        // Only C# emits CSharpMethodDeclaration, so this mapping never affects other languages.
        bindings.ClrTypeMappings[typeof(CSharpMethodDeclaration)] = "CSharpMethod";

        var csharpMethodAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Method"])
        {
            ["IsExtension"] = o => (object)((CSharpMethodDeclaration)o).IsExtension,
            ["IsPartial"] = o => (object)((CSharpMethodDeclaration)o).IsPartial,
            ["IsUnsafe"] = o => (object)((CSharpMethodDeclaration)o).IsUnsafe,
            ["IsExtern"] = o => (object)((CSharpMethodDeclaration)o).IsExtern,
            ["IsExpressionBodied"] = o => (object)((CSharpMethodDeclaration)o).IsExpressionBodied,
            ["IsGeneric"] = o => (object)((CSharpMethodDeclaration)o).IsGeneric,
        };
        bindings.Accessors["CSharpMethod"] = csharpMethodAccessors;

        // Only C# emits CSharpStatementInfo, so this mapping never affects other languages.
        bindings.ClrTypeMappings[typeof(CSharpStatementInfo)] = "CSharpStatement";

        var csharpStatementAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Statement"])
        {
            ["IsLock"] = o => (object)((CSharpStatementInfo)o).IsLock,
            ["IsUnsafe"] = o => (object)((CSharpStatementInfo)o).IsUnsafe,
            ["IsFixed"] = o => (object)((CSharpStatementInfo)o).IsFixed,
            ["IsChecked"] = o => (object)((CSharpStatementInfo)o).IsChecked,
            ["IsUnchecked"] = o => (object)((CSharpStatementInfo)o).IsUnchecked,
            ["IsYield"] = o => (object)((CSharpStatementInfo)o).IsYield,
            ["IsGoto"] = o => (object)((CSharpStatementInfo)o).IsGoto,
            ["IsAwaitForeach"] = o => (object)((CSharpStatementInfo)o).IsAwaitForeach,
            ["HasCatchFilter"] = o => (object)((CSharpStatementInfo)o).HasCatchFilter,
        };
        bindings.Accessors["CSharpStatement"] = csharpStatementAccessors;

        return bindings;
    }
}
