using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// JavaScript-specific runtime bindings: the shared <see cref="CodeBindings"/> plus the
/// <c>JavaScriptType</c> accessor set, keyed to <see cref="JavaScriptTypeDeclaration"/>.
/// </summary>
public static class JavaScriptBindings
{
    public static RuntimeBindings Build()
    {
        var bindings = CodeBindings.Build();

        bindings.ClrTypeMappings[typeof(JavaScriptTypeDeclaration)] = "JavaScriptType";

        var jsAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Type"])
        {
            ["IsExported"] = o => (object)((JavaScriptTypeDeclaration)o).IsExported,
            ["HasBaseClass"] = o => (object)((JavaScriptTypeDeclaration)o).HasBaseClass,
        };
        bindings.Accessors["JavaScriptType"] = jsAccessors;

        return bindings;
    }
}
