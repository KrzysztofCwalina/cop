using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Python-specific runtime bindings: the shared <see cref="CodeBindings"/> plus the
/// <c>PythonType</c> accessor set, keyed to <see cref="PythonTypeDeclaration"/>.
/// </summary>
public static class PythonBindings
{
    public static RuntimeBindings Build()
    {
        var bindings = CodeBindings.Build();

        bindings.ClrTypeMappings[typeof(PythonTypeDeclaration)] = "PythonType";

        var pythonAccessors = new Dictionary<string, Func<object, object?>>(bindings.Accessors["Type"])
        {
            ["IsDataclass"] = o => (object)((PythonTypeDeclaration)o).IsDataclass,
            ["IsEnum"] = o => (object)((PythonTypeDeclaration)o).IsEnum,
        };
        bindings.Accessors["PythonType"] = pythonAccessors;

        return bindings;
    }
}
