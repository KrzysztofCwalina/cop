// Exposes Go type, method, and statement facts to the Cop runtime.

using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Go-specific runtime bindings: the shared <see cref="CodeBindings"/> plus Go accessor sets,
/// keyed to the Go-specific CLR subtypes.
/// </summary>
public static class GoBindings
{
    /// <summary>
    /// Extends shared bindings with Go declaration and statement accessors.
    /// </summary>
    public static RuntimeBindings Build()
    {
        RuntimeBindings bindings = CodeBindings.Build();

        bindings.ClrTypeMappings[typeof(GoTypeDeclaration)] = "GoType";

        Dictionary<string, Func<object, object?>> goAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Type"])
        {
            ["IsInterface"] = o => (object)((GoTypeDeclaration)o).IsInterface,
            ["IsStruct"] = o => (object)((GoTypeDeclaration)o).IsStruct,
            ["IsTypeAlias"] = o => (object)((GoTypeDeclaration)o).IsTypeAlias,
            ["HasStructTags"] = o => (object)((GoTypeDeclaration)o).HasStructTags,
            ["HasUnionTypeSet"] = o => (object)((GoTypeDeclaration)o).HasUnionTypeSet,
            ["HasUnderlyingTypeTerms"] = o => (object)((GoTypeDeclaration)o).HasUnderlyingTypeTerms,
        };
        bindings.Accessors["GoType"] = goAccessors;

        bindings.ClrTypeMappings[typeof(GoMethodDeclaration)] = "GoMethod";

        Dictionary<string, Func<object, object?>> goMethodAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Method"])
        {
            ["IsPointerReceiver"] = o => (object)((GoMethodDeclaration)o).IsPointerReceiver,
            ["HasNamedReturns"] = o => (object)((GoMethodDeclaration)o).HasNamedReturns,
            ["IsVariadic"] = o => (object)((GoMethodDeclaration)o).IsVariadic,
            ["IsGeneric"] = o => (object)((GoMethodDeclaration)o).IsGeneric,
        };
        bindings.Accessors["GoMethod"] = goMethodAccessors;

        bindings.ClrTypeMappings[typeof(GoStatementInfo)] = "GoStatement";

        Dictionary<string, Func<object, object?>> goStatementAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Statement"])
        {
            ["IsDefer"] = o => (object)((GoStatementInfo)o).IsDefer,
            ["IsGoroutine"] = o => (object)((GoStatementInfo)o).IsGoroutine,
            ["IsSelect"] = o => (object)((GoStatementInfo)o).IsSelect,
            ["IsRangeLoop"] = o => (object)((GoStatementInfo)o).IsRangeLoop,
            ["IsTypeSwitch"] = o => (object)((GoStatementInfo)o).IsTypeSwitch,
        };
        bindings.Accessors["GoStatement"] = goStatementAccessors;

        return bindings;
    }
}
